// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Srt;

/// <summary>
/// Windows Media Foundation H.264/AAC encoder feeding an in-memory MPEG-2 transport stream.
/// The encoder consumes the already materialized Runtime Program RGBA8/Float32 A/V sample and
/// never owns Production authority.
/// </summary>
public sealed class WindowsMediaFoundationSrtEncoder : ISrtPayloadEncoder
{
	private NetworkOutputConfiguration? _configuration;
	private ChunkedComStream? _stream;
	private IntPtr _byteStream;
	private IMFAttributes? _attributes;
	private IMFSinkWriter? _sinkWriter;
	private uint _videoStream;
	private uint _audioStream;
	private bool _mediaFoundationStarted;
	private bool _started;
	private long? _originHundredNanoseconds;
	private long _lastVideoTimestamp = -1;
	private long _lastAudioTimestamp = -1;
	private byte[]? _nv12Buffer;
	private byte[]? _pcm16Buffer;
	private bool _disposed;

	public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		cancellationToken.ThrowIfCancellationRequested();
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("The reference H.264/AAC network encoder requires Windows Media Foundation.");
		if (!SrtNetworkOutputProvider.SupportedFormats.Contains(configuration.VideoFormat))
			throw new NotSupportedException("The requested video format is not supported by the SRT reference encoder.");
		if (configuration.AudioFormat != AudioFormat.Stereo48kFloat32)
			throw new NotSupportedException("The SRT reference encoder requires 48 kHz stereo Float32 Program audio.");
		_configuration = configuration;
		return ValueTask.CompletedTask;
	}

	public ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(sample);
		cancellationToken.ThrowIfCancellationRequested();
		ThrowIfDisposed();
		var configuration = _configuration ?? throw new InvalidOperationException("Network encoder has not been initialized.");

		ValidatePayload(configuration, sample);
		if (!_started)
			InitializeSink(configuration, sample);

		WriteVideo(configuration, sample);
		WriteAudio(configuration, sample);
		return ValueTask.FromResult<ReadOnlyMemory<byte>>(_stream!.Drain());
	}

	public ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_started || _sinkWriter is null || _stream is null)
			return ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);

		MediaFoundationNetwork.ThrowIfFailed(_sinkWriter.Flush(_videoStream));
		MediaFoundationNetwork.ThrowIfFailed(_sinkWriter.Flush(_audioStream));
		return ValueTask.FromResult<ReadOnlyMemory<byte>>(_stream.Drain());
	}

	private void InitializeSink(NetworkOutputConfiguration configuration, NetworkOutputProgramSample firstSample)
	{
		IMFSinkWriter? sink = null;
		IMFMediaType? videoOutput = null;
		IMFMediaType? videoInput = null;
		IMFMediaType? audioOutput = null;
		IMFMediaType? audioInput = null;
		try
		{
			MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFStartup(MediaFoundationNetwork.MfVersion, MediaFoundationNetwork.MfStartupFull));
			_mediaFoundationStarted = true;

			_stream = new ChunkedComStream();
			MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateMFByteStreamOnStream(_stream, out _byteStream));
			MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateAttributes(out _attributes, 2));
			var containerKey = MediaFoundationNetwork.MfTranscodeContainerType;
			var containerValue = MediaFoundationNetwork.MfTranscodeContainerTypeMpeg2;
			MediaFoundationNetwork.ThrowIfFailed(_attributes.SetGUID(ref containerKey, ref containerValue));

			MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateSinkWriterFromURL(
				null,
				_byteStream,
				_attributes,
				out sink));

			videoOutput = CreateVideoOutputType(configuration);
			MediaFoundationNetwork.ThrowIfFailed(sink.AddStream(videoOutput, out _videoStream));
			videoInput = CreateVideoInputType(configuration.VideoFormat);
			MediaFoundationNetwork.ThrowIfFailed(sink.SetInputMediaType(_videoStream, videoInput, null));

			audioOutput = CreateAudioOutputType(configuration);
			MediaFoundationNetwork.ThrowIfFailed(sink.AddStream(audioOutput, out _audioStream));
			audioInput = CreateAudioInputType(configuration.AudioFormat);
			MediaFoundationNetwork.ThrowIfFailed(sink.SetInputMediaType(_audioStream, audioInput, null));

			MediaFoundationNetwork.ThrowIfFailed(sink.BeginWriting());

			var videoTime = ToHundredNanoseconds(firstSample.VideoTiming.PresentationTimestamp, firstSample.VideoTiming.Timebase);
			var audioTime = ToHundredNanoseconds(firstSample.AudioTiming.PresentationTimestamp, firstSample.AudioTiming.Timebase);
			_originHundredNanoseconds = Math.Min(videoTime, audioTime);
			_nv12Buffer = new byte[checked((int)((long)configuration.VideoFormat.Width * configuration.VideoFormat.Height * 3 / 2))];

			_sinkWriter = sink;
			sink = null;
			_started = true;
		}
		catch
		{
			if (sink is not null)
				MediaFoundationNetwork.ReleaseComObject(sink);
			Release();
			throw;
		}
		finally
		{
			if (videoOutput is not null) MediaFoundationNetwork.ReleaseComObject(videoOutput);
			if (videoInput is not null) MediaFoundationNetwork.ReleaseComObject(videoInput);
			if (audioOutput is not null) MediaFoundationNetwork.ReleaseComObject(audioOutput);
			if (audioInput is not null) MediaFoundationNetwork.ReleaseComObject(audioInput);
		}
	}

	private static void ValidatePayload(NetworkOutputConfiguration configuration, NetworkOutputProgramSample sample)
	{
		var videoBytes = checked((int)((long)configuration.VideoFormat.Width * configuration.VideoFormat.Height * 4));
		if (sample.Video.Memory.Length != videoBytes)
			throw new InvalidDataException($"Program RGBA payload length {sample.Video.Memory.Length} does not match expected {videoBytes}.");

		var audioBytes = checked((int)((long)sample.AudioTiming.SampleCount * configuration.AudioFormat.ChannelCount * sizeof(float)));
		if (sample.Audio.Length != audioBytes)
			throw new InvalidDataException($"Program Float32 audio payload length {sample.Audio.Length} does not match expected {audioBytes}.");
		if (sample.VideoTiming.PresentationTimestamp < 0 || sample.AudioTiming.PresentationTimestamp < 0)
			throw new InvalidDataException("Network output timestamps must be non-negative.");
	}

	private void WriteVideo(NetworkOutputConfiguration configuration, NetworkOutputProgramSample sample)
	{
		var required = checked((int)((long)configuration.VideoFormat.Width * configuration.VideoFormat.Height * 3 / 2));
		if (_nv12Buffer is null || _nv12Buffer.Length < required)
			_nv12Buffer = new byte[required];
		ConvertRgbaToNv12(
			sample.Video.Memory.Span,
			_nv12Buffer.AsSpan(0, required),
			checked((int)configuration.VideoFormat.Width),
			checked((int)configuration.VideoFormat.Height));

		var absolute = ToHundredNanoseconds(sample.VideoTiming.PresentationTimestamp, sample.VideoTiming.Timebase);
		var timestamp = checked(absolute - _originHundredNanoseconds!.Value);
		if (timestamp < 0 || timestamp <= _lastVideoTimestamp)
			throw new InvalidDataException("Program video timestamps must be strictly monotonic for streaming.");
		var duration = DivideRound(
			checked((Int128)configuration.VideoFormat.FrameRate.Denominator * 10_000_000),
			configuration.VideoFormat.FrameRate.Numerator);

		WriteSample(_videoStream, _nv12Buffer, required, timestamp, duration);
		_lastVideoTimestamp = timestamp;
	}

	private void WriteAudio(NetworkOutputConfiguration configuration, NetworkOutputProgramSample sample)
	{
		var required = checked((int)((long)sample.AudioTiming.SampleCount * configuration.AudioFormat.ChannelCount * 2));
		if (_pcm16Buffer is null || _pcm16Buffer.Length < required)
			_pcm16Buffer = new byte[required];
		ConvertFloat32ToPcm16(sample.Audio.Span, _pcm16Buffer.AsSpan(0, required));

		var absolute = ToHundredNanoseconds(sample.AudioTiming.PresentationTimestamp, sample.AudioTiming.Timebase);
		var timestamp = checked(absolute - _originHundredNanoseconds!.Value);
		if (timestamp < 0 || timestamp <= _lastAudioTimestamp)
			throw new InvalidDataException("Program audio timestamps must be strictly monotonic for streaming.");
		var duration = DivideRound(
			checked((Int128)sample.AudioTiming.SampleCount * 10_000_000),
			configuration.AudioFormat.SampleRate);

		WriteSample(_audioStream, _pcm16Buffer, required, timestamp, duration);
		_lastAudioTimestamp = timestamp;
	}

	private void WriteSample(uint streamIndex, byte[] payload, int payloadLength, long timestamp, long duration)
	{
		IMFSample? sample = null;
		IMFMediaBuffer? buffer = null;
		try
		{
			MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateSample(out sample));
			MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateMemoryBuffer(checked((uint)payloadLength), out buffer));
			MediaFoundationNetwork.ThrowIfFailed(buffer.Lock(out var destination, out _, out _));
			try
			{
				Marshal.Copy(payload, 0, destination, payloadLength);
			}
			finally
			{
				MediaFoundationNetwork.ThrowIfFailed(buffer.Unlock());
			}
			MediaFoundationNetwork.ThrowIfFailed(buffer.SetCurrentLength(checked((uint)payloadLength)));
			MediaFoundationNetwork.ThrowIfFailed(sample.AddBuffer(buffer));
			MediaFoundationNetwork.ThrowIfFailed(sample.SetSampleTime(timestamp));
			MediaFoundationNetwork.ThrowIfFailed(sample.SetSampleDuration(duration));
			MediaFoundationNetwork.ThrowIfFailed(_sinkWriter!.WriteSample(streamIndex, sample));
		}
		finally
		{
			if (buffer is not null) MediaFoundationNetwork.ReleaseComObject(buffer);
			if (sample is not null) MediaFoundationNetwork.ReleaseComObject(sample);
		}
	}

	private static IMFMediaType CreateVideoOutputType(NetworkOutputConfiguration configuration)
	{
		MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateMediaType(out var mediaType));
		try
		{
			SetGuid(mediaType, MediaFoundationNetwork.MfMtMajorType, MediaFoundationNetwork.MfMediaTypeVideo);
			SetGuid(mediaType, MediaFoundationNetwork.MfMtSubtype, MediaFoundationNetwork.MfVideoFormatH264);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAvgBitrate, configuration.VideoBitRate);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtInterlaceMode, MediaFoundationNetwork.MfVideoInterlaceProgressive);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtMpeg2Profile, MediaFoundationNetwork.H264MainProfile);
			SetRatio(mediaType, MediaFoundationNetwork.MfMtFrameSize, configuration.VideoFormat.Width, configuration.VideoFormat.Height);
			SetRatio(mediaType, MediaFoundationNetwork.MfMtFrameRate,
				checked((uint)configuration.VideoFormat.FrameRate.Numerator),
				checked((uint)configuration.VideoFormat.FrameRate.Denominator));
			SetRatio(mediaType, MediaFoundationNetwork.MfMtPixelAspectRatio, 1, 1);
			return mediaType;
		}
		catch
		{
			MediaFoundationNetwork.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static IMFMediaType CreateVideoInputType(VideoFormat format)
	{
		MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateMediaType(out var mediaType));
		try
		{
			SetGuid(mediaType, MediaFoundationNetwork.MfMtMajorType, MediaFoundationNetwork.MfMediaTypeVideo);
			SetGuid(mediaType, MediaFoundationNetwork.MfMtSubtype, MediaFoundationNetwork.MfVideoFormatNv12);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtInterlaceMode, MediaFoundationNetwork.MfVideoInterlaceProgressive);
			SetRatio(mediaType, MediaFoundationNetwork.MfMtFrameSize, format.Width, format.Height);
			SetRatio(mediaType, MediaFoundationNetwork.MfMtFrameRate,
				checked((uint)format.FrameRate.Numerator),
				checked((uint)format.FrameRate.Denominator));
			SetRatio(mediaType, MediaFoundationNetwork.MfMtPixelAspectRatio, 1, 1);
			return mediaType;
		}
		catch
		{
			MediaFoundationNetwork.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static IMFMediaType CreateAudioOutputType(NetworkOutputConfiguration configuration)
	{
		var format = configuration.AudioFormat;
		MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateMediaType(out var mediaType));
		try
		{
			SetGuid(mediaType, MediaFoundationNetwork.MfMtMajorType, MediaFoundationNetwork.MfMediaTypeAudio);
			SetGuid(mediaType, MediaFoundationNetwork.MfMtSubtype, MediaFoundationNetwork.MfAudioFormatAac);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioNumChannels, format.ChannelCount);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioSamplesPerSecond, format.SampleRate);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioBitsPerSample, 16);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioAvgBytesPerSecond, configuration.AudioBitRate / 8);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioBlockAlignment, 1);
			return mediaType;
		}
		catch
		{
			MediaFoundationNetwork.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static IMFMediaType CreateAudioInputType(AudioFormat format)
	{
		MediaFoundationNetwork.ThrowIfFailed(MediaFoundationNetwork.MFCreateMediaType(out var mediaType));
		try
		{
			var blockAlignment = checked(format.ChannelCount * 2u);
			SetGuid(mediaType, MediaFoundationNetwork.MfMtMajorType, MediaFoundationNetwork.MfMediaTypeAudio);
			SetGuid(mediaType, MediaFoundationNetwork.MfMtSubtype, MediaFoundationNetwork.MfAudioFormatPcm);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioNumChannels, format.ChannelCount);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioSamplesPerSecond, format.SampleRate);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioBitsPerSample, 16);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioBlockAlignment, blockAlignment);
			SetUInt32(mediaType, MediaFoundationNetwork.MfMtAudioAvgBytesPerSecond, checked(format.SampleRate * blockAlignment));
			return mediaType;
		}
		catch
		{
			MediaFoundationNetwork.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static void SetGuid(IMFMediaType mediaType, Guid key, Guid value) =>
		MediaFoundationNetwork.ThrowIfFailed(mediaType.SetGUID(ref key, ref value));

	private static void SetUInt32(IMFMediaType mediaType, Guid key, uint value) =>
		MediaFoundationNetwork.ThrowIfFailed(mediaType.SetUINT32(ref key, value));

	private static void SetRatio(IMFMediaType mediaType, Guid key, uint numerator, uint denominator) =>
		MediaFoundationNetwork.ThrowIfFailed(mediaType.SetUINT64(ref key, ((ulong)numerator << 32) | denominator));

	internal static long ToHundredNanoseconds(long timestamp, Timebase timebase)
	{
		if (timestamp < 0) throw new ArgumentOutOfRangeException(nameof(timestamp));
		return DivideRound(checked((Int128)timestamp * timebase.Numerator * 10_000_000), timebase.Denominator);
	}

	internal static void ConvertFloat32ToPcm16(ReadOnlySpan<byte> input, Span<byte> output)
	{
		if ((input.Length & 3) != 0 || output.Length != input.Length / 2)
			throw new ArgumentException("Float32/PCM16 buffer sizes are incompatible.");
		for (var inputOffset = 0; inputOffset < input.Length; inputOffset += 4)
		{
			var bits = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(inputOffset, 4));
			var value = BitConverter.Int32BitsToSingle(bits);
			if (!float.IsFinite(value)) value = 0;
			var clamped = Math.Clamp(value, -1.0f, 1.0f);
			var scaled = clamped <= -1.0f
				? short.MinValue
				: checked((short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero));
			BinaryPrimitives.WriteInt16LittleEndian(output.Slice(inputOffset / 2, 2), scaled);
		}
	}

	internal static void ConvertRgbaToNv12(ReadOnlySpan<byte> rgba, Span<byte> nv12, int width, int height)
	{
		if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
			throw new ArgumentOutOfRangeException(nameof(width));
		if (rgba.Length != checked(width * height * 4) || nv12.Length != checked(width * height * 3 / 2))
			throw new ArgumentException("RGBA/NV12 buffer sizes do not match the video dimensions.");

		var yPlaneLength = checked(width * height);
		for (var y = 0; y < height; y++)
		{
			var row = y * width;
			for (var x = 0; x < width; x++)
			{
				var pixel = (row + x) * 4;
				nv12[row + x] = ToByte(((47 * rgba[pixel]) + (157 * rgba[pixel + 1]) + (16 * rgba[pixel + 2]) + 128) / 256 + 16);
			}
		}
		for (var y = 0; y < height; y += 2)
		{
			for (var x = 0; x < width; x += 2)
			{
				var r = 0; var g = 0; var b = 0;
				for (var dy = 0; dy < 2; dy++)
				for (var dx = 0; dx < 2; dx++)
				{
					var pixel = (((y + dy) * width) + x + dx) * 4;
					r += rgba[pixel]; g += rgba[pixel + 1]; b += rgba[pixel + 2];
				}
				r = (r + 2) / 4; g = (g + 2) / 4; b = (b + 2) / 4;
				var chroma = yPlaneLength + (y / 2 * width) + x;
				nv12[chroma] = ToByte(((-26 * r) - (87 * g) + (112 * b) + 128) / 256 + 128);
				nv12[chroma + 1] = ToByte(((112 * r) - (102 * g) - (10 * b) + 128) / 256 + 128);
			}
		}
	}

	private static byte ToByte(int value) => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
	private static long DivideRound(Int128 numerator, long denominator)
	{
		if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
		return checked((long)((numerator + (denominator / 2)) / denominator));
	}

	private void Release()
	{
		var sink = _sinkWriter;
		_sinkWriter = null;
		_started = false;
		if (sink is not null) MediaFoundationNetwork.ReleaseComObject(sink);
		if (_attributes is not null)
		{
			MediaFoundationNetwork.ReleaseComObject(_attributes);
			_attributes = null;
		}
		if (_byteStream != IntPtr.Zero)
		{
			Marshal.Release(_byteStream);
			_byteStream = IntPtr.Zero;
		}
		_stream = null;
		if (_mediaFoundationStarted)
		{
			_mediaFoundationStarted = false;
			MediaFoundationNetwork.MFShutdown();
		}
	}

	public ValueTask DisposeAsync()
	{
		if (_disposed) return ValueTask.CompletedTask;
		_disposed = true;
		if (_sinkWriter is not null)
		{
			try { MediaFoundationNetwork.ThrowIfFailed(_sinkWriter.FinalizeWriter()); }
			catch { }
		}
		Release();
		return ValueTask.CompletedTask;
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ChunkedComStream : IStream
{
	private readonly MemoryStream _stream = new();
	private long _drainedLength;

	public byte[] Drain()
	{
		lock (_stream)
		{
			if (_stream.Length <= _drainedLength)
				return Array.Empty<byte>();
			var count = checked((int)(_stream.Length - _drainedLength));
			var result = new byte[count];
			var position = _stream.Position;
			_stream.Position = _drainedLength;
			_ = _stream.Read(result, 0, count);
			_drainedLength = _stream.Length;
			_stream.Position = position;
			return result;
		}
	}

	public void Read(byte[] pv, int cb, IntPtr pcbRead)
	{
		lock (_stream)
		{
			var read = _stream.Read(pv, 0, cb);
			if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
		}
	}

	public void Write(byte[] pv, int cb, IntPtr pcbWritten)
	{
		lock (_stream)
		{
			_stream.Write(pv, 0, cb);
			if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
		}
	}

	public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
	{
		lock (_stream)
		{
			var origin = dwOrigin switch
			{
				0 => SeekOrigin.Begin,
				1 => SeekOrigin.Current,
				2 => SeekOrigin.End,
				_ => throw new ArgumentOutOfRangeException(nameof(dwOrigin))
			};
			var position = _stream.Seek(dlibMove, origin);
			if (position < _drainedLength)
				throw new IOException("Streaming container attempted to rewrite already published bytes.");
			if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
		}
	}

	public void SetSize(long libNewSize)
	{
		lock (_stream)
		{
			if (libNewSize < _drainedLength)
				throw new IOException("Streaming container attempted to truncate already published bytes.");
			_stream.SetLength(libNewSize);
		}
	}

	public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
	{
		var buffer = new byte[Math.Min(64 * 1024, checked((int)Math.Min(cb, int.MaxValue)))];
		long total = 0;
		while (total < cb)
		{
			var count = checked((int)Math.Min(buffer.Length, cb - total));
			int read;
			lock (_stream) read = _stream.Read(buffer, 0, count);
			if (read == 0) break;
			pstm.Write(buffer, read, IntPtr.Zero);
			total += read;
		}
		if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, total);
		if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, total);
	}

	public void Commit(int grfCommitFlags) { }
	public void Revert() => throw new COMException("Revert is not supported.", unchecked((int)0x80004001));
	public void LockRegion(long libOffset, long cb, int dwLockType) { }
	public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
	public void Stat(out STATSTG pstatstg, int grfStatFlag)
	{
		lock (_stream)
			pstatstg = new STATSTG { cbSize = _stream.Length, type = 2 };
	}
	public void Clone(out IStream ppstm) => throw new COMException("Clone is not supported.", unchecked((int)0x80004001));
}

