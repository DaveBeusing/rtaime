// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Srt;

namespace rtaime.Tests.Unit;

public sealed class NetworkOutputFoundationTests
{
	[Fact]
	public void Configuration_accepts_reference_h264_aac_profile_and_redacts_target_identity()
	{
		var configuration = Configuration(new Uri("srt://127.0.0.1:9000/live?streamid=program"));

		Assert.Equal(NetworkOutputVideoCodec.H264, configuration.VideoCodec);
		Assert.Equal(NetworkOutputAudioCodec.AacLc, configuration.AudioCodec);
		Assert.Equal("srt://127.0.0.1:9000/live", configuration.SafeTargetIdentity);
		Assert.Equal(8, configuration.QueueCapacity);
	}

	[Fact]
	public void Configuration_rejects_credentials_and_unsupported_codecs()
	{
		Assert.Throws<ArgumentException>(() =>
			Configuration(new Uri("srt://user@127.0.0.1:9000/live")));

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new NetworkOutputConfiguration(
				"program-srt",
				new Uri("srt://127.0.0.1:9000/live"),
				NetworkOutputProtocolFamily.Srt,
				NetworkOutputConnectionMode.Caller,
				VideoFormat.Hd1080p50Rgba8,
				AudioFormat.Stereo48kFloat32,
				(NetworkOutputVideoCodec)999,
				NetworkOutputAudioCodec.AacLc,
				12_000_000,
				192_000,
				120));
	}

	[Fact]
	public async Task Saturated_queue_drops_oldest_complete_sample_without_blocking_submitter()
	{
		var connectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var transport = new BlockingTransport(connectGate.Task);
		var encoder = new PassThroughEncoder();
		await using var session = new SrtNetworkOutputSession(
			Configuration(new Uri("srt://127.0.0.1:9000/live"), queueCapacity: 1),
			() => transport,
			() => encoder);

		var first = Sample(1);
		var second = Sample(2);
		var third = Sample(3);

		Assert.Equal(NetworkOutputEnqueueStatus.Accepted, session.TrySubmit(first).Status);
		await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(NetworkOutputEnqueueStatus.Accepted, session.TrySubmit(second).Status);
		var thirdResult = session.TrySubmit(third);

		Assert.Equal(NetworkOutputEnqueueStatus.DroppedOldest, thirdResult.Status);
		Assert.Equal("network.output.backpressure.drop_oldest", thirdResult.Failure?.Code);
		Assert.True(second.VideoLease.IsDisposed);

		var snapshot = session.Snapshot;
		Assert.Equal(3UL, snapshot.Statistics.AcceptedSamples);
		Assert.Equal(1UL, snapshot.Statistics.DroppedSamples);
		Assert.Equal(1, snapshot.Statistics.QueueDepth);

		connectGate.TrySetCanceled();
	}

	[Fact]
	public async Task Transport_failure_is_observational_and_does_not_reject_future_submission_synchronously()
	{
		var transport = new FailingTransport();
		await using var session = new SrtNetworkOutputSession(
			Configuration(new Uri("srt://127.0.0.1:9000/live"), reconnectMaximumAttempts: 1),
			() => transport,
			() => new PassThroughEncoder());

		var result = session.TrySubmit(Sample(1));

		Assert.True(result.Accepted);
		await transport.ConnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await Task.Delay(50);

		var snapshot = session.Snapshot;
		Assert.True(snapshot.Lifecycle is NetworkOutputLifecycleState.Reconnecting or NetworkOutputLifecycleState.Faulted);
		Assert.False(snapshot.Connected);
	}

	private static NetworkOutputConfiguration Configuration(
		Uri endpoint,
		int queueCapacity = 8,
		int reconnectMaximumAttempts = 0) =>
		new(
			"program-srt",
			endpoint,
			NetworkOutputProtocolFamily.Srt,
			NetworkOutputConnectionMode.Caller,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			NetworkOutputVideoCodec.H264,
			NetworkOutputAudioCodec.AacLc,
			12_000_000,
			192_000,
			120,
			queueCapacity,
			NetworkOutputLatencyMode.Normal,
			null,
			50,
			100,
			reconnectMaximumAttempts);

	private static TestSample Sample(ulong sequence)
	{
		var lease = new TestLease(new byte[] { 1, 2, 3, 4 });
		var timebase = new Timebase(1, 50);
		var sample = new NetworkOutputProgramSample(
			sequence,
			new FrameTiming(sequence, checked((long)sequence), timebase),
			new AudioBufferTiming(sequence * 960, 960, checked((long)sequence), timebase),
			lease,
			new byte[] { 1, 2, 3, 4 });
		return new TestSample(sample, lease);
	}

	private sealed record TestSample(NetworkOutputProgramSample Value, TestLease VideoLease)
	{
		public static implicit operator NetworkOutputProgramSample(TestSample sample) => sample.Value;
	}

	private sealed class TestLease : INetworkOutputPayloadLease
	{
		private ReadOnlyMemory<byte> _memory;
		public TestLease(ReadOnlyMemory<byte> memory) => _memory = memory;
		public bool IsDisposed { get; private set; }
		public ReadOnlyMemory<byte> Memory => IsDisposed ? ReadOnlyMemory<byte>.Empty : _memory;
		public void Dispose()
		{
			IsDisposed = true;
			_memory = ReadOnlyMemory<byte>.Empty;
		}
	}

	private sealed class BlockingTransport : ISrtTransport
	{
		private readonly Task _gate;
		public BlockingTransport(Task gate) => _gate = gate;
		public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsConnected { get; private set; }
		public async ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken cancellationToken)
		{
			_ = configuration;
			_ = passphrase;
			ConnectStarted.TrySetResult();
			await _gate.WaitAsync(cancellationToken);
			IsConnected = true;
		}
		public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(payload.Length);
		}
		public ValueTask DisposeAsync()
		{
			IsConnected = false;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FailingTransport : ISrtTransport
	{
		public TaskCompletionSource ConnectAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsConnected => false;
		public ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken cancellationToken)
		{
			_ = configuration;
			_ = passphrase;
			cancellationToken.ThrowIfCancellationRequested();
			ConnectAttempted.TrySetResult();
			throw new IOException("Synthetic connect failure.");
		}
		public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
			throw new InvalidOperationException();
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class PassThroughEncoder : ISrtPayloadEncoder
	{
		public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken cancellationToken)
		{
			_ = configuration;
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}
		public ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0x47, 0x40, 0x00, 0x10 });
		}
		public ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
