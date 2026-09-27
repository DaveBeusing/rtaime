// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class HostIpcSessionTrackerTests
{
	[Theory]
	[InlineData(1000, 500)]
	[InlineData(10000, 2000)]
	public void Host_drain_interval_stays_inside_shutdown_budget(int hostTimeoutMilliseconds, int expectedMilliseconds)
	{
		var interval = HostIpcSessionTracker.DrainIntervalForHost(TimeSpan.FromMilliseconds(hostTimeoutMilliseconds));

		Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), interval);
	}

	[Fact]
	public async Task Stop_and_drain_cancels_cooperative_session_and_removes_it()
	{
		var tracker = new HostIpcSessionTracker();
		var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		Assert.True(tracker.TryStart(async cancellationToken =>
		{
			started.TrySetResult(true);
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}
			finally
			{
				cancelled.TrySetResult(cancellationToken.IsCancellationRequested);
			}
		}, out var completion));

		await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
		var drain = await tracker.StopAndDrainAsync(TimeSpan.FromSeconds(1));
		await completion.WaitAsync(TimeSpan.FromSeconds(1));

		Assert.Equal(HostIpcSessionDrainStatus.Drained, drain.Status);
		Assert.True(await cancelled.Task);
		Assert.Equal(0, tracker.Snapshot.ActiveSessions);
		Assert.Equal(1UL, tracker.Snapshot.StartedSessions);
		Assert.Equal(1UL, tracker.Snapshot.CompletedSessions);
		Assert.Equal(0UL, tracker.Snapshot.FaultedSessions);
		Assert.True(tracker.Snapshot.StopRequested);
		Assert.False(tracker.Snapshot.AcceptingSessions);
		Assert.False(tracker.Snapshot.DrainTimedOut);
		Assert.False(tracker.TryStart(_ => Task.CompletedTask));
	}

	[Fact]
	public async Task Stop_and_drain_times_out_for_non_cooperative_session_without_losing_tracking()
	{
		var tracker = new HostIpcSessionTracker();
		var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		Assert.True(tracker.TryStart(async _ =>
		{
			started.TrySetResult(true);
			await release.Task;
		}, out var completion));

		await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
		var drain = await tracker.StopAndDrainAsync(TimeSpan.FromMilliseconds(50));

		Assert.Equal(HostIpcSessionDrainStatus.TimedOut, drain.Status);
		Assert.Equal(1, drain.Snapshot.ActiveSessions);
		Assert.True(drain.Snapshot.DrainTimedOut);

		release.TrySetResult(true);
		await completion.WaitAsync(TimeSpan.FromSeconds(1));

		Assert.Equal(0, tracker.Snapshot.ActiveSessions);
		Assert.Equal(1UL, tracker.Snapshot.CompletedSessions);
		Assert.True(tracker.Snapshot.DrainTimedOut);
	}

	[Fact]
	public async Task Handler_exception_is_observed_and_completed_session_is_not_retained()
	{
		var tracker = new HostIpcSessionTracker();

		Assert.True(tracker.TryStart(
			_ => Task.FromException(new InvalidOperationException("synthetic session failure")),
			out var completion));

		await completion.WaitAsync(TimeSpan.FromSeconds(1));
		var snapshot = tracker.Snapshot;

		Assert.Equal(0, snapshot.ActiveSessions);
		Assert.Equal(1UL, snapshot.StartedSessions);
		Assert.Equal(1UL, snapshot.CompletedSessions);
		Assert.Equal(1UL, snapshot.FaultedSessions);
		Assert.Contains(nameof(InvalidOperationException), snapshot.LastFailureType, StringComparison.Ordinal);
		Assert.Equal("synthetic session failure", snapshot.LastFailureDetail);
	}
	[Fact]
	public async Task Protocol_counter_never_rolls_over_when_concurrent_callers_reach_signed_limit()
	{
		var counter = new HostIpcProtocolCounter(checked((ulong)(long.MaxValue - 1)));
		using var start = new ManualResetEventSlim(false);
		var attempts = Enumerable.Range(0, 2)
			.Select(_ => Task.Run(() =>
			{
				start.Wait();
				return counter.TryIncrement(out var value) ? value : (ulong?)null;
			}))
			.ToArray();

		start.Set();
		var results = await Task.WhenAll(attempts);

		Assert.Single(results, value => value == checked((ulong)long.MaxValue));
		Assert.Single(results, value => value is null);
		Assert.Equal(checked((ulong)long.MaxValue), counter.Value);
		Assert.False(counter.TryIncrement(out var exhaustedValue));
		Assert.Equal(checked((ulong)long.MaxValue), exhaustedValue);
		Assert.Equal(checked((ulong)long.MaxValue), counter.Value);
	}

}
