// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

public sealed partial class ControlHostIpcServer
{
    private sealed class PreparedMediaTake(PreparedRundownMedia candidate)
    {
        public PreparedRundownMedia Candidate { get; } = candidate;
        public ulong ExpectedRevision { get; set; } = candidate.Preparation.AuthorityRevision;
    }
    private readonly AsyncLocal<PreparedMediaTake?> _preparedMediaTake = new();

    private async ValueTask<PreparedRundownMedia> PrepareRundownMediaAsync(RundownMediaItem item, CancellationToken token)
    {
        var control = _controlAccessor() ?? throw new InvalidOperationException("Control is unavailable.");
        if (_mediaAssetCatalog is null || _mediaDeck is null) throw new InvalidOperationException("Media services are unavailable.");
        var source = new MediaSourceId(item.SourceId.Value);
        if (control.State.Routing.ProgramSourceId.Value == source.Value)
            throw new InvalidOperationException("Prepare the next clip on the other A/B deck; Program is protected.");
        var catalogue = await _mediaAssetCatalog.GetSnapshotAsync(true, token).ConfigureAwait(false);
        var asset = catalogue.Assets.SingleOrDefault(a => a.AssetId.Value == item.AssetId)
            ?? throw new InvalidOperationException("Next asset is absent from the catalogue.");
        if (asset.Availability != MediaAssetAvailability.Online) throw new InvalidOperationException("Next asset is offline.");
        var opened = await _mediaDeck.PreloadAsync(new MediaDeckOpenRequest(MediaContractVersion.Current,
            source, asset.SourceLocation, asset.AssetId), asset.FingerprintSha256, token).ConfigureAwait(false);
        if (opened.State != MediaDeckState.Ready) throw new InvalidOperationException(opened.Failure?.Message ?? "Next clip did not prepare.");
        var evidence = await _runtimeTransport.PrepareLoadedNextAsync(source, asset.AssetId, token).ConfigureAwait(false);
        var candidate = new PreparedRundownMedia(evidence, catalogue.Revision, asset.SourceLocation,
            _runtimeTransport.HostInstanceId ?? throw new InvalidOperationException("Runtime identity is absent."));
        await ValidatePreparedRundownMediaAsync(candidate, token).ConfigureAwait(false);
        return candidate;
    }

    private async ValueTask ValidatePreparedRundownMediaAsync(PreparedRundownMedia candidate, CancellationToken token)
    {
        var evidence = candidate.Preparation;
        var control = _controlAccessor() ?? throw new InvalidOperationException("Control is unavailable.");
        if (DateTimeOffset.UtcNow >= evidence.ExpiresAtUtc || _runtimeTransport.HostInstanceId != candidate.RuntimeHostInstanceId ||
            control.State.Revision.Value != (_preparedMediaTake.Value?.ExpectedRevision ?? evidence.AuthorityRevision))
            throw new InvalidOperationException("Prepared-next authority, Runtime lifetime or deadline changed.");
        var catalogue = await _mediaAssetCatalog!.GetSnapshotAsync(false, token).ConfigureAwait(false);
        var asset = catalogue.Assets.SingleOrDefault(a => a.AssetId.ToString() == evidence.AssetId);
        if (catalogue.Revision != candidate.CatalogueRevision || asset is null ||
            !StringComparer.OrdinalIgnoreCase.Equals(asset.SourceLocation, candidate.SourceLocation) ||
            !StringComparer.OrdinalIgnoreCase.Equals(asset.FingerprintSha256, evidence.FingerprintSha256))
            throw new InvalidOperationException("Next asset was removed/relinked or the catalogue changed.");
    }

    private async ValueTask<ShowControlWorkspaceSnapshot> GoPreparedRundownMediaAsync(PreparedRundownMedia candidate, CancellationToken token)
    {
        await ValidatePreparedRundownMediaAsync(candidate, token).ConfigureAwait(false);
        var previous = _preparedMediaTake.Value;
        _preparedMediaTake.Value = new PreparedMediaTake(candidate);
        try { return await _showControl!.GoAsync(token).ConfigureAwait(false); }
        finally { _preparedMediaTake.Value = previous; }
    }

    private async ValueTask CancelPreparedRundownMediaAsync(PreparedRundownMedia candidate, CancellationToken token)
    {
        try { await _runtimeTransport.CancelMediaPreparationAsync(candidate.Preparation.TicketId, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or TimeoutException)
        { /* An unreachable Runtime retains bounded ownership until explicit cleanup/restart. */ }
    }
}
