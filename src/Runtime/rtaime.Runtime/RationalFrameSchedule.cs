// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Runtime;

/// <summary>
/// Single-writer, constant-space cadence arithmetic. Deadlines are rounded up once from the epoch,
/// never accumulated from a rounded frame period. Media sequence and audio positions remain Runtime-owned.
/// </summary>
public sealed class RationalFrameSchedule
{
	private FrameRate _rate;
	private TimeSpan _epoch;
	private TimeSpan _lastObservedAt;
	private ulong _nextSlot;

	public RationalFrameSchedule(FrameRate rate, TimeSpan epoch) => Reset(rate, epoch);

	public TimeSpan NextDeadline => Deadline(_nextSlot);

	public void Reset(FrameRate rate, TimeSpan epoch)
	{
		if (rate.Numerator <= 0 || rate.Denominator <= 0 ||
			(Int128)rate.Denominator * TimeSpan.TicksPerSecond < rate.Numerator)
			throw new ArgumentOutOfRangeException(nameof(rate));
		if (epoch < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(epoch));
		_rate = rate;
		_epoch = epoch;
		_lastObservedAt = epoch;
		_nextSlot = 1;
	}

	public TimeSpan Deadline(ulong slot)
	{
		// Int128 covers UInt64 slots multiplied by the supported edit-rate denominator and tick scale.
		var numerator = checked((Int128)slot * _rate.Denominator * TimeSpan.TicksPerSecond);
		var ticks = checked((long)((numerator + _rate.Numerator - 1) / _rate.Numerator));
		return TimeSpan.FromTicks(checked(_epoch.Ticks + ticks));
	}

	public bool TryTake(TimeSpan observedAt, out FrameScheduleOpportunity opportunity)
	{
		if (observedAt < _lastObservedAt)
			throw new ArgumentException("Schedule observations must be monotonic; reset explicitly on resync.", nameof(observedAt));
		_lastObservedAt = observedAt;
		var elapsedTicks = observedAt.Ticks - _epoch.Ticks;
		var latest = checked((ulong)((Int128)elapsedTicks * _rate.Numerator /
			((Int128)_rate.Denominator * TimeSpan.TicksPerSecond)));
		if (latest < _nextSlot)
		{
			opportunity = default;
			return false;
		}

		var due = Deadline(latest);
		var next = Deadline(checked(latest + 1));
		opportunity = new FrameScheduleOpportunity(latest, due, next, latest - _nextSlot, observedAt - due);
		_nextSlot = checked(latest + 1);
		return true;
	}
}

public readonly record struct FrameScheduleOpportunity(
	ulong Slot,
	TimeSpan Deadline,
	TimeSpan PresentationDeadline,
	ulong MissedSlots,
	TimeSpan StartLateness);
