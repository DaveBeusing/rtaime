// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.RuntimeHost;

public sealed partial class LocalMediaDeckRuntimeService
{
    /// <summary>
    /// Report only the retained native preparation identity. This does not
    /// claim actual Preview/Program playback or an active media sink.
    /// </summary>
    internal SequencePreparationStatus GetPreparedSequence(
        SequencePreparationReference reference, string runtimeHostInstanceId)
    {
        reference.Validate();
        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            if (prepared is null ||
                reference.RuntimeHostInstanceId != runtimeHostInstanceId ||
                prepared.SequenceId != reference.SequenceId ||
                prepared.AuthoringRevision != reference.AuthoringRevision ||
                prepared.PreparedExecution.ToString() != reference.PreparedExecutionId ||
                prepared.InitialGeneration != reference.Generation)
                throw new InvalidOperationException(
                    "Sequence preparation identity, revision, generation or RuntimeHost lifetime changed.");
            return new SequencePreparationStatus(
                SequencePreparationContract.Version,
                prepared.SequenceId, prepared.AuthoringRevision, runtimeHostInstanceId,
                prepared.PreparedExecution.ToString(), prepared.InitialGeneration,
                prepared.Owners.Length, "PREPARED", null,
                "Native sequence sources are prepared; no Preview/Program playback or Take is authorized.");
        }
    }

    /// <summary>
    /// Explicitly cancel the exact staged preparation. Management serialization
    /// keeps the identity check and native cleanup in one coherent lifecycle.
    /// </summary>
    internal SequencePreparationStatus CancelPreparedSequence(
        SequencePreparationReference reference, string runtimeHostInstanceId)
    {
        reference.Validate();
        lock (_management)
        {
            _ = GetPreparedSequence(reference, runtimeHostInstanceId);
            CloseSequenceMedia();
            return new SequencePreparationStatus(
                SequencePreparationContract.Version,
                reference.SequenceId, reference.AuthoringRevision,
                runtimeHostInstanceId, reference.PreparedExecutionId, reference.Generation,
                0, "CANCELED", null,
                "Prepared native owners were closed; retained or quarantined resources remain charged until release.");
        }
    }
}
