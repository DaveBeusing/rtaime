// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public sealed partial class NamedPipeOperatorControlTransport
{
    private sealed record FrameAccess(string Endpoint, string Token, string ConsumerId);
    private readonly object _mediaFrameGate = new();
    private readonly Dictionary<string, FrameAccess> _mediaFrameAccess = new(StringComparer.Ordinal);
    public async ValueTask<MediaAnalysisBegin> BeginMediaAnalysisAsync(MediaAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var message = await ExchangeAsync("control.media.analysis.begin", request, cancellationToken).ConfigureAwait(false);
        var response = message.Payload.Deserialize<MediaAnalysisBegin>(Wire.JsonOptions)
            ?? throw new InvalidDataException("Media analysis begin response is missing.");
        if (response.Version != MediaAnalysisContract.Version || response.AssetId != request.AssetId ||
            response.ConsumerId != request.ConsumerId || response.Generation != request.Generation)
            throw new InvalidDataException("Media analysis generation/asset was changed in transit.");
        lock (_mediaFrameGate)
        {
            foreach (var prior in _mediaFrameAccess.Where(entry => entry.Value.ConsumerId == request.ConsumerId)
                .Select(entry => entry.Key).ToArray()) _mediaFrameAccess.Remove(prior);
            if (response.ObservationEndpoint is { Length: > 0 } endpoint &&
                response.ObservationToken is { Length: 64 } token &&
                response.State is MediaAnalysisState.Pending or MediaAnalysisState.Running)
            {
                if (_mediaFrameAccess.Count >= MediaAnalysisContract.MaximumConsumers)
                    _mediaFrameAccess.Remove(_mediaFrameAccess.Keys.First());
                _mediaFrameAccess[response.JobId] = new(endpoint, token, request.ConsumerId);
            }
        }
        return response;
    }

    public async ValueTask<MediaAnalysisObservation> GetMediaAnalysisAsync(MediaAnalysisJobRequest job,
        CancellationToken cancellationToken = default)
    {
        job.Validate();
        var message = await ExchangeAsync("control.media.analysis.get", job, cancellationToken).ConfigureAwait(false);
        var response = message.Payload.Deserialize<MediaAnalysisObservation>(Wire.JsonOptions)
            ?? throw new InvalidDataException("Media analysis observation is missing.");
        response.Validate();
        if (response.JobId != job.JobId || response.AssetId != job.AssetId ||
            response.Generation != job.Generation || response.RuntimeEpoch != job.RuntimeEpoch)
            throw new InvalidDataException("Media analysis response is stale or belongs to another asset.");
        if (response.Kind == MediaAnalysisKind.Frame && response.State == MediaAnalysisState.Complete &&
            response.RgbaPixels is null)
        {
            FrameAccess? access;
            lock (_mediaFrameGate) _mediaFrameAccess.TryGetValue(job.JobId, out access);
            if (access is null)
                return response with { State = MediaAnalysisState.Stale,
                    Detail = "Observation token expired or was replaced; request a new frame." };
            try
            {
                var frame = await MediaAnalysisFramePipeReader.ReadAsync(access.Endpoint, job.JobId,
                    access.Token, response.Width, response.Height, cancellationToken).ConfigureAwait(false);
                return response with { RgbaPixels = frame };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                or OperationCanceledException or TimeoutException)
            {
                return response with { State = MediaAnalysisState.Unavailable,
                    Detail = "Isolated analysis frame observation is unavailable; retry or request a new generation.",
                    ErrorCode = "media.analysis.frame_unavailable" };
            }
        }
        return response;
    }

    public async ValueTask<MediaAnalysisObservation> CancelMediaAnalysisAsync(MediaAnalysisJobRequest job,
        CancellationToken cancellationToken = default)
    {
        job.Validate();
        var message = await ExchangeAsync("control.media.analysis.cancel", job, cancellationToken).ConfigureAwait(false);
        var response = message.Payload.Deserialize<MediaAnalysisObservation>(Wire.JsonOptions)
            ?? throw new InvalidDataException("Media analysis cancellation response is missing.");
        response.Validate();
        if (response.JobId != job.JobId || response.RuntimeEpoch != job.RuntimeEpoch)
            throw new InvalidDataException("Media analysis cancellation response is stale.");
        return response;
    }
}
