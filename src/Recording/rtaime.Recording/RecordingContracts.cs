using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

public static class RecordingContractVersion
{
    public static CompatibilityVersion Current => new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported recording contract version {version}.");
    }
}

public readonly record struct RecordingSessionId
{
    public RecordingSessionId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Recording session identity must not be empty.", nameof(value));

        Value = value;
    }

    public Identity Value { get; }
    public static RecordingSessionId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct RecordingOutputId
{
    public RecordingOutputId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Recording output identity must not be empty.", nameof(value));

        Value = value;
    }

    public Identity Value { get; }
    public static RecordingOutputId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public sealed record RecordingOutputDescriptor
{
    public RecordingOutputDescriptor(
        RecordingOutputId outputId,
        MediaSinkId programSinkId,
        string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Recording output name is required.", nameof(name));

        OutputId = outputId;
        ProgramSinkId = programSinkId;
        Name = name.Trim();
    }

    public RecordingOutputId OutputId { get; }
    public MediaSinkId ProgramSinkId { get; }
    public string Name { get; }
}

public sealed record RecordingStartRequest
{
    public RecordingStartRequest(
        CompatibilityVersion version,
        RecordingSessionId sessionId,
        RecordingOutputDescriptor output)
    {
        RecordingContractVersion.EnsureSupported(version);
        Version = version;
        SessionId = sessionId;
        Output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public CompatibilityVersion Version { get; }
    public RecordingSessionId SessionId { get; }
    public RecordingOutputDescriptor Output { get; }
}

public sealed record RecordingProgramSample
{
    public RecordingProgramSample(
        CompatibilityVersion version,
        RecordingOutputId outputId,
        FrameDescriptor video,
        AudioBufferDescriptor? audio)
    {
        RecordingContractVersion.EnsureSupported(version);
        Version = version;
        OutputId = outputId;
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Audio = audio;
    }

    public CompatibilityVersion Version { get; }
    public RecordingOutputId OutputId { get; }
    public FrameDescriptor Video { get; }
    public AudioBufferDescriptor? Audio { get; }
    public ulong SequenceNumber => Video.Timing.SequenceNumber;
}

public enum RecordingLifecycleState
{
    Idle = 1,
    Recording = 2,
    Finalizing = 3,
    Completed = 4,
    Failed = 5
}

public enum RecordingStartStatus
{
    Started = 1,
    Rejected = 2,
    Failed = 3
}

public sealed record RecordingStartResult
{
    private RecordingStartResult(RecordingStartStatus status, Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RecordingStartStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RecordingStartStatus.Started && failure is not null)
            throw new ArgumentException("Successful recording start must not carry a failure.", nameof(failure));
        if (status != RecordingStartStatus.Started && failure is null)
            throw new ArgumentException("Unsuccessful recording start requires a failure.", nameof(failure));

        Status = status;
        Failure = failure;
    }

    public RecordingStartStatus Status { get; }
    public Failure? Failure { get; }
    public bool Succeeded => Status == RecordingStartStatus.Started;

    public static RecordingStartResult Started() => new(RecordingStartStatus.Started, null);
    public static RecordingStartResult Rejected(Failure failure) => new(RecordingStartStatus.Rejected, failure);
    public static RecordingStartResult Failed(Failure failure) => new(RecordingStartStatus.Failed, failure);
}

public enum RecordingStopStatus
{
    Stopped = 1,
    Noop = 2,
    Failed = 3
}

public sealed record RecordingStopResult
{
    private RecordingStopResult(RecordingStopStatus status, Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RecordingStopStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RecordingStopStatus.Failed && failure is null)
            throw new ArgumentException("Failed recording stop requires a failure.", nameof(failure));
        if (status != RecordingStopStatus.Failed && failure is not null)
            throw new ArgumentException("Successful/no-op recording stop must not carry a failure.", nameof(failure));

        Status = status;
        Failure = failure;
    }

    public RecordingStopStatus Status { get; }
    public Failure? Failure { get; }

    public static RecordingStopResult Stopped() => new(RecordingStopStatus.Stopped, null);
    public static RecordingStopResult Noop() => new(RecordingStopStatus.Noop, null);
    public static RecordingStopResult Failed(Failure failure) => new(RecordingStopStatus.Failed, failure);
}

public enum RecordingEnqueueStatus
{
    Accepted = 1,
    Dropped = 2,
    Rejected = 3
}

public sealed record RecordingEnqueueResult
{
    private RecordingEnqueueResult(RecordingEnqueueStatus status, Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RecordingEnqueueStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RecordingEnqueueStatus.Accepted && failure is not null)
            throw new ArgumentException("Accepted recording sample must not carry a failure.", nameof(failure));
        if (status != RecordingEnqueueStatus.Accepted && failure is null)
            throw new ArgumentException("Dropped/rejected recording sample requires a failure.", nameof(failure));

        Status = status;
        Failure = failure;
    }

    public RecordingEnqueueStatus Status { get; }
    public Failure? Failure { get; }
    public bool Accepted => Status == RecordingEnqueueStatus.Accepted;

    public static RecordingEnqueueResult AcceptedSample() => new(RecordingEnqueueStatus.Accepted, null);
    public static RecordingEnqueueResult Dropped(Failure failure) => new(RecordingEnqueueStatus.Dropped, failure);
    public static RecordingEnqueueResult Rejected(Failure failure) => new(RecordingEnqueueStatus.Rejected, failure);
}

public sealed record RecordingStatistics(
    ulong Accepted,
    ulong Written,
    ulong Dropped,
    ulong Rejected,
    ulong WriterFailures);

public sealed record RecordingObservation(
    UtcTimestamp Timestamp,
    string Code,
    string Message,
    RecordingSessionId? SessionId,
    RecordingOutputId? OutputId,
    ulong? SequenceNumber);

public sealed record RecordingSnapshot(
    RecordingLifecycleState State,
    RecordingSessionId? SessionId,
    RecordingOutputDescriptor? Output,
    RecordingStatistics Statistics,
    Failure? Failure);
