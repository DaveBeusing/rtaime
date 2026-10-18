// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using Rtaime.ExternalControl.V1;
namespace rtaime.Client;

public sealed partial class GrpcOperatorControlTransport : ISequenceAuthoringTransport
{
    public async ValueTask<SequenceList> ListSequencesAsync(CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceList>((await ExecuteShowRequestAsync(r => r.ListSequences = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.ToStringUtf8(), 1024 * 1024);
    public async ValueTask<SequenceSnapshot> GetSequenceAsync(Guid sequenceId, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceSnapshot>((await ExecuteShowRequestAsync(r => r.GetSequence = Raw(new { SequenceId = sequenceId }), cancellationToken).ConfigureAwait(false)).PayloadJson.ToStringUtf8(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> SaveSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExecuteShowRequestAsync(r => r.SaveSequence = Raw(request), cancellationToken).ConfigureAwait(false)).PayloadJson.ToStringUtf8(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> EditSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExecuteShowRequestAsync(r => r.EditSequence = Raw(request), cancellationToken).ConfigureAwait(false)).PayloadJson.ToStringUtf8(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> UndoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExecuteShowRequestAsync(r => r.UndoSequence = Raw(request), cancellationToken).ConfigureAwait(false)).PayloadJson.ToStringUtf8(), 1024 * 1024);
    public async ValueTask<SequenceMutationResult> RedoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default) =>
        SequenceJson.Read<SequenceMutationResult>((await ExecuteShowRequestAsync(r => r.RedoSequence = Raw(request), cancellationToken).ConfigureAwait(false)).PayloadJson.ToStringUtf8(), 1024 * 1024);
}
