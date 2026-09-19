// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Media;

public sealed record LocalMediaDecodedFrame(
	FrameDescriptor Video,
	ReadOnlyMemory<byte> RgbaPixels,
	AudioBufferDescriptor? Audio,
	ReadOnlyMemory<byte> AudioPayload);

[SupportedOSPlatform("windows")]
internal sealed class WindowsMediaFoundationLocalMediaDecoder : ILocalMediaDecoder
{
	private static readonly Timebase MediaFoundationTimebase = new(1, 10_000_000);
	private readonly IMFSourceReader _reader;
	private readonly MediaAssetId _assetId;
	private readonly MediaSourceId _sourceId;
	private readonly bool _hasAudio;
	private long? _pendingSeekTimestamp;
	private bool _disposed;

	private WindowsMediaFoundationLocalMediaDecoder(
		IMFSourceReader reader,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		LocalMediaProbe probe,
		bool hasAudio)
	{
		_reader = reader;
		_assetId = assetId;
		_sourceId = sourceId;
		Probe = probe;
		_hasAudio = hasAudio;
	}

	public LocalMediaProbe Probe { get; }

	public static LocalMediaDecoderOpenResult TryOpen(
		string path,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		VideoFormat outputFormat)
	{
		if (!OperatingSystem.IsWindows())
			return LocalMediaDecoderOpenResult.Rejected("media.file.platform_unsupported", "Windows Media Foundation is not available on this platform.");

		IMFSourceReader? reader = null;
		var mediaFoundationStarted = false;
		try
		{
			MediaFoundation.ThrowIfFailed(MediaFoundation.MFStartup(MediaFoundation.MfVersion, MediaFoundation.MfStartupFull));
			mediaFoundationStarted = true;

			MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateAttributes(out var readerAttributes, 2));
			try
			{
				var advancedVideoProcessing = MediaFoundation.MfSourceReaderEnableAdvancedVideoProcessing;
				MediaFoundation.ThrowIfFailed(readerAttributes.SetUINT32(ref advancedVideoProcessing, 1));
				MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateSourceReaderFromURL(path, readerAttributes, out reader));
			}
			finally
			{
				MediaFoundation.ReleaseComObject(readerAttributes);
			}

			MediaFoundation.ThrowIfFailed(reader.SetStreamSelection(MediaFoundation.AllStreams, false));
			MediaFoundation.ThrowIfFailed(reader.SetStreamSelection(MediaFoundation.FirstVideoStream, true));

