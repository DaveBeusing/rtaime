// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Media;

/// <summary>
/// Desired in-process media I/O session. It expresses planning/admission intent only; it does not carry media bytes
/// and grants no production authority.
/// </summary>
public sealed record MediaIoSessionRequest
{
	public MediaIoSessionRequest(
		CompatibilityVersion version,
		ProviderId providerId,
		MediaIoPortId portId,
		MediaIoDirection direction,
		VideoFormat videoFormat,
		MediaIoTransferMode transferMode,
		AudioFormat? audioFormat = null,
		bool requireExternalReference = false)
	{
		MediaIoContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaIoDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
		if (!Enum.IsDefined(typeof(MediaIoTransferMode), transferMode)) throw new ArgumentOutOfRangeException(nameof(transferMode));

		Version = version;
		ProviderId = providerId;
		PortId = portId;
		Direction = direction;
		VideoFormat = videoFormat;
		TransferMode = transferMode;
		AudioFormat = audioFormat;
		RequireExternalReference = requireExternalReference;
	}

	public CompatibilityVersion Version { get; }
	public ProviderId ProviderId { get; }
	public MediaIoPortId PortId { get; }
	public MediaIoDirection Direction { get; }
	public VideoFormat VideoFormat { get; }
	public MediaIoTransferMode TransferMode { get; }
	public AudioFormat? AudioFormat { get; }
	public bool RequireExternalReference { get; }
}

public static class MediaIoAdmission
{
	public static ValidationResult Validate(MediaIoProviderDescriptor descriptor, MediaIoSessionRequest request)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(request);
		var issues = new List<ValidationIssue>();

		if (descriptor.Provider.ProviderId != request.ProviderId)
			issues.Add(new ValidationIssue("media.io.provider_mismatch", "Requested provider identity does not match the Media I/O provider profile.", nameof(request.ProviderId)));

		if (descriptor.Provider.Availability.State == ProviderAvailabilityState.Unavailable)
			issues.Add(new ValidationIssue("media.io.provider_unavailable", descriptor.Provider.Availability.Failure?.Message ?? "Media I/O provider is unavailable."));

		var port = descriptor.Ports.SingleOrDefault(candidate => candidate.PortId == request.PortId);
		if (port is null)
		{
			issues.Add(new ValidationIssue("media.io.port_unknown", "Requested Media I/O port is not exposed by the provider.", nameof(request.PortId)));
			return ValidationResult.From(issues);
		}

		if (port.Direction != request.Direction)
			issues.Add(new ValidationIssue("media.io.direction_mismatch", "Requested Media I/O direction does not match the selected port.", nameof(request.Direction)));
		if (!port.NormalizedVideoFormats.Contains(request.VideoFormat))
			issues.Add(new ValidationIssue("media.io.video_format_unsupported", "Requested normalized video format is not supported by the selected port.", nameof(request.VideoFormat)));
		if (!port.TransferModes.Contains(request.TransferMode))
			issues.Add(new ValidationIssue("media.io.transfer_mode_unsupported", "Requested buffer transfer mode is not supported by the selected port.", nameof(request.TransferMode)));
		if (request.AudioFormat is { } audioFormat && !port.AudioFormats.Contains(audioFormat))
			issues.Add(new ValidationIssue("media.io.audio_format_unsupported", "Requested embedded audio format is not supported by the selected port.", nameof(request.AudioFormat)));
		if (request.RequireExternalReference && !port.SupportsExternalReference)
			issues.Add(new ValidationIssue("media.io.external_reference_unsupported", "Selected port does not support the required external reference mode.", nameof(request.RequireExternalReference)));

		var capabilityKind = request.Direction == MediaIoDirection.Input
			? MediaIoCapabilityKinds.VideoInput
			: MediaIoCapabilityKinds.VideoOutput;
		var capabilityAvailable = descriptor.Provider.Capabilities.Any(candidate =>
			string.Equals(candidate.Kind, capabilityKind, StringComparison.Ordinal) &&
			candidate.VideoFormats.Contains(request.VideoFormat));
		if (!capabilityAvailable)
			issues.Add(new ValidationIssue("media.io.capability_missing", $"Provider does not advertise '{capabilityKind}' for the requested video format."));

		return ValidationResult.From(issues);
	}
}

/// <summary>
/// Vendor-neutral in-process adapter seam. Implementations may wrap a native C/C++ provider, but the managed side
/// sees only stable descriptors and leases. No host-to-host transport or bulk pixel buffer is defined here.
/// </summary>
public interface IMediaIoProviderAdapter : IAsyncDisposable
{
	MediaIoProviderDescriptor Descriptor { get; }
	MediaIoPortStatus GetPortStatus(MediaIoPortId portId);
	IMediaIoInputSession OpenInput(MediaIoSessionRequest request);
	IMediaIoOutputSession OpenOutput(MediaIoSessionRequest request);
}

public interface IMediaIoInputSession : IDisposable
{
	MediaIoPortDescriptor Port { get; }
	MediaIoPortStatus Status { get; }
	bool TryAcquire(out MediaIoInputFrameLease? lease);
}

public interface IMediaIoOutputSession : IDisposable
{
	MediaIoPortDescriptor Port { get; }
	MediaIoPortStatus Status { get; }
	MediaIoOutputSubmitResult TrySubmit(FrameDescriptor video, AudioBufferDescriptor? embeddedAudio = null);
}

/// <summary>
/// Owns exactly one adapter-provided input lease. Disposal is idempotent and is the only supported release path.
/// </summary>
public sealed class MediaIoInputFrameLease : IDisposable
{
	private readonly Action<MediaIoInputFrameLease> _release;
	private int _disposed;

	public MediaIoInputFrameLease(MediaIoInputFrameDescriptor descriptor, Action<MediaIoInputFrameLease> release)
	{
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		_release = release ?? throw new ArgumentNullException(nameof(release));
	}

	public MediaIoInputFrameDescriptor Descriptor { get; }
	public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			_release(this);
	}
}

public sealed record MediaIoOutputSubmitResult
{
	private MediaIoOutputSubmitResult(bool accepted, Failure? failure)
	{
		if (accepted == (failure is not null))
			throw new ArgumentException("Media I/O output result requires exactly one of acceptance or failure.");
		Accepted = accepted;
		Failure = failure;
	}

	public bool Accepted { get; }
	public Failure? Failure { get; }

	public static MediaIoOutputSubmitResult Success() => new(true, null);
	public static MediaIoOutputSubmitResult Rejected(Failure failure) => new(false, failure);
}
