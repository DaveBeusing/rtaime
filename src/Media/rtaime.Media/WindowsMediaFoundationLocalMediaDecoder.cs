// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private readonly LocalMediaSourceReaderCallback _callback;
    private CancellationToken _readCancellation;
	private readonly MediaAssetId _assetId;
	private readonly MediaSourceId _sourceId;
	private readonly bool _hasAudio;
	private readonly byte[] _rgbaFrameBuffer;
	private long? _pendingSeekTimestamp;
	private bool _disposed;
    private byte[]? _pendingAudioPacket;
    private int _pendingAudioFrameOffset;
    private long _pendingAudioSamplePosition;
    private bool _audioEnded;
    private bool _videoEnded;
    private int _videoStride;

	private WindowsMediaFoundationLocalMediaDecoder(
		IMFSourceReader reader,
        LocalMediaSourceReaderCallback callback,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		LocalMediaProbe probe,
		bool hasAudio)
	{
		_reader = reader;
        _callback = callback;
		_assetId = assetId;
		_sourceId = sourceId;
		Probe = probe;
		_hasAudio = hasAudio;
		_rgbaFrameBuffer = new byte[checked((int)((ulong)probe.VideoFormat.Width * probe.VideoFormat.Height * 4UL))];
        _videoStride = checked((int)probe.VideoFormat.Width * 4);
	}

	public LocalMediaProbe Probe { get; }
    public LocalMediaReadMeasurements VideoReadMeasurements { get; private set; }
    public LocalMediaReadMeasurements AudioReadMeasurements { get; private set; }

	public static LocalMediaDecoderOpenResult TryOpen(
		string path,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		VideoFormat outputFormat,
        LocalMediaStreamMetadata metadata)
	{
		if (!OperatingSystem.IsWindows())
			return LocalMediaDecoderOpenResult.Rejected("media.file.platform_unsupported", "Windows Media Foundation is not available on this platform.");

		IMFSourceReader? reader = null;
		var mediaFoundationStarted = false;
        var callback = new LocalMediaSourceReaderCallback();
        var callbackTransferred = false;
		try
		{
			MediaFoundation.ThrowIfFailed(MediaFoundation.MFStartup(MediaFoundation.MfVersion, MediaFoundation.MfStartupFull));
			mediaFoundationStarted = true;

			MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateAttributes(out var readerAttributes, 3));
			try
			{
				var advancedVideoProcessing = MediaFoundation.MfSourceReaderEnableAdvancedVideoProcessing;
				MediaFoundation.ThrowIfFailed(readerAttributes.SetUINT32(ref advancedVideoProcessing, 1));
                var callbackKey = new Guid("1E3DBEAC-BB43-4C35-B507-CD644464C965");
                MediaFoundation.ThrowIfFailed(readerAttributes.SetUnknown(ref callbackKey, callback));
				MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateSourceReaderFromURL(path, readerAttributes, out reader));
			}
			finally
			{
				MediaFoundation.ReleaseComObject(readerAttributes);
			}

			MediaFoundation.ThrowIfFailed(reader.SetStreamSelection(MediaFoundation.AllStreams, false));
			MediaFoundation.ThrowIfFailed(reader.SetStreamSelection(MediaFoundation.FirstVideoStream, true));

            WindowsMediaFoundationLocalMediaDecoder decoder;
			IMFMediaType? videoNative = null;
            IMFMediaType? audioNative = null;
			try
			{
                videoNative = GetNativeMediaType(reader, MediaFoundation.FirstVideoStream);
                audioNative = TryGetNativeMediaType(reader, MediaFoundation.FirstAudioStream);
				var videoMajor = GetGuid(videoNative, MediaFoundation.MfMtMajorType);
				var videoSubtype = GetGuid(videoNative, MediaFoundation.MfMtSubtype);
				if (videoMajor != MediaFoundation.MfMediaTypeVideo)
					return LocalMediaDecoderOpenResult.Rejected("media.file.video_stream_invalid", "The local media video stream has an invalid major type.");

				var videoCodec = ResolveVideoCodec(videoSubtype);
				if (videoCodec is null)
				{
					return LocalMediaDecoderOpenResult.Rejected(
						"media.file.video_codec_unsupported",
						$"The local video subtype '{videoSubtype}' is not supported by the local media path.");
				}

				if (metadata.VideoCodec != videoCodec)
                    return LocalMediaDecoderOpenResult.Rejected("media.file.codec_mismatch", "Container sample entry and native video codec disagree.");

                var audioCodec = audioNative is not null &&
					TryGetGuid(audioNative, MediaFoundation.MfMtMajorType) == MediaFoundation.MfMediaTypeAudio
						? ResolveAudioCodec(TryGetGuid(audioNative, MediaFoundation.MfMtSubtype))
						: MediaAudioCodec.None;
				var hasAudio = audioCodec != MediaAudioCodec.None;
                if (metadata.Container == MediaContainerFormat.QuickTimeMov &&
                    (metadata.AudioSampleEntry.Length != 0 && !hasAudio ||
                     metadata.AudioSampleEntry == "mp4a" && audioCodec != MediaAudioCodec.Aac ||
                     metadata.AudioSampleEntry == "sowt" && audioCodec != MediaAudioCodec.Pcm))
                    return LocalMediaDecoderOpenResult.Rejected("media.file.audio_codec_unsupported",
                        "The QuickTime audio track could not be matched to the admitted AAC/PCM decoder.");

				if (metadata.FrameRateNumerator <= 0 || metadata.FrameRateDenominator <= 0)
					return LocalMediaDecoderOpenResult.Rejected("media.file.metadata_invalid", "The local media file reports an invalid frame rate.");
				if (metadata.Duration <= TimeSpan.Zero)
					return LocalMediaDecoderOpenResult.Rejected("media.file.metadata_invalid", "The local media file reports an invalid duration.");

				var nativeSizeKey = MediaFoundation.MfMtFrameSize;
                MediaFoundation.ThrowIfFailed(videoNative.GetUINT64(ref nativeSizeKey, out var nativeSize));
                var nativeWidth = (uint)(nativeSize >> 32);
                var nativeHeight = (uint)nativeSize;
                if (!NativeGeometryMatches(metadata.Width, metadata.Height, nativeWidth, nativeHeight,
                    ReadMinimumDisplayAperture(videoNative)))
                    return LocalMediaDecoderOpenResult.Rejected("media.file.metadata_mismatch",
                        $"Container visible dimensions {metadata.Width}x{metadata.Height} and native coded dimensions {nativeWidth}x{nativeHeight} have no matching zero-origin display aperture.");
                var nativeTransfer = TryGetUInt32(videoNative, new Guid("5FB0FCE9-BE5C-4935-A811-EC838F8EED93"));
                if (nativeTransfer is >= 9)
                    return LocalMediaDecoderOpenResult.Rejected("media.file.color_unsupported",
                        "Native logarithmic, HDR or wide-gamut transfer conversion is not qualified.");
                var nativeAspectKey = MediaFoundation.MfMtPixelAspectRatio;
                if (videoNative.GetUINT64(ref nativeAspectKey, out var nativeAspect) >= 0 &&
                    ((uint)(nativeAspect >> 32) == 0 || (uint)(nativeAspect >> 32) != (uint)nativeAspect))
                    return LocalMediaDecoderOpenResult.Rejected("media.file.pixel_aspect_unsupported",
                        "The native video stream reports non-square or invalid pixels.");
                var nativeRotation = TryGetUInt32(videoNative, new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1"));
                if (nativeRotation is > 0)
                    return LocalMediaDecoderOpenResult.Rejected("media.file.transform_unsupported", "Native video rotation is not qualified.");

                var averageBitRate = TryGetUInt32(videoNative, MediaFoundation.MfMtAvgBitrate);
				var inputProfile = new LocalMediaInputProfile(
					videoCodec.Value,
					metadata.Width,
					metadata.Height,
					new FrameRate(metadata.FrameRateNumerator, metadata.FrameRateDenominator),
					averageBitRate);
				if (LocalMediaInputPolicy.Validate(inputProfile) is { } inputFailure)
					return LocalMediaDecoderOpenResult.Rejected(inputFailure.Code, inputFailure.Message);

				try
                {
                    ConfigureDecodedVideo(reader, MediaFoundation.FirstVideoStream, outputFormat);
                }
                catch (ExternalException exception)
                {
                    return LocalMediaDecoderOpenResult.Rejected(
                        exception.HResult == unchecked((int)0xC00D5212) ? "media.file.decoder_unavailable" : "media.file.normalization_failed",
                        $"{metadata.Container}/{metadata.SampleEntry} profile {metadata.Profile} ({metadata.BitDepth}-bit) could not negotiate the Windows RGB32 decoder: {exception.Message}");
                }
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
					metadata.Container,
					videoCodec.Value,
					audioCodec,
					outputFormat,
					AudioFormat.Stereo48kFloat32,
					metadata.Duration);

				decoder = new WindowsMediaFoundationLocalMediaDecoder(
					reader,
                    callback,
					assetId,
					sourceId,
					probe,
					hasAudio);
			}
			finally
			{
                try { MediaFoundation.ReleaseComObject(videoNative); }
                finally { MediaFoundation.ReleaseComObject(audioNative); }
			}
            callbackTransferred = true;
            return LocalMediaDecoderOpenResult.Ready(decoder);
		}
		catch (Exception exception) when (exception is COMException or ExternalException or InvalidDataException or IOException or OverflowException)
		{
			return LocalMediaDecoderOpenResult.Rejected("media.file.probe_failed", $"The local media file could not be probed: {exception.Message}");
		}
        finally
        {
            if (!callbackTransferred)
            {
                try { MediaFoundation.ReleaseComObject(reader); }
                finally
                {
                    try { callback.Dispose(); }
                    finally { if (mediaFoundationStarted) MediaFoundation.MFShutdown(); }
                }
            }
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
			_callback.Flush(_reader);
			var timeFormat = Guid.Empty;
			var position = new PropVariant
			{
				VarType = MediaFoundation.VtI8,
				Int64Value = target
			};
			MediaFoundation.ThrowIfFailed(_reader.SetCurrentPosition(ref timeFormat, ref position));
			_pendingSeekTimestamp = LocalMediaFrameMath.FrameSeekMinimumTimestamp(frameNumber, Probe.VideoFormat.FrameRate);
            _pendingAudioPacket = null;
            _pendingAudioFrameOffset = 0;
            _audioEnded = false;
            _videoEnded = false;
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

	public bool TryReadNext(ulong sequenceNumber, out LocalMediaDecodedFrame? frame) =>
        TryReadNext(sequenceNumber, CancellationToken.None, out frame);

    public bool TryReadNext(ulong sequenceNumber, CancellationToken cancellationToken, out LocalMediaDecodedFrame? frame)
    {
        _readCancellation = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
		ObjectDisposedException.ThrowIf(_disposed, this);
        if (_videoEnded) { frame = null; return false; }
		var minimumTimestamp = _pendingSeekTimestamp;
        var progress = new LocalMediaSampleProgress();
		long videoTimestamp;
		try
		{
			if (!TryReadVideoSample(
				MediaFoundation.FirstVideoStream,
				minimumTimestamp,
                progress,
				out videoTimestamp))
			{
				frame = null;
				return false;
			}
		}
		catch (ExternalException exception)
		{
			throw new InvalidDataException($"Video sample read failed: {exception.Message}", exception);
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
            try
            {
                if (TryReadAlignedAudio(videoTimestamp, progress, out var audioSamplePosition, out var floatAudio))
                {
                    var sampleCount = checked((uint)(floatAudio.Length / (Probe.AudioFormat.ChannelCount * sizeof(float))));
                    audio = new AudioBufferDescriptor(
                        MediaContractVersion.Current,
                        new AudioStreamId(LocalMediaIdentity.Create("audio-stream", _assetId.ToString())),
                        Probe.AudioFormat,
                        LocalMediaIdentity.Create("timing-domain", _assetId.ToString()),
                        new AudioBufferTiming(audioSamplePosition, sampleCount, videoTimestamp, MediaFoundationTimebase),
                        new OpaqueAudioHandle("local.media.audio.float32", $"{_assetId}:{sequenceNumber}"));
                    audioPayload = floatAudio;
                }
            }
            catch (ExternalException exception)
            {
                throw new InvalidDataException($"Audio sample read failed: {exception.Message}", exception);
            }
        }

		_pendingSeekTimestamp = null;
		frame = new LocalMediaDecodedFrame(video, _rgbaFrameBuffer, audio, audioPayload);
		return true;
	}

    private bool TryReadAlignedAudio(long videoTimestamp, LocalMediaSampleProgress progress, out ulong samplePosition, out byte[] output)
    {
        var rate = Probe.AudioFormat.SampleRate;
        var start = Math.Max(0L, AudioSamplePosition(videoTimestamp, rate));
        var end = checked((long)decimal.Round(
            (decimal)videoTimestamp * rate / 10_000_000m +
            (decimal)rate * Probe.VideoFormat.FrameRate.Denominator / Probe.VideoFormat.FrameRate.Numerator,
            0, MidpointRounding.AwayFromZero));
        var frameCount = checked((int)(end - start));
        if (frameCount is <= 0 or > 2_048)
            throw new InvalidDataException("Decoded audio window does not fit the bounded output cadence.");
        var channels = checked((int)Probe.AudioFormat.ChannelCount);
        var pcmFrameBytes = checked(channels * sizeof(short));
        output = new byte[checked(frameCount * channels * sizeof(float))];
        samplePosition = checked((ulong)start);
        var cursor = start;
        // A single native packet is retained, including its original sample position.
        // Packet sizes are independent of video cadence. Seek scanning is bounded;
        // native COM calls themselves cannot be forcibly interrupted.
        for (var packet = 0; packet < 1_024 && cursor < end; packet++)
        {
            if (_pendingAudioPacket is null || _pendingAudioFrameOffset >= _pendingAudioPacket.Length / pcmFrameBytes)
            {
                _pendingAudioPacket = null;
                _pendingAudioFrameOffset = 0;
                if (_audioEnded || !TryReadSample(MediaFoundation.FirstAudioStream, null, progress,
                    out var timestamp, out var decoded))
                {
                    _audioEnded = true;
                    output = Array.Empty<byte>();
                    return false;
                }
                if (decoded.Length == 0 || decoded.Length % pcmFrameBytes != 0)
                    throw new InvalidDataException("Decoded PCM packet is not aligned to complete audio frames.");
                _pendingAudioPacket = decoded;
                _pendingAudioSamplePosition = AudioSamplePosition(timestamp, rate);
            }
            var available = _pendingAudioPacket.Length / pcmFrameBytes - _pendingAudioFrameOffset;
            var position = _pendingAudioSamplePosition + _pendingAudioFrameOffset;
            if (position < cursor)
            {
                var skip = checked((int)Math.Min(available, cursor - position));
                _pendingAudioFrameOffset += skip;
                continue;
            }
            if (position > cursor)
            {
                // Preserve the future packet, but never manufacture silence to hide a gap.
                output = Array.Empty<byte>();
                return false;
            }
            var copy = checked((int)Math.Min(available, end - cursor));
            CopyPcm16ToFloat32(
                _pendingAudioPacket.AsSpan(_pendingAudioFrameOffset * pcmFrameBytes, copy * pcmFrameBytes),
                output.AsSpan(checked((int)(cursor - start) * channels * sizeof(float)), copy * channels * sizeof(float)));
            _pendingAudioFrameOffset += copy;
            cursor += copy;
        }
        if (cursor != end) throw new InvalidDataException("Local audio packet/seek scan exceeded its bounded read budget.");
        return true;
    }

    private static long AudioSamplePosition(long timestamp, uint sampleRate) =>
        checked((long)decimal.Round((decimal)timestamp * sampleRate / 10_000_000m, 0, MidpointRounding.AwayFromZero));

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		try { MediaFoundation.ReleaseComObject(_reader); }
        finally
        {
            try { _callback.Dispose(); }
            finally { MediaFoundation.MFShutdown(); }
        }
	}

	private bool TryReadVideoSample(
		uint streamIndex,
		long? minimumTimestamp,
        LocalMediaSampleProgress progress,
		out long timestamp)
	{
        while (true)
		{
            var remaining = progress.BeforeRead(_readCancellation);
			var readStarted = Stopwatch.GetTimestamp();
            uint flags;
            IMFSample? sample;
            try
            {
                var result = _callback.Read(_reader, streamIndex, _readCancellation, remaining);
                flags = result.Flags;
                timestamp = result.Timestamp;
                sample = result.Sample;
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
                if (streamIndex == MediaFoundation.FirstVideoStream) VideoReadMeasurements = VideoReadMeasurements.Observe(elapsed);
                else AudioReadMeasurements = AudioReadMeasurements.Observe(elapsed);
            }

            try
            {
                _ = progress.Remaining;
                var typeQualified = (flags & 0x30) != 0 && (flags & (0x1 | 0x4 | 0x200)) == 0
                    && ValidateDecodedLayout(streamIndex);
                var disposition = progress.Observe(flags, timestamp, sample is not null, minimumTimestamp, typeQualified);
                if (disposition == LocalMediaSampleDisposition.Ended) return false;
                if (disposition == LocalMediaSampleDisposition.Gap) continue;

				MediaFoundation.ThrowIfFailed(sample!.ConvertToContiguousBuffer(out var buffer));
				try
				{
					CopyRgb32ToRgba(buffer, Probe.VideoFormat, _rgbaFrameBuffer, _videoStride);
                    if ((flags & MediaFoundation.SourceReaderEndOfStream) != 0) _videoEnded = true;
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

	private bool TryReadSample(
		uint streamIndex,
		long? minimumTimestamp,
        LocalMediaSampleProgress progress,
		out long timestamp,
		out byte[] payload)
	{
        while (true)
		{
            var remaining = progress.BeforeRead(_readCancellation);
			var readStarted = Stopwatch.GetTimestamp();
            uint flags;
            IMFSample? sample;
            try
            {
                var result = _callback.Read(_reader, streamIndex, _readCancellation, remaining);
                flags = result.Flags;
                timestamp = result.Timestamp;
                sample = result.Sample;
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
                if (streamIndex == MediaFoundation.FirstVideoStream) VideoReadMeasurements = VideoReadMeasurements.Observe(elapsed);
                else AudioReadMeasurements = AudioReadMeasurements.Observe(elapsed);
            }

            try
            {
                _ = progress.Remaining;
                var typeQualified = (flags & 0x30) != 0 && (flags & (0x1 | 0x4 | 0x200)) == 0
                    && ValidateDecodedLayout(streamIndex);
                var disposition = progress.Observe(flags, timestamp, sample is not null, minimumTimestamp, typeQualified);
                if (disposition == LocalMediaSampleDisposition.Ended) { payload = Array.Empty<byte>(); return false; }
                if (disposition == LocalMediaSampleDisposition.Gap) continue;

				MediaFoundation.ThrowIfFailed(sample!.ConvertToContiguousBuffer(out var buffer));
				try
				{
					payload = CopyMediaBuffer(buffer);
                    if ((flags & MediaFoundation.SourceReaderEndOfStream) != 0) _audioEnded = true;
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

    private bool ValidateDecodedLayout(uint stream)
    {
        IMFMediaType? type = null;
        try
        {
            MediaFoundation.ThrowIfFailed(_reader.GetCurrentMediaType(stream, out type));
            if (stream == MediaFoundation.FirstVideoStream)
            {
                var sizeKey = MediaFoundation.MfMtFrameSize;
                var rateKey = MediaFoundation.MfMtFrameRate;
                MediaFoundation.ThrowIfFailed(type.GetUINT64(ref sizeKey, out var size));
                MediaFoundation.ThrowIfFailed(type.GetUINT64(ref rateKey, out var rate));
                var qualified = GetGuid(type, MediaFoundation.MfMtMajorType) == MediaFoundation.MfMediaTypeVideo &&
                    GetGuid(type, MediaFoundation.MfMtSubtype) == MediaFoundation.MfVideoFormatRgb32 &&
                    size == MediaFoundation.PackRatio(Probe.VideoFormat.Width, Probe.VideoFormat.Height) &&
                    rate == MediaFoundation.PackRatio(Probe.VideoFormat.FrameRate.Numerator, Probe.VideoFormat.FrameRate.Denominator) &&
                    TryGetUInt32(type, MediaFoundation.MfMtInterlaceMode) == MediaFoundation.MfVideoInterlaceProgressive;
                var stride = TryGetUInt32(type, MediaFoundation.MfMtDefaultStride);
                var resolvedStride = stride is { } reported ? unchecked((int)reported) : checked((int)Probe.VideoFormat.Width * 4);
                qualified &= Math.Abs((long)resolvedStride) >= Probe.VideoFormat.Width * 4L;
                if (!qualified) throw new LocalMediaDecodeException("media.file.decoded_type_changed",
                    $"Video layout rejected: major={TryGetGuid(type, MediaFoundation.MfMtMajorType)}, subtype={TryGetGuid(type, MediaFoundation.MfMtSubtype)}, size=0x{size:X}, rate=0x{rate:X}, interlace={TryGetUInt32(type, MediaFoundation.MfMtInterlaceMode)}, stride={TryGetUInt32(type, MediaFoundation.MfMtDefaultStride)}.");
                _videoStride = resolvedStride;
                return true;
            }
            var audioQualified = GetGuid(type, MediaFoundation.MfMtMajorType) == MediaFoundation.MfMediaTypeAudio &&
                GetGuid(type, MediaFoundation.MfMtSubtype) == MediaFoundation.MfAudioFormatPcm &&
                TryGetUInt32(type, MediaFoundation.MfMtAudioNumChannels) == 2 &&
                TryGetUInt32(type, MediaFoundation.MfMtAudioSamplesPerSecond) == 48_000 &&
                TryGetUInt32(type, MediaFoundation.MfMtAudioBitsPerSample) == 16 &&
                TryGetUInt32(type, MediaFoundation.MfMtAudioBlockAlignment) == 4;
            if (!audioQualified) throw new LocalMediaDecodeException("media.file.decoded_type_changed",
                $"Audio layout rejected: subtype={TryGetGuid(type, MediaFoundation.MfMtSubtype)}, channels={TryGetUInt32(type, MediaFoundation.MfMtAudioNumChannels)}, rate={TryGetUInt32(type, MediaFoundation.MfMtAudioSamplesPerSecond)}, bits={TryGetUInt32(type, MediaFoundation.MfMtAudioBitsPerSample)}, alignment={TryGetUInt32(type, MediaFoundation.MfMtAudioBlockAlignment)}.");
            return true;
        }
        catch (ExternalException exception)
        {
            throw new LocalMediaDecodeException("media.file.decoded_type_changed",
                "Could not qualify changed decoded layout.", exception);
        }
        finally { MediaFoundation.ReleaseComObject(type); }
    }

	private static byte[] CopyMediaBuffer(IMFMediaBuffer buffer)
	{
		MediaFoundation.ThrowIfFailed(buffer.Lock(out var address, out var maximumLength, out var currentLength));
		try
		{
			if (currentLength > maximumLength || address == IntPtr.Zero)
                throw new LocalMediaDecodeException("media.file.audio_payload_invalid", "Native PCM buffer extent is invalid.");
            var payload = new byte[LocalMediaSampleProgress.ValidatePcmLength(currentLength)];
			if (payload.Length > 0)
				Marshal.Copy(address, payload, 0, payload.Length);
			return payload;
		}
		finally
		{
			MediaFoundation.ThrowIfFailed(buffer.Unlock());
		}
	}


	private static void CopyRgb32ToRgba(
		IMFMediaBuffer buffer,
		VideoFormat format,
		byte[] destination,
        int defaultStride)
	{
		var width = checked((int)format.Width);
		var height = checked((int)format.Height);
		var rowBytes = checked(width * 4);
		var requiredLength = checked(rowBytes * height);
		if (destination.Length != requiredLength)
			throw new InvalidDataException("Reusable RGBA frame buffer length does not match the configured output format.");

		if (buffer is IMF2DBuffer buffer2D)
		{
			MediaFoundation.ThrowIfFailed(buffer2D.Lock2D(out var scanline0, out var pitch));
			try
			{
				if (Math.Abs((long)pitch) < rowBytes)
					throw new InvalidDataException($"Decoded RGB32 surface pitch '{pitch}' is smaller than the visible row width '{rowBytes}'.");

                CopyRgb32Rows(scanline0, pitch, width, height, destination);
			}
			finally
			{
				MediaFoundation.ThrowIfFailed(buffer2D.Unlock2D());
			}
		}
		else
		{
			MediaFoundation.ThrowIfFailed(buffer.Lock(out var address, out var maximumLength, out var currentLength));
			try
			{
                CopyContiguousRgb32Rows(address, currentLength, maximumLength, width, height, defaultStride, destination);
			}
			finally
			{
				MediaFoundation.ThrowIfFailed(buffer.Unlock());
			}
		}

        SwizzleRgb32(destination);
    }

    internal static void CopyContiguousRgb32Rows(IntPtr address, uint length, uint capacity,
        int width, int height, int stride, byte[] destination)
    {
        var rowBytes = checked(width * 4);
        var extent = checked((height - 1L) * Math.Abs((long)stride) + rowBytes);
        if (address == IntPtr.Zero || length > capacity || width <= 0 || height <= 0 ||
            Math.Abs((long)stride) < rowBytes || extent > length)
            throw new LocalMediaDecodeException("media.file.video_payload_invalid", "Contiguous RGB32 extent or stride is invalid.");
        var firstRow = stride < 0 ? IntPtr.Add(address, checked((int)((height - 1L) * -(long)stride))) : address;
        CopyRgb32Rows(firstRow, stride, width, height, destination);
    }

    internal static void CopyRgb32Rows(IntPtr scanline0, int pitch, int width, int height, byte[] destination)
    {
        var rowBytes = checked(width * 4);
        if (width <= 0 || height <= 0 || Math.Abs((long)pitch) < rowBytes ||
            destination.Length != checked(rowBytes * height))
            throw new InvalidDataException("RGB32 row extents do not match the visible surface.");
        for (var y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(scanline0, checked(y * pitch)), destination, checked(y * rowBytes), rowBytes);
    }

    internal static void SwizzleRgb32(byte[] destination)
    {
        if (destination.Length % 4 != 0) throw new InvalidDataException("RGB32 extent is not pixel aligned.");
		for (var offset = 0; offset < destination.Length; offset += 4)
		{
			(destination[offset], destination[offset + 2]) = (destination[offset + 2], destination[offset]);
			destination[offset + 3] = byte.MaxValue;
		}
	}

    private static void CopyPcm16ToFloat32(ReadOnlySpan<byte> source, Span<byte> output)
    {
        if ((source.Length & 1) != 0 || output.Length != source.Length * 2)
            throw new InvalidDataException("Decoded PCM conversion extents do not match.");
        for (var sourceOffset = 0; sourceOffset < source.Length; sourceOffset += 2)
        {
            var sample = (short)(source[sourceOffset] | (source[sourceOffset + 1] << 8));
            var bits = BitConverter.SingleToInt32Bits(sample / 32768f);
            var target = sourceOffset * 2;
            output[target] = (byte)bits;
            output[target + 1] = (byte)(bits >> 8);
            output[target + 2] = (byte)(bits >> 16);
            output[target + 3] = (byte)(bits >> 24);
        }
    }


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

    private static byte[] ReadMinimumDisplayAperture(IMFMediaType type)
    {
        var key = new Guid("D7388766-18FE-48C6-A177-EE894867C8C4");
        if (type.GetBlobSize(ref key, out var size) < 0) return [];
        if (size != 16) throw new InvalidDataException("Native display aperture has an invalid size.");
        var address = Marshal.AllocHGlobal(16);
        try
        {
            MediaFoundation.ThrowIfFailed(type.GetBlob(ref key, address, 16, out var actual));
            if (actual != 16) throw new InvalidDataException("Native display aperture is truncated.");
            var bytes = new byte[16]; Marshal.Copy(address, bytes, 0, 16); return bytes;
        }
        finally { Marshal.FreeHGlobal(address); }
    }

    internal static bool NativeGeometryMatches(uint visibleWidth, uint visibleHeight,
        uint codedWidth, uint codedHeight, ReadOnlySpan<byte> aperture)
    {
        if (codedWidth < visibleWidth || codedHeight < visibleHeight ||
            codedWidth > LocalMediaInputPolicy.MaximumWidth || codedHeight > LocalMediaInputPolicy.MaximumHeight)
            return false;
        if (aperture.IsEmpty) return visibleWidth == codedWidth && visibleHeight == codedHeight;
        return aperture.Length == 16 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(aperture) == 0 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(aperture[8..]) == visibleWidth &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(aperture[12..]) == visibleHeight;
    }

	private static MediaVideoCodec? ResolveVideoCodec(Guid subtype)
	{
		if (subtype == MediaFoundation.MfVideoFormatH264 ||
			subtype == MediaFoundation.MfMpeg4FormatAvc1 ||
			subtype == MediaFoundation.MfMpeg4FormatAvc3)
			return MediaVideoCodec.H264;
		if (subtype == MediaFoundation.MfVideoFormatHevc ||
			subtype == MediaFoundation.MfMpeg4FormatHvc1 ||
			subtype == MediaFoundation.MfMpeg4FormatHev1)
			return MediaVideoCodec.Hevc;
		if (subtype == MediaFoundation.MfVideoFormatAv1 ||
			subtype == MediaFoundation.MfMpeg4FormatAv01)
			return MediaVideoCodec.Av1;
		if (subtype == MediaFoundation.MfVideoFormatVp90 ||
			subtype == MediaFoundation.MfMpeg4FormatVp09)
			return MediaVideoCodec.Vp9;
		if (subtype == MediaFoundation.MfVideoFormatM4S2 ||
			subtype == MediaFoundation.MfVideoFormatMp4V ||
			subtype == MediaFoundation.MfMpeg4FormatMp4V)
			return MediaVideoCodec.Mpeg4Part2;
		if (subtype == MediaFoundation.MfVideoFormatWvc1 ||
			subtype == MediaFoundation.MfMpeg4FormatVc1)
			return MediaVideoCodec.Vc1;
		if (subtype == MediaFoundation.MfVideoFormatMjpg ||
			subtype == MediaFoundation.MfMpeg4FormatJpeg)
			return MediaVideoCodec.Mjpeg;

		return null;
	}

	private static MediaAudioCodec ResolveAudioCodec(Guid? subtype)
	{
		if (subtype == MediaFoundation.MfAudioFormatAac)
			return MediaAudioCodec.Aac;
		if (subtype == MediaFoundation.MfAudioFormatMp3)
			return MediaAudioCodec.Mp3;
		if (subtype == MediaFoundation.MfAudioFormatPcm)
			return MediaAudioCodec.Pcm;
		return MediaAudioCodec.None;
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
			var subtype = MediaFoundation.MfVideoFormatRgb32;
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

			var strideKey = MediaFoundation.MfMtDefaultStride;
			MediaFoundation.ThrowIfFailed(mediaType.SetUINT32(
				ref strideKey,
				checked(outputFormat.Width * 4U)));

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
	public static Guid MfMtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
	public static Guid MfMtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
	public static Guid MfMtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
	public static Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
	public static Guid MfMtAudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
	public static Guid MfMtAudioAverageBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
	public static Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
	public static Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatAv1 = new("31305641-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatVp90 = new("30395056-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatM4S2 = new("3253344D-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatMp4V = new("5634504D-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatWvc1 = new("31435657-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatMjpg = new("47504A4D-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatAvc1 = new("31637661-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatAvc3 = new("33637661-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatHvc1 = new("31637668-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatHev1 = new("31766568-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatAv01 = new("31307661-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatVp09 = new("39307076-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatMp4V = new("7634706D-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatVc1 = new("312D6376-0000-0010-8000-00AA00389B71");
	public static Guid MfMpeg4FormatJpeg = new("6765706A-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
	public static Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
	public static Guid MfAudioFormatMp3 = new("00000055-0000-0010-8000-00AA00389B71");
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
        uint streamIndex, uint controlFlags, IntPtr actualStreamIndex,
        IntPtr streamFlags, IntPtr timestamp, IntPtr sample);
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