			var videoNative = GetNativeMediaType(reader, MediaFoundation.FirstVideoStream);
			var audioNative = TryGetNativeMediaType(reader, MediaFoundation.FirstAudioStream);
			try
			{
				var videoMajor = GetGuid(videoNative, MediaFoundation.MfMtMajorType);
				var videoSubtype = GetGuid(videoNative, MediaFoundation.MfMtSubtype);
				if (videoMajor != MediaFoundation.MfMediaTypeVideo)
					return RejectAndRelease("media.file.video_stream_invalid", "The local media video stream has an invalid major type.", reader, mediaFoundationStarted);
				if (videoSubtype != MediaFoundation.MfVideoFormatH264)
					return RejectAndRelease("media.file.video_codec_unsupported", "V1 local media supports H.264 video only.", reader, mediaFoundationStarted);

				var hasAudio = audioNative is not null &&
					TryGetGuid(audioNative, MediaFoundation.MfMtMajorType) == MediaFoundation.MfMediaTypeAudio &&
					TryGetGuid(audioNative, MediaFoundation.MfMtSubtype) == MediaFoundation.MfAudioFormatAac;

				var metadata = Mp4LocalMediaMetadataReader.Read(path);
				if (metadata.FrameRateNumerator <= 0 || metadata.FrameRateDenominator <= 0)
					return RejectAndRelease("media.file.metadata_invalid", "The local media file reports an invalid frame rate.", reader, mediaFoundationStarted);
				if (metadata.Duration <= TimeSpan.Zero)
					return RejectAndRelease("media.file.metadata_invalid", "The local media file reports an invalid duration.", reader, mediaFoundationStarted);

				var averageBitRate = TryGetUInt32(videoNative, MediaFoundation.MfMtAvgBitrate);
				var inputProfile = new LocalMediaInputProfile(
					metadata.Width,
					metadata.Height,
					new FrameRate(metadata.FrameRateNumerator, metadata.FrameRateDenominator),
					averageBitRate);
				if (LocalMediaInputPolicy.Validate(inputProfile) is { } inputFailure)
					return RejectAndRelease(inputFailure.Code, inputFailure.Message, reader, mediaFoundationStarted);

				ConfigureDecodedVideo(reader, MediaFoundation.FirstVideoStream, outputFormat);
				MediaFoundation.ThrowIfFailed(reader.SetStreamSelection(MediaFoundation.FirstVideoStream, true));
				if (hasAudio)
				{
					MediaFoundation.ThrowIfFailed(reader.SetStreamSelection(MediaFoundation.FirstAudioStream, true));
					ConfigureDecodedAudio(reader, MediaFoundation.FirstAudioStream);
				}

				var probe = new LocalMediaProbe(
					MediaContractVersion.Current,
					assetId,
					sourceId,
					System.IO.Path.GetFileName(path),
					MediaContainerFormat.Mp4,
					MediaVideoCodec.H264,
					hasAudio ? MediaAudioCodec.Aac : MediaAudioCodec.None,
					outputFormat,
					AudioFormat.Stereo48kFloat32,
					metadata.Duration);

				return LocalMediaDecoderOpenResult.Ready(new WindowsMediaFoundationLocalMediaDecoder(
					reader,
					assetId,
					sourceId,
					probe,
					hasAudio));
			}
			finally
			{
				MediaFoundation.ReleaseComObject(videoNative);
				MediaFoundation.ReleaseComObject(audioNative);
			}
		}
		catch (Exception exception) when (exception is COMException or ExternalException or InvalidDataException or IOException or OverflowException)
		{
			if (reader is not null)
				MediaFoundation.ReleaseComObject(reader);
			if (mediaFoundationStarted)
				MediaFoundation.MFShutdown();
			return LocalMediaDecoderOpenResult.Rejected("media.file.probe_failed", $"The local media file could not be probed: {exception.Message}");
		}
	}

	public LocalMediaSeekResult SeekToFrame(long frameNumber)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var totalFrames = LocalMediaFrameMath.GetTotalFrames(Probe);
		if (frameNumber < 0 || frameNumber >= totalFrames)
		{
			return LocalMediaSeekResult.Rejected(
				frameNumber,
				"media.file.seek_out_of_range",
				$"Frame '{frameNumber}' is outside the valid range 0..{totalFrames - 1}.");
		}

		try
		{
			var target = LocalMediaFrameMath.FrameToTimeSpan(frameNumber, Probe.VideoFormat.FrameRate).Ticks;
			MediaFoundation.ThrowIfFailed(_reader.Flush(MediaFoundation.AllStreams));
			var timeFormat = Guid.Empty;
			var position = new PropVariant
			{
				VarType = MediaFoundation.VtI8,
				Int64Value = target
			};
			MediaFoundation.ThrowIfFailed(_reader.SetCurrentPosition(ref timeFormat, ref position));
			_pendingSeekTimestamp = target;
			return LocalMediaSeekResult.Positioned(frameNumber);
		}
		catch (Exception exception) when (exception is COMException or ExternalException or InvalidDataException or OverflowException)
		{
			return LocalMediaSeekResult.Rejected(
				frameNumber,
				"media.file.seek_failed",
				$"Local media seek failed: {exception.Message}");
		}
	}

	public bool TryReadNext(ulong sequenceNumber, out LocalMediaDecodedFrame? frame)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var minimumTimestamp = _pendingSeekTimestamp;
		long videoTimestamp;
		byte[] videoPayload;
		try
		{
			if (!TryReadSample(
				MediaFoundation.FirstVideoStream,
				minimumTimestamp,
				copy2DContiguous: true,
				out videoTimestamp,
				out videoPayload))
			{
				frame = null;
				return false;
			}
		}
		catch (ExternalException exception)
		{
			throw new InvalidDataException($"Video sample read failed: {exception.Message}", exception);
		}

		byte[] rgba;
		try
		{
			rgba = ConvertNv12ToRgba(videoPayload, Probe.VideoFormat);
		}
		catch (InvalidDataException exception)
		{
			throw new InvalidDataException($"Video frame conversion failed: {exception.Message}", exception);
		}
		var timing = new FrameTiming(sequenceNumber, videoTimestamp, MediaFoundationTimebase);
		var surface = new SurfaceDescriptor(
			new SurfaceId(LocalMediaIdentity.Create("surface", _assetId.ToString(), sequenceNumber.ToString())),
			Probe.VideoFormat,
			SurfaceStorageDomain.Host,
			SurfaceOwnership.ProducerOwned,
			new SurfaceLifetimeDescriptor(new Generation(sequenceNumber), null),
			new OpaqueSurfaceHandle("local.media.rgba", $"{_assetId}:{sequenceNumber}"));
		var video = new FrameDescriptor(MediaContractVersion.Current, _sourceId, surface, timing);

		AudioBufferDescriptor? audio = null;
		ReadOnlyMemory<byte> audioPayload = ReadOnlyMemory<byte>.Empty;
		if (_hasAudio)
		{
			long audioTimestamp;
			byte[] decodedAudio;
			bool audioAvailable;
			try
			{
				audioAvailable = TryReadSample(
					MediaFoundation.FirstAudioStream,
					minimumTimestamp,
					copy2DContiguous: false,
					out audioTimestamp,
					out decodedAudio);
			}
			catch (ExternalException exception)
			{
				throw new InvalidDataException($"Audio sample read failed: {exception.Message}", exception);
			}

			if (audioAvailable && decodedAudio.Length > 0)
			{
				var bytesPerPcmFrame = checked((int)Probe.AudioFormat.ChannelCount * sizeof(short));
				if (decodedAudio.Length % bytesPerPcmFrame != 0)
					throw new InvalidDataException("Decoded local media PCM payload is not aligned to complete stereo Int16 samples.");
				var sampleCount = checked((uint)(decodedAudio.Length / bytesPerPcmFrame));
				if (sampleCount > 0)
				{
					var floatAudio = ConvertPcm16ToFloat32(decodedAudio);
					var audioSamplePosition = TimestampToAudioSamplePosition(audioTimestamp, Probe.AudioFormat.SampleRate);
					audio = new AudioBufferDescriptor(
						MediaContractVersion.Current,
						new AudioStreamId(LocalMediaIdentity.Create("audio-stream", _assetId.ToString())),
						Probe.AudioFormat,
						LocalMediaIdentity.Create("timing-domain", _assetId.ToString()),
						new AudioBufferTiming(audioSamplePosition, sampleCount, audioTimestamp, MediaFoundationTimebase),
						new OpaqueAudioHandle("local.media.audio.float32", $"{_assetId}:{sequenceNumber}"));
					audioPayload = floatAudio;
				}
			}
		}

		_pendingSeekTimestamp = null;
		frame = new LocalMediaDecodedFrame(video, rgba, audio, audioPayload);
		return true;
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		MediaFoundation.ReleaseComObject(_reader);
		MediaFoundation.MFShutdown();
	}

	private bool TryReadSample(
		uint streamIndex,
		long? minimumTimestamp,
		bool copy2DContiguous,
		out long timestamp,
		out byte[] payload)
	{
		while (true)
		{
			MediaFoundation.ThrowIfFailed(_reader.ReadSample(
				streamIndex,
				0,
				out _,
				out var flags,
				out timestamp,
				out var sample));

			if (sample is null)
			{
				if ((flags & MediaFoundation.SourceReaderEndOfStream) != 0)
				{
					payload = Array.Empty<byte>();
					return false;
				}

				continue;
			}

			try
			{
				if (minimumTimestamp is not null && timestamp < minimumTimestamp.Value)
					continue;

				MediaFoundation.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer));
				try
				{
					payload = copy2DContiguous
						? Copy2DBufferToContiguous(buffer)
						: CopyMediaBuffer(buffer);
					return true;
				}
				finally
				{
					MediaFoundation.ReleaseComObject(buffer);
				}
			}
			finally
			{
				MediaFoundation.ReleaseComObject(sample);
			}
		}
	}

	private static byte[] Copy2DBufferToContiguous(IMFMediaBuffer buffer)
	{
		if (buffer is not IMF2DBuffer buffer2D)
			return CopyMediaBuffer(buffer);

		MediaFoundation.ThrowIfFailed(buffer2D.GetContiguousLength(out var contiguousLength));
		var payload = new byte[checked((int)contiguousLength)];
		if (payload.Length == 0)
			return payload;

		var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
		try
		{
			MediaFoundation.ThrowIfFailed(
				buffer2D.ContiguousCopyTo(handle.AddrOfPinnedObject(), contiguousLength));
		}
		finally
		{
			handle.Free();
		}

		return payload;
	}

	private static byte[] CopyMediaBuffer(IMFMediaBuffer buffer)
	{
		MediaFoundation.ThrowIfFailed(buffer.Lock(out var address, out _, out var currentLength));
		try
		{
			var payload = new byte[checked((int)currentLength)];
			if (payload.Length > 0)
				Marshal.Copy(address, payload, 0, payload.Length);
			return payload;
		}
		finally
		{
			MediaFoundation.ThrowIfFailed(buffer.Unlock());
		}
	}

	private static ulong TimestampToAudioSamplePosition(long timestamp, uint sampleRate)
	{
		if (timestamp <= 0)
			return 0;

		var position = decimal.Floor((decimal)timestamp * sampleRate / 10_000_000m);
		return checked((ulong)position);
	}

	private static byte[] ConvertNv12ToRgba(byte[] source, VideoFormat format)
	{
		var width = checked((int)format.Width);
		var height = checked((int)format.Height);
		if ((width & 1) != 0 || (height & 1) != 0)
			throw new InvalidDataException("NV12 local media frames require even width and height.");

		var visibleYPlaneLength = checked(width * height);
		var minimumLength = checked(visibleYPlaneLength + (visibleYPlaneLength / 2));
		if (source.Length < minimumLength)
			throw new InvalidDataException($"Decoded NV12 frame has '{source.Length}' bytes; expected at least '{minimumLength}'.");

		var storageHeightNumerator = checked((long)source.Length * 2);
		var storageHeightDenominator = checked((long)width * 3);
		if (storageHeightNumerator % storageHeightDenominator != 0)
			throw new InvalidDataException($"Decoded NV12 frame length '{source.Length}' cannot be mapped to an integral storage height at width '{width}'.");
		var storageHeight = checked((int)(storageHeightNumerator / storageHeightDenominator));
		if (storageHeight < height || (storageHeight & 1) != 0)
			throw new InvalidDataException($"Decoded NV12 storage height '{storageHeight}' is invalid for visible height '{height}'.");

		var rgba = new byte[checked(visibleYPlaneLength * 4)];
		var uvOffset = checked(width * storageHeight);
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var yValue = source[(y * width) + x];
				var uvIndex = uvOffset + ((y / 2) * width) + (x & ~1);
				var uValue = source[uvIndex];
				var vValue = source[uvIndex + 1];

				var c = Math.Max(0, yValue - 16);
				var d = uValue - 128;
				var e = vValue - 128;
				var red = ClampByte((298 * c + 459 * e + 128) >> 8);
				var green = ClampByte((298 * c - 55 * d - 136 * e + 128) >> 8);
				var blue = ClampByte((298 * c + 541 * d + 128) >> 8);

				var output = ((y * width) + x) * 4;
				rgba[output] = red;
				rgba[output + 1] = green;
				rgba[output + 2] = blue;
				rgba[output + 3] = byte.MaxValue;
			}
		}
		return rgba;
	}

	private static byte[] ConvertPcm16ToFloat32(byte[] source)
	{
		if ((source.Length & 1) != 0)
			throw new InvalidDataException("Decoded PCM16 audio payload has an odd byte count.");

		var output = new byte[checked(source.Length * 2)];
		for (var sourceOffset = 0; sourceOffset < source.Length; sourceOffset += 2)
		{
			var sample = (short)(source[sourceOffset] | (source[sourceOffset + 1] << 8));
			var value = sample / 32768f;
			var bits = BitConverter.SingleToInt32Bits(value);
			var target = sourceOffset * 2;
			output[target] = (byte)bits;
			output[target + 1] = (byte)(bits >> 8);
			output[target + 2] = (byte)(bits >> 16);
			output[target + 3] = (byte)(bits >> 24);
		}
		return output;
	}

	private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

	private static IMFMediaType GetNativeMediaType(IMFSourceReader reader, uint streamIndex)
	{
		MediaFoundation.ThrowIfFailed(reader.GetNativeMediaType(streamIndex, 0, out var mediaType));
		return mediaType;
	}

	private static IMFMediaType? TryGetNativeMediaType(IMFSourceReader reader, uint streamIndex)
	{
		var result = reader.GetNativeMediaType(streamIndex, 0, out var mediaType);
		if (result >= 0)
			return mediaType;

		MediaFoundation.ReleaseComObject(mediaType);
		return null;
	}

	private static Guid GetGuid(IMFMediaType attributes, Guid key)
	{
		MediaFoundation.ThrowIfFailed(attributes.GetGUID(ref key, out var value));
		return value;
	}

	private static Guid? TryGetGuid(IMFMediaType attributes, Guid key)
	{
		var result = attributes.GetGUID(ref key, out var value);
		return result >= 0 ? value : null;
	}

	private static uint? TryGetUInt32(IMFMediaType attributes, Guid key)
	{
		var result = attributes.GetUINT32(ref key, out var value);
		return result >= 0 ? value : null;
	}

	private static void ConfigureDecodedVideo(
		IMFSourceReader reader,
		uint streamIndex,
		VideoFormat outputFormat)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			var majorKey = MediaFoundation.MfMtMajorType;
			var majorType = MediaFoundation.MfMediaTypeVideo;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref majorKey, ref majorType));

			var subtypeKey = MediaFoundation.MfMtSubtype;
			var subtype = MediaFoundation.MfVideoFormatNv12;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref subtypeKey, ref subtype));

			var frameSizeKey = MediaFoundation.MfMtFrameSize;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT64(
				ref frameSizeKey,
				MediaFoundation.PackRatio(outputFormat.Width, outputFormat.Height)));

			var frameRateKey = MediaFoundation.MfMtFrameRate;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT64(
				ref frameRateKey,
				MediaFoundation.PackRatio(
					outputFormat.FrameRate.Numerator,
					outputFormat.FrameRate.Denominator)));

			var interlaceKey = MediaFoundation.MfMtInterlaceMode;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(
				ref interlaceKey,
				MediaFoundation.MfVideoInterlaceProgressive));

			var pixelAspectKey = MediaFoundation.MfMtPixelAspectRatio;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT64(
				ref pixelAspectKey,
				MediaFoundation.PackRatio(1, 1)));

			MediaFoundation.ThrowIfFailed(reader.SetCurrentMediaType(streamIndex, IntPtr.Zero, mediaType));
		}
		finally
		{
			MediaFoundation.ReleaseComObject(mediaType);
		}
	}

	private static void ConfigureDecodedAudio(IMFSourceReader reader, uint streamIndex)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			var majorKey = MediaFoundation.MfMtMajorType;
			var majorType = MediaFoundation.MfMediaTypeAudio;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref majorKey, ref majorType));

			var subtypeKey = MediaFoundation.MfMtSubtype;
			var subtype = MediaFoundation.MfAudioFormatPcm;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref subtypeKey, ref subtype));

			var channelsKey = MediaFoundation.MfMtAudioNumChannels;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(ref channelsKey, 2));

			var sampleRateKey = MediaFoundation.MfMtAudioSamplesPerSecond;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(ref sampleRateKey, 48_000));

			var bitsPerSampleKey = MediaFoundation.MfMtAudioBitsPerSample;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(ref bitsPerSampleKey, 16));

			var blockAlignmentKey = MediaFoundation.MfMtAudioBlockAlignment;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(ref blockAlignmentKey, 4));

			var averageBytesKey = MediaFoundation.MfMtAudioAverageBytesPerSecond;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(ref averageBytesKey, 192_000));

			MediaFoundation.ThrowIfFailed(reader.SetCurrentMediaType(streamIndex, IntPtr.Zero, mediaType));
		}
		finally
		{
			MediaFoundation.ReleaseComObject(mediaType);
		}
	}

	private static LocalMediaDecoderOpenResult RejectAndRelease(
		string code,
		string message,
		IMFSourceReader reader,
		bool mediaFoundationStarted)
	{
		MediaFoundation.ReleaseComObject(reader);
		if (mediaFoundationStarted)
			MediaFoundation.MFShutdown();
		return LocalMediaDecoderOpenResult.Rejected(code, message);
	}
}

