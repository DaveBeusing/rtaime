// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Media.Contracts;

/// <summary>
/// Independently versioned contract for professional media I/O descriptors. Bulk media bytes are never part of
/// this contract; frames cross the provider boundary only as opaque handles plus explicit ownership/lifetime data.
/// </summary>
public static class MediaIoContractVersion
{
	public static CompatibilityVersion Current { get; } = new(1, 0);

	public static bool IsSupported(CompatibilityVersion version) => version == Current;

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (!IsSupported(version))
			throw new NotSupportedException($"Unsupported Media I/O contract version '{version}'. Supported version is '{Current}'.");
	}
}

public readonly record struct MediaIoPortId
{
	public MediaIoPortId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Media I/O port identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static MediaIoPortId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public enum MediaIoDirection
{
	Input = 1,
	Output = 2
}

public enum MediaIoTransportKind
{
	Sdi = 1
}

public enum MediaIoSignalState
{
	Unknown = 1,
	Locked = 2,
	Unstable = 3,
	Lost = 4,
	Faulted = 5
}

/// <summary>
/// Transfer strategy at the provider boundary. All modes are descriptor/lease based and intentionally exclude
/// managed bulk-media payloads.
/// </summary>
public enum MediaIoTransferMode
{
	SharedOpaqueHandle = 1,
	PinnedHostLease = 2,
	DeviceDirectLease = 3
}

/// <summary>
/// Native wire/storage pixel encoding used by the physical I/O adapter before or after normalization to the
/// platform media format. Values remain vendor-neutral.
/// </summary>
public enum MediaIoNativePixelFormat
{
	Rgba8 = 1,
	Uyvy8 = 2,
	V210 = 3
}

public sealed record MediaIoNativeVideoFormat
{
	public MediaIoNativeVideoFormat(
		uint width,
		uint height,
		FrameRate frameRate,
		MediaIoNativePixelFormat pixelFormat,
		ScanMode scanMode)
	{
		if (width == 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height == 0) throw new ArgumentOutOfRangeException(nameof(height));
		if (!Enum.IsDefined(typeof(MediaIoNativePixelFormat), pixelFormat)) throw new ArgumentOutOfRangeException(nameof(pixelFormat));
		if (!Enum.IsDefined(typeof(ScanMode), scanMode)) throw new ArgumentOutOfRangeException(nameof(scanMode));

		Width = width;
		Height = height;
		FrameRate = frameRate;
		PixelFormat = pixelFormat;
		ScanMode = scanMode;
	}

	public uint Width { get; }
	public uint Height { get; }
	public FrameRate FrameRate { get; }
	public MediaIoNativePixelFormat PixelFormat { get; }
	public ScanMode ScanMode { get; }
}

/// <summary>
/// A captured frame after provider normalization. The surface must remain an explicit shared lease with an opaque
/// handle; ownership is released by the session lease rather than by copying frame bytes into management memory.
/// </summary>
public sealed record MediaIoInputFrameDescriptor
{
	public MediaIoInputFrameDescriptor(
		CompatibilityVersion version,
		MediaIoPortId portId,
		FrameDescriptor video,
		AudioBufferDescriptor? embeddedAudio = null)
	{
		MediaIoContractVersion.EnsureSupported(version);
		ArgumentNullException.ThrowIfNull(video);
		if (video.Surface.Ownership != SurfaceOwnership.SharedLease)
			throw new ArgumentException("Media I/O input video surfaces must use SharedLease ownership.", nameof(video));
		if (video.Surface.Lifetime.LeaseId is null)
			throw new ArgumentException("Media I/O input video surfaces require an explicit lease identity.", nameof(video));
		if (video.Surface.Handle is null)
			throw new ArgumentException("Media I/O input video surfaces require an opaque handle.", nameof(video));
		if (embeddedAudio is not null && embeddedAudio.Handle is null)
			throw new ArgumentException("Media I/O embedded audio requires an opaque handle.", nameof(embeddedAudio));

		Version = version;
		PortId = portId;
		Video = video;
		EmbeddedAudio = embeddedAudio;
	}

	public CompatibilityVersion Version { get; }
	public MediaIoPortId PortId { get; }
	public FrameDescriptor Video { get; }
	public AudioBufferDescriptor? EmbeddedAudio { get; }
}

/// <summary>
/// Output submission descriptor. The provider may borrow a producer-owned surface or consume a shared lease, but
/// the surface must always be represented by an opaque handle rather than a managed payload.
/// </summary>
public sealed record MediaIoOutputFrameDescriptor
{
	public MediaIoOutputFrameDescriptor(
		CompatibilityVersion version,
		MediaIoPortId portId,
		FrameDescriptor video,
		AudioBufferDescriptor? embeddedAudio = null)
	{
		MediaIoContractVersion.EnsureSupported(version);
		ArgumentNullException.ThrowIfNull(video);
		if (video.Surface.Handle is null)
			throw new ArgumentException("Media I/O output video surfaces require an opaque handle.", nameof(video));
		if (video.Surface.Ownership == SurfaceOwnership.SharedLease && video.Surface.Lifetime.LeaseId is null)
			throw new ArgumentException("Shared Media I/O output video surfaces require an explicit lease identity.", nameof(video));
		if (embeddedAudio is not null && embeddedAudio.Handle is null)
			throw new ArgumentException("Media I/O embedded audio requires an opaque handle.", nameof(embeddedAudio));

		Version = version;
		PortId = portId;
		Video = video;
		EmbeddedAudio = embeddedAudio;
	}

	public CompatibilityVersion Version { get; }
	public MediaIoPortId PortId { get; }
	public FrameDescriptor Video { get; }
	public AudioBufferDescriptor? EmbeddedAudio { get; }
}

public sealed record MediaIoPortStatus
{
	public MediaIoPortStatus(
		CompatibilityVersion version,
		MediaIoPortId portId,
		MediaIoSignalState signalState,
		UtcTimestamp observedAt,
		VideoFormat? detectedVideoFormat = null,
		Failure? failure = null)
	{
		MediaIoContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaIoSignalState), signalState)) throw new ArgumentOutOfRangeException(nameof(signalState));
		if (signalState == MediaIoSignalState.Faulted && failure is null)
			throw new ArgumentException("Faulted Media I/O port status requires a failure.", nameof(failure));
		if (signalState == MediaIoSignalState.Locked && failure is not null)
			throw new ArgumentException("Locked Media I/O port status must not carry a failure.", nameof(failure));

		Version = version;
		PortId = portId;
		SignalState = signalState;
		ObservedAt = observedAt;
		DetectedVideoFormat = detectedVideoFormat;
		Failure = failure;
	}

	public CompatibilityVersion Version { get; }
	public MediaIoPortId PortId { get; }
	public MediaIoSignalState SignalState { get; }
	public UtcTimestamp ObservedAt { get; }
	public VideoFormat? DetectedVideoFormat { get; }
	public Failure? Failure { get; }
}