internal static class MediaFoundationNetwork
{
	public const int MfVersion = 0x00020070;
	public const int MfStartupFull = 0;
	public const uint MfVideoInterlaceProgressive = 2;
	public const uint H264MainProfile = 77;

	public static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
	public static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
	public static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
	public static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
	public static readonly Guid MfMtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
	public static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
	public static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
	public static readonly Guid MfMtAudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
	public static readonly Guid MfMtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
	public static readonly Guid MfMtAudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
	public static readonly Guid MfMtAudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
	public static readonly Guid MfMtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
	public static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
	public static readonly Guid MfTranscodeContainerType = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
	public static readonly Guid MfTranscodeContainerTypeMpeg2 = new("BFC2DBF9-7BB4-4F8F-AFDE-E112C44BA882");

	public static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");

	[DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(int version, int flags);
	[DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFShutdown();
	[DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType mediaType);
	[DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
	[DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(uint maximumLength, out IMFMediaBuffer buffer);
	[DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMFByteStreamOnStream([MarshalAs(UnmanagedType.Interface)] IStream stream, out IntPtr byteStream);
	[DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	public static extern int MFCreateSinkWriterFromURL(
		[MarshalAs(UnmanagedType.LPWStr)] string? outputUrl,
		IntPtr byteStream,
		IMFAttributes? attributes,
		out IMFSinkWriter sinkWriter);

	public static void ThrowIfFailed(int hresult)
	{
		if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
	}
	public static void ReleaseComObject(object value)
	{
		if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
	}
}

[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
	[PreserveSig] int GetItem(ref Guid key, IntPtr value);
	[PreserveSig] int GetItemType(ref Guid key, out int type);
	[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
	[PreserveSig] int Compare(IMFAttributes other, int matchType, out int result);
	[PreserveSig] int GetUINT32(ref Guid key, out uint value);
	[PreserveSig] int GetUINT64(ref Guid key, out ulong value);
	[PreserveSig] int GetDouble(ref Guid key, out double value);
	[PreserveSig] int GetGUID(ref Guid key, out Guid value);
	[PreserveSig] int GetStringLength(ref Guid key, out uint length);
	[PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
	[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
	[PreserveSig] int GetBlobSize(ref Guid key, out uint size);
	[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint blobSize);
	[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
	[PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
	[PreserveSig] int SetItem(ref Guid key, IntPtr value);
	[PreserveSig] int DeleteItem(ref Guid key);
	[PreserveSig] int DeleteAllItems();
	[PreserveSig] int SetUINT32(ref Guid key, uint value);
	[PreserveSig] int SetUINT64(ref Guid key, ulong value);
	[PreserveSig] int SetDouble(ref Guid key, double value);
	[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
	[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
	[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
	[PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
	[PreserveSig] int LockStore();
	[PreserveSig] int UnlockStore();
	[PreserveSig] int GetCount(out uint count);
	[PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
	[PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
	[PreserveSig] int GetItem(ref Guid key, IntPtr value);
	[PreserveSig] int GetItemType(ref Guid key, out int type);
	[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
	[PreserveSig] int Compare(IMFAttributes other, int matchType, out int result);
	[PreserveSig] int GetUINT32(ref Guid key, out uint value);
	[PreserveSig] int GetUINT64(ref Guid key, out ulong value);
	[PreserveSig] int GetDouble(ref Guid key, out double value);
	[PreserveSig] int GetGUID(ref Guid key, out Guid value);
	[PreserveSig] int GetStringLength(ref Guid key, out uint length);
	[PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
	[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
	[PreserveSig] int GetBlobSize(ref Guid key, out uint size);
	[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint blobSize);
	[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, uint size, out uint blobSize);
	[PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
	[PreserveSig] int SetItem(ref Guid key, IntPtr value);
	[PreserveSig] int DeleteItem(ref Guid key);
	[PreserveSig] int DeleteAllItems();
	[PreserveSig] int SetUINT32(ref Guid key, uint value);
	[PreserveSig] int SetUINT64(ref Guid key, ulong value);
	[PreserveSig] int SetDouble(ref Guid key, double value);
	[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
	[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
	[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
	[PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
	[PreserveSig] int LockStore();
	[PreserveSig] int UnlockStore();
	[PreserveSig] int GetCount(out uint count);
	[PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
	[PreserveSig] int CopyAllItems(IMFAttributes destination);
	[PreserveSig] int GetMajorType(out Guid majorType);
	[PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
	[PreserveSig] int IsEqual(IMFMediaType mediaType, out uint flags);
	[PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr value);
	[PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr value);
}

[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
	[PreserveSig] int GetItem(ref Guid key, IntPtr value);
	[PreserveSig] int GetItemType(ref Guid key, out int type);
	[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
	[PreserveSig] int Compare(IMFAttributes other, int matchType, out int result);
	[PreserveSig] int GetUINT32(ref Guid key, out uint value);
	[PreserveSig] int GetUINT64(ref Guid key, out ulong value);
	[PreserveSig] int GetDouble(ref Guid key, out double value);
	[PreserveSig] int GetGUID(ref Guid key, out Guid value);
	[PreserveSig] int GetStringLength(ref Guid key, out uint length);
	[PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
	[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
	[PreserveSig] int GetBlobSize(ref Guid key, out uint size);
	[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint blobSize);
	[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
	[PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
	[PreserveSig] int SetItem(ref Guid key, IntPtr value);
	[PreserveSig] int DeleteItem(ref Guid key);
	[PreserveSig] int DeleteAllItems();
	[PreserveSig] int SetUINT32(ref Guid key, uint value);
	[PreserveSig] int SetUINT64(ref Guid key, ulong value);
	[PreserveSig] int SetDouble(ref Guid key, double value);
	[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
	[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
	[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
	[PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
	[PreserveSig] int LockStore();
	[PreserveSig] int UnlockStore();
	[PreserveSig] int GetCount(out uint count);
	[PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
	[PreserveSig] int CopyAllItems(IMFAttributes destination);
	[PreserveSig] int GetSampleFlags(out uint flags);
	[PreserveSig] int SetSampleFlags(uint flags);
	[PreserveSig] int GetSampleTime(out long time);
	[PreserveSig] int SetSampleTime(long time);
	[PreserveSig] int GetSampleDuration(out long duration);
	[PreserveSig] int SetSampleDuration(long duration);
	[PreserveSig] int GetBufferCount(out uint count);
	[PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
	[PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
	[PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
	[PreserveSig] int RemoveBufferByIndex(uint index);
	[PreserveSig] int RemoveAllBuffers();
	[PreserveSig] int GetTotalLength(out uint totalLength);
	[PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
	[PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
	[PreserveSig] int Unlock();
	[PreserveSig] int GetCurrentLength(out uint currentLength);
	[PreserveSig] int SetCurrentLength(uint currentLength);
	[PreserveSig] int GetMaxLength(out uint maxLength);
}

[ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
	[PreserveSig] int AddStream(IMFMediaType targetMediaType, out uint streamIndex);
	[PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputMediaType, IMFAttributes? encodingParameters);
	[PreserveSig] int BeginWriting();
	[PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
	[PreserveSig] int SendStreamTick(uint streamIndex, long timestamp);
	[PreserveSig] int PlaceMarker(uint streamIndex, IntPtr context);
	[PreserveSig] int NotifyEndOfSegment(uint streamIndex);
	[PreserveSig] int Flush(uint streamIndex);
	[PreserveSig] int FinalizeWriter();
	[PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid riid, out IntPtr value);
	[PreserveSig] int GetStatistics(uint streamIndex, IntPtr statistics);
}