[SupportedOSPlatform("windows")]
internal static class MediaFoundation
{
	public const int MfVersion = 0x00020070;
	public const int MfStartupFull = 0;
	public const ushort VtI8 = 20;
	public const uint FirstVideoStream = 0xFFFFFFFC;
	public const uint FirstAudioStream = 0xFFFFFFFD;
	public const uint AllStreams = 0xFFFFFFFE;
	public const uint SourceReaderEndOfStream = 0x00000002;
	public const uint MfVideoInterlaceProgressive = 2;

	public static Guid MfSourceReaderEnableAdvancedVideoProcessing = new("0F81DA2C-B537-4672-A8B2-A681B17307A3");
	public static Guid MfMtAvgBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
	public static Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
	public static Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
	public static Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
	public static Guid MfMtFrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
	public static Guid MfMtInterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
	public static Guid MfMtPixelAspectRatio = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
	public static Guid MfMtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
	public static Guid MfMtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
	public static Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
	public static Guid MfMtAudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
	public static Guid MfMtAudioAverageBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
	public static Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
	public static Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
	public static Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
	public static Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFShutdown();

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMediaType(out IMFMediaType mediaType);

	[DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	public static extern int MFCreateSourceReaderFromURL(
		string url,
		IMFAttributes? attributes,
		out IMFSourceReader sourceReader);

	public static ulong PackRatio(uint high, uint low) =>
		((ulong)high << 32) | low;

	public static ulong PackRatio(long high, long low) =>
		PackRatio(checked((uint)high), checked((uint)low));

	public static void ThrowIfFailed(int hr)
	{
		if (hr < 0)
			Marshal.ThrowExceptionForHR(hr);
	}

	public static void ReleaseComObject(object? value)
	{
		if (value is not null && Marshal.IsComObject(value))
			Marshal.FinalReleaseComObject(value);
	}
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
	[FieldOffset(0)]
	public ushort VarType;

	[FieldOffset(8)]
	public long Int64Value;

	[FieldOffset(8)]
	public ulong UInt64Value;
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
	[PreserveSig] int GetMajorType(out Guid majorType);
	[PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
	[PreserveSig] int IsEqual(IMFMediaType mediaType, out uint flags);
	[PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr value);
	[PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr value);
}

[ComImport]
[Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
	[PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
	[PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
	[PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
	[PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
	[PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
	[PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
	[PreserveSig] int ReadSample(
		uint streamIndex,
		uint controlFlags,
		out uint actualStreamIndex,
		out uint streamFlags,
		out long timestamp,
		[MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);
	[PreserveSig] int Flush(uint streamIndex);
	[PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid riid, out IntPtr value);
	[PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, out PropVariant value);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

[ComImport]
[Guid("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
	[PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
	[PreserveSig] int Unlock2D();
	[PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
	[PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
	[PreserveSig] int GetContiguousLength(out uint length);
	[PreserveSig] int ContiguousCopyTo(IntPtr destination, uint destinationLength);
	[PreserveSig] int ContiguousCopyFrom(IntPtr source, uint sourceLength);
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
	[PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
	[PreserveSig] int Unlock();
	[PreserveSig] int GetCurrentLength(out uint currentLength);
	[PreserveSig] int SetCurrentLength(uint currentLength);
	[PreserveSig] int GetMaxLength(out uint maxLength);
}
