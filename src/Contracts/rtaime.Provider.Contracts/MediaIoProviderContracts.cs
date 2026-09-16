// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Contracts;

public static class MediaIoCapabilityKinds
{
	public const string VideoInput = "media.io.video.input";
	public const string VideoOutput = "media.io.video.output";
	public const string AudioInput = "media.io.audio.input";
	public const string AudioOutput = "media.io.audio.output";
	public const string ExternalReference = "media.io.reference.external";
}

/// <summary>
/// Provider-owned physical/logical port profile. It declares normalized formats separately from the native wire
/// encodings so vendor-specific DMA formats never leak into the stable media model.
/// </summary>
public sealed class MediaIoPortDescriptor
{
	private readonly ReadOnlyCollection<VideoFormat> _normalizedVideoFormats;
	private readonly ReadOnlyCollection<MediaIoNativeVideoFormat> _nativeVideoFormats;
	private readonly ReadOnlyCollection<AudioFormat> _audioFormats;
	private readonly ReadOnlyCollection<MediaIoTransferMode> _transferModes;

	public MediaIoPortDescriptor(
		CompatibilityVersion version,
		ProviderId providerId,
		ProviderResourceId resourceId,
		MediaIoPortId portId,
		string name,
		MediaIoDirection direction,
		MediaIoTransportKind transport,
		IReadOnlyList<VideoFormat> normalizedVideoFormats,
		IReadOnlyList<MediaIoNativeVideoFormat> nativeVideoFormats,
		IReadOnlyList<AudioFormat> audioFormats,
		IReadOnlyList<MediaIoTransferMode> transferModes,
		bool supportsExternalReference)
	{
		MediaIoContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Media I/O port name is required.", nameof(name));
		if (!Enum.IsDefined(typeof(MediaIoDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
		if (!Enum.IsDefined(typeof(MediaIoTransportKind), transport)) throw new ArgumentOutOfRangeException(nameof(transport));
		ArgumentNullException.ThrowIfNull(normalizedVideoFormats);
		ArgumentNullException.ThrowIfNull(nativeVideoFormats);
		ArgumentNullException.ThrowIfNull(audioFormats);
		ArgumentNullException.ThrowIfNull(transferModes);
		if (normalizedVideoFormats.Count == 0) throw new ArgumentException("Media I/O ports require at least one normalized video format.", nameof(normalizedVideoFormats));
		if (nativeVideoFormats.Count == 0) throw new ArgumentException("Media I/O ports require at least one native video format.", nameof(nativeVideoFormats));
		if (transferModes.Count == 0) throw new ArgumentException("Media I/O ports require at least one transfer mode.", nameof(transferModes));
		if (normalizedVideoFormats.Distinct().Count() != normalizedVideoFormats.Count) throw new ArgumentException("Normalized video formats must be unique.", nameof(normalizedVideoFormats));
		if (nativeVideoFormats.Distinct().Count() != nativeVideoFormats.Count) throw new ArgumentException("Native video formats must be unique.", nameof(nativeVideoFormats));
		if (audioFormats.Distinct().Count() != audioFormats.Count) throw new ArgumentException("Audio formats must be unique.", nameof(audioFormats));
		if (transferModes.Distinct().Count() != transferModes.Count) throw new ArgumentException("Transfer modes must be unique.", nameof(transferModes));
		if (transferModes.Any(mode => !Enum.IsDefined(typeof(MediaIoTransferMode), mode))) throw new ArgumentOutOfRangeException(nameof(transferModes));

		Version = version;
		ProviderId = providerId;
		ResourceId = resourceId;
		PortId = portId;
		Name = name.Trim();
		Direction = direction;
		Transport = transport;
		_normalizedVideoFormats = Array.AsReadOnly(normalizedVideoFormats.ToArray());
		_nativeVideoFormats = Array.AsReadOnly(nativeVideoFormats.ToArray());
		_audioFormats = Array.AsReadOnly(audioFormats.ToArray());
		_transferModes = Array.AsReadOnly(transferModes.ToArray());
		SupportsExternalReference = supportsExternalReference;
	}

	public CompatibilityVersion Version { get; }
	public ProviderId ProviderId { get; }
	public ProviderResourceId ResourceId { get; }
	public MediaIoPortId PortId { get; }
	public string Name { get; }
	public MediaIoDirection Direction { get; }
	public MediaIoTransportKind Transport { get; }
	public IReadOnlyList<VideoFormat> NormalizedVideoFormats => _normalizedVideoFormats;
	public IReadOnlyList<MediaIoNativeVideoFormat> NativeVideoFormats => _nativeVideoFormats;
	public IReadOnlyList<AudioFormat> AudioFormats => _audioFormats;
	public IReadOnlyList<MediaIoTransferMode> TransferModes => _transferModes;
	public bool SupportsExternalReference { get; }
}

/// <summary>
/// Media-I/O-specific projection over the generic provider descriptor. The generic descriptor remains the source
/// for planning/admission resource identity; this profile only adds port and interop details.
/// </summary>
public sealed class MediaIoProviderDescriptor
{
	private readonly ReadOnlyCollection<MediaIoPortDescriptor> _ports;

	public MediaIoProviderDescriptor(
		CompatibilityVersion version,
		ProviderDescriptor provider,
		IReadOnlyList<MediaIoPortDescriptor> ports)
	{
		MediaIoContractVersion.EnsureSupported(version);
		Provider = provider ?? throw new ArgumentNullException(nameof(provider));
		ArgumentNullException.ThrowIfNull(ports);
		if (ports.Any(port => port is null)) throw new ArgumentException("Media I/O ports must not contain null values.", nameof(ports));
		if (ports.Count == 0) throw new ArgumentException("Media I/O provider requires at least one port.", nameof(ports));
		if (ports.Any(port => port.ProviderId != provider.ProviderId)) throw new ArgumentException("Media I/O ports must belong to the provider descriptor.", nameof(ports));
		if (ports.Select(port => port.PortId).Distinct().Count() != ports.Count) throw new ArgumentException("Media I/O port identities must be unique.", nameof(ports));
		if (ports.Select(port => port.ResourceId).Distinct().Count() != ports.Count) throw new ArgumentException("Media I/O resource identities must be unique per port.", nameof(ports));

		var providerResources = provider.Resources.Select(resource => resource.ResourceId).ToHashSet();
		if (ports.Any(port => !providerResources.Contains(port.ResourceId)))
			throw new ArgumentException("Every Media I/O port must map to a declared provider resource.", nameof(ports));

		Version = version;
		_ports = Array.AsReadOnly(ports.ToArray());
	}

	public CompatibilityVersion Version { get; }
	public ProviderDescriptor Provider { get; }
	public IReadOnlyList<MediaIoPortDescriptor> Ports => _ports;
}
