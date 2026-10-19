// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Control.Contracts;

/// <summary>
/// Source/revision-fenced Native Preview shuttle staging. This capability only
/// controls preadmitted decoder positions. It cannot select Preview, take Program,
/// publish pixels or authorize an output sink.
/// </summary>
public static class SequenceShuttleContract
{
    public const string Version = "rtaime.sequence-shuttle.v1";
    public const int MaximumDetailLength = 512;
}

public enum SequenceShuttleAction
{
    Cue = 1,
    Play = 2,
    Pause = 3,
    Stop = 4,
    Seek = 5,
    StepBackward = 6,
    StepForward = 7,
    SetLoop = 8
}

/// <summary>
/// A command identifies the exact prepared Runtime owner and the last observed
/// shuttle revision; unacknowledged commands are never automatically retried.
/// Positions use 240000 integer authored ticks per second.
/// </summary>
public sealed record SequenceShuttleRequest(
    string Version, SequencePreparationReference Preparation,
    ulong ExpectedStateRevision, SequenceShuttleAction Action,
    long? PositionTicks = null, bool? LoopEnabled = null)
{
    public void Validate()
    {
        if (Version != SequenceShuttleContract.Version || Preparation is null)
            throw new ArgumentException("Unsupported or incomplete sequence shuttle capability.");
        Preparation.Validate();
        if (!Enum.IsDefined(Action) ||
            (Action is SequenceShuttleAction.Cue or SequenceShuttleAction.Seek) != PositionTicks.HasValue ||
            (Action is SequenceShuttleAction.SetLoop) != LoopEnabled.HasValue ||
            PositionTicks is < 0 or >= SequenceContract.MaximumTime)
            throw new ArgumentException("Sequence shuttle action, authored position or loop intent is invalid.");
    }
}

/// <summary>
/// An observed Runtime position and *real native staging* state, explicitly
/// separate from an output frame and from committed production authority.
/// </summary>
public sealed record SequenceShuttleObservation(
    string Version, SequencePreparationReference Preparation,
    ulong StateRevision, ulong DecodeGeneration, ulong LoopPass,
    string State, long PositionTicks, ulong RuntimeProgramSlot, bool Loop,
    bool NativePrepared, string OutputState, string Detail)
{
    public void Validate()
    {
        if (Version != SequenceShuttleContract.Version || Preparation is null)
            throw new InvalidDataException("Unsupported or unbound shuttle observation.");
        Preparation.Validate();
        if (DecodeGeneration == 0 || State is not ("CUED" or "PAUSED" or "PLAYING" or "STOPPED" or "ENDED") ||
            PositionTicks < 0 || PositionTicks >= SequenceContract.MaximumTime ||
            OutputState != "UNAVAILABLE" || Detail is null ||
            Detail.Length > SequenceShuttleContract.MaximumDetailLength)
            throw new InvalidDataException("Shuttle evidence cannot assert a fabricated Preview or Program picture.");
    }
}
