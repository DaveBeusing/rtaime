// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Client;

public sealed partial class NamedPipeOperatorControlTransport : ISequenceShuttleTransport
{
    public async ValueTask<SequenceShuttleObservation> GetSequenceShuttleAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        return ValidateShuttleResponse(reference,
            (await ExchangeAsync("control.sequence.shuttle.get", reference, cancellationToken)
                .ConfigureAwait(false)).Payload.GetRawText());
    }

    public async ValueTask<SequenceShuttleObservation> ApplySequenceShuttleAsync(
        SequenceShuttleRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var result = ValidateShuttleResponse(request.Preparation,
            (await ExchangeAsync("control.sequence.shuttle.command", request, cancellationToken)
                .ConfigureAwait(false)).Payload.GetRawText());
        if (result.StateRevision <= request.ExpectedStateRevision)
            throw new InvalidDataException("Sequence shuttle command was not confirmed at a newer revision.");
        return result;
    }

    internal static SequenceShuttleObservation ValidateShuttleResponse(
        SequencePreparationReference reference, string json)
    {
        reference.Validate();
        var result = SequenceJson.Read<SequenceShuttleObservation>(json, 16 * 1024);
        result.Validate();
        if (result.Preparation != reference)
            throw new InvalidDataException("Sequence shuttle observation belongs to another RuntimeHost or sequence.");
        return result;
    }
}

public sealed partial class GrpcOperatorControlTransport : ISequenceShuttleTransport
{
    public async ValueTask<SequenceShuttleObservation> GetSequenceShuttleAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        return NamedPipeOperatorControlTransport.ValidateShuttleResponse(reference,
            (await ExecuteShowRequestAsync(r => r.GetSequenceShuttle = Raw(reference), cancellationToken)
                .ConfigureAwait(false)).PayloadJson.ToStringUtf8());
    }

    public async ValueTask<SequenceShuttleObservation> ApplySequenceShuttleAsync(
        SequenceShuttleRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var result = NamedPipeOperatorControlTransport.ValidateShuttleResponse(request.Preparation,
            (await ExecuteShowRequestAsync(r => r.ApplySequenceShuttle = Raw(request), cancellationToken)
                .ConfigureAwait(false)).PayloadJson.ToStringUtf8());
        if (result.StateRevision <= request.ExpectedStateRevision)
            throw new InvalidDataException("Sequence shuttle command did not advance the authorized state.");
        return result;
    }
}
