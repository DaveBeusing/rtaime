using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Runtime;

public interface IRuntimeMediaSource
{
    MediaSourceId SourceId { get; }
    VideoFormat Format { get; }
    FrameDescriptor ReadFrame(ulong sequenceNumber);
}

public interface IRuntimeMediaOutput
{
    MediaSinkId SinkId { get; }
    VideoFormat Format { get; }
    void WriteFrame(FrameDescriptor frame);
}

public sealed class RuntimeMediaEndpointRegistry
{
    private readonly IReadOnlyDictionary<MediaSourceId, IRuntimeMediaSource> _sources;
    private readonly IReadOnlyDictionary<MediaSinkId, IRuntimeMediaOutput> _outputs;

    public RuntimeMediaEndpointRegistry(
        IReadOnlyList<IRuntimeMediaSource> sources,
        IReadOnlyList<IRuntimeMediaOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(outputs);

        if (sources.Any(source => source is null))
            throw new ArgumentException("Runtime media sources must not contain null values.", nameof(sources));
        if (outputs.Any(output => output is null))
            throw new ArgumentException("Runtime media outputs must not contain null values.", nameof(outputs));
        if (sources.Select(source => source.SourceId).Distinct().Count() != sources.Count)
            throw new ArgumentException("Runtime media source identities must be unique.", nameof(sources));
        if (outputs.Select(output => output.SinkId).Distinct().Count() != outputs.Count)
            throw new ArgumentException("Runtime media output identities must be unique.", nameof(outputs));

        _sources = new ReadOnlyDictionary<MediaSourceId, IRuntimeMediaSource>(
            sources.ToDictionary(source => source.SourceId));
        _outputs = new ReadOnlyDictionary<MediaSinkId, IRuntimeMediaOutput>(
            outputs.ToDictionary(output => output.SinkId));
    }

    public bool TryGetSource(MediaSourceId sourceId, out IRuntimeMediaSource? source) =>
        _sources.TryGetValue(sourceId, out source);

    public bool TryGetOutput(MediaSinkId sinkId, out IRuntimeMediaOutput? output) =>
        _outputs.TryGetValue(sinkId, out output);
}

public sealed record RuntimeMediaEmission(
    MediaSinkId SinkId,
    MediaSourceId SourceId,
    FrameDescriptor Frame);

public enum RuntimeMediaStepStatus
{
    Emitted = 1,
    Rejected = 2
}

public sealed class RuntimeMediaStepResult
{
    private readonly ReadOnlyCollection<RuntimeMediaEmission> _emissions;

    private RuntimeMediaStepResult(
        RuntimeMediaStepStatus status,
        ulong sequenceNumber,
        Revision executionRevision,
        ExecutionInstanceId? executionInstanceId,
        IReadOnlyList<RuntimeMediaEmission> emissions,
        Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RuntimeMediaStepStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RuntimeMediaStepStatus.Emitted && executionInstanceId is null)
            throw new ArgumentException("Successful media steps require an execution instance identity.", nameof(executionInstanceId));
        if (status == RuntimeMediaStepStatus.Emitted && failure is not null)
            throw new ArgumentException("Successful media steps must not carry a failure.", nameof(failure));
        if (status == RuntimeMediaStepStatus.Emitted && emissions.Count == 0)
            throw new ArgumentException("Successful media steps require at least one emitted frame.", nameof(emissions));
        if (status == RuntimeMediaStepStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected media steps require a failure.", nameof(failure));
        if (status == RuntimeMediaStepStatus.Rejected && emissions.Count != 0)
            throw new ArgumentException("Rejected media steps must not expose emissions.", nameof(emissions));

        Status = status;
        SequenceNumber = sequenceNumber;
        ExecutionRevision = executionRevision;
        ExecutionInstanceId = executionInstanceId;
        _emissions = Array.AsReadOnly(emissions.ToArray());
        Failure = failure;
    }

    public RuntimeMediaStepStatus Status { get; }
    public ulong SequenceNumber { get; }
    public Revision ExecutionRevision { get; }
    public ExecutionInstanceId? ExecutionInstanceId { get; }
    public IReadOnlyList<RuntimeMediaEmission> Emissions => _emissions;
    public Failure? Failure { get; }
    public bool Succeeded => Status == RuntimeMediaStepStatus.Emitted;

