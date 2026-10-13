// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Diagnostics;

namespace rtaime.Runtime;

public enum TimingQualificationState
{
	Recovering = 1,
	Healthy = 2,
	Degraded = 3,
	Unstable = 4,
	Lost = 5
}

public sealed record TimingQualificationThresholds(
	TimeSpan ExpectedFramePeriod,
	TimeSpan MaximumAbsoluteJitter,
	TimeSpan MaximumProcessingDuration,
	int RetainedSampleCapacity = 512)
{
	public void Validate()
	{
		if (ExpectedFramePeriod <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(ExpectedFramePeriod));
		if (MaximumAbsoluteJitter < TimeSpan.Zero || MaximumAbsoluteJitter >= ExpectedFramePeriod)
			throw new ArgumentOutOfRangeException(nameof(MaximumAbsoluteJitter));
		if (MaximumProcessingDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(MaximumProcessingDuration));
		if (RetainedSampleCapacity < 8 || RetainedSampleCapacity > 65_536)
			throw new ArgumentOutOfRangeException(nameof(RetainedSampleCapacity));
	}
}

public readonly record struct TimingBoundaryObservation(
	ulong SequenceNumber,
	TimeSpan ObservedAt,
	TimeSpan Interval,
	TimeSpan AbsoluteJitter,
	TimeSpan ProcessingDuration,
	bool SequenceContinuous,
	bool WithinJitterBudget,
	bool WithinProcessingBudget);

public sealed record TimingQualificationSnapshot(
	TimingQualificationState State,
	ulong TotalBoundaries,
	ulong SequenceDiscontinuities,
	ulong JitterViolations,
	ulong ProcessingViolations,
	int ConsecutiveViolations,
	TimeSpan MaximumObservedJitter,
	TimeSpan MaximumObservedProcessingDuration,
	ulong? LastSequenceNumber,
	TimeSpan? LastObservedAt,
	IReadOnlyList<TimingBoundaryObservation> Samples);

/// <summary>
/// Bounded, monotonic timing evidence collector for committed Runtime boundaries. It is observational only and never
/// changes production authority, routing or execution plans.
/// </summary>
public sealed class RuntimeTimingQualificationProbe
{
	private const int UnstableViolationThreshold = 3;
	private const int LostFramePeriods = 3;

	private readonly object _gate = new();
	private readonly object _pipelineGate = new();
	private readonly RuntimePipelineTimingSample[] _pipelineSamples;
	private int _pipelineCount;
	private int _pipelineNext;
	private ulong _pipelineRecorded;
	private ulong _pipelineOverwritten;
	private long _pipelineDropped;
	private readonly TimingQualificationThresholds _thresholds;
	private readonly TimingBoundaryObservation[] _samples;
	private int _sampleCount;
	private int _nextSampleIndex;
	private ulong _totalBoundaries;
	private ulong _sequenceDiscontinuities;
	private ulong _jitterViolations;
	private ulong _processingViolations;
	private int _consecutiveViolations;
	private TimeSpan _maximumObservedJitter;
	private TimeSpan _maximumObservedProcessingDuration;
	private ulong? _lastSequenceNumber;
	private TimeSpan? _lastObservedAt;

	public RuntimeTimingQualificationProbe(TimingQualificationThresholds thresholds)
	{
		_thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
		_thresholds.Validate();
		_samples = new TimingBoundaryObservation[_thresholds.RetainedSampleCapacity];
		_pipelineSamples = new RuntimePipelineTimingSample[_thresholds.RetainedSampleCapacity];
	}

	public TimingQualificationThresholds Thresholds => _thresholds;

	public TimingQualificationSnapshot RecordBoundary(
		ulong sequenceNumber,
		TimeSpan observedAt,
		TimeSpan processingDuration)
	{
		ValidateObservation(observedAt, processingDuration);
		lock (_gate)
		{
			RecordBoundaryUnsafe(sequenceNumber, observedAt, processingDuration);
			return CreateSnapshot(observedAt);
		}
	}

	/// <summary>Records the same evidence without materializing retained samples on the Program path.</summary>
	public TimingQualificationState ObserveBoundary(
		ulong sequenceNumber,
		TimeSpan observedAt,
		TimeSpan processingDuration)
	{
		ValidateObservation(observedAt, processingDuration);
		lock (_gate)
		{
			RecordBoundaryUnsafe(sequenceNumber, observedAt, processingDuration);
			return EvaluateState(observedAt);
		}
	}

	private static void ValidateObservation(TimeSpan observedAt, TimeSpan processingDuration)
	{
		if (observedAt < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(observedAt));
		if (processingDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(processingDuration));

	}

