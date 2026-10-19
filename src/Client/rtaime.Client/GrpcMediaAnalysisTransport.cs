// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Media.Contracts;
using Rtaime.ExternalControl.V1;

namespace rtaime.Client;

public sealed partial class GrpcOperatorControlTransport
{
    public async ValueTask<MediaAnalysisBegin> BeginMediaAnalysisAsync(MediaAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var response = await ExecuteShowRequestAsync(
            message => message.BeginMediaAnalysis = Raw(request), cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<MediaAnalysisBegin>(response.PayloadJson.Span, JsonOptions)
            ?? throw new InvalidDataException("Media analysis begin response is missing.");
        if (value.Version != MediaAnalysisContract.Version || value.AssetId != request.AssetId ||
            value.Generation != request.Generation || value.ConsumerId != request.ConsumerId)
            throw new InvalidDataException("Media analysis begin response does not match the requested identity.");
        return value;
    }

    public async ValueTask<MediaAnalysisObservation> GetMediaAnalysisAsync(MediaAnalysisJobRequest job,
        CancellationToken cancellationToken = default)
    {
        job.Validate();
        var response = await ExecuteShowRequestAsync(
            message => message.GetMediaAnalysis = Raw(job), cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<MediaAnalysisObservation>(response.PayloadJson.Span, JsonOptions)
            ?? throw new InvalidDataException("Media analysis response is missing.");
        value.Validate();
        if (value.JobId != job.JobId || value.RuntimeEpoch != job.RuntimeEpoch ||
            value.AssetId != job.AssetId || value.Generation != job.Generation)
            throw new InvalidDataException("Media analysis observation is stale.");
        if (value.Kind == MediaAnalysisKind.Frame && value.State == MediaAnalysisState.Complete &&
            value.RgbaPixels is null)
            return value with {
                State = MediaAnalysisState.Unavailable,
                Detail = "Raw frames require a local authorized media-observation channel; external gRPC returns metadata only.",
                ErrorCode = "media.analysis.frame_transport_unavailable"
            };
        return value;
    }

    public async ValueTask<MediaAnalysisObservation> CancelMediaAnalysisAsync(MediaAnalysisJobRequest job,
        CancellationToken cancellationToken = default)
    {
        job.Validate();
        var response = await ExecuteShowRequestAsync(
            message => message.CancelMediaAnalysis = Raw(job), cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<MediaAnalysisObservation>(response.PayloadJson.Span, JsonOptions)
            ?? throw new InvalidDataException("Media analysis cancellation response is missing.");
        value.Validate();
        if (value.JobId != job.JobId || value.RuntimeEpoch != job.RuntimeEpoch)
            throw new InvalidDataException("Media analysis cancellation is stale.");
        return value;
    }
}
