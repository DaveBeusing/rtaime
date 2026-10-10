// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Provider.Contracts;

namespace rtaime.ControlHost;

public sealed record MediaSourceAdoptionResult(
    ProductionSourceSpecification Source,
    PersistedShowProject Project,
    bool AlreadyAdopted,
    ProductionRoutingState Routing);

/// <summary>
/// ControlHost-owned boundary between observational provider discovery and the durable production source catalog.
/// Discovery cannot mutate production state; adoption is explicit and persists before the live specification changes.
/// </summary>
public sealed class MediaSourceDiscoveryAdoptionService
{
    private readonly Func<ControlHostService?> _controlAccessor;
    private readonly IControlRuntimeTransportSeam _runtime;
    private readonly ShowProjectPersistenceStore _persistence;
    private readonly ProductionSpecification _baseline;

    public MediaSourceDiscoveryAdoptionService(
        Func<ControlHostService?> controlAccessor,
        IControlRuntimeTransportSeam runtime,
        ShowProjectPersistenceStore persistence,
        ProductionSpecification baseline)
    {
        _controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
    }

    public ValueTask<MediaSourceDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        _runtime.GetMediaSourceDiscoveryAsync(cancellationToken);

    public ValueTask<IReadOnlyList<MediaInputHealthSnapshot>> GetInputHealthAsync(CancellationToken cancellationToken = default) =>
        _runtime.GetMediaInputHealthAsync(cancellationToken);

    public async ValueTask<MediaSourceAdoptionResult> AdoptAsync(
        DiscoveredMediaSourceId discoveredSourceId,
        CancellationToken cancellationToken = default)
    {
        var control = _controlAccessor()
            ?? throw new InvalidOperationException("ControlHost authority is unavailable.");
        var routingBefore = control.State.Routing;
        var discovery = await _runtime.GetMediaSourceDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        var discovered = discovery.Sources.SingleOrDefault(source => source.SourceId == discoveredSourceId)
            ?? throw new InvalidOperationException($"Discovered source '{discoveredSourceId}' is no longer present.");
        if (discovered.Availability.State != ProviderAvailabilityState.Available)
            throw new InvalidOperationException($"Discovered source '{discoveredSourceId}' is not currently available.");
        if (!discovered.VideoFormats.Any())
            throw new InvalidOperationException("Discovered source does not advertise a compatible video capability.");

        var existing = control.Specification.Sources.FirstOrDefault(source =>
            source.ProviderBinding is { } binding &&
            binding.ProviderId == discovered.ProviderId.Value &&
            binding.ExternalSourceId == discovered.SourceId.Value);
        if (existing is not null)
            return new MediaSourceAdoptionResult(existing, await _persistence.LoadAsync(_baseline, cancellationToken).ConfigureAwait(false), true, routingBefore);

        var sourceId = StableProductionSourceId(discovered.ProviderId, discovered.SourceId);
        var adopted = new ProductionSourceSpecification(
            sourceId,
            discovered.DisplayName,
            new ProductionSourceProviderBinding(
                discovered.ProviderId.Value,
                MediaSourceCapabilityKinds.Input,
                discovered.SourceId.Value,
                discovered.SafeSourceIdentity));

        // Durable catalog write is intentionally first. A process interruption after this point recovers the
        // adopted source on restart, while discovery itself still never changes routing.
        var project = await _persistence.AdoptSourceAsync(_baseline, adopted, cancellationToken).ConfigureAwait(false);
        var live = control.AdoptSource(adopted);
        if (control.State.Routing != routingBefore)
            throw new InvalidOperationException("Source adoption must not change Preview or Program routing.");

        return new MediaSourceAdoptionResult(live, project, false, routingBefore);
    }

    internal static ProductionSourceId StableProductionSourceId(ProviderId providerId, DiscoveredMediaSourceId externalSourceId)
    {
        var key = $"rtaime:production-source:{providerId}:{externalSourceId}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new ProductionSourceId(new Identity(new Guid(digest.AsSpan(0, 16))));
    }
}