    internal static RuntimeMediaStepResult Emitted(
        ulong sequenceNumber,
        Revision executionRevision,
        ExecutionInstanceId executionInstanceId,
        IReadOnlyList<RuntimeMediaEmission> emissions) =>
        new(
            RuntimeMediaStepStatus.Emitted,
            sequenceNumber,
            executionRevision,
            executionInstanceId,
            emissions,
            null);

    internal static RuntimeMediaStepResult Rejected(
        ulong sequenceNumber,
        Revision executionRevision,
        Failure failure) =>
        new(
            RuntimeMediaStepStatus.Rejected,
            sequenceNumber,
            executionRevision,
            null,
            Array.Empty<RuntimeMediaEmission>(),
            failure);
}

public sealed class CommittedMediaRuntime
{
    private readonly object _gate = new();
    private readonly TransactionalRuntime _runtime;
    private readonly RuntimeMediaEndpointRegistry _endpoints;
    private readonly IRuntimeClock _clock;
    private readonly List<RuntimeObservation> _observations = new();
    private ulong _nextSequenceNumber;

    public CommittedMediaRuntime(
        TransactionalRuntime runtime,
        RuntimeMediaEndpointRegistry endpoints,
        IRuntimeClock? clock = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _clock = clock ?? new SystemRuntimeClock();
    }

    public ulong NextSequenceNumber
    {
        get
        {
            lock (_gate)
                return _nextSequenceNumber;
        }
    }

    public IReadOnlyList<RuntimeObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<RuntimeObservation>(_observations.ToArray());
        }
    }

    public RuntimeMediaStepResult ProcessNextFrameBoundary()
    {
        lock (_gate)
        {
            var activeExecution = _runtime.ActiveExecution;
            if (activeExecution is null)
            {
                return Reject(
                    Revision.Initial,
                    new Failure(
                        "runtime.media.execution_missing",
                        "No committed execution is active at the requested frame boundary."));
            }

            var sequenceNumber = _nextSequenceNumber;
            var staged = new List<StagedEmission>();

            foreach (var binding in activeExecution.PreparedExecution.Bindings
                         .OrderBy(binding => binding.LogicalNodeId.ToString(), StringComparer.Ordinal))
            {
                if (binding.MediaSourceId is null || binding.MediaSinkId is null)
                {
                    return Reject(
                        activeExecution.ExecutionRevision,
                        new Failure(
                            "runtime.media.binding_incomplete",
                            "Committed media bindings require both a media source and media sink identity."));
                }

                var sourceId = binding.MediaSourceId.Value;
                var sinkId = binding.MediaSinkId.Value;

                if (!_endpoints.TryGetSource(sourceId, out var source) || source is null)
                {
                    return Reject(
                        activeExecution.ExecutionRevision,
                        new Failure(
                            "runtime.media.source_missing",
                            $"No runtime media source is registered for '{sourceId}'."));
                }

                if (!_endpoints.TryGetOutput(sinkId, out var output) || output is null)
                {
                    return Reject(
                        activeExecution.ExecutionRevision,
                        new Failure(
                            "runtime.media.output_missing",
                            $"No runtime media output is registered for '{sinkId}'."));
                }

                if (source.Format != output.Format)
                {
                    return Reject(
                        activeExecution.ExecutionRevision,
                        new Failure(
                            "runtime.media.format_mismatch",
                            "Runtime media source and output formats must match before a frame boundary is processed."));
                }

                FrameDescriptor frame;
                try
                {
                    frame = source.ReadFrame(sequenceNumber)
                        ?? throw new InvalidOperationException("Runtime media source returned no frame.");
                }
                catch (Exception exception)
                {
                    return Reject(
                        activeExecution.ExecutionRevision,
                        new Failure(
                            "runtime.media.source_exception",
                            $"Runtime media source failed unexpectedly: {exception.GetType().Name}."));
                }

                var frameFailure = ValidateFrame(source, sequenceNumber, frame);
                if (frameFailure is not null)
                    return Reject(activeExecution.ExecutionRevision, frameFailure.Value);

                staged.Add(new StagedEmission(output, sinkId, sourceId, frame));
            }

            foreach (var emission in staged)
            {
                try
                {
                    emission.Output.WriteFrame(emission.Frame);
                }
                catch (Exception exception)
                {
                    return Reject(
                        activeExecution.ExecutionRevision,
                        new Failure(
                            "runtime.media.output_exception",
                            $"Runtime media output failed unexpectedly: {exception.GetType().Name}."));
                }
            }

            var emissions = staged
                .Select(emission => new RuntimeMediaEmission(
                    emission.SinkId,
                    emission.SourceId,
                    emission.Frame))
                .ToArray();

            Observe(activeExecution.ExecutionRevision, "runtime.media.frame_boundary", null);

            if (_nextSequenceNumber == ulong.MaxValue)
            {
                return Reject(
                    activeExecution.ExecutionRevision,
                    new Failure(
                        "runtime.media.sequence_exhausted",
                        "Runtime media sequence cannot advance beyond UInt64.MaxValue."));
            }

            _nextSequenceNumber++;

            return RuntimeMediaStepResult.Emitted(
                sequenceNumber,
                activeExecution.ExecutionRevision,
                activeExecution.ExecutionInstanceId,
                emissions);
        }
    }

    private static Failure? ValidateFrame(
        IRuntimeMediaSource source,
        ulong expectedSequenceNumber,
        FrameDescriptor frame)
    {
        if (frame.SourceId != source.SourceId)
        {
            return new Failure(
                "runtime.media.source_identity_mismatch",
                "Runtime media source emitted a frame with an unexpected source identity.");
        }

        if (frame.Surface.Format != source.Format)
        {
            return new Failure(
                "runtime.media.frame_format_mismatch",
                "Runtime media source emitted a frame whose format does not match the registered source format.");
        }

        if (frame.Timing.SequenceNumber != expectedSequenceNumber)
        {
            return new Failure(
                "runtime.media.sequence_mismatch",
                $"Runtime media source emitted sequence '{frame.Timing.SequenceNumber}' while '{expectedSequenceNumber}' was requested.");
        }

        return null;
    }

    private RuntimeMediaStepResult Reject(Revision executionRevision, Failure failure)
    {
        Observe(executionRevision, "runtime.media.frame_rejected", failure);
        return RuntimeMediaStepResult.Rejected(_nextSequenceNumber, executionRevision, failure);
    }

    private void Observe(Revision executionRevision, string code, Failure? failure) =>
        _observations.Add(new RuntimeObservation(
            RuntimeContractVersion.Current,
            executionRevision,
            _clock.GetUtcNow(),
            code,
            failure));

    private sealed record StagedEmission(
        IRuntimeMediaOutput Output,
        MediaSinkId SinkId,
        MediaSourceId SourceId,
        FrameDescriptor Frame);
}

