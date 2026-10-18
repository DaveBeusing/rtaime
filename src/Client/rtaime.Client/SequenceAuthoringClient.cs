// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
namespace rtaime.Client;

public interface ISequenceAuthoringTransport
{
    ValueTask<SequenceList> ListSequencesAsync(CancellationToken cancellationToken = default);
    ValueTask<SequenceSnapshot> GetSequenceAsync(Guid sequenceId, CancellationToken cancellationToken = default);
    ValueTask<SequenceMutationResult> SaveSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<SequenceMutationResult> EditSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<SequenceMutationResult> UndoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<SequenceMutationResult> RedoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Authored state only. This client cannot cue, arm, take or play a sequence.</summary>
public sealed class SequenceAuthoringClient(ISequenceAuthoringTransport transport)
{
    public ValueTask<SequenceList> ListAsync(CancellationToken cancellationToken = default) => transport.ListSequencesAsync(cancellationToken);
    public ValueTask<SequenceSnapshot> GetAsync(Guid sequenceId, CancellationToken cancellationToken = default) => transport.GetSequenceAsync(sequenceId, cancellationToken);
    public ValueTask<SequenceMutationResult> SaveAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) => transport.SaveSequenceAsync(request, cancellationToken);
    public ValueTask<SequenceMutationResult> EditAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) => transport.EditSequenceAsync(request, cancellationToken);
    public ValueTask<SequenceMutationResult> UndoAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) => transport.UndoSequenceAsync(request, cancellationToken);
    public ValueTask<SequenceMutationResult> RedoAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) => transport.RedoSequenceAsync(request, cancellationToken);
}
