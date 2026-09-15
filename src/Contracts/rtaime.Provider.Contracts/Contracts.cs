using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Contracts;

public static class ProviderContractVersion
{
    public static CompatibilityVersion Current { get; } = new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported Provider contract version '{version}'. Supported version is '{Current}'.");
    }
}

public readonly record struct ProviderId
{
    public ProviderId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Provider identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ProviderId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct CapabilityId
{
    public CapabilityId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Capability identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static CapabilityId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProviderResourceId
{
    public ProviderResourceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Provider resource identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ProviderResourceId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public enum ProviderAvailabilityState
{
    Available = 1,
    Degraded = 2,
    Unavailable = 3
}

public sealed record ProviderAvailability
{
    public ProviderAvailability(ProviderAvailabilityState state, Failure? failure = null)
    {
        if (!Enum.IsDefined(typeof(ProviderAvailabilityState), state))
            throw new ArgumentOutOfRangeException(nameof(state), "Provider availability state must be a defined contract value.");
        if (state == ProviderAvailabilityState.Available && failure is not null)
            throw new ArgumentException("Available providers must not carry a failure.", nameof(failure));
        if (state == ProviderAvailabilityState.Unavailable && failure is null)
            throw new ArgumentException("Unavailable providers require a failure.", nameof(failure));

        State = state;
        Failure = failure;
    }

    public ProviderAvailabilityState State { get; }
    public Failure? Failure { get; }
}

public sealed record ProviderCapabilityDescriptor
{
    private readonly ReadOnlyCollection<VideoFormat> _videoFormats;

    public ProviderCapabilityDescriptor(
        CapabilityId capabilityId,
        string kind,
        IReadOnlyList<VideoFormat> videoFormats)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Capability kind is required.", nameof(kind));
        if (videoFormats is null)
            throw new ArgumentNullException(nameof(videoFormats));

        CapabilityId = capabilityId;
        Kind = kind.Trim();
        _videoFormats = Array.AsReadOnly(videoFormats.ToArray());
    }

    public CapabilityId CapabilityId { get; }
    public string Kind { get; }
    public IReadOnlyList<VideoFormat> VideoFormats => _videoFormats;
}

public sealed record ProviderResourceDescriptor
{
    public ProviderResourceDescriptor(
        ProviderResourceId resourceId,
        ProviderId providerId,
        string kind,
        uint capacityUnits,
        bool reservable)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Resource kind is required.", nameof(kind));
        if (capacityUnits == 0)
            throw new ArgumentOutOfRangeException(nameof(capacityUnits), "Resource capacity must be greater than zero.");

        ResourceId = resourceId;
        ProviderId = providerId;
        Kind = kind.Trim();
        CapacityUnits = capacityUnits;
        Reservable = reservable;
    }

    public ProviderResourceId ResourceId { get; }
    public ProviderId ProviderId { get; }
    public string Kind { get; }
    public uint CapacityUnits { get; }
    public bool Reservable { get; }
}

public sealed class ProviderDescriptor
{
    private readonly ReadOnlyCollection<ProviderCapabilityDescriptor> _capabilities;
    private readonly ReadOnlyCollection<ProviderResourceDescriptor> _resources;

    public ProviderDescriptor(
        CompatibilityVersion version,
        ProviderId providerId,
        string name,
        ProviderAvailability availability,
        IReadOnlyList<ProviderCapabilityDescriptor> capabilities,
        IReadOnlyList<ProviderResourceDescriptor> resources)
    {
        ProviderContractVersion.EnsureSupported(version);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Provider name is required.", nameof(name));
        if (capabilities is null)
            throw new ArgumentNullException(nameof(capabilities));
        if (resources is null)
            throw new ArgumentNullException(nameof(resources));
        if (capabilities.Any(capability => capability is null))
            throw new ArgumentException("Provider capabilities must not contain null values.", nameof(capabilities));
        if (resources.Any(resource => resource is null))
            throw new ArgumentException("Provider resources must not contain null values.", nameof(resources));
        if (resources.Any(resource => resource.ProviderId != providerId))
            throw new ArgumentException("Provider resources must belong to the descriptor provider.", nameof(resources));

        Version = version;
        ProviderId = providerId;
        Name = name.Trim();
        Availability = availability ?? throw new ArgumentNullException(nameof(availability));
        _capabilities = Array.AsReadOnly(capabilities.ToArray());
        _resources = Array.AsReadOnly(resources.ToArray());
    }

    public CompatibilityVersion Version { get; }
    public ProviderId ProviderId { get; }
    public string Name { get; }
    public ProviderAvailability Availability { get; }
    public IReadOnlyList<ProviderCapabilityDescriptor> Capabilities => _capabilities;
    public IReadOnlyList<ProviderResourceDescriptor> Resources => _resources;
}

public sealed record CapabilityRequirement
{
    private readonly ReadOnlyCollection<VideoFormat> _acceptedVideoFormats;

    public CapabilityRequirement(
        CompatibilityVersion version,
        Identity requirementId,
        string kind,
        uint requiredCapacityUnits,
        IReadOnlyList<VideoFormat> acceptedVideoFormats)
    {
        ProviderContractVersion.EnsureSupported(version);
        if (requirementId.IsEmpty)
            throw new ArgumentException("Capability requirement identity must not be empty.", nameof(requirementId));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Capability requirement kind is required.", nameof(kind));
        if (requiredCapacityUnits == 0)
            throw new ArgumentOutOfRangeException(nameof(requiredCapacityUnits), "Required capacity must be greater than zero.");
        if (acceptedVideoFormats is null)
            throw new ArgumentNullException(nameof(acceptedVideoFormats));

        Version = version;
        RequirementId = requirementId;
        Kind = kind.Trim();
        RequiredCapacityUnits = requiredCapacityUnits;
        _acceptedVideoFormats = Array.AsReadOnly(acceptedVideoFormats.ToArray());
    }

    public CompatibilityVersion Version { get; }
    public Identity RequirementId { get; }
    public string Kind { get; }
    public uint RequiredCapacityUnits { get; }
    public IReadOnlyList<VideoFormat> AcceptedVideoFormats => _acceptedVideoFormats;
}