	private void RecordBoundaryUnsafe(ulong sequenceNumber, TimeSpan observedAt, TimeSpan processingDuration)
	{
		if (_lastObservedAt is { } previousObservedAt && observedAt < previousObservedAt)
			throw new ArgumentException("Timing observations must be monotonic.", nameof(observedAt));

		var interval = _lastObservedAt is { } previous
			? observedAt - previous
			: TimeSpan.Zero;
		var absoluteJitter = _lastObservedAt is null
			? TimeSpan.Zero
			: Absolute(interval - _thresholds.ExpectedFramePeriod);
		var sequenceContinuous = _lastSequenceNumber is null ||
			(_lastSequenceNumber.Value != ulong.MaxValue && sequenceNumber == _lastSequenceNumber.Value + 1);
		var withinJitterBudget = _lastObservedAt is null || absoluteJitter <= _thresholds.MaximumAbsoluteJitter;
		var withinProcessingBudget = processingDuration <= _thresholds.MaximumProcessingDuration;

		if (!sequenceContinuous)
			_sequenceDiscontinuities++;
		if (!withinJitterBudget)
			_jitterViolations++;
		if (!withinProcessingBudget)
			_processingViolations++;

		var violated = !sequenceContinuous || !withinJitterBudget || !withinProcessingBudget;
		_consecutiveViolations = violated ? _consecutiveViolations + 1 : 0;
		if (absoluteJitter > _maximumObservedJitter)
			_maximumObservedJitter = absoluteJitter;
		if (processingDuration > _maximumObservedProcessingDuration)
			_maximumObservedProcessingDuration = processingDuration;

		var observation = new TimingBoundaryObservation(
			sequenceNumber,
			observedAt,
			interval,
			absoluteJitter,
			processingDuration,
			sequenceContinuous,
			withinJitterBudget,
			withinProcessingBudget);
		_samples[_nextSampleIndex] = observation;
		_nextSampleIndex = (_nextSampleIndex + 1) % _samples.Length;
		_sampleCount = Math.Min(_sampleCount + 1, _samples.Length);
		_totalBoundaries++;
		_lastSequenceNumber = sequenceNumber;
		_lastObservedAt = observedAt;

	}

	public TimingQualificationSnapshot Snapshot(TimeSpan observedAt)
	{
		if (observedAt < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(observedAt));
		lock (_gate)
			return CreateSnapshot(observedAt);
	}

	private TimingQualificationSnapshot CreateSnapshot(TimeSpan observedAt)
	{
		var state = EvaluateState(observedAt);
		var retained = new TimingBoundaryObservation[_sampleCount];
		var start = _sampleCount == _samples.Length ? _nextSampleIndex : 0;
		for (var index = 0; index < _sampleCount; index++)
			retained[index] = _samples[(start + index) % _samples.Length];

		return new TimingQualificationSnapshot(
			state,
			_totalBoundaries,
			_sequenceDiscontinuities,
			_jitterViolations,
			_processingViolations,
			_consecutiveViolations,
			_maximumObservedJitter,
			_maximumObservedProcessingDuration,
			_lastSequenceNumber,
			_lastObservedAt,
			new ReadOnlyCollection<TimingBoundaryObservation>(retained));
	}

	private TimingQualificationState EvaluateState(TimeSpan observedAt)
	{
		if (_totalBoundaries == 0)
			return TimingQualificationState.Recovering;

		if (_lastObservedAt is { } lastObservedAt)
		{
			var lostAfter = TimeSpan.FromTicks(checked(_thresholds.ExpectedFramePeriod.Ticks * LostFramePeriods));
			if (observedAt - lastObservedAt > lostAfter)
				return TimingQualificationState.Lost;
		}

		if (_sequenceDiscontinuities > 0 || _consecutiveViolations >= UnstableViolationThreshold)
			return TimingQualificationState.Unstable;
		if (_jitterViolations > 0 || _processingViolations > 0)
			return TimingQualificationState.Degraded;
		return TimingQualificationState.Healthy;
	}

	public RuntimePipelineTimingScope MeasureStage(RuntimePipelineTimingStage stage, ulong sequenceNumber, long? startedAt = null) =>
		new(this, stage, sequenceNumber, startedAt ?? Stopwatch.GetTimestamp());

