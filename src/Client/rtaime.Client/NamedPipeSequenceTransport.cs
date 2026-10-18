// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
namespace rtaime.Client;

public sealed partial class NamedPipeOperatorControlTransport : ISequenceAuthoringTransport
{
    public async ValueTask<SequenceList> ListSequencesAsync(CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceList>((await ExchangeAsync("control.sequence.list", new { }, cancellationToken).ConfigureAwait(false)).Payload.GetRawText(), 1024 * 1024);
    public async ValueTask<SequenceSnapshot> GetSequenceAsync(Guid sequenceId, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceSnapshot>((await ExchangeAsync("control.sequence.get", new { SequenceId = sequenceId }, cancellationToken).ConfigureAwait(false)).Payload.GetRawText(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> SaveSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExchangeAsync("control.sequence.save", request, cancellationToken).ConfigureAwait(false)).Payload.GetRawText(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> EditSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExchangeAsync("control.sequence.edit", request, cancellationToken).ConfigureAwait(false)).Payload.GetRawText(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> UndoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExchangeAsync("control.sequence.undo", request, cancellationToken).ConfigureAwait(false)).Payload.GetRawText(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> RedoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExchangeAsync("control.sequence.redo", request, cancellationToken).ConfigureAwait(false)).Payload.GetRawText(), 1024 * 1024);
}
