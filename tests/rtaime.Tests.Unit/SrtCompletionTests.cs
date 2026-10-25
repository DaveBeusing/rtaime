// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Srt;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SrtCompletionTests
{
	[Theory]
	[InlineData(188)]
	[InlineData(100)]
	public async Task Orderly_completion_preserves_terminal_packets_and_a_packet_spanning_live_and_terminal_output(int split)
	{
		var bytes = SrtTransportPacketizationTests.Packets(2);
		var encoder = new Encoder([bytes[..split]], bytes[split..]);
		var transport = new Transport();
		var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		var firstStop = session.DisposeAsync().AsTask();
		Assert.Same(firstStop, session.DisposeAsync().AsTask());
		await firstStop.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(bytes, transport.Messages.SelectMany(message => message).ToArray());
		Assert.Equal(1, encoder.CompletionCalls);
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.CompletionCalls);
		Assert.Equal(1, transport.DisposeCalls);
		Assert.Equal((ulong)bytes.Length, session.Snapshot.Statistics.BytesSent);
		Assert.Equal((ulong)transport.Messages.Count, session.Snapshot.Statistics.PacketsSent);
		Assert.Equal(0UL, session.Snapshot.Statistics.DroppedSamples);
		Assert.Null(session.Snapshot.Failure);
		Assert.Equal(NetworkOutputLifecycleState.Disabled, session.Snapshot.Lifecycle);
		Assert.Equal(new[] { "complete", "dispose" }, transport.Events);
	}

	[Fact]
	public async Task Orderly_stop_finishes_accepted_work_before_finalization_and_rejects_later_admission()
	{
		var packets = SrtTransportPacketizationTests.Packets(3);
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var encoder = new Encoder([packets[..188], packets[188..376]], packets[376..])
		{
			BeforeEncode = async token => { started.TrySetResult(); await resume.Task.WaitAsync(token); }
		};
		var transport = new Transport();
		var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		var first = Sample(0);
		var second = Sample(1);
		Assert.True(session.TrySubmit(first.Sample).Accepted);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(session.TrySubmit(second.Sample).Accepted);
		var completion = session.DisposeAsync().AsTask();
		var rejected = Sample(2);
		Assert.False(session.TrySubmit(rejected.Sample).Accepted);
		await rejected.Lease.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, encoder.CompletionCalls);
		resume.SetResult();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.WhenAll(first.Lease.Returned.Task, second.Lease.Returned.Task).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(packets, transport.Messages.SelectMany(message => message).ToArray());
		Assert.Equal(2, encoder.EncodeCalls);
		Assert.Equal(1, encoder.CompletionCalls);
		Assert.Equal(0UL, session.Snapshot.Statistics.DroppedSamples);
		Assert.Equal(1UL, session.Snapshot.Statistics.RejectedSamples);
	}

	[Fact]
	public async Task Truncated_terminal_packet_reports_failure_and_releases_both_owners()
	{
		var bytes = SrtTransportPacketizationTests.Packets(1);
		var encoder = new Encoder([bytes[..100]], bytes[100..187]);
		var transport = new Transport();
		var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		var failure = await Assert.ThrowsAsync<InvalidDataException>(() => session.DisposeAsync().AsTask());
		Assert.Contains("residualBytes=187", failure.Message);
		Assert.Contains("residualBytes=187", session.Snapshot.Failure!.Value.Message);
		Assert.Equal(NetworkOutputLifecycleState.Faulted, session.Snapshot.Lifecycle);
		Assert.Empty(transport.Messages);
		Assert.Equal(0UL, session.Snapshot.Statistics.BytesSent);
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.DisposeCalls);
	}

	[Fact]
	public async Task Final_send_failure_preserves_the_successful_prefix_and_cleanup()
	{
		var live = SrtTransportPacketizationTests.Packets(1);
		var tail = SrtTransportPacketizationTests.Packets(20, 0x40);
		var encoder = new Encoder([live], tail);
		var transport = new Transport { FailSend = 3 };
		var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
		Assert.Equal(live.Concat(tail[..1316]).ToArray(), transport.Messages.SelectMany(message => message).ToArray());
		Assert.Equal(2UL, session.Snapshot.Statistics.PacketsSent);
		Assert.Equal(1504UL, session.Snapshot.Statistics.BytesSent);
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.DisposeCalls);
		Assert.Equal(0, transport.CompletionCalls);
		Assert.Equal(NetworkOutputLifecycleState.Faulted, session.Snapshot.Lifecycle);
	}

	[Fact]
	public async Task Finalization_and_each_cleanup_failure_remain_observable_after_every_owner_is_attempted()
	{
		var finalization = new IOException("finalization failed");
		var encoderCleanup = new InvalidOperationException("encoder cleanup failed");
		var transportCleanup = new IOException("transport cleanup failed");
		var encoder = new Encoder([SrtTransportPacketizationTests.Packets(1)], [])
		{
			CompletionFailure = finalization,
			DisposeFailure = encoderCleanup
		};
		var transport = new Transport { DisposeFailure = transportCleanup };
		var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		var failure = await Assert.ThrowsAsync<AggregateException>(() => session.DisposeAsync().AsTask());
		Assert.Equal(new Exception[] { finalization, encoderCleanup, transportCleanup }, failure.InnerExceptions);
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.DisposeCalls);
		Assert.Equal(NetworkOutputLifecycleState.Faulted, session.Snapshot.Lifecycle);
		Assert.Equal("network.output.completion_failed", session.Snapshot.Failure!.Value.Code);
	}

	[Fact]
	public async Task Native_buffer_completion_failure_does_not_skip_encoder_or_transport_cleanup()
	{
		var expected = new IOException("pending native sends failed");
		var encoder = new Encoder([SrtTransportPacketizationTests.Packets(1)], SrtTransportPacketizationTests.Packets(1, 0x40));
		var transport = new Transport { CompletionFailure = expected };
		var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask()));
		Assert.Equal(376UL, session.Snapshot.Statistics.BytesSent);
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.DisposeCalls);
		Assert.Equal(NetworkOutputLifecycleState.Faulted, session.Snapshot.Lifecycle);
	}

	[Fact]
	public async Task Reconnect_aborts_old_encoder_tail_and_residual_bytes_before_the_new_epoch()
	{
		var invalid = SrtTransportPacketizationTests.Packets(1);
		invalid[0] = 0;
		var firstEncoder = new Encoder([invalid[..100], invalid[100..]], SrtTransportPacketizationTests.Packets(1, 0x70));
		var secondLive = SrtTransportPacketizationTests.Packets(1, 0x40);
		var secondTail = SrtTransportPacketizationTests.Packets(1, 0x50);
		var secondEncoder = new Encoder([secondLive], secondTail);
		var firstTransport = new Transport();
		var secondTransport = new Transport();
		var encoders = new Queue<Encoder>([firstEncoder, secondEncoder]);
		var transports = new Queue<Transport>([firstTransport, secondTransport]);
		var session = new SrtNetworkOutputSession(Configuration(), () => transports.Dequeue(), () => encoders.Dequeue());
		await SubmitAsync(session, 0);
		await SubmitAsync(session, 1);
		Assert.Equal(0, firstEncoder.CompletionCalls);
		Assert.Equal(1, firstEncoder.DisposeCalls);
		Assert.Equal(0, firstTransport.CompletionCalls);
		await SubmitAsync(session, 2);
		await session.DisposeAsync();
		Assert.Empty(firstTransport.Messages);
		Assert.Equal(secondLive.Concat(secondTail).ToArray(), secondTransport.Messages.SelectMany(message => message).ToArray());
		Assert.Equal(1, secondEncoder.CompletionCalls);
		Assert.Null(session.Snapshot.Failure);
	}

	[Fact]
	public async Task Noncancelable_completion_times_out_without_releasing_resources_that_are_still_in_use()
	{
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var encoder = new Encoder([SrtTransportPacketizationTests.Packets(1)], SrtTransportPacketizationTests.Packets(1))
		{
			BeforeComplete = async _ => { started.SetResult(); await resume.Task; }
		};
		var transport = new Transport();
		var session = CreateWithDeadline(encoder, transport, TimeSpan.FromMilliseconds(100));
		await SubmitAsync(session, 0);
		var completion = session.DisposeAsync().AsTask();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAsync<TimeoutException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Same(completion, session.DisposeAsync().AsTask());
		Assert.Equal(0, encoder.DisposeCalls);
		Assert.Equal(0, transport.DisposeCalls);
		Assert.Equal(NetworkOutputLifecycleState.Faulted, session.Snapshot.Lifecycle);
		Assert.Equal("network.output.completion_timeout", session.Snapshot.Failure!.Value.Code);
		resume.SetResult();
		await transport.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.DisposeCalls);
		Assert.Single(transport.Messages);
		Assert.Equal("network.output.completion_timeout", session.Snapshot.Failure!.Value.Code);
	}

	private static SrtNetworkOutputSession CreateWithDeadline(Encoder encoder, Transport transport, TimeSpan deadline) =>
		(SrtNetworkOutputSession)Activator.CreateInstance(typeof(SrtNetworkOutputSession),
			BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
			[Configuration(), (Func<ISrtTransport>)(() => transport), (Func<ISrtPayloadEncoder>)(() => encoder), deadline], culture: null)!;

	[Fact]
	public async Task Noncancelable_input_retains_its_lease_until_return_then_aborts_and_returns_every_queued_lease()
	{
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var encoder = new Encoder([SrtTransportPacketizationTests.Packets(1)], SrtTransportPacketizationTests.Packets(1))
		{
			BeforeEncode = async _ => { started.SetResult(); await resume.Task; }
		};
		var transport = new Transport();
		var session = CreateWithDeadline(encoder, transport, TimeSpan.FromMilliseconds(100));
		var active = Sample(0);
		var queued = Sample(1);
		Assert.True(session.TrySubmit(active.Sample).Accepted);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(session.TrySubmit(queued.Sample).Accepted);
		await Assert.ThrowsAsync<TimeoutException>(() => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.False(active.Lease.Returned.Task.IsCompleted);
		Assert.False(queued.Lease.Returned.Task.IsCompleted);
		Assert.Equal(0, encoder.DisposeCalls);
		Assert.Equal(0, transport.DisposeCalls);
		resume.SetResult();
		await Task.WhenAll(active.Lease.Returned.Task, queued.Lease.Returned.Task, transport.Disposed.Task)
			.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(0, encoder.CompletionCalls);
		Assert.Equal(1, encoder.DisposeCalls);
		Assert.Equal(1, transport.DisposeCalls);
		Assert.Empty(transport.Messages);
		Assert.Equal(2UL, session.Snapshot.Statistics.DroppedSamples);
		Assert.Equal(0, session.Snapshot.Statistics.QueueDepth);
		Assert.Equal("network.output.completion_timeout", session.Snapshot.Failure!.Value.Code);
	}

	private static async Task SubmitAsync(SrtNetworkOutputSession session, ulong sequence)
	{
		var submitted = Sample(sequence);
		Assert.True(session.TrySubmit(submitted.Sample).Accepted);
		await submitted.Lease.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static (NetworkOutputProgramSample Sample, Lease Lease) Sample(ulong sequence)
	{
		var lease = new Lease();
		var timebase = new Timebase(1, 50);
		return (new NetworkOutputProgramSample(sequence, new FrameTiming(sequence, (long)sequence, timebase),
			new AudioBufferTiming(sequence * 960, 960, (long)sequence, timebase), lease, new byte[8]), lease);
	}

	private static NetworkOutputConfiguration Configuration() => new(
		"completion", new Uri("srt://127.0.0.1:9000"), NetworkOutputProtocolFamily.Srt,
		NetworkOutputConnectionMode.Caller, VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32,
		NetworkOutputVideoCodec.H264, NetworkOutputAudioCodec.AacLc, 12_000_000, 192_000, 120);

	private sealed class Lease : INetworkOutputPayloadLease
	{
		public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ReadOnlyMemory<byte> Memory => new byte[4];
		public void Dispose() => Returned.TrySetResult();
	}

	private sealed class Encoder(byte[][] live, byte[] tail) : ISrtPayloadEncoder
	{
		public int EncodeCalls { get; private set; }
		public int CompletionCalls { get; private set; }
		public int DisposeCalls { get; private set; }
		public Func<CancellationToken, Task>? BeforeEncode { get; init; }
		public Func<CancellationToken, Task>? BeforeComplete { get; init; }
		public Exception? CompletionFailure { get; init; }
		public Exception? DisposeFailure { get; init; }
		public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken token) => ValueTask.CompletedTask;
		public async ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken token)
		{
			if (BeforeEncode is not null) await BeforeEncode(token);
			return live[EncodeCalls++];
		}
		public async ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken token)
		{
			CompletionCalls++;
			if (BeforeComplete is not null) await BeforeComplete(token);
			if (CompletionFailure is not null) throw CompletionFailure;
			return tail;
		}
		public ValueTask DisposeAsync()
		{
			DisposeCalls++;
			return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
		}
	}

	private sealed class Transport : ISrtTransport
	{
		public bool IsConnected { get; private set; }
		public int CompletionCalls { get; private set; }
		public int DisposeCalls { get; private set; }
		public int SendCalls { get; private set; }
		public int FailSend { get; init; }
		public Exception? CompletionFailure { get; init; }
		public Exception? DisposeFailure { get; init; }
		public List<byte[]> Messages { get; } = [];
		public List<string> Events { get; } = [];
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken token)
		{
			IsConnected = true;
			return ValueTask.CompletedTask;
		}
		public ValueTask<int> SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();
			if (++SendCalls == FailSend) throw new IOException("injected send failure");
			Assert.True(IsConnected);
			Messages.Add(bytes.ToArray());
			return ValueTask.FromResult(bytes.Length);
		}
		public ValueTask CompleteAsync(CancellationToken token)
		{
			CompletionCalls++;
			Events.Add("complete");
			return CompletionFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(CompletionFailure);
		}
		public ValueTask DisposeAsync()
		{
			IsConnected = false;
			DisposeCalls++;
			Events.Add("dispose");
			Disposed.TrySetResult();
			return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
		}
	}
}
