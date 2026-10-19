// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

public sealed partial class ControlHostIpcServer
{
    private sealed record MediaAnalysisBinding(
        MediaAnalysisJobRequest Job, ulong CatalogueRevision, string FingerprintSha256,
        long LengthBytes, string SourceLocation);
    private readonly object _mediaAnalysisBindingsGate = new();
    private readonly Dictionary<string, MediaAnalysisBinding> _mediaAnalysisBindings =
        new(StringComparer.Ordinal);

    private async ValueTask<WireEnvelope> RunMediaAnalysisAsync(WireEnvelope message, CancellationToken cancellationToken)
    {
        try
        {
            if (_mediaAssetCatalog is null)
                return Error(message, "control.media.analysis.unavailable", "The authoritative media catalogue is unavailable.");
            if (message.MessageType == "control.media.analysis.begin")
            {
                var request = message.Payload.Deserialize<MediaAnalysisRequest>(Wire.JsonOptions)
                    ?? throw new InvalidDataException("Media analysis request is missing.");
                request.Validate();
                var snapshot = await _mediaAssetCatalog.GetSnapshotAsync(
                    refreshAvailability: false, cancellationToken).ConfigureAwait(false);
                var asset = snapshot.Assets.SingleOrDefault(x => x.AssetId.ToString() == request.AssetId)
                    ?? throw new InvalidOperationException("The referenced media asset is not in the current catalogue.");
                if (asset.Availability != MediaAssetAvailability.Online)
                    throw new InvalidOperationException("The selected media asset is not online.");
                var source = new FileInfo(asset.SourceLocation);
                if (!source.Exists || source.Length != asset.LengthBytes)
                    throw new InvalidOperationException("The catalogue file identity is stale; refresh or relink the asset.");
                var identity = new MediaAnalysisFileIdentity(asset.FingerprintSha256,
                    asset.LengthBytes, source.LastWriteTimeUtc.Ticks);
                identity.Validate();
                var result = await _runtimeTransport.BeginMediaAnalysisAsync(
                    request, identity, asset.SourceLocation, cancellationToken).ConfigureAwait(false);
                if (result.State is MediaAnalysisState.Pending or MediaAnalysisState.Running)
                {
                    var reference = new MediaAnalysisJobRequest(result.Version, result.JobId,
                        result.AssetId, result.ConsumerId, result.Generation, result.RuntimeEpoch);
                    reference.Validate();
                    lock (_mediaAnalysisBindingsGate)
                    {
                        foreach (var old in _mediaAnalysisBindings.Where(x =>
                            x.Value.Job.ConsumerId == reference.ConsumerId).Select(x => x.Key).ToArray())
                            _mediaAnalysisBindings.Remove(old);
                        if (_mediaAnalysisBindings.Count >= MediaAnalysisContract.MaximumConsumers)
                            throw new InvalidOperationException("Analysis session capacity is full.");
                        _mediaAnalysisBindings.Add(reference.JobId, new(reference, snapshot.Revision,
                            asset.FingerprintSha256, asset.LengthBytes, asset.SourceLocation));
                    }
                }
                return Success(message, message.MessageType + ".response", result);
            }

            var job = message.Payload.Deserialize<MediaAnalysisJobRequest>(Wire.JsonOptions)
                ?? throw new InvalidDataException("Media analysis job reference is missing.");
            job.Validate();
            MediaAnalysisBinding binding;
            lock (_mediaAnalysisBindingsGate)
            {
                if (!_mediaAnalysisBindings.TryGetValue(job.JobId, out var located) ||
                    located.Job != job)
                    throw new InvalidOperationException("Analysis job reference is unknown to this Control lifetime.");
                binding = located;
            }
            var observation = message.MessageType == "control.media.analysis.cancel"
                ? await _runtimeTransport.CancelMediaAnalysisAsync(job.JobId, cancellationToken).ConfigureAwait(false)
                : await _runtimeTransport.GetMediaAnalysisAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            observation.Validate();
            if (observation.JobId != job.JobId || observation.AssetId != job.AssetId ||
                observation.ConsumerId != job.ConsumerId || observation.Generation != job.Generation ||
                observation.RuntimeEpoch != job.RuntimeEpoch)
                throw new InvalidDataException("Runtime analysis observation identity/epoch is stale.");

            var current = await _mediaAssetCatalog.GetSnapshotAsync(
                refreshAvailability: false, cancellationToken).ConfigureAwait(false);
            var assetNow = current.Assets.SingleOrDefault(x => x.AssetId.ToString() == job.AssetId);
            if (current.Revision != binding.CatalogueRevision || assetNow is null ||
                assetNow.FingerprintSha256 != binding.FingerprintSha256 ||
                assetNow.LengthBytes != binding.LengthBytes ||
                !string.Equals(assetNow.SourceLocation, binding.SourceLocation, StringComparison.OrdinalIgnoreCase))
            {
                if (observation.State is MediaAnalysisState.Pending or MediaAnalysisState.Running)
                    observation = await _runtimeTransport.CancelMediaAnalysisAsync(job.JobId, cancellationToken).ConfigureAwait(false);
                observation = observation with {
                    State = MediaAnalysisState.Stale, RgbaPixels = null, Waveform = null,
                    Detail = "Media catalogue identity/revision changed; begin a new analysis request."
                };
            }
            return Success(message, message.MessageType + ".response", observation);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException
            or IOException or TimeoutException or NotSupportedException or JsonException)
        {
            return Error(message, "control.media.analysis.rejected", exception.Message);
        }
    }
}
