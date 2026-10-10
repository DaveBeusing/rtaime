// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.RuntimeHost;

/// <summary>
/// O(1), allocation-free Program cadence accounting. It observes scheduler boundary times and cumulative
/// output backpressure/rejection counters; it never participates in scheduling or command authority.
/// </summary>
public sealed class RuntimeFrameDropCounter
{
	private const double OutputRateSmoothingFactor = 0.2;
	private TimeSpan? _lastBoundaryObservedAt;
	private ulong _schedulerDroppedFrames;
	private double? _outputFramesPerSecond;

	public double? OutputFramesPerSecond => _outputFramesPerSecond;

	public ulong Observe(
		TimeSpan boundaryObservedAt,
		TimeSpan expectedFramePeriod,
		ulong outputBackpressure = 0,
		ulong outputRejected = 0)
	{
		if (boundaryObservedAt < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(boundaryObservedAt));
		if (expectedFramePeriod <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(expectedFramePeriod));

		if (_lastBoundaryObservedAt is { } previous)
		{
			if (boundaryObservedAt < previous)
				throw new ArgumentException("Boundary observations must be monotonic.", nameof(boundaryObservedAt));

			var intervalTicks = boundaryObservedAt.Ticks - previous.Ticks;
			if (intervalTicks > 0)
			{
				var instantaneousFramesPerSecond = TimeSpan.TicksPerSecond / (double)intervalTicks;
				_outputFramesPerSecond = _outputFramesPerSecond is { } smoothed
					? smoothed + (OutputRateSmoothingFactor * (instantaneousFramesPerSecond - smoothed))
					: instantaneousFramesPerSecond;
			}

			var elapsedPeriods = intervalTicks / expectedFramePeriod.Ticks;
			if (elapsedPeriods > 1)
				_schedulerDroppedFrames = SaturatingAdd(_schedulerDroppedFrames, checked((ulong)(elapsedPeriods - 1)));
		}

		_lastBoundaryObservedAt = boundaryObservedAt;
		return SaturatingAdd(
			_schedulerDroppedFrames,
			SaturatingAdd(outputBackpressure, outputRejected));
	}

	/// <summary>Uses cumulative skipped scheduler slots; lateness and output reasons remain separate.</summary>
	public ulong ObserveScheduled(
		TimeSpan boundaryObservedAt,
		ulong missedSchedulerSlots,
		ulong outputBackpressure = 0,
		ulong outputRejected = 0)
	{
		if (boundaryObservedAt < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(boundaryObservedAt));
		if (missedSchedulerSlots < _schedulerDroppedFrames)
			throw new ArgumentException("Missed-slot evidence must be cumulative.", nameof(missedSchedulerSlots));
		if (_lastBoundaryObservedAt is { } previous && boundaryObservedAt <= previous)
			throw new ArgumentException("Scheduled boundaries must be strictly increasing.", nameof(boundaryObservedAt));

		// Reuse only the cadence estimator; the schedule supplies loss rather than interval division.
		Observe(boundaryObservedAt, TimeSpan.MaxValue);
		_schedulerDroppedFrames = missedSchedulerSlots;
		return SaturatingAdd(missedSchedulerSlots, SaturatingAdd(outputBackpressure, outputRejected));
	}

	private static ulong SaturatingAdd(ulong left, ulong right) =>
		ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}
