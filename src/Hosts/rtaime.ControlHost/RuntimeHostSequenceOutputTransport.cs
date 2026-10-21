// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// RuntimeHost management operations; only ControlHost may authorize source
/// selection and committed Program promotion. No decoder payloads cross IPC.
/// </summary>
public sealed partial class NamedPipeRuntimeHostTransport
{
    public ValueTask<SequenceOutputObservation> BindSequencePreviewAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        SequenceOutputCommandAsync("runtime.sequence.output.preview", command, cancellationToken);

    public ValueTask<SequenceOutputObservation> ConfirmSequenceTakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        SequenceOutputCommandAsync("runtime.sequence.output.take", command, cancellationToken);

    public ValueTask<SequenceOutputObservation> ArmSequenceTakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        SequenceOutputCommandAsync("runtime.sequence.output.arm_take", command, cancellationToken);

    public ValueTask<SequenceOutputObservation> DisarmSequenceTakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default) =>
        SequenceOutputCommandAsync("runtime.sequence.output.disarm_take", command, cancellationToken);

    public async ValueTask<SequenceOutputObservation> GetSequenceOutputAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        var reply = await ExchangeAsync(
            "runtime.sequence.output.get", reference, cancellationToken).ConfigureAwait(false);
        var result = SequenceJson.Read<SequenceOutputObservation>(
            reply.Payload.GetRawText(), 16 * 1024);
        result.Validate();
        if (result.Preparation != reference)
            throw new InvalidDataException("Native sequence output observation belongs to another preparation.");
        return result;
    }

    private async ValueTask<SequenceOutputObservation> SequenceOutputCommandAsync(
        string message, SequenceOutputCommand command, CancellationToken cancellationToken)
    {
        command.Validate();
        var reply = await ExchangeAsync(message, command, cancellationToken)
            .ConfigureAwait(false);
        var result = SequenceJson.Read<SequenceOutputObservation>(
            reply.Payload.GetRawText(), 16 * 1024);
        result.Validate();
        if (result.Preparation != command.Preparation ||
            result.PreviewSourceId != command.PreviewSourceId ||
            result.ProductionRevision != command.ExpectedProductionRevision +
                (message == "runtime.sequence.output.take" ? 1UL : 0UL))
            throw new InvalidDataException("Sequence output confirmation has an unexpected authority or source.");
        return result;
    }
}
