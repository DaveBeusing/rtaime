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
	private readonly byte[] _rgbaFrameBuffer;
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
		_rgbaFrameBuffer = new byte[checked((int)((ulong)probe.VideoFormat.Width * probe.VideoFormat.Height * 4UL))];
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

				var videoCodec = ResolveVideoCodec(videoSubtype);
				if (videoCodec is null)
				{
					return RejectAndRelease(
						"media.file.video_codec_unsupported",
						$"The MP4 video subtype '{videoSubtype}' is not supported by the local media path.",
						reader,
						mediaFoundationStarted);
				}

				var audioCodec = audioNative is not null &&
					TryGetGuid(audioNative, MediaFoundation.MfMtMajorType) == MediaFoundation.MfMediaTypeAudio
						? ResolveAudioCodec(TryGetGuid(audioNative, MediaFoundation.MfMtSubtype))
						: MediaAudioCodec.None;
				var hasAudio = audioCodec != MediaAudioCodec.None;

				var metadata = Mp4LocalMediaMetadataReader.Read(path);
				if (metadata.FrameRateNumerator <= 0 || metadata.FrameRateDenominator <= 0)
					return RejectAndRelease("media.file.metadata_invalid", "The local media file reports an invalid frame rate.", reader, mediaFoundationStarted);
				if (metadata.Duration <= TimeSpan.Zero)
					return RejectAndRelease("media.file.metadata_invalid", "The local media file reports an invalid duration.", reader, mediaFoundationStarted);

				var averageBitRate = TryGetUInt32(videoNative, MediaFoundation.MfMtAvgBitrate);
				var inputProfile = new LocalMediaInputProfile(
					videoCodec.Value,
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
					videoCodec.Value,
					audioCodec,
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
		try
		{
			if (!TryReadVideoSample(
				MediaFoundation.FirstVideoStream,
				minimumTimestamp,
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
		frame = new LocalMediaDecodedFrame(video, _rgbaFrameBuffer, audio, audioPayload);
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

	private bool TryReadVideoSample(
		uint streamIndex,
		long? minimumTimestamp,
		out long timestamp)
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
					return false;
				continue;
			}

			try
			{
				if (minimumTimestamp is not null && timestamp < minimumTimestamp.Value)
					continue;

				MediaFoundation.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer));
				try
				{
					CopyRgb32ToRgba(buffer, Probe.VideoFormat, _rgbaFrameBuffer);
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

	private static void CopyRgb32ToRgba(
		IMFMediaBuffer buffer,
		VideoFormat format,
		byte[] destination)
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

				for (var y = 0; y < height; y++)
				{
					var row = IntPtr.Add(scanline0, checked(y * pitch));
					Marshal.Copy(row, destination, checked(y * rowBytes), rowBytes);
				}
			}
			finally
			{
				MediaFoundation.ThrowIfFailed(buffer2D.Unlock2D());
			}
		}
		else
		{
			MediaFoundation.ThrowIfFailed(buffer.Lock(out var address, out _, out var currentLength));
			try
			{
				if (currentLength < requiredLength)
					throw new InvalidDataException($"Decoded RGB32 frame has '{currentLength}' bytes; expected at least '{requiredLength}'.");
				Marshal.Copy(address, destination, 0, requiredLength);
			}
			finally
			{
				MediaFoundation.ThrowIfFailed(buffer.Unlock());
			}
		}

		for (var offset = 0; offset < destination.Length; offset += 4)
		{
			(destination[offset], destination[offset + 2]) = (destination[offset + 2], destination[offset]);
			destination[offset + 3] = byte.MaxValue;
		}
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
