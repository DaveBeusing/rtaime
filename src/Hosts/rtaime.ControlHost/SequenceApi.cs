// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;
namespace rtaime.ControlHost;

public sealed partial class ControlHostIpcServer
{
    private sealed record SequenceGetRequest(Guid SequenceId);
    private sealed record SequenceEmptyRequest;
    private async ValueTask<WireEnvelope> RunSequenceAsync(WireEnvelope request, CancellationToken cancellationToken)
    {
        if (_showProjectStore is null || _mediaAssetCatalog is null)
            return Error(request, "sequence.unavailable", "Durable show persistence and the media catalogue are required.");
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var control = _controlAccessor() ?? throw new InvalidOperationException("Control authority is unavailable.");
            var baseline = _outputCanvasBaseline ?? control.Specification;
            var project = await _showProjectStore.LoadAsync(baseline, cancellationToken).ConfigureAwait(false);
            var library = project.SequenceJson is null
                ? new SequenceLibrary(SequenceContract.Version, 0, [])
                : ShowProjectPersistenceStore.ReadSequenceDocument(project.SequenceJson).Saved;
            if (request.MessageType == "control.sequence.list")
            {
                _ = SequenceJson.Read<SequenceEmptyRequest>(request.Payload.GetRawText());
                return Success(request, request.MessageType + ".response", new SequenceList(library.Revision,
                    library.Sequences.Select(s => new SequenceSummary(s.SequenceId, s.Name, s.FrameRate, s.Tracks.Length, s.Clips.Length)).ToArray(),
                    SequenceContract.Version, "UNAVAILABLE"));
            }
            var catalog = await _mediaAssetCatalog.GetSnapshotAsync(refreshAvailability: true, cancellationToken).ConfigureAwait(false);
            await using var lease = await _mediaAssetCatalog.AcquireRevisionLeaseAsync(catalog.Revision, cancellationToken).ConfigureAwait(false);
            var assets = new List<SequenceAsset>();
            foreach (var asset in catalog.Assets.Where(a => a.Availability == MediaAssetAvailability.Online))
            {
                var id = Guid.Parse(asset.AssetId.ToString());
                var duration = SequenceTime.FromTimeSpanTicks(asset.Duration.Ticks);
                if (asset.VideoFormat.Width > 0)
                    assets.Add(new(id, SequenceMediaKind.Video, duration, asset.FingerprintSha256));
                if (asset.AudioFormat.SampleRate > 0)
                    assets.Add(new(id, SequenceMediaKind.Audio, duration, asset.FingerprintSha256));
            }
            if (project.Graphics.Bitmap is { } bitmap)
                assets.Add(new(Guid.Parse(bitmap.AssetId.ToString()), SequenceMediaKind.Graphics, SequenceContract.MaximumTime, bitmap.ChecksumSha256));
            var buses = project.AudioProduction?.Buses.Select(b => b.BusId.ToString()).ToArray() ?? ["program"];
            if (request.MessageType == "control.sequence.get")
            {
                var get = SequenceJson.Read<SequenceGetRequest>(request.Payload.GetRawText());
                var sequence = library.Sequences.SingleOrDefault(s => s.SequenceId == get.SequenceId);
                return sequence is null ? Error(request, "sequence.missing", "Sequence identity is unknown.")
                    : Success(request, request.MessageType + ".response", new SequenceSnapshot(library.Revision, sequence,
                        null, "UNAVAILABLE", SequenceModel.Validate(sequence, library.Revision, assets.ToArray(), buses)));
            }
            var edit = SequenceJson.Read<SequenceMutationRequest>(request.Payload.GetRawText());
            var operation = request.MessageType["control.sequence.".Length..];
            var result = await _showProjectStore.MutateSequenceAsync(baseline, operation, edit, assets.ToArray(), buses, cancellationToken).ConfigureAwait(false);
            if (result.Saved && !result.Replayed) NotifyObservableStateChanged();
            return Success(request, request.MessageType + ".response", result);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or JsonException or OverflowException)
        { return Error(request, "sequence.rejected", exception.Message); }
        finally { _mutationGate.Release(); }
    }
}
