// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Client;

public sealed partial class NamedPipeOperatorControlTransport : ISequenceOutputTransport
{
    public ValueTask<SequenceOutputObservation> ActivatePreviewAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        ExecuteSequenceOutputAsync("control.sequence.output.preview", command, false, cancellationToken);

    public ValueTask<SequenceOutputObservation> TakeProgramAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        ExecuteSequenceOutputAsync("control.sequence.output.take", command, true, cancellationToken);

    public ValueTask<SequenceOutputObservation> RecoverTakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        ExecuteSequenceOutputAsync("control.sequence.output.recover", command, true, cancellationToken);

    public async ValueTask<SequenceOutputObservation> GetOutputAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        var response = await ExchangeAsync(
            "control.sequence.output.get", reference, cancellationToken).ConfigureAwait(false);
        return VerifySequenceOutput(reference, response.Payload.GetRawText());
    }

    private async ValueTask<SequenceOutputObservation> ExecuteSequenceOutputAsync(
        string operation, SequenceOutputCommand command, bool successor,
        CancellationToken cancellationToken)
    {
        command.Validate();
        var response = await ExchangeAsync(operation, command, cancellationToken)
            .ConfigureAwait(false);
        var result = VerifySequenceOutput(command.Preparation, response.Payload.GetRawText());
        if (result.PreviewSourceId != command.PreviewSourceId ||
            result.ProductionRevision != checked(command.ExpectedProductionRevision + (successor ? 1UL : 0UL)))
            throw new InvalidDataException("Sequence output result changed the requested source or committed revision.");
        return result;
    }

    internal static SequenceOutputObservation VerifySequenceOutput(
        SequencePreparationReference reference, string json)
    {
        reference.Validate();
        var result = SequenceJson.Read<SequenceOutputObservation>(json, 16 * 1024);
        result.Validate();
        if (result.Preparation != reference)
            throw new InvalidDataException("Sequence output observation belongs to a different native preparation.");
        return result;
    }
}

public sealed partial class GrpcOperatorControlTransport : ISequenceOutputTransport
{
    public ValueTask<SequenceOutputObservation> ActivatePreviewAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        ExecuteSequenceOutputAsync(command, take: false, recover: false, cancellationToken);

    public ValueTask<SequenceOutputObservation> TakeProgramAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        ExecuteSequenceOutputAsync(command, take: true, recover: false, cancellationToken);

    public ValueTask<SequenceOutputObservation> RecoverTakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        ExecuteSequenceOutputAsync(command, take: false, recover: true, cancellationToken);

    public async ValueTask<SequenceOutputObservation> GetOutputAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        var reply = await ExecuteShowRequestAsync(
            request => request.GetSequenceOutput = Raw(reference), cancellationToken).ConfigureAwait(false);
        return NamedPipeOperatorControlTransport.VerifySequenceOutput(
            reference, reply.PayloadJson.ToStringUtf8());
    }

    private async ValueTask<SequenceOutputObservation> ExecuteSequenceOutputAsync(
        SequenceOutputCommand command, bool take, bool recover,
        CancellationToken cancellationToken)
    {
        command.Validate();
        EnsureSnapshotNotRequired();
        var request = Request();
        if (recover)
            request.RecoverSequenceTake = Raw(command);
        else if (take)
            request.TakeSequenceProgram = Raw(command);
        else
            request.ActivateSequencePreview = Raw(command);

        // Preview activation and Program Take are not replayable operations.
        // Transport uncertainty must be resolved by an explicit observation
        // and, if a committed successor exists, Recover; never by RPC retry.
        var deadline = DateTime.UtcNow + _options.RequestTimeout;
        var reply = await SendRequestAsync(
            request, deadline, cancellationToken).ConfigureAwait(false);
        ValidateReply(request, reply, snapshot: false);
        var result = NamedPipeOperatorControlTransport.VerifySequenceOutput(
            command.Preparation, reply.PayloadJson.ToStringUtf8());
        if (result.PreviewSourceId != command.PreviewSourceId ||
            result.ProductionRevision != checked(command.ExpectedProductionRevision + (take || recover ? 1UL : 0UL)))
            throw new InvalidDataException("Sequence output response no longer matches authoritative source/revision.");
        return result;
    }
}
