// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;

namespace rtaime.Runtime;

public enum SequenceRuntimeTransportState { Stopped, Paused, Playing }

/// <summary>
/// An immutable mapping of the existing Runtime Program slot to one authored
/// sequence position. It does not own a timer, open media or change production.
/// The generation and pass jointly fence prepared decoder samples after seek/loop.
/// </summary>
public sealed class SequenceRuntimePositionMap
{
    private const long AuthoringTicksPerSecond = 240000;
    private const long MaximumTimeTicks = 24 * 60 * 60 * AuthoringTicksPerSecond;

    private SequenceRuntimePositionMap(Guid sequenceId, ulong authoringRevision,
        FrameRate programRate, long durationTicks, ulong anchorProgramSlot,
        long startTick, ulong generation, bool loop, SequenceRuntimeTransportState state)
    {
        SequenceId = sequenceId;
        AuthoringRevision = authoringRevision;
        ProgramRate = programRate;
        DurationTicks = durationTicks;
        AnchorProgramSlot = anchorProgramSlot;
        StartTick = startTick;
        Generation = generation;
        Loop = loop;
        State = state;
    }

    public Guid SequenceId { get; }
    public ulong AuthoringRevision { get; }
    public FrameRate ProgramRate { get; }
    public long DurationTicks { get; }
    public ulong AnchorProgramSlot { get; }
    public long StartTick { get; }
    public ulong Generation { get; }
    public bool Loop { get; }
    public SequenceRuntimeTransportState State { get; }

    public static SequenceRuntimePositionMap Prepare(Guid sequenceId, ulong authoringRevision,
        FrameRate programRate, long durationTicks, ulong programSlot, ulong generation)
    {
        if (sequenceId == Guid.Empty || authoringRevision == 0 || generation == 0 ||
            durationTicks <= 0 || durationTicks > MaximumTimeTicks ||
            programRate.Numerator is <= 0 or > 240000 ||
            programRate.Denominator is <= 0 or > 100000 ||
            (Int128)programRate.Numerator > (Int128)240 * programRate.Denominator)
            throw new ArgumentException("A valid, finite saved sequence and existing Program rate are required.");
        return new(sequenceId, authoringRevision, programRate, durationTicks,
            programSlot, 0, generation, false, SequenceRuntimeTransportState.Stopped);
    }

    public bool TryMap(ulong programSlot, out SequenceRuntimePosition position)
    {
        position = default;
        if (programSlot < AnchorProgramSlot || State == SequenceRuntimeTransportState.Stopped)
            return false;
        if (State == SequenceRuntimeTransportState.Paused)
        {
            position = new(StartTick, Generation, 0);
            return true;
        }
        var elapsed = (Int128)(programSlot - AnchorProgramSlot) *
            AuthoringTicksPerSecond * ProgramRate.Denominator / ProgramRate.Numerator;
        var total = (Int128)StartTick + elapsed;
        if (total >= DurationTicks && !Loop) return false;
        var passes = total / DurationTicks;
        if (passes > ulong.MaxValue)
            throw new OverflowException("Sequence loop generation would overflow.");
        position = new((long)(total % DurationTicks), Generation, (ulong)passes);
        return true;
    }

    public SequenceRuntimePositionMap Play(ulong programSlot)
    {
        if (State == SequenceRuntimeTransportState.Playing)
            return this;
        if (State == SequenceRuntimeTransportState.Stopped)
            return Reanchor(programSlot, 0, SequenceRuntimeTransportState.Playing);
        return Reanchor(programSlot, StartTick, SequenceRuntimeTransportState.Playing);
    }

    public SequenceRuntimePositionMap Pause(ulong programSlot)
    {
        if (State == SequenceRuntimeTransportState.Paused || State == SequenceRuntimeTransportState.Stopped)
            return this;
        if (!TryMap(programSlot, out var mapped))
            return Stop(programSlot);
        return Reanchor(programSlot, mapped.Tick, SequenceRuntimeTransportState.Paused);
    }

    public SequenceRuntimePositionMap Seek(ulong programSlot, long requestedTick)
    {
        if (requestedTick < 0 || requestedTick >= DurationTicks)
            throw new ArgumentOutOfRangeException(nameof(requestedTick),
                "Seek must target a tick inside the saved sequence.");
        if (programSlot < AnchorProgramSlot)
            throw new ArgumentException("Program slot cannot move backwards.", nameof(programSlot));
        return Reanchor(programSlot, requestedTick, State);
    }

    public SequenceRuntimePositionMap Stop(ulong programSlot)
    {
        if (programSlot < AnchorProgramSlot)
            throw new ArgumentException("Program slot cannot move backwards.", nameof(programSlot));
        return Reanchor(programSlot, 0, SequenceRuntimeTransportState.Stopped);
    }

    public SequenceRuntimePositionMap SetLoop(ulong programSlot, bool loop)
    {
        if (Loop == loop) return this;
        if (State == SequenceRuntimeTransportState.Playing &&
            !TryMap(programSlot, out _))
            throw new InvalidOperationException("A completed sequence requires an explicit Stop, Seek or new approved activation.");
        var tick = State == SequenceRuntimeTransportState.Playing
            ? GetPlayingTick(programSlot) : StartTick;
        var update = Reanchor(programSlot, tick, State);
        return new(update.SequenceId, update.AuthoringRevision, update.ProgramRate, update.DurationTicks,
            update.AnchorProgramSlot, update.StartTick, update.Generation, loop, update.State);
    }

    private long GetPlayingTick(ulong programSlot)
    {
        if (!TryMap(programSlot, out var mapped))
            throw new InvalidOperationException("The current Program slot is not inside an active sequence.");
        return mapped.Tick;
    }

    private SequenceRuntimePositionMap Reanchor(ulong programSlot, long tick, SequenceRuntimeTransportState state)
    {
        if (programSlot < AnchorProgramSlot)
            throw new ArgumentException("Program slot cannot move backwards.", nameof(programSlot));
        return new(SequenceId, AuthoringRevision, ProgramRate, DurationTicks, programSlot,
            tick, checked(Generation + 1), Loop, state);
    }
}

/// <summary>
/// Queue identity is (Runtime epoch, execution generation, loop pass, clip/source).
/// A previous pass cannot be reused after a loop, even at the same authoring tick.
/// </summary>
public readonly record struct SequenceRuntimePosition(long Tick, ulong Generation, ulong LoopPass);
