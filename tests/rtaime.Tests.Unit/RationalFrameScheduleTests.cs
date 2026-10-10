// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Runtime;

namespace rtaime.Tests.Unit;

public sealed class RationalFrameScheduleTests
{
	[Theory]
	[InlineData(50, 1, 4_320_000)]
	[InlineData(60_000, 1_001, 5_180_000)]
	public void Day_long_cadence_uses_exact_epoch_deadlines_without_drift(long numerator, long denominator, int count)
	{
		var schedule = new RationalFrameSchedule(new FrameRate(numerator, denominator), TimeSpan.FromSeconds(7));
		for (ulong slot = 1; slot <= (ulong)count; slot++)
		{
			var exact = (Int128)slot * denominator * TimeSpan.TicksPerSecond;
			var expected = checked((long)((exact + numerator - 1) / numerator)) + TimeSpan.FromSeconds(7).Ticks;
			Assert.Equal(expected, schedule.NextDeadline.Ticks);
			Assert.True(schedule.TryTake(schedule.NextDeadline, out var taken));
			Assert.Equal(slot, taken.Slot);
			Assert.Equal(0UL, taken.MissedSlots);
			Assert.Equal(TimeSpan.Zero, taken.StartLateness);
		}
	}

	[Theory]
	[InlineData(50, 1)]
	[InlineData(60_000, 1_001)]
	public void First_slot_early_repeated_and_delayed_wakes_have_unambiguous_accounting(long numerator, long denominator)
	{
		var schedule = new RationalFrameSchedule(new FrameRate(numerator, denominator), TimeSpan.Zero);
		Assert.False(schedule.TryTake(TimeSpan.Zero, out _));
		Assert.False(schedule.TryTake(schedule.NextDeadline - TimeSpan.FromTicks(1), out _));
		var firstDeadline = schedule.NextDeadline;
		Assert.True(schedule.TryTake(firstDeadline, out var first));
		Assert.Equal(0UL, first.MissedSlots);
		Assert.False(schedule.TryTake(firstDeadline, out _));

		var fourth = schedule.Deadline(4);
		Assert.True(schedule.TryTake(fourth - TimeSpan.FromTicks(1), out var third));
		Assert.Equal(3UL, third.Slot);
		Assert.Equal(1UL, third.MissedSlots);
		Assert.False(schedule.TryTake(fourth - TimeSpan.FromTicks(1), out _));
		Assert.True(schedule.TryTake(fourth, out var next));
		Assert.Equal(4UL, next.Slot);
		Assert.Equal(0UL, next.MissedSlots);
	}

	[Theory]
	[InlineData(50, 1)]
	[InlineData(60_000, 1_001)]
	public void Overload_coalesces_to_one_latest_slot_and_recovers_without_a_catchup_queue(long numerator, long denominator)
	{
		var schedule = new RationalFrameSchedule(new FrameRate(numerator, denominator), TimeSpan.Zero);
		Assert.True(schedule.TryTake(schedule.Deadline(1), out _));
		Assert.True(schedule.TryTake(schedule.Deadline(100_000), out var overrun));
		Assert.Equal(99_998UL, overrun.MissedSlots);
		Assert.Equal(100_000UL, overrun.Slot);
		Assert.False(schedule.TryTake(overrun.Deadline, out _));
		Assert.True(schedule.TryTake(schedule.NextDeadline, out var recovered));
		Assert.Equal(0UL, recovered.MissedSlots);
		Assert.Equal(100_001UL, recovered.Slot);
	}

	[Fact]
	public void Format_change_and_resync_create_an_explicit_new_epoch()
	{
		var schedule = new RationalFrameSchedule(FrameRate.Fps50, TimeSpan.FromSeconds(10));
		Assert.True(schedule.TryTake(schedule.Deadline(100), out _));
		schedule.Reset(FrameRate.Fps59_94, TimeSpan.Zero);
		Assert.False(schedule.TryTake(TimeSpan.Zero, out _));
		Assert.Equal(166_834L, schedule.NextDeadline.Ticks);
		Assert.True(schedule.TryTake(schedule.NextDeadline, out var first));
		Assert.Equal(1UL, first.Slot);
		Assert.Equal(0UL, first.MissedSlots);
	}

	[Fact]
	public void Clock_regression_is_rejected_without_consuming_a_slot()
	{
		var schedule = new RationalFrameSchedule(FrameRate.Fps50, TimeSpan.Zero);
		Assert.True(schedule.TryTake(TimeSpan.FromMilliseconds(20), out _));
		Assert.Throws<ArgumentException>(() => schedule.TryTake(TimeSpan.Zero, out _));
		Assert.Equal(TimeSpan.FromMilliseconds(40), schedule.NextDeadline);
	}

	[Fact]
	public void Invalid_rates_epochs_and_deadline_overflow_fail_explicitly()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new RationalFrameSchedule(default, TimeSpan.Zero));
		Assert.Throws<ArgumentOutOfRangeException>(() => new RationalFrameSchedule(new FrameRate(20_000_000, 1), TimeSpan.Zero));
		Assert.Throws<ArgumentOutOfRangeException>(() => new RationalFrameSchedule(FrameRate.Fps50, TimeSpan.FromTicks(-1)));
		var schedule = new RationalFrameSchedule(FrameRate.Fps50, TimeSpan.Zero);
		Assert.Throws<OverflowException>(() => schedule.Deadline(ulong.MaxValue));
	}
}
