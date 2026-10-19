// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public sealed partial class NamedPipeOperatorControlTransport
{
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
