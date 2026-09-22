// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

/// <summary>
/// Windows Media Foundation MP4 delivery writer for committed Program video/audio.
/// Storage and encoder work execute on the existing ProgramRecorder worker.
/// </summary>
public sealed class WindowsMediaFoundationMp4RecordingWriter :
	IProgramRecordingPayloadWriter,
	IConfigurableProgramRecordingWriter,
	IProgramRecordingFormatCapabilityProvider
{
	private readonly object _gate = new();
	private readonly string _defaultRootDirectory;
	private readonly long? _maximumPayloadBytes;
	private readonly Dictionary<ulong, StagedPayload> _stagedPayloads = new();

	private string? _configuredRootDirectory;
	private string? _configuredFileName;
	private string? _partialPath;
	private string? _finalPath;
	private string? _reservationPath;
	private FileStream? _reservation;
	private IMFSinkWriter? _sinkWriter;
	private uint _videoStream;
	private uint _audioStream;
	private bool _mediaFoundationStarted;
	private bool _sinkStarted;
	private bool _opened;
	private VideoFormat? _videoFormat;
	private AudioFormat? _audioFormat;
	private long? _originHundredNanoseconds;
	private long _lastVideoTimestamp = -1;
	private long _lastAudioTimestamp = -1;
	private byte[]? _nv12Buffer;
	private byte[]? _pcm16Buffer;
	private long _payloadBytesWritten;

	public WindowsMediaFoundationMp4RecordingWriter(string rootDirectory, long? maximumPayloadBytes = null)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory))
			throw new ArgumentException("Recording root directory is required.", nameof(rootDirectory));

		if (maximumPayloadBytes is <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes), "Recording payload quota must be greater than zero when specified.");

		_defaultRootDirectory = Path.GetFullPath(rootDirectory);
		_maximumPayloadBytes = maximumPayloadBytes;
	}

	public ProgramRecordingFormatAvailability FormatAvailability =>
		ProfessionalRecordingFormats.GetMp4H264AacAvailability();

	public string? FinalPath => _finalPath;

	public string ConfigureTarget(string destinationDirectory, string fileName)
	{
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ArgumentException("Recording destination directory is required.", nameof(destinationDirectory));
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("Recording file name is required.", nameof(fileName));

		var normalized = NormalizeFileName(fileName);
		lock (_gate)
		{
			if (_opened)
				throw new InvalidOperationException("Recording target cannot change while the writer is open.");

			_configuredRootDirectory = Path.GetFullPath(destinationDirectory.Trim());
			_configuredFileName = normalized;
		}

		return normalized;
	}

	public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();

		if (!OperatingSystem.IsWindows())
			throw new RecordingOutputUnavailableException("MP4 H.264/AAC recording requires Windows Media Foundation.");

		string rootDirectory;
		string fileName;
		lock (_gate)
		{
			if (_opened)
				throw new InvalidOperationException("MP4 recording writer is already open.");

			rootDirectory = _configuredRootDirectory ?? _defaultRootDirectory;
			fileName = _configuredFileName ?? request.Output.OutputId + ProfessionalRecordingFormats.Mp4H264Aac.FileExtension;
		}

		Directory.CreateDirectory(rootDirectory);
		var finalPath = Path.Combine(rootDirectory, fileName);
		var partialPath = BuildPartialPath(finalPath);
		var reservationPath = finalPath + ".lock";
		if (File.Exists(finalPath) || File.Exists(partialPath) || File.Exists(reservationPath))
			throw new RecordingOutputUnavailableException("Recording output identity already exists in the target directory.");

		FileStream? reservation = null;
		try
		{
			reservation = new FileStream(
				reservationPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 1,
				FileOptions.WriteThrough);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			reservation?.Dispose();
			throw new RecordingOutputUnavailableException($"Recording output could not be reserved: {exception.Message}");
		}

		lock (_gate)
		{
			_stagedPayloads.Clear();
			_partialPath = partialPath;
			_finalPath = finalPath;
			_reservationPath = reservationPath;
			_reservation = reservation;
			_videoFormat = null;
			_audioFormat = null;
			_originHundredNanoseconds = null;
			_lastVideoTimestamp = -1;
			_lastAudioTimestamp = -1;
			_nv12Buffer = null;
			_pcm16Buffer = null;
			_payloadBytesWritten = 0;
			_opened = true;
		}

		return ValueTask.CompletedTask;
	}

	public void StagePayload(
		ulong sequenceNumber,
		ReadOnlyMemory<byte> videoPayload,
		ReadOnlyMemory<byte> audioPayload)
	{
		if (videoPayload.IsEmpty)
			throw new ArgumentException("MP4 recording requires a non-empty Program video payload.", nameof(videoPayload));

		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (!_stagedPayloads.TryAdd(sequenceNumber, new StagedPayload(videoPayload, audioPayload)))
				throw new InvalidOperationException($"Recording payload for sequence '{sequenceNumber}' is already staged.");
		}
	}

	public void DiscardPayload(ulong sequenceNumber)
	{
		lock (_gate)
			_stagedPayloads.Remove(sequenceNumber);
	}

	public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(sample);
		cancellationToken.ThrowIfCancellationRequested();

		StagedPayload payload;
		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (!_stagedPayloads.Remove(sample.SequenceNumber, out payload))
				throw new InvalidDataException($"MP4 recording payload for sequence '{sample.SequenceNumber}' was not staged.");
		}

		ValidateSample(sample, payload);
		var payloadBytes = checked((long)payload.Video.Length + payload.Audio.Length);
		lock (_gate)
		{
			var nextTotal = checked(_payloadBytesWritten + payloadBytes);
			if (_maximumPayloadBytes is { } maximum && nextTotal > maximum)
				throw new IOException($"MP4 recording payload quota of {maximum} bytes was exhausted.");
			_payloadBytesWritten = nextTotal;
		}

		if (!_sinkStarted)
			InitializeSink(sample);

		WriteVideo(sample, payload.Video.Span);
		WriteAudio(sample, payload.Audio.Span);
		return ValueTask.CompletedTask;
	}

	public ValueTask FinalizeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		IMFSinkWriter sink;
		string partialPath;
		string finalPath;
		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (_stagedPayloads.Count != 0)
				throw new InvalidDataException("MP4 recording cannot finalize while staged payloads remain unwritten.");
			if (!_sinkStarted || _sinkWriter is null)
				throw new InvalidDataException("MP4 recording cannot finalize before at least one complete A/V sample was written.");

			sink = _sinkWriter;
			partialPath = _partialPath!;
			finalPath = _finalPath!;
		}

		MediaFoundation.ThrowIfFailed(sink.FinalizeWriter());
		ReleaseSinkAndMediaFoundation();

		if (!File.Exists(partialPath))
			throw new InvalidDataException("Media Foundation did not publish the expected partial MP4 artifact.");
		File.Move(partialPath, finalPath, overwrite: false);
		ReleaseReservation(deleteReservation: true);

		lock (_gate)
			_opened = false;

		return ValueTask.CompletedTask;
	}

	public ValueTask AbortAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		string? partialPath;
		lock (_gate)
		{
			partialPath = _partialPath;
			_stagedPayloads.Clear();
			_opened = false;
		}

		ReleaseSinkAndMediaFoundation();
		if (partialPath is not null)
			TryDelete(partialPath);
		ReleaseReservation(deleteReservation: true);
		return ValueTask.CompletedTask;
	}

	private static string NormalizeFileName(string fileName)
	{
		var trimmed = fileName.Trim();
		if (!string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal) ||
			trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new ArgumentException(
				"Recording file name must be a valid file name without directory components.",
				nameof(fileName));
		}

		var extension = Path.GetExtension(trimmed);
		if (string.IsNullOrEmpty(extension))
			return trimmed + ProfessionalRecordingFormats.Mp4H264Aac.FileExtension;
		if (!string.Equals(extension, ProfessionalRecordingFormats.Mp4H264Aac.FileExtension, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("The professional recording writer supports the .mp4 container only.", nameof(fileName));

		return trimmed;
	}

	private static string BuildPartialPath(string finalPath)
	{
		var directory = Path.GetDirectoryName(finalPath)!;
		var stem = Path.GetFileNameWithoutExtension(finalPath);
		return Path.Combine(directory, stem + ".partial.mp4");
	}

	private void ValidateSample(RecordingProgramSample sample, StagedPayload payload)
	{
		var videoFormat = sample.Video.Surface.Format;
		var capability = ProfessionalRecordingFormats.Mp4H264Aac;
		if (!capability.SupportedInputVideoFormats.Contains(videoFormat))
		{
			throw new InvalidDataException(
				$"MP4 recording does not support Program format '{videoFormat.Width}x{videoFormat.Height} {videoFormat.FrameRate}'.");
		}

		if (sample.Audio is null)
			throw new InvalidDataException("Qualified MP4 recording requires Program audio.");
		if (sample.Audio.Format != capability.RequiredInputAudioFormat)
			throw new InvalidDataException("Qualified MP4 recording requires 48 kHz stereo Float32 Program audio.");

		var expectedVideo = checked((int)((long)videoFormat.Width * videoFormat.Height * 4));
		if (payload.Video.Length != expectedVideo)
			throw new InvalidDataException($"Program RGBA8 payload length {payload.Video.Length} does not match expected {expectedVideo}.");

		var expectedAudio = checked((int)((long)sample.Audio.Timing.SampleCount * sample.Audio.Format.ChannelCount * sizeof(float)));
		if (payload.Audio.Length != expectedAudio)
			throw new InvalidDataException($"Program Float32 audio payload length {payload.Audio.Length} does not match expected {expectedAudio}.");

		if (_videoFormat is { } establishedVideo && establishedVideo != videoFormat)
			throw new InvalidDataException("Program video format changed during an active MP4 recording.");
		if (_audioFormat is { } establishedAudio && establishedAudio != sample.Audio.Format)
			throw new InvalidDataException("Program audio format changed during an active MP4 recording.");
	}

	private void InitializeSink(RecordingProgramSample firstSample)
	{
		var videoFormat = firstSample.Video.Surface.Format;
		var audioFormat = firstSample.Audio!.Format;
		IMFSinkWriter? sink = null;
		IMFMediaType? videoOutput = null;
		IMFMediaType? videoInput = null;
		IMFMediaType? audioOutput = null;
		IMFMediaType? audioInput = null;

		try
		{
			MediaFoundation.ThrowIfFailed(MediaFoundation.MFStartup(MediaFoundation.MfVersion, MediaFoundation.MfStartupFull));
			_mediaFoundationStarted = true;

			MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateSinkWriterFromURL(_partialPath!, IntPtr.Zero, null, out sink));

			videoOutput = CreateVideoOutputType(videoFormat);
			MediaFoundation.ThrowIfFailed(sink.AddStream(videoOutput, out _videoStream));
			videoInput = CreateVideoInputType(videoFormat);
			MediaFoundation.ThrowIfFailed(sink.SetInputMediaType(_videoStream, videoInput, null));

			audioOutput = CreateAudioOutputType(audioFormat);
			MediaFoundation.ThrowIfFailed(sink.AddStream(audioOutput, out _audioStream));
			audioInput = CreateAudioInputType(audioFormat);
			MediaFoundation.ThrowIfFailed(sink.SetInputMediaType(_audioStream, audioInput, null));

			MediaFoundation.ThrowIfFailed(sink.BeginWriting());

			var videoTime = ToHundredNanoseconds(
				firstSample.Video.Timing.PresentationTimestamp,
				firstSample.Video.Timing.Timebase);
			var audioTime = ToHundredNanoseconds(
				firstSample.Audio.Timing.PresentationTimestamp,
				firstSample.Audio.Timing.Timebase);

			_sinkWriter = sink;
			sink = null;
			_videoFormat = videoFormat;
			_audioFormat = audioFormat;
			_originHundredNanoseconds = Math.Min(videoTime, audioTime);
			_nv12Buffer = new byte[checked((int)((long)videoFormat.Width * videoFormat.Height * 3 / 2))];
			_sinkStarted = true;
		}
		catch
		{
			if (sink is not null)
				MediaFoundation.ReleaseComObject(sink);
			ReleaseSinkAndMediaFoundation();
			throw;
		}
		finally
		{
			if (videoOutput is not null) MediaFoundation.ReleaseComObject(videoOutput);
			if (videoInput is not null) MediaFoundation.ReleaseComObject(videoInput);
			if (audioOutput is not null) MediaFoundation.ReleaseComObject(audioOutput);
			if (audioInput is not null) MediaFoundation.ReleaseComObject(audioInput);
		}
	}

	private static IMFMediaType CreateVideoOutputType(VideoFormat format)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			SetGuid(mediaType, MediaFoundation.MfMtMajorType, MediaFoundation.MfMediaTypeVideo);
			SetGuid(mediaType, MediaFoundation.MfMtSubtype, MediaFoundation.MfVideoFormatH264);
			SetUInt32(mediaType, MediaFoundation.MfMtAvgBitrate, ProfessionalRecordingFormats.Mp4H264Aac.VideoBitRate);
			SetUInt32(mediaType, MediaFoundation.MfMtInterlaceMode, MediaFoundation.MfVideoInterlaceProgressive);
			SetUInt32(mediaType, MediaFoundation.MfMtMpeg2Profile, MediaFoundation.H264MainProfile);
			SetRatio(mediaType, MediaFoundation.MfMtFrameSize, format.Width, format.Height);
			SetRatio(mediaType, MediaFoundation.MfMtFrameRate, checked((uint)format.FrameRate.Numerator), checked((uint)format.FrameRate.Denominator));
			SetRatio(mediaType, MediaFoundation.MfMtPixelAspectRatio, 1, 1);
			return mediaType;
		}
		catch
		{
			MediaFoundation.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static IMFMediaType CreateVideoInputType(VideoFormat format)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			SetGuid(mediaType, MediaFoundation.MfMtMajorType, MediaFoundation.MfMediaTypeVideo);
			SetGuid(mediaType, MediaFoundation.MfMtSubtype, MediaFoundation.MfVideoFormatNv12);
			SetUInt32(mediaType, MediaFoundation.MfMtInterlaceMode, MediaFoundation.MfVideoInterlaceProgressive);
			SetRatio(mediaType, MediaFoundation.MfMtFrameSize, format.Width, format.Height);
			SetRatio(mediaType, MediaFoundation.MfMtFrameRate, checked((uint)format.FrameRate.Numerator), checked((uint)format.FrameRate.Denominator));
			SetRatio(mediaType, MediaFoundation.MfMtPixelAspectRatio, 1, 1);
			return mediaType;
		}
		catch
		{
			MediaFoundation.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static IMFMediaType CreateAudioOutputType(AudioFormat format)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			SetGuid(mediaType, MediaFoundation.MfMtMajorType, MediaFoundation.MfMediaTypeAudio);
			SetGuid(mediaType, MediaFoundation.MfMtSubtype, MediaFoundation.MfAudioFormatAac);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioNumChannels, format.ChannelCount);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioSamplesPerSecond, format.SampleRate);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioBitsPerSample, 16);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioAvgBytesPerSecond, ProfessionalRecordingFormats.Mp4H264Aac.AudioBitRate / 8);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioBlockAlignment, 1);
			return mediaType;
		}
		catch
		{
			MediaFoundation.ReleaseComObject(mediaType);
			throw;
		}
	}

	private static IMFMediaType CreateAudioInputType(AudioFormat format)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			var blockAlignment = checked(format.ChannelCount * 2u);
			SetGuid(mediaType, MediaFoundation.MfMtMajorType, MediaFoundation.MfMediaTypeAudio);
			SetGuid(mediaType, MediaFoundation.MfMtSubtype, MediaFoundation.MfAudioFormatPcm);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioNumChannels, format.ChannelCount);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioSamplesPerSecond, format.SampleRate);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioBitsPerSample, 16);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioBlockAlignment, blockAlignment);
			SetUInt32(mediaType, MediaFoundation.MfMtAudioAvgBytesPerSecond, checked(format.SampleRate * blockAlignment));
			return mediaType;
		}
		catch
		{
			MediaFoundation.ReleaseComObject(mediaType);
			throw;
		}
	}

	private void WriteVideo(RecordingProgramSample sample, ReadOnlySpan<byte> rgba)
	{
		var format = sample.Video.Surface.Format;
		var nv12 = _nv12Buffer ?? throw new InvalidOperationException("NV12 buffer is not initialized.");
		ConvertRgbaToNv12(rgba, nv12, checked((int)format.Width), checked((int)format.Height));

		var absolute = ToHundredNanoseconds(sample.Video.Timing.PresentationTimestamp, sample.Video.Timing.Timebase);
		var timestamp = checked(absolute - _originHundredNanoseconds!.Value);
		if (timestamp < 0 || timestamp <= _lastVideoTimestamp)
			throw new InvalidDataException("Program video timestamps are not strictly monotonic for MP4 recording.");
		var duration = DivideRound(
			checked((Int128)10_000_000 * format.FrameRate.Denominator),
			format.FrameRate.Numerator);

		WriteMediaFoundationSample(_videoStream, nv12, nv12.Length, timestamp, duration);
		_lastVideoTimestamp = timestamp;
	}

	private void WriteAudio(RecordingProgramSample sample, ReadOnlySpan<byte> float32)
	{
		var audio = sample.Audio!;
		var required = checked((int)((long)audio.Timing.SampleCount * audio.Format.ChannelCount * 2));
		if (_pcm16Buffer is null || _pcm16Buffer.Length < required)
			_pcm16Buffer = new byte[required];

		var pcm = _pcm16Buffer.AsSpan(0, required);
		ConvertFloat32ToPcm16(float32, pcm);

		var absolute = ToHundredNanoseconds(audio.Timing.PresentationTimestamp, audio.Timing.Timebase);
		var timestamp = checked(absolute - _originHundredNanoseconds!.Value);
		if (timestamp < 0 || timestamp <= _lastAudioTimestamp)
			throw new InvalidDataException("Program audio timestamps are not strictly monotonic for MP4 recording.");
		var duration = DivideRound(
			checked((Int128)audio.Timing.SampleCount * 10_000_000),
			audio.Format.SampleRate);

		WriteMediaFoundationSample(_audioStream, _pcm16Buffer!, required, timestamp, duration);
		_lastAudioTimestamp = timestamp;
	}

	private void WriteMediaFoundationSample(
		uint streamIndex,
		byte[] payload,
		int payloadLength,
		long timestamp,
		long duration)
	{
		ArgumentNullException.ThrowIfNull(payload);
		if (payloadLength <= 0 || payloadLength > payload.Length)
			throw new ArgumentOutOfRangeException(nameof(payloadLength));

		IMFSample? sample = null;
		IMFMediaBuffer? buffer = null;
		try
		{
			MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateSample(out sample));
			MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMemoryBuffer(checked((uint)payloadLength), out buffer));
			MediaFoundation.ThrowIfFailed(buffer.Lock(out var destination, out _, out _));
			try
			{
				Marshal.Copy(payload, 0, destination, payloadLength);
			}
			finally
			{
				MediaFoundation.ThrowIfFailed(buffer.Unlock());
			}
			MediaFoundation.ThrowIfFailed(buffer.SetCurrentLength(checked((uint)payloadLength)));
			MediaFoundation.ThrowIfFailed(sample.AddBuffer(buffer));
			MediaFoundation.ThrowIfFailed(sample.SetSampleTime(timestamp));
			MediaFoundation.ThrowIfFailed(sample.SetSampleDuration(duration));
			MediaFoundation.ThrowIfFailed(_sinkWriter!.WriteSample(streamIndex, sample));
		}
		finally
		{
			if (buffer is not null) MediaFoundation.ReleaseComObject(buffer);
			if (sample is not null) MediaFoundation.ReleaseComObject(sample);
		}
	}

	internal static long ToHundredNanoseconds(long timestamp, Timebase timebase)
	{
		if (timestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timestamp), "Media timestamp must not be negative.");
		var numerator = checked((Int128)timestamp * timebase.Numerator * 10_000_000);
		return DivideRound(numerator, timebase.Denominator);
	}

	internal static void ConvertFloat32ToPcm16(ReadOnlySpan<byte> input, Span<byte> output)
	{
		if ((input.Length & 3) != 0)
			throw new ArgumentException("Float32 audio payload length must be divisible by four.", nameof(input));
		if (output.Length != input.Length / 2)
			throw new ArgumentException("PCM16 output must contain one 16-bit sample per Float32 input sample.", nameof(output));

		for (var inputOffset = 0; inputOffset < input.Length; inputOffset += 4)
		{
			var bits = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(inputOffset, 4));
			var value = BitConverter.Int32BitsToSingle(bits);
			if (!float.IsFinite(value))
				value = 0;
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
			throw new ArgumentOutOfRangeException(nameof(width), "NV12 conversion requires positive even dimensions.");
		if (rgba.Length != checked(width * height * 4))
			throw new ArgumentException("RGBA payload length does not match dimensions.", nameof(rgba));
		if (nv12.Length != checked(width * height * 3 / 2))
			throw new ArgumentException("NV12 payload length does not match dimensions.", nameof(nv12));

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
				var r = 0;
				var g = 0;
				var b = 0;
				for (var dy = 0; dy < 2; dy++)
				{
					for (var dx = 0; dx < 2; dx++)
					{
						var pixel = (((y + dy) * width) + x + dx) * 4;
						r += rgba[pixel];
						g += rgba[pixel + 1];
						b += rgba[pixel + 2];
					}
				}

				r = (r + 2) / 4;
				g = (g + 2) / 4;
				b = (b + 2) / 4;
				var chroma = yPlaneLength + (y / 2 * width) + x;
				nv12[chroma] = ToByte(((-26 * r) - (87 * g) + (112 * b) + 128) / 256 + 128);
				nv12[chroma + 1] = ToByte(((112 * r) - (102 * g) - (10 * b) + 128) / 256 + 128);
			}
		}
	}

	private static byte ToByte(int value) => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);

	private static long DivideRound(Int128 numerator, long denominator)
	{
		if (denominator <= 0)
			throw new ArgumentOutOfRangeException(nameof(denominator));
		return checked((long)((numerator + (denominator / 2)) / denominator));
	}

	private static void SetGuid(IMFMediaType mediaType, Guid key, Guid value)
	{
		MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref key, ref value));
	}

	private static void SetUInt32(IMFMediaType mediaType, Guid key, uint value)
	{
		MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(ref key, value));
	}

	private static void SetRatio(IMFMediaType mediaType, Guid key, uint numerator, uint denominator)
	{
		var packed = ((ulong)numerator << 32) | denominator;
		MediaFoundation.ThrowIfFailed(mediaType.SetUINT64(ref key, packed));
	}

	private void EnsureOpenUnsafe()
	{
		if (!_opened || _partialPath is null || _finalPath is null)
			throw new InvalidOperationException("MP4 recording writer is not open.");
	}

	private void ReleaseSinkAndMediaFoundation()
	{
		var sink = _sinkWriter;
		_sinkWriter = null;
		_sinkStarted = false;
		if (sink is not null)
			MediaFoundation.ReleaseComObject(sink);

		if (_mediaFoundationStarted)
		{
			_mediaFoundationStarted = false;
			MediaFoundation.MFShutdown();
		}
	}

	private void ReleaseReservation(bool deleteReservation)
	{
		FileStream? reservation;
		string? reservationPath;
		lock (_gate)
		{
			reservation = _reservation;
			reservationPath = _reservationPath;
			_reservation = null;
			_reservationPath = null;
		}

		reservation?.Dispose();
		if (deleteReservation && reservationPath is not null)
			TryDelete(reservationPath);
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private readonly record struct StagedPayload(ReadOnlyMemory<byte> Video, ReadOnlyMemory<byte> Audio);
}

internal static class MediaFoundation
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

	public static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");
	public static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFShutdown();

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMediaType(out IMFMediaType mediaType);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateSample(out IMFSample sample);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMemoryBuffer(uint maximumLength, out IMFMediaBuffer buffer);

	[DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	public static extern int MFCreateSinkWriterFromURL(
		string outputUrl,
		IntPtr byteStream,
		IMFAttributes? attributes,
		out IMFSinkWriter sinkWriter);

	public static void ThrowIfFailed(int hresult)
	{
		if (hresult < 0)
			Marshal.ThrowExceptionForHR(hresult);
	}

	public static void ReleaseComObject(object value)
	{
		if (OperatingSystem.IsWindows() && Marshal.IsComObject(value))
			Marshal.FinalReleaseComObject(value);
	}
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

[ComImport]
[Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