	public bool TryObserveStage(RuntimePipelineTimingStage stage, ulong sequenceNumber, TimeSpan duration)
	{
		if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
		if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
		// A diagnostic snapshot must never stall the Program writer.
		if (!Monitor.TryEnter(_pipelineGate))
		{
			Interlocked.Increment(ref _pipelineDropped);
			return false;
		}
		try
		{
			_pipelineSamples[_pipelineNext] = new RuntimePipelineTimingSample(stage, sequenceNumber, duration.TotalMilliseconds);
			_pipelineNext = (_pipelineNext + 1) % _pipelineSamples.Length;
			if (_pipelineCount == _pipelineSamples.Length)
			{
				if (_pipelineOverwritten < ulong.MaxValue) _pipelineOverwritten++;
			}
			else _pipelineCount++;
			if (_pipelineRecorded < ulong.MaxValue) _pipelineRecorded++;
			return true;
		}
		finally { Monitor.Exit(_pipelineGate); }
	}

	public RuntimePipelineTimingSnapshot PipelineSnapshot()
	{
		RuntimePipelineTimingSample[] samples;
		ulong recorded, overwritten;
		lock (_pipelineGate)
		{
			samples = new RuntimePipelineTimingSample[_pipelineCount];
			var start = _pipelineCount == _pipelineSamples.Length ? _pipelineNext : 0;
			for (var index = 0; index < samples.Length; index++)
				samples[index] = _pipelineSamples[(start + index) % _pipelineSamples.Length];
			recorded = _pipelineRecorded;
			overwritten = _pipelineOverwritten;
		}
		// Sorting and report allocation belong exclusively to the snapshot caller.
		var stages = new List<RuntimePipelineTimingSummary>();
		foreach (var stage in Enum.GetValues<RuntimePipelineTimingStage>())
		{
			var values = samples.Where(sample => sample.Stage == stage).Select(sample => sample.Milliseconds).Order().ToArray();
			if (values.Length == 0) continue;
			double Percentile(double percentile) => values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
			stages.Add(new RuntimePipelineTimingSummary(stage, stage == RuntimePipelineTimingStage.SchedulerStartLateness ? "SCHEDULER_LATENESS" :
				stage == RuntimePipelineTimingStage.PresentationLateness ? "PRESENTATION_LATENESS" : "CPU_WALL", values.Length,
				Percentile(0.50), Percentile(0.95), Percentile(0.99), values[^1]));
		}
		return new RuntimePipelineTimingSnapshot(_pipelineSamples.Length, recorded, overwritten,
			checked((ulong)Interlocked.Read(ref _pipelineDropped)), samples, stages);
	}

	public void ResetPipelineSamples()
	{
		lock (_pipelineGate)
		{
			_pipelineCount = _pipelineNext = 0;
			_pipelineRecorded = _pipelineOverwritten = 0;
			Interlocked.Exchange(ref _pipelineDropped, 0);
		}
	}

	private static TimeSpan Absolute(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}

public enum RuntimePipelineTimingStage
{
	CompleteProgramBoundary = 1,
	SchedulerStartLateness = 2,
	PresentationLateness = 3,
	ServiceBoundaryAttempt = 4,
	ExecutionLockWait = 5,
	ExecutionLockHold = 6,
	CaptureLockWait = 7,
	CaptureLockHold = 8,
	MetadataLockWait = 9,
	MetadataLockHold = 10,
	InputAdmission = 11,
	Composite = 12,
	Readback = 13,
	MonitoringExport = 14,
	ConsumerSubmission = 15,
	OutputSubmission = 16
}

public readonly record struct RuntimePipelineTimingSample(RuntimePipelineTimingStage Stage, ulong SequenceNumber, double Milliseconds);
public sealed record RuntimePipelineTimingSummary(RuntimePipelineTimingStage Stage, string MeasurementKind,
	int RetainedSamples, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds);
public sealed record RuntimePipelineTimingSnapshot(int Capacity, ulong RecordedSamples, ulong OverwrittenSamples,
	ulong ContentionDroppedSamples, IReadOnlyList<RuntimePipelineTimingSample> Samples, IReadOnlyList<RuntimePipelineTimingSummary> Stages);

/// <summary>Value-type measurement lifetime; no diagnostic allocation or waiting occurs when the scope completes.</summary>
public readonly struct RuntimePipelineTimingScope : IDisposable
{
	private readonly RuntimeTimingQualificationProbe? _probe;
	private readonly RuntimePipelineTimingStage _stage;
	private readonly ulong _sequence;
	private readonly long _startedAt;
	internal RuntimePipelineTimingScope(RuntimeTimingQualificationProbe probe, RuntimePipelineTimingStage stage, ulong sequence, long startedAt)
	{
		_probe = probe; _stage = stage; _sequence = sequence; _startedAt = startedAt;
	}
	public void Dispose() => _probe?.TryObserveStage(_stage, _sequence, Stopwatch.GetElapsedTime(_startedAt));
}
