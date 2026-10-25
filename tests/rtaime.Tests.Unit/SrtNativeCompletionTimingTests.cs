// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Threading.Channels;
using rtaime.Provider.Srt;

namespace rtaime.Tests.Unit;

public sealed class SrtNativeCompletionTimingTests
{
	[Fact]
	public async Task Early_ack_does_not_close_before_negotiated_peer_delivery_time()
	{
		var clock = new ManualClock();
		var empty = false;
		var completion = CompleteAsync(() => empty, 40, clock);
		await clock.NextPollAsync();
		empty = true;
		clock.Advance(10); // Native send buffer becomes empty after early ACK.
		await clock.NextPollAsync();
		clock.Advance(30);
		await clock.NextPollAsync();
		Assert.False(completion.IsCompleted); // Tail is still inside the peer's negotiated window.
		clock.Advance(10);
		await completion.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(50, clock.ElapsedMilliseconds);
	}

	[Fact]
	public async Task Renewed_pending_bytes_restart_the_peer_delivery_window()
	{
		var clock = new ManualClock();
		var empty = true;
		var completion = CompleteAsync(() => empty, 40, clock);
		await clock.NextPollAsync();
		empty = false;
		clock.Advance(30);
		await clock.NextPollAsync();
		empty = true;
		clock.Advance(10);
		await clock.NextPollAsync();
		clock.Advance(30);
		await clock.NextPollAsync();
		Assert.False(completion.IsCompleted);
		clock.Advance(10);
		await completion.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(80, clock.ElapsedMilliseconds);
	}

	[Fact]
	public async Task Cancellation_during_peer_delivery_wait_cannot_report_completed()
	{
		var clock = new ManualClock();
		using var cancellation = new CancellationTokenSource();
		var completion = CompleteAsync(() => true, 120, clock, cancellation.Token);
		await clock.NextPollAsync();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
	}

	[Fact]
	public async Task Disconnect_after_send_buffer_drains_remains_a_completion_failure()
	{
		var clock = new ManualClock();
		var connected = true;
		var failure = new IOException("Peer disconnected before its delivery window elapsed.");
		var completion = CompleteAsync(() => connected ? true : throw failure, 120, clock);
		await clock.NextPollAsync();
		connected = false;
		clock.Advance(10);
		Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => completion));
	}

	private static Task CompleteAsync(Func<bool> empty, int latencyMilliseconds, TimeProvider clock, CancellationToken token = default)
	{
		var method = typeof(NativeSrtTransport).GetMethod("WaitForSendCompletionAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
		return ((ValueTask)method.Invoke(null, [empty, TimeSpan.FromMilliseconds(latencyMilliseconds), clock, token])!).AsTask();
	}

	private sealed class ManualClock : TimeProvider
	{
		private readonly object _gate = new();
		private readonly List<ManualTimer> _timers = [];
		private readonly Channel<bool> _scheduled = Channel.CreateUnbounded<bool>();
		private long _ticks;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => Interlocked.Read(ref _ticks);
		public long ElapsedMilliseconds => GetTimestamp() / TimeSpan.TicksPerMillisecond;
		public async Task NextPollAsync() => await _scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			Assert.Equal(Timeout.InfiniteTimeSpan, period);
			var timer = new ManualTimer(callback, state, checked(GetTimestamp() + dueTime.Ticks));
			lock (_gate) _timers.Add(timer);
			_scheduled.Writer.TryWrite(true);
			return timer;
		}

		public void Advance(int milliseconds)
		{
			Interlocked.Add(ref _ticks, milliseconds * TimeSpan.TicksPerMillisecond);
			ManualTimer[] elapsed;
			lock (_gate)
			{
				elapsed = _timers.Where(timer => timer.Due <= GetTimestamp()).ToArray();
				_timers.RemoveAll(timer => timer.Due <= GetTimestamp());
			}
			foreach (var timer in elapsed) timer.Fire();
		}
	}

	private sealed class ManualTimer(TimerCallback callback, object? state, long due) : ITimer
	{
		private int _disposed;
		public long Due { get; } = due;
		public void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
		public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
		public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
		public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
	}
}
