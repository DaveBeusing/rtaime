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
	private ulong _audioSamplePosition;
	private bool _disposed;

	private WindowsMediaFoundationLocalMediaDecoder(
		IMFSourceReader reader,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		LocalMediaProbe probe)
	{
		_reader = reader;
		_assetId = assetId;
		_sourceId = sourceId;
		Probe = probe;
	}

	public LocalMediaProbe Probe { get; }

	public static LocalMediaDecoderOpenResult TryOpen(
		string path,
		MediaAssetId assetId,
		MediaSourceId sourceId)
	{
		if (!OperatingSystem.IsWindows())
			return LocalMediaDecoderOpenResult.Rejected("media.file.platform_unsupported", "Windows Media Foundation is not available on this platform.");

		IMFSourceReader? reader = null;
		var mediaFoundationStarted = false;
		try
		{
			MediaFoundation.ThrowIfFailed(MediaFoundation.MFStartup(MediaFoundation.MfVersion, MediaFoundation.MfStartupFull));
			mediaFoundationStarted = true;

			MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateAttributes(out var attributes, 1));
			try
			{
				var processingKey = MediaFoundation.MfSourceReaderEnableVideoProcessing;
				MediaFoundation.ThrowIfFailed(attributes.SetUINT32(ref processingKey, 1));
				MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateSourceReaderFromURL(path, attributes, out reader));
			}
			finally
			{
				MediaFoundation.ReleaseComObject(attributes);
			}

			var videoNative = GetNativeMediaType(reader, MediaFoundation.FirstVideoStream);
			var audioNative = GetNativeMediaType(reader, MediaFoundation.FirstAudioStream);
			try
			{
				var videoMajor = GetGuid(videoNative, MediaFoundation.MfMtMajorType);
				var videoSubtype = GetGuid(videoNative, MediaFoundation.MfMtSubtype);
				if (videoMajor != MediaFoundation.MfMediaTypeVideo)
					return RejectAndRelease("media.file.video_stream_invalid", "The local media video stream has an invalid major type.", reader, mediaFoundationStarted);
				if (videoSubtype != MediaFoundation.MfVideoFormatH264)
					return RejectAndRelease("media.file.video_codec_unsupported", "V1 local media supports H.264 video only.", reader, mediaFoundationStarted);

				var audioMajor = GetGuid(audioNative, MediaFoundation.MfMtMajorType);
				var audioSubtype = GetGuid(audioNative, MediaFoundation.MfMtSubtype);
				if (audioMajor != MediaFoundation.MfMediaTypeAudio)
					return RejectAndRelease("media.file.audio_stream_invalid", "The local media audio stream has an invalid major type.", reader, mediaFoundationStarted);
				if (audioSubtype != MediaFoundation.MfAudioFormatAac)
					return RejectAndRelease("media.file.audio_codec_unsupported", "V1 local media supports embedded AAC audio only.", reader, mediaFoundationStarted);

				var metadata = Mp4LocalMediaMetadataReader.Read(path);
				if (metadata.AudioChannels != 2 || metadata.AudioSampleRate != 48_000)
					return RejectAndRelease("media.file.audio_format_unsupported", "V1 local media requires embedded 48 kHz stereo audio.", reader, mediaFoundationStarted);
				if (metadata.FrameRateNumerator <= 0 || metadata.FrameRateDenominator <= 0)
					return RejectAndRelease("media.file.metadata_invalid", "The local media file reports an invalid frame rate.", reader, mediaFoundationStarted);
				if (metadata.Duration <= TimeSpan.Zero)
					return RejectAndRelease("media.file.metadata_invalid", "The local media file reports an invalid duration.", reader, mediaFoundationStarted);

				ConfigureDecodedVideo(reader);
				ConfigureDecodedAudio(reader);

				var probe = new LocalMediaProbe(
					MediaContractVersion.Current,
					assetId,
					sourceId,
					System.IO.Path.GetFileName(path),
					MediaContainerFormat.Mp4,
					MediaVideoCodec.H264,
					MediaAudioCodec.Aac,
					new VideoFormat(
						metadata.Width,
						metadata.Height,
						new FrameRate(metadata.FrameRateNumerator, metadata.FrameRateDenominator),
						PixelFormat.Rgba8,
						ScanMode.Progressive),
					AudioFormat.Stereo48kFloat32,
					metadata.Duration);

				return LocalMediaDecoderOpenResult.Ready(new WindowsMediaFoundationLocalMediaDecoder(reader, assetId, sourceId, probe));
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

	public bool TryReadNext(ulong sequenceNumber, out LocalMediaDecodedFrame? frame)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!TryReadSample(MediaFoundation.FirstVideoStream, out var videoTimestamp, out var videoPayload))
		{
			frame = null;
			return false;
		}

		var rgba = ConvertRgb32ToRgba(videoPayload, Probe.VideoFormat);
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
		if (TryReadSample(MediaFoundation.FirstAudioStream, out var audioTimestamp, out var decodedAudio) && decodedAudio.Length > 0)
		{
			var bytesPerSampleFrame = checked((int)Probe.AudioFormat.ChannelCount * sizeof(float));
			if (decodedAudio.Length % bytesPerSampleFrame != 0)
				throw new InvalidDataException("Decoded local media audio payload is not aligned to complete stereo Float32 samples.");
			var sampleCount = checked((uint)(decodedAudio.Length / bytesPerSampleFrame));
			if (sampleCount > 0)
			{
				audio = new AudioBufferDescriptor(
					MediaContractVersion.Current,
					new AudioStreamId(LocalMediaIdentity.Create("audio-stream", _assetId.ToString())),
					Probe.AudioFormat,
					LocalMediaIdentity.Create("timing-domain", _assetId.ToString()),
					new AudioBufferTiming(_audioSamplePosition, sampleCount, audioTimestamp, MediaFoundationTimebase),
					new OpaqueAudioHandle("local.media.audio.float32", $"{_assetId}:{sequenceNumber}"));
				_audioSamplePosition = checked(_audioSamplePosition + sampleCount);
				audioPayload = decodedAudio;
			}
		}

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

	private bool TryReadSample(uint streamIndex, out long timestamp, out byte[] payload)
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

			if ((flags & MediaFoundation.SourceReaderEndOfStream) != 0)
			{
				if (sample is not null)
					MediaFoundation.ReleaseComObject(sample);
				payload = Array.Empty<byte>();
				return false;
			}

			if (sample is null)
				continue;

			try
			{
				MediaFoundation.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer));
				try
				{
					MediaFoundation.ThrowIfFailed(buffer.Lock(out var address, out _, out var currentLength));
					try
					{
						payload = new byte[checked((int)currentLength)];
						if (payload.Length > 0)
							Marshal.Copy(address, payload, 0, payload.Length);
						return true;
					}
					finally
					{
						MediaFoundation.ThrowIfFailed(buffer.Unlock());
					}
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

	private static byte[] ConvertRgb32ToRgba(byte[] source, VideoFormat format)
	{
		var required = checked((int)((ulong)format.Width * format.Height * 4UL));
		if (source.Length != required)
			throw new InvalidDataException($"Decoded RGB32 frame has '{source.Length}' bytes; expected '{required}'.");

		var rgba = new byte[required];
		for (var offset = 0; offset < required; offset += 4)
		{
			rgba[offset] = source[offset + 2];
			rgba[offset + 1] = source[offset + 1];
			rgba[offset + 2] = source[offset];
			rgba[offset + 3] = byte.MaxValue;
		}
		return rgba;
	}

	private static IMFMediaType GetNativeMediaType(IMFSourceReader reader, uint streamIndex)
	{
		MediaFoundation.ThrowIfFailed(reader.GetNativeMediaType(streamIndex, 0, out var mediaType));
		return mediaType;
	}

	private static Guid GetGuid(IMFAttributes attributes, Guid key)
	{
		MediaFoundation.ThrowIfFailed(attributes.GetGUID(ref key, out var value));
		return value;
	}

	private static void ConfigureDecodedVideo(IMFSourceReader reader)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			var majorKey = MediaFoundation.MfMtMajorType;
			var majorValue = MediaFoundation.MfMediaTypeVideo;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref majorKey, ref majorValue));
			var subtypeKey = MediaFoundation.MfMtSubtype;
			var subtypeValue = MediaFoundation.MfVideoFormatRgb32;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref subtypeKey, ref subtypeValue));
			MediaFoundation.ThrowIfFailed(reader.SetCurrentMediaType(MediaFoundation.FirstVideoStream, IntPtr.Zero, mediaType));
		}
		finally
		{
			MediaFoundation.ReleaseComObject(mediaType);
		}
	}

	private static void ConfigureDecodedAudio(IMFSourceReader reader)
	{
		MediaFoundation.ThrowIfFailed(MediaFoundation.MFCreateMediaType(out var mediaType));
		try
		{
			var majorKey = MediaFoundation.MfMtMajorType;
			var majorValue = MediaFoundation.MfMediaTypeAudio;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref majorKey, ref majorValue));
			var subtypeKey = MediaFoundation.MfMtSubtype;
			var subtypeValue = MediaFoundation.MfAudioFormatFloat;
			MediaFoundation.ThrowIfFailed(mediaType.SetGUID(ref subtypeKey, ref subtypeValue));
			MediaFoundation.ThrowIfFailed(reader.SetCurrentMediaType(MediaFoundation.FirstAudioStream, IntPtr.Zero, mediaType));
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
	public const uint FirstVideoStream = 0xFFFFFFFC;
	public const uint FirstAudioStream = 0xFFFFFFFD;
	public const uint SourceReaderEndOfStream = 0x00000002;

	public static Guid MfSourceReaderEnableVideoProcessing = new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");
	public static Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
	public static Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
	public static Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
	public static Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
	public static Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
	public static Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
	public static Guid MfAudioFormatFloat = new("00000003-0000-0010-8000-00AA00389B71");

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
internal interface IMFMediaType : IMFAttributes
{
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
internal interface IMFSample : IMFAttributes
{
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
