// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// Authoritative catalogue/revision admission of a bounded native sequence
/// preparation. The productive Preview/Program mutation pipeline is untouched.
/// </summary>
public sealed partial class ControlHostIpcServer
{
    private readonly SemaphoreSlim _sequencePreparationGate = new(1, 1);
    private PreparedSequenceBinding? _preparedSequence;

    private sealed record SequencePreparationEmpty;
    private sealed record PreparedSequenceBinding(
        SequencePreparationReference Reference, Identity AuthorityStateId,
        Revision AuthorityRevision, ulong CatalogueRevision);

    /// <summary>
    /// Release optional native staging when this ControlHost lifetime ends.
    /// A bounded failure never claims that Runtime released native ownership;
    /// Runtime shutdown/quarantine remains responsible for final cleanup.
    /// </summary>
    private async ValueTask DrainPreparedSequenceAsync()
    {
        if (_preparedSequence is not { } staged || !_runtimeTransport.IsConnected) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var result = await _runtimeTransport.CancelSequencePreparationAsync(
                staged.Reference, timeout.Token).ConfigureAwait(false);
            if (result.State == "CANCELED") _preparedSequence = null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or
            OperationCanceledException or InvalidOperationException or NotSupportedException)
        {
            // The native Runtime ledger retains uncertain ownership; there is
            // no optimistic release or silent successful cancellation.
        }
    }

    private async ValueTask<WireEnvelope> RunSequencePreparationAsync(
        WireEnvelope message, CancellationToken cancellationToken)
    {
        await _sequencePreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_runtimeTransport.IsConnected || _showProjectStore is null ||
                _mediaAssetCatalog is null)
                return Error(message, "sequence.preparation.unavailable",
                    "Live RuntimeHost, durable show project and catalogue are required.");

            if (message.MessageType == "control.sequence.preparation.context.get")
            {
                _ = SequenceJson.Read<SequencePreparationEmpty>(
                    message.Payload.GetRawText(), 16 * 1024);
                var authority = _controlAccessor();
                if (authority is null || !authority.HasAuthoritativeState)
                    return Error(message, "sequence.preparation.authority_unavailable",
                        "ControlHost has no committed production authority.");
                var runtimeContext = await _runtimeTransport.GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
                var baselineContext = _outputCanvasBaseline ?? authority.Specification;
                var savedContext = await _showProjectStore.LoadSequencesAsync(
                    baselineContext, cancellationToken).ConfigureAwait(false);
                var result = new SequencePreparationContext(SequencePreparationContract.Version,
                    runtimeContext.HostInstanceId, savedContext.Revision);
                result.Validate();
                return Success(message, message.MessageType + ".response", result);
            }

            if (message.MessageType == "control.sequence.prepare")
            {
                var request = SequenceJson.Read<SequencePreparationRequest>(
                    message.Payload.GetRawText(), 16 * 1024);
                request.Validate();
                return await PrepareSequenceAsync(message, request, cancellationToken)
                    .ConfigureAwait(false);
            }

            var reference = SequenceJson.Read<SequencePreparationReference>(
                message.Payload.GetRawText(), 16 * 1024);
            reference.Validate();
            if (_preparedSequence is not { } bound || bound.Reference != reference)
                return Error(message, "sequence.preparation.unknown",
                    "No matching prepared sequence exists in this ControlHost lifetime.");

            if (_runtimeTransport.HostInstanceId != reference.RuntimeHostInstanceId)
            {
                _preparedSequence = null;
                return Error(message, "sequence.preparation.host_changed",
                    "RuntimeHost was replaced; previous decoder preparation is invalid.");
            }

            if (message.MessageType == "control.sequence.preparation.cancel")
            {
                var closed = await _runtimeTransport.CancelSequencePreparationAsync(
                    reference, cancellationToken).ConfigureAwait(false);
                if (closed.State != "CANCELED")
                    return Error(message, "sequence.preparation.cancel_unconfirmed",
                        "RuntimeHost did not confirm native cleanup.");
                _preparedSequence = null;
                return Success(message, message.MessageType + ".response", closed);
            }

            var status = await _runtimeTransport.GetSequencePreparationAsync(
                reference, cancellationToken).ConfigureAwait(false);
            var control = _controlAccessor();
            var baseline = control is null ? null : _outputCanvasBaseline ?? control.Specification;
            var saved = baseline is null ? null :
                await _showProjectStore.LoadSequencesAsync(baseline, cancellationToken).ConfigureAwait(false);
            // A catalogue remove/relink invalidates the prepared native owners even
            // when the saved sequence itself has not been edited. Do not report
            // PREPARED for a media identity that Control no longer authorizes.
            var catalogue = await _mediaAssetCatalog.GetSnapshotAsync(
                refreshAvailability: false, cancellationToken).ConfigureAwait(false);
            if (control is null || !control.HasAuthoritativeState ||
                control.State.ProductionId.Value != bound.AuthorityStateId ||
                control.State.Revision != bound.AuthorityRevision ||
                saved?.Revision != reference.AuthoringRevision ||
                catalogue.Revision != bound.CatalogueRevision)
            {
                var cancelled = await _runtimeTransport.CancelSequencePreparationAsync(
                    reference, cancellationToken).ConfigureAwait(false);
                if (cancelled.State != "CANCELED")
                    return Error(message, "sequence.preparation.cleanup_unconfirmed",
                        "Stale sequence preparation could not be closed.");
                _preparedSequence = null;
                status = cancelled with { State = "STALE", Detail =
                    "Authoritative production or saved sequence revision changed; prepare a new sequence." };
            }
            return Success(message, message.MessageType + ".response", status);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or JsonException or TimeoutException or
            NotSupportedException or OverflowException)
        {
            return Error(message, "sequence.preparation.rejected",
                "Sequence preparation failed: " + error.GetType().Name);
        }
        finally { _sequencePreparationGate.Release(); }
    }

    private async ValueTask<WireEnvelope> PrepareSequenceAsync(
        WireEnvelope message, SequencePreparationRequest request, CancellationToken cancellationToken)
    {
        if (_preparedSequence is not null)
            return Error(message, "sequence.preparation.already_prepared",
                "Cancel the current native sequence preparation before preparing another.");

        var control = _controlAccessor();
        if (control is null || !control.HasAuthoritativeState)
            return Error(message, "sequence.preparation.authority_unavailable",
                "ControlHost has no committed production authority.");
        var authority = control.State;
        var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (runtime.HostInstanceId != request.ExpectedRuntimeHostInstanceId ||
            runtime.AuthorityStateId != authority.ProductionId.Value ||
            runtime.AuthorityRevision != authority.Revision)
            return Error(message, "sequence.preparation.authority_changed",
                "RuntimeHost identity or committed production revision changed; synchronize first.");

        var baseline = _outputCanvasBaseline ?? control.Specification;
        var project = await _showProjectStore!.LoadAsync(baseline, cancellationToken).ConfigureAwait(false);
        var library = project.SequenceJson is null
            ? new SequenceLibrary(SequenceContract.Version, 0, [])
            : ShowProjectPersistenceStore.ReadSequenceDocument(project.SequenceJson).Saved;
        if (library.Revision != request.ExpectedAuthoringRevision)
            return Error(message, "sequence.preparation.revision_conflict",
                "The requested sequence library revision is stale.");

        var definition = library.Sequences.SingleOrDefault(s => s.SequenceId == request.SequenceId);
        if (definition is null)
            return Error(message, "sequence.preparation.missing", "Saved sequence identity is unknown.");

        var catalog = await _mediaAssetCatalog!.GetSnapshotAsync(
            refreshAvailability: true, cancellationToken).ConfigureAwait(false);
        await using var catalogLease = await _mediaAssetCatalog.AcquireRevisionLeaseAsync(
            catalog.Revision, cancellationToken).ConfigureAwait(false);
        var assets = new List<SequenceAsset>();
        foreach (var asset in catalog.Assets.Where(a => a.Availability == MediaAssetAvailability.Online))
        {
            var guid = Guid.Parse(asset.AssetId.ToString());
            var duration = SequenceTime.FromTimeSpanTicks(asset.Duration.Ticks);
            if (asset.VideoFormat.Width > 0)
                assets.Add(new(guid, SequenceMediaKind.Video, duration, asset.FingerprintSha256));
            if (asset.AudioFormat.SampleRate > 0)
                assets.Add(new(guid, SequenceMediaKind.Audio, duration, asset.FingerprintSha256));
        }
        if (project.Graphics.Bitmap is { } bitmap)
        {
            try
            {
                _ = await _showProjectStore.LoadBitmapAssetAsync(bitmap, cancellationToken).ConfigureAwait(false);
                assets.Add(new(Guid.Parse(bitmap.AssetId.ToString()), SequenceMediaKind.Graphics,
                    SequenceContract.MaximumTime, bitmap.ChecksumSha256));
            }
            catch (IOException) { }
        }
        var audioBuses = project.AudioProduction?.Buses.Select(x => x.BusId.Value).ToArray() ?? ["program"];
        var compiled = SequenceExecutionCompiler.Compile(definition, library.Revision,
            assets, audioBuses, SequencePreparationContract.MaximumNativeOwners);
        if (!compiled.Succeeded)
            return Error(message, "sequence.preparation.invalid",
                "Saved sequence is unavailable or exceeds native decoder admission; inspect sequence validation.");

        // Conservatively stage every distinct native source/offset. The peak
        // interval bound alone cannot guarantee enough simultaneously retained
        // decoders across a complete sequence.
        var owners = compiled.Plan!.Windows.SelectMany(w => w.Clips)
            .Where(c => c.Kind != SequenceMediaKind.Graphics)
            .GroupBy(c => (c.AssetId, Offset: checked(c.SourceIn - c.TimelineStart)))
            .OrderBy(g => g.Key.AssetId).ThenBy(g => g.Key.Offset)
            .Select(g => g.First()).ToArray();
        if (owners.Length is < 1 or > SequencePreparationContract.MaximumNativeOwners)
            return Error(message, "sequence.preparation.owner_capacity",
                "Sequence requires more simultaneously retained native owners than this bounded profile admits.");

        var providers = await _runtimeTransport.GetProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(candidate =>
            candidate.Availability.State == ProviderAvailabilityState.Available &&
            candidate.Capabilities.Any(cap => cap.Kind == "media.route") &&
            candidate.Capabilities.Any(cap => cap.Kind == "media.file.decode"));
        var capability = provider?.Capabilities.FirstOrDefault(cap => cap.Kind == "media.route");
        var resources = provider?.Resources.Where(resource =>
            resource.Kind == "media.route" && resource.Reservable).ToArray();
        if (provider is null || capability is null || resources is null ||
            resources.Length < owners.Length)
            return Error(message, "sequence.preparation.provider_unavailable",
                "No sufficient admissible native local-media route capacity is advertised.");

        var remoteOwners = new List<SequenceRuntimeSource>(owners.Length);
        var bindings = new List<PreparedExecutionBinding>(owners.Length);
        for (var i = 0; i < owners.Length; i++)
        {
            var clip = owners[i];
            var catalogAsset = catalog.Assets.SingleOrDefault(asset =>
                asset.AssetId.ToString() == clip.AssetId.ToString() &&
                asset.Availability == MediaAssetAvailability.Online &&
                asset.FingerprintSha256 == clip.FingerprintSha256);
            if (catalogAsset is null)
                return Error(message, "sequence.preparation.source_stale",
                    "Saved sequence media reference changed since catalogue validation.");
            var source = MediaSourceId.New();
            remoteOwners.Add(new SequenceRuntimeSource(source,
                new MediaAssetId(Identity.Parse(clip.AssetId.ToString())),
                catalogAsset.SourceLocation, clip.FingerprintSha256,
                clip.SourceIn, clip.TimelineStart));
            bindings.Add(new PreparedExecutionBinding(source.Value,
                capability.CapabilityId, resources[i], source, null));
        }
        var execution = new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(authority.ProductionId.Value, authority.Revision),
            // Revision.Initial is a valid committed production state. Decoder preparation
            // needs a nonzero independent resource generation, not a higher
            // production revision or an implicit production mutation.
            new Generation(authority.Revision.Next().Value), bindings.ToArray());
        var command = new SequenceRuntimePrepareCommand(
            SequencePreparationContract.Version, request.ExpectedRuntimeHostInstanceId,
            request.SequenceId, request.ExpectedAuthoringRevision,
            compiled.Plan.DurationTicks, runtime.Format.FrameRate,
            execution, remoteOwners);

        SequencePreparationStatus? admitted = null;
        try
        {
            admitted = await _runtimeTransport.PrepareSequenceAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (admitted.State != "PREPARED")
                return Success(message, message.MessageType + ".response", admitted);

            var reference = new SequencePreparationReference(
                SequencePreparationContract.Version, admitted.SequenceId,
                admitted.AuthoringRevision, admitted.RuntimeHostInstanceId,
                admitted.PreparedExecutionId, admitted.Generation);
            _preparedSequence = new PreparedSequenceBinding(
                reference, authority.ProductionId.Value, authority.Revision, catalog.Revision);

            // Native staging happens outside the global production mutation lock.
            // An authored edit or Program change during preroll cannot become
            // current output authorization.
            var latest = await _showProjectStore.LoadSequencesAsync(baseline, cancellationToken)
                .ConfigureAwait(false);
            if (latest.Revision == library.Revision &&
                _controlAccessor()?.State.ProductionId.Value == authority.ProductionId.Value &&
                _controlAccessor()?.State.Revision == authority.Revision &&
                _runtimeTransport.HostInstanceId == request.ExpectedRuntimeHostInstanceId)
                return Success(message, message.MessageType + ".response", admitted);

            var closed = await _runtimeTransport.CancelSequencePreparationAsync(
                reference, CancellationToken.None).ConfigureAwait(false);
            if (closed.State != "CANCELED")
                return Error(message, "sequence.preparation.cleanup_unconfirmed",
                    "Stale native preparation cleanup was not confirmed.");
            _preparedSequence = null;
            return Success(message, message.MessageType + ".response",
                closed with { State = "STALE", Detail =
                    "Sequence, RuntimeHost or committed production changed during preroll." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (admitted?.State == "PREPARED")
            {
                var owner = new SequencePreparationReference(
                    SequencePreparationContract.Version, admitted.SequenceId,
                    admitted.AuthoringRevision, admitted.RuntimeHostInstanceId,
                    admitted.PreparedExecutionId, admitted.Generation);
                try
                {
                    _ = await _runtimeTransport.CancelSequencePreparationAsync(
                        owner, CancellationToken.None).ConfigureAwait(false);
                    _preparedSequence = null;
                }
                catch (Exception) when (true)
                {
                    _preparedSequence ??= new PreparedSequenceBinding(
                        owner, authority.ProductionId.Value, authority.Revision, catalog.Revision);
                }
            }
            throw;
        }
    }
}
