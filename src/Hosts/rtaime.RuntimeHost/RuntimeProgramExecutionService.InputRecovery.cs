// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Fail-closed recovery for input sequence ownership when a Program boundary
/// aborts. The single Program clock remains authoritative: an uncommitted
/// slot can be retried, while a slot already accepted by the output must not
/// be published twice. The normal boundary path incurs no recovery work.
/// </summary>
public sealed partial class RuntimeProgramExecutionService
{
    private void ResetInputPipelinesAfterFailedBoundary()
    {
        // The calling ProcessNextBoundary still owns _boundaryExecutionGate.
        // Maintain the established boundary-capture -> metadata lock order,
        // including concurrent source registration on the management lane.
        lock (_boundaryCaptureGate)
        lock (_gate)
        {
            var replacementSlot = _nextSequenceNumber;
            if (_programOutput?.LastFrame?.Frame.Timing.SequenceNumber == replacementSlot)
            {
                // An exception after WriteFrame is an ambiguous partial
                // consumer failure. Do not repeat that already published
                // Program slot or overwrite its monotonic sink identity.
                if (replacementSlot == ulong.MaxValue)
                    throw new InvalidOperationException(
                        "Program frame sequence exhausted during output recovery.");
                _nextSequenceNumber = ++replacementSlot;
                Observe($"runtime.boundary.partial_publication:{replacementSlot - 1}");
            }

            // The previous media pipelines may have consumed a descriptor
            // before a later GPU/audio/sink failure. Closing them drains
            // their bounded queues; replacements join this exact slot.
            var previousA = _sourceAPipeline;
            var previousB = _sourceBPipeline;
            _sourceAPipeline = CreatePipeline(replacementSlot);
            _sourceBPipeline = CreatePipeline(replacementSlot);
            previousA.Dispose();
            previousB.Dispose();

            foreach (var sourceId in _externalSourcePipelines.Keys.ToArray())
            {
                var previous = _externalSourcePipelines[sourceId];
                _externalSourcePipelines[sourceId] = CreatePipeline(replacementSlot);
                previous.Dispose();
            }

            _boundaryFrameScratch.Clear();
            Observe($"runtime.boundary.input_reanchored:{replacementSlot}");
        }
    }
}
