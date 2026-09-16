// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;

namespace rtaime.Runtime;

public enum TimingQualificationState
{
	Healthy = 1,
	Degraded = 2,
	Unstable = 3,
	Lost = 4
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
	}

	public TimingQualificationThresholds Thresholds => _thresholds;

	public TimingQualificationSnapshot RecordBoundary(
		ulong sequenceNumber,
		TimeSpan observedAt,
		TimeSpan processingDuration)
	{
		if (observedAt < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(observedAt));
		if (processingDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(processingDuration));

		lock (_gate)
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

			return CreateSnapshot(observedAt);
		}
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

	private static TimeSpan Absolute(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}
