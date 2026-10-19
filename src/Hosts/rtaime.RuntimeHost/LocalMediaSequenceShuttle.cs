// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// Management-only shuttle for an already authorized native sequence staging.
/// All positions use the existing Runtime Program frame counter, never a local
/// timer. Actual decoded pixels/PCM are not published by this control surface.
/// </summary>
public sealed partial class LocalMediaDeckRuntimeService
{
    private SequenceRuntimePositionMap? _sequenceShuttleMap;
    private ulong _sequenceShuttleRevision;

    internal SequenceShuttleObservation GetSequenceShuttle(
        SequencePreparationReference reference, string runtimeHostInstanceId,
        Identity authorityStateId, Revision authorityRevision, ulong programSlot)
    {
        reference.Validate();
        lock (_management)
        lock (_sequenceGate)
        {
            var map = RequireSequenceShuttle(reference, runtimeHostInstanceId,
                authorityStateId, authorityRevision);
            return ObserveSequenceShuttle(reference, map, programSlot);
        }
    }

    internal SequenceShuttleObservation ApplySequenceShuttle(
        SequenceShuttleRequest command, string runtimeHostInstanceId,
        Identity authorityStateId, Revision authorityRevision,
        Func<ulong> currentProgramSlot)
    {
        command.Validate();
        ArgumentNullException.ThrowIfNull(currentProgramSlot);
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SequenceRuntimePositionMap map;
            lock (_sequenceGate)
                map = RequireSequenceShuttle(command.Preparation,
                    runtimeHostInstanceId, authorityStateId, authorityRevision);
            if (command.ExpectedStateRevision != _sequenceShuttleRevision)
                throw new InvalidOperationException(
                    "Sequence shuttle revision changed. Observe the current Runtime owner before another action.");

            var slot = currentProgramSlot();
            var next = command.Action switch
            {
                SequenceShuttleAction.Cue => map.Cue(slot, command.PositionTicks!.Value),
                SequenceShuttleAction.Play when map.State != SequenceRuntimeTransportState.Playing =>
                    map.Play(slot),
                SequenceShuttleAction.Pause when map.State == SequenceRuntimeTransportState.Stopped =>
                    map.Cue(slot, map.StartTick),
                SequenceShuttleAction.Pause when map.State == SequenceRuntimeTransportState.Playing =>
                    map.Pause(slot),
                SequenceShuttleAction.Stop when map.State != SequenceRuntimeTransportState.Stopped =>
                    map.Stop(slot),
                SequenceShuttleAction.Seek when map.State == SequenceRuntimeTransportState.Playing =>
                    map.Seek(slot, command.PositionTicks!.Value),
                SequenceShuttleAction.Seek => map.Cue(slot, command.PositionTicks!.Value),
                SequenceShuttleAction.StepBackward => map.Step(slot, -1),
                SequenceShuttleAction.StepForward => map.Step(slot, 1),
                SequenceShuttleAction.SetLoop when map.Loop != command.LoopEnabled!.Value =>
                    map.SetLoop(slot, command.LoopEnabled.Value),
                _ => throw new InvalidOperationException(
                    "Sequence shuttle action is unchanged or incompatible with the current state.")
            };

            if (next.Generation != map.Generation &&
                next.State != SequenceRuntimeTransportState.Stopped)
            {
                // The client cannot observe a newer map until native decoder
                // generations have joined their old reads and confirmed preroll.
                lock (_sequenceGate) _sequenceShuttleMap = null;
                var receipt = RepositionSequenceMedia(command.Preparation.SequenceId,
                    command.Preparation.AuthoringRevision,
                    new SequenceRuntimePosition(next.StartTick, next.Generation, 0));
                if (!receipt.Prepared)
                    throw new InvalidOperationException(receipt.Failure?.Message ??
                        "Native sequence repreroll failed; the previous shuttle is not current.");
                next = next.WithConfirmedPrerollAnchor(currentProgramSlot());
            }

            lock (_sequenceGate)
            {
                if (_sequenceOwners is null) throw new InvalidOperationException(
                    "Native sequence owners were lost during shuttle transition.");
                _sequenceShuttleMap = next;
                _sequenceShuttleRevision = checked(_sequenceShuttleRevision + 1);
                return ObserveSequenceShuttle(command.Preparation, next, currentProgramSlot());
            }
        }
    }

    private SequenceRuntimePositionMap RequireSequenceShuttle(
        SequencePreparationReference reference, string runtimeHostInstanceId,
        Identity authorityStateId, Revision authorityRevision)
    {
        var prepared = _sequenceOwners;
        if (prepared is null || _sequenceShuttleMap is not { } map ||
            reference.RuntimeHostInstanceId != runtimeHostInstanceId ||
            reference.SequenceId != prepared.SequenceId ||
            reference.AuthoringRevision != prepared.AuthoringRevision ||
            reference.PreparedExecutionId != prepared.PreparedExecution.ToString() ||
            reference.Generation != prepared.InitialGeneration ||
            prepared.AuthorityStateId != authorityStateId ||
            prepared.AuthorityRevision != authorityRevision.Value ||
            map.SequenceId != reference.SequenceId ||
            map.AuthoringRevision != reference.AuthoringRevision)
            throw new InvalidOperationException(
                "The staged sequence, RuntimeHost epoch or committed Control authority changed.");
        return map;
    }

    private SequenceShuttleObservation ObserveSequenceShuttle(
        SequencePreparationReference reference, SequenceRuntimePositionMap map, ulong slot)
    {
        var inRange = map.TryMap(slot, out var position);
        var state = map.State switch
        {
            SequenceRuntimeTransportState.Stopped => "STOPPED",
            SequenceRuntimeTransportState.Paused => "PAUSED",
            _ when inRange => "PLAYING",
            _ => "ENDED"
        };
        var currentTick = inRange ? position.Tick :
            map.State == SequenceRuntimeTransportState.Stopped ? 0 : map.DurationTicks - 1;
        var pass = inRange ? position.LoopPass : 0;
        var prepared = _sequenceOwners;
        var nativeReady = prepared is not null &&
            prepared.Generation == map.Generation && prepared.LoopPass == pass &&
            prepared.Owners.All(owner =>
                owner.Decode.IsPrepared && owner.Decode.Snapshot.Failure is null);
        var detail = nativeReady
            ? "Native decoder queues are prepared at the current transport generation; no Preview or Program output is available."
            : "Native frame generations require a new qualified preroll; no Preview or Program output is available.";
        var result = new SequenceShuttleObservation(
            SequenceShuttleContract.Version, reference, _sequenceShuttleRevision,
            map.Generation, pass, state, currentTick, slot, map.Loop,
            nativeReady, "UNAVAILABLE", detail);
        result.Validate();
        return result;
    }
}
