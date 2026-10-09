// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Ndi;
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

	[Fact]
	public void Ndi_configuration_uses_typed_source_identity_without_SRT_fields()
	{
		var configuration = NdiConfiguration("rtaime Program");

		Assert.Equal(NetworkOutputProtocolFamily.Ndi, configuration.Protocol);
		Assert.Equal("ndi://rtaime Program", configuration.SafeTargetIdentity);
		Assert.Equal("rtaime Program", configuration.NdiSettings?.SourceName);
		Assert.Null(configuration.SrtSettings);
		Assert.Equal(NetworkOutputVideoCodec.NdiHighBandwidth, configuration.VideoCodec);
		Assert.Equal(NetworkOutputAudioCodec.Float32, configuration.AudioCodec);
		Assert.Equal(0u, configuration.VideoBitRate);
		Assert.Equal(0u, configuration.AudioBitRate);
	}

	[Fact]
	public void Ndi_provider_declares_only_the_qualified_1080p_formats()
	{
		Assert.Equal(
			new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
			NdiNetworkOutputProvider.SupportedFormats);
	}

	[Fact]
	public async Task Ndi_saturated_queue_drops_oldest_complete_sample_without_blocking_submitter()
	{
		var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var sender = new BlockingNdiSender(sendGate.Task);
		await using var session = new NdiNetworkOutputSession(
			NdiConfiguration("rtaime Program", queueCapacity: 1),
			_ => sender);

		var first = Sample(1);
		var second = Sample(2);
		var third = Sample(3);

		Assert.Equal(NetworkOutputEnqueueStatus.Accepted, session.TrySubmit(first).Status);
		await sender.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(NetworkOutputEnqueueStatus.Accepted, session.TrySubmit(second).Status);
		var thirdResult = session.TrySubmit(third);

		Assert.Equal(NetworkOutputEnqueueStatus.DroppedOldest, thirdResult.Status);
		Assert.Equal("network.output.backpressure.drop_oldest", thirdResult.Failure?.Code);
		Assert.True(second.VideoLease.IsDisposed);
		Assert.Equal(1UL, session.Snapshot.Statistics.DroppedSamples);

		sendGate.TrySetResult();
	}

	[Fact]
	public async Task Missing_NDI_runtime_fails_closed_without_rejecting_Program_submission_synchronously()
	{
		await using var session = new NdiNetworkOutputSession(
			NdiConfiguration("rtaime Program", reconnectMaximumAttempts: 1),
			_ => throw new DllNotFoundException("Synthetic missing NDI runtime."));

		var result = session.TrySubmit(Sample(1));
		Assert.True(result.Accepted);

		await WaitUntilAsync(
			() => session.Snapshot.Lifecycle == NetworkOutputLifecycleState.Faulted,
			TimeSpan.FromSeconds(2));

		Assert.False(session.Snapshot.Connected);
		Assert.Equal("network.output.ndi_runtime_unavailable", session.Snapshot.Failure?.Code);
	}

	[Fact]
	public async Task Ndi_send_failure_recovers_on_a_later_sample_and_remains_observational()
	{
		var first = new FailingNdiSender();
		var second = new CapturingNdiSender();
		var creation = 0;
		await using var session = new NdiNetworkOutputSession(
			NdiConfiguration("rtaime Program", reconnectMaximumAttempts: 2),
			_ => Interlocked.Increment(ref creation) == 1 ? first : second);

		Assert.True(session.TrySubmit(Sample(1)).Accepted);
		await first.SendAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await WaitUntilAsync(
			() => session.Snapshot.Lifecycle == NetworkOutputLifecycleState.Reconnecting,
			TimeSpan.FromSeconds(2));

		Assert.True(session.TrySubmit(Sample(2)).Accepted);
		await second.SampleReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.True(session.Snapshot.Connected);
		Assert.Equal(NetworkOutputLifecycleState.Connected, session.Snapshot.Lifecycle);
		Assert.True(session.Snapshot.Statistics.ReconnectCount >= 1);
		Assert.Equal(2UL, second.LastSequence);
	}

	[Fact]
	public async Task Ndi_submission_preserves_Runtime_audio_cadence_and_timing()
	{
		var sender = new CapturingNdiSender(expectedSamples: 2);
		await using var session = new NdiNetworkOutputSession(
			NdiConfiguration("rtaime Aux"),
			_ => sender);

		Assert.True(session.TrySubmit(Sample(10, new Timebase(1_001, 60_000), 8_008, 800)).Accepted);
		Assert.True(session.TrySubmit(Sample(11, new Timebase(1_001, 60_000), 8_808, 801)).Accepted);
		await sender.AllSamplesReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(new uint[] { 800, 801 }, sender.AudioSampleCounts);
		Assert.Equal(new ulong[] { 10, 11 }, sender.Sequences);
	}

	[Fact]
	public async Task Ndi_session_disposes_sender_and_queued_leases_cleanly()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var sender = new BlockingNdiSender(gate.Task);
		var session = new NdiNetworkOutputSession(NdiConfiguration("rtaime Program"), _ => sender);
		var sample = Sample(1);
		Assert.True(session.TrySubmit(sample).Accepted);
		await sender.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		gate.TrySetCanceled();
		await session.DisposeAsync();

		Assert.True(sender.IsDisposed);
		Assert.True(sample.VideoLease.IsDisposed);
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (!condition())
		{
			if (DateTimeOffset.UtcNow >= deadline)
				throw new TimeoutException("Expected network-output state was not observed within the bounded wait.");
			await Task.Delay(10);
		}
	}

	private static NetworkOutputConfiguration NdiConfiguration(
		string sourceName,
		int queueCapacity = 8,
		int reconnectMaximumAttempts = 0) =>
		new(
			"ndi-target",
			NetworkOutputProtocolFamily.Ndi,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			new NdiNetworkOutputSettings(sourceName),
			queueCapacity,
			50,
			100,
			reconnectMaximumAttempts);

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

	private static TestSample Sample(ulong sequence) =>
		Sample(sequence, new Timebase(1, 50), sequence * 960, 960);

	private static TestSample Sample(ulong sequence, Timebase timebase, ulong samplePosition, uint sampleCount)
	{
		var lease = new TestLease(new byte[] { 1, 2, 3, 4 });
		var sample = new NetworkOutputProgramSample(
			sequence,
			new FrameTiming(sequence, checked((long)sequence), timebase),
			new AudioBufferTiming(samplePosition, sampleCount, checked((long)sequence), timebase),
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

	private sealed class BlockingNdiSender : INdiSender
	{
		private readonly Task _gate;
		public BlockingNdiSender(Task gate) => _gate = gate;
		public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsReady => !IsDisposed;
		public bool IsDisposed { get; private set; }
		public async ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
		{
			_ = sample;
			SendStarted.TrySetResult();
			await _gate.WaitAsync(cancellationToken);
			return new NdiSendResult(2, 8);
		}
		public ValueTask DisposeAsync()
		{
			IsDisposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FailingNdiSender : INdiSender
	{
		public TaskCompletionSource SendAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsReady => true;
		public ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
		{
			_ = sample;
			cancellationToken.ThrowIfCancellationRequested();
			SendAttempted.TrySetResult();
			throw new IOException("Synthetic NDI send failure.");
		}
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class CapturingNdiSender : INdiSender
	{
		private readonly int _expectedSamples;
		private readonly List<uint> _audioSampleCounts = new();
		private readonly List<ulong> _sequences = new();
		public CapturingNdiSender(int expectedSamples = 1) => _expectedSamples = expectedSamples;
		public bool IsReady => true;
		public TaskCompletionSource SampleReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AllSamplesReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public IReadOnlyList<uint> AudioSampleCounts => _audioSampleCounts;
		public IReadOnlyList<ulong> Sequences => _sequences;
		public ulong LastSequence => _sequences.Count == 0 ? 0 : _sequences[^1];
		public ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_audioSampleCounts.Add(sample.AudioTiming.SampleCount);
			_sequences.Add(sample.SequenceNumber);
			SampleReceived.TrySetResult();
			if (_sequences.Count >= _expectedSamples)
				AllSamplesReceived.TrySetResult();
			return ValueTask.FromResult(new NdiSendResult(2, checked((ulong)(sample.Video.Memory.Length + sample.Audio.Length))));
		}
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