public sealed class InMemoryRuntimeResourceReservationManager : IRuntimeResourceReservationManager
{
    private readonly object _gate = new();
    private readonly Dictionary<Identity, PreparedExecutionId> _reservations = new();

    public RuntimeResourceReservationResult Reserve(PreparedExecutionContract preparedExecution)
    {
        ArgumentNullException.ThrowIfNull(preparedExecution);

        lock (_gate)
        {
            var resourceKey = string.Join(
                ";",
                preparedExecution.Bindings
                    .Select(binding => binding.Resource.ResourceId.ToString())
                    .OrderBy(value => value, StringComparer.Ordinal));

            var reservationId = RuntimeIdentity.Create(
                "runtime-in-memory-reservation",
                preparedExecution.PreparedExecutionId.ToString(),
                resourceKey);

            if (_reservations.ContainsKey(reservationId))
            {
                return RuntimeResourceReservationResult.Rejected(new Failure(
                    "runtime.reservation.identity_conflict",
                    "The deterministic in-memory reservation identity is already active."));
            }

            _reservations.Add(reservationId, preparedExecution.PreparedExecutionId);
            return RuntimeResourceReservationResult.Reserved(reservationId);
        }
    }

    public RuntimeResourceReleaseResult Release(Identity reservationId)
    {
        if (reservationId.IsEmpty)
            throw new ArgumentException("Reservation identity must not be empty.", nameof(reservationId));

        lock (_gate)
        {
            if (_reservations.Remove(reservationId))
                return RuntimeResourceReleaseResult.Released();

            return RuntimeResourceReleaseResult.Rejected(new Failure(
                "runtime.reservation.unknown",
                "The in-memory reservation identity is not active."));
        }
    }
}
