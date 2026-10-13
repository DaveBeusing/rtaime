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
	public async Task Cancelling_pending_SRT_connect_releases_attempt_resources_and_frame_once()
	{
		var transport = new BlockingTransport(new TaskCompletionSource().Task);
		var encoder = new PassThroughEncoder();
		var session = new SrtNetworkOutputSession(
			Configuration(new Uri("srt://127.0.0.1:9000/live")), () => transport, () => encoder);
		await using (session)
		{
			var sample = Sample(1);
			Assert.True(session.TrySubmit(sample).Accepted);
			await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
			await session.DisposeAsync();
			Assert.Equal(1, transport.DisposeCount);
			Assert.Equal(1, encoder.DisposeCount);
			Assert.Equal(1, sample.VideoLease.DisposeCount);
		}
	}

	[Fact]
	public async Task Failed_SRT_encoder_initialization_releases_connected_transport_and_encoder()
	{
		var transport = new BlockingTransport(Task.CompletedTask);
		var encoder = new PassThroughEncoder(failInitialization: true);
		await using var session = new SrtNetworkOutputSession(
			Configuration(new Uri("srt://127.0.0.1:9000/live"), reconnectMaximumAttempts: 1),
			() => transport, () => encoder);
		Assert.True(session.TrySubmit(Sample(1)).Accepted);
		await WaitUntilAsync(() => session.Snapshot.Lifecycle == NetworkOutputLifecycleState.Faulted,
			TimeSpan.FromSeconds(2));
		Assert.Equal(1, transport.DisposeCount);
		Assert.Equal(1, encoder.DisposeCount);
		Assert.False(transport.IsConnected);
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
	public void Ndi_planar_float_audio_FourCC_matches_the_NDI_runtime_ABI()
	{
		Assert.Equal(
			'F' | ('L' << 8) | ('T' << 16) | ('p' << 24),
			NdiFourCc.Fltp);
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
	public async Task Unsupported_NDI_runtime_fails_closed()
	{
		await using var session = new NdiNetworkOutputSession(
			NdiConfiguration("rtaime Program", reconnectMaximumAttempts: 1),
			_ => throw new PlatformNotSupportedException("Synthetic unsupported NDI runtime."));

		Assert.True(session.TrySubmit(Sample(1)).Accepted);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Runtime_frame_clock_and_audio_sample_clock_are_accepted_without_timestamp_rewriting(bool ndi, bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var configuration = ndi
            ? new NetworkOutputConfiguration("clock-ndi", NetworkOutputProtocolFamily.Ndi, format,
                AudioFormat.Stereo48kFloat32, new NdiNetworkOutputSettings("Clock evidence"))
            : new NetworkOutputConfiguration("clock-srt", new Uri("srt://127.0.0.1:9000/live"),
                NetworkOutputProtocolFamily.Srt, NetworkOutputConnectionMode.Caller, format,
                AudioFormat.Stereo48kFloat32, NetworkOutputVideoCodec.H264, NetworkOutputAudioCodec.AacLc,
                12_000_000, 192_000, 120, 8, NetworkOutputLatencyMode.Normal);
        await using INetworkOutputSession session = ndi
            ? new NdiNetworkOutputSession(configuration, _ => new CapturingNdiSender())
            : new SrtNetworkOutputSession(configuration, () => new BlockingTransport(Task.CompletedTask), () => new PassThroughEncoder());
        var sequence = 11UL;
        var window = rtaime.Media.AudioVideoTimingRelationship.GetSampleWindow(format.FrameRate, 48_000, sequence);
        var lease = new TestLease(new byte[] { 1, 2, 3, 4 });
        var sample = new NetworkOutputProgramSample(sequence,
            new FrameTiming(sequence, (long)sequence, new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator)),
            new AudioBufferTiming(window.SamplePosition, window.SampleCount, window.PresentationTimestamp, window.Timebase),
            lease, new byte[checked((int)window.SampleCount * 8)]);
        Assert.NotEqual(sample.VideoTiming.Timebase, sample.AudioTiming.Timebase);
        Assert.True(session.TrySubmit(sample).Accepted);
        await WaitUntilAsync(() => session.Snapshot.Statistics.SentSamples == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(window.PresentationTimestamp, sample.AudioTiming.PresentationTimestamp);
        Assert.Equal(window.SampleCount, sample.AudioTiming.SampleCount);
        Assert.Equal(0UL, session.Snapshot.Statistics.RejectedSamples);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Independent_clocks_reject_wrong_audio_window_and_return_each_lease_once(bool ndi, bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var configuration = ndi
            ? new NetworkOutputConfiguration("invalid-ndi", NetworkOutputProtocolFamily.Ndi, format,
                AudioFormat.Stereo48kFloat32, new NdiNetworkOutputSettings("Invalid clock"))
            : new NetworkOutputConfiguration("invalid-srt", new Uri("srt://127.0.0.1:9000/live"),
                NetworkOutputProtocolFamily.Srt, NetworkOutputConnectionMode.Caller, format,
                AudioFormat.Stereo48kFloat32, NetworkOutputVideoCodec.H264, NetworkOutputAudioCodec.AacLc,
                12_000_000, 192_000, 120, 8, NetworkOutputLatencyMode.Normal);
        await using INetworkOutputSession session = ndi
            ? new NdiNetworkOutputSession(configuration, _ => new CapturingNdiSender())
            : new SrtNetworkOutputSession(configuration, () => new BlockingTransport(Task.CompletedTask), () => new PassThroughEncoder());
        var window = rtaime.Media.AudioVideoTimingRelationship.GetSampleWindow(format.FrameRate, 48_000, 11);
        for (var fault = 0; fault < 4; fault++)
        {
            var lease = new TestLease(new byte[] { 1, 2, 3, 4 });
            var sample = new NetworkOutputProgramSample(11,
                new FrameTiming(11, fault == 3 ? 12 : 11, new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator)),
                new AudioBufferTiming(window.SamplePosition + (fault == 0 ? 1UL : 0UL),
                    window.SampleCount + (fault == 1 ? 1U : 0U),
                    window.PresentationTimestamp + (fault == 2 ? 1 : 0), window.Timebase),
                lease, new byte[checked((int)window.SampleCount * 8)]);
            Assert.False(session.TrySubmit(sample).Accepted);
            Assert.Equal(1, lease.DisposeCount);
        }
        Assert.Equal(4UL, session.Snapshot.Statistics.RejectedSamples);
        Assert.Equal(0, session.Snapshot.Statistics.QueueDepth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_dispose_waits_for_native_cleanup(bool ndi)
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new CleanupNdiSender(cleanupStarted, releaseCleanup);
        var transport = new CleanupSrtTransport(cleanupStarted, releaseCleanup);
        INetworkOutputSession session = ndi
            ? new NdiNetworkOutputSession(NdiConfiguration("Cleanup"), _ => sender)
            : new SrtNetworkOutputSession(Configuration(new Uri("srt://127.0.0.1:9000/live")),
                () => transport, () => new PassThroughEncoder());
        Task? first = null;
        Task? second = null;
        try
        {
            Assert.True(session.TrySubmit(Sample(1)).Accepted);
            await WaitUntilAsync(() => session.Snapshot.Statistics.SentSamples == 1, TimeSpan.FromSeconds(5));
            first = session.DisposeAsync().AsTask();
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = session.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            releaseCleanup.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, ndi ? sender.Disposals : transport.Disposals);
            Assert.Equal(0, session.Snapshot.Statistics.QueueDepth);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
        }
    }


    [Fact]
    public void Warmed_Runtime_clock_validation_does_not_allocate_per_sample()
    {
        var format = VideoFormat.Hd1080p59_94Rgba8;
        var window = rtaime.Media.AudioVideoTimingRelationship.GetSampleWindow(format.FrameRate, 48_000, 11);
        var sample = new NetworkOutputProgramSample(11,
            new FrameTiming(11, 11, new Timebase(1001, 60000)),
            new AudioBufferTiming(window.SamplePosition, window.SampleCount, window.PresentationTimestamp, window.Timebase),
            new TestLease(new byte[] { 1, 2, 3, 4 }), new byte[checked((int)window.SampleCount * 8)]);
        var valid = 0;
        for (var index = 0; index < 20_000; index++)
            if (sample.HasCompatibleRuntimeTiming(format, AudioFormat.Stereo48kFloat32)) valid++;
        Assert.Equal(20_000, valid);
        valid = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++)
            if (sample.HasCompatibleRuntimeTiming(format, AudioFormat.Stereo48kFloat32)) valid++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(100_000, valid);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_failure_is_shared_and_releases_inflight_and_queued_leases(bool ndi)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new CleanupNdiSender(started, release, fail: true, stall: true);
        var transport = new CleanupSrtTransport(started, release, fail: true, stall: true);
        INetworkOutputSession session = ndi
            ? new NdiNetworkOutputSession(NdiConfiguration("Failed cleanup", queueCapacity: 2), _ => sender)
            : new SrtNetworkOutputSession(Configuration(new Uri("srt://127.0.0.1:9000/live"), queueCapacity: 2),
                () => transport, () => new PassThroughEncoder());
        var samples = new[] { Sample(0), Sample(1), Sample(2) };
        Task? first = null;
        Task? second = null;
        try
        {
            Assert.True(session.TrySubmit(samples[0]).Accepted);
            await (ndi ? sender.SendStarted.Task : transport.SendStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.TrySubmit(samples[1]).Accepted);
            Assert.True(session.TrySubmit(samples[2]).Accepted);
            Assert.Equal(2, session.Snapshot.Statistics.QueueDepth);
            first = session.DisposeAsync().AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = session.DisposeAsync().AsTask();
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            var firstFailure = await Assert.ThrowsAsync<IOException>(() => first);
            var repeatedFailure = await Assert.ThrowsAsync<IOException>(() => second);
            Assert.Same(firstFailure, repeatedFailure);
            Assert.All(samples, sample => Assert.Equal(1, sample.VideoLease.DisposeCount));
            Assert.Equal(0, session.Snapshot.Statistics.QueueDepth);
            Assert.Equal(1, ndi ? sender.Disposals : transport.Disposals);
        }
        finally
        {
            release.TrySetResult();
            try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
        }
    }


    private sealed class CleanupNdiSender(TaskCompletionSource started, TaskCompletionSource release, bool fail = false, bool stall = false) : INdiSender
    {
        public bool IsReady => true;
        public int Disposals { get; private set; }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken token)
        {
            SendStarted.TrySetResult();
            if (stall) await Task.Delay(Timeout.Infinite, token);
            return new NdiSendResult(1, 4);
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            started.TrySetResult();
            await release.Task;
            if (fail) throw new IOException("Native cleanup failed.");
        }
    }

    private sealed class CleanupSrtTransport(TaskCompletionSource started, TaskCompletionSource release, bool fail = false, bool stall = false) : ISrtTransport
    {
        public bool IsConnected { get; private set; }
        public int Disposals { get; private set; }
        public ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken token)
        {
            IsConnected = true;
            return ValueTask.CompletedTask;
        }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            SendStarted.TrySetResult();
            if (stall) await Task.Delay(Timeout.Infinite, token);
            return payload.Length;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            started.TrySetResult();
            await release.Task;
            IsConnected = false;
            if (fail) throw new IOException("Native cleanup failed.");
        }
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

	[Theory]
	[InlineData(false, 50, 1)]
	[InlineData(false, 60_000, 1_001)]
	[InlineData(true, 50, 1)]
	[InlineData(true, 60_000, 1_001)]
	public async Task Stalled_consumer_burst_bounds_queue_and_wakeups_and_recovers(bool ndi, long numerator, long denominator)
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var transport = new BlockingTransport(gate.Task);
		var sender = new BlockingNdiSender(gate.Task);
		await using INetworkOutputSession session = ndi
			? new NdiNetworkOutputSession(NdiConfiguration("Bounded Program", queueCapacity: 1), _ => sender)
			: new SrtNetworkOutputSession(
				Configuration(new Uri("srt://127.0.0.1:9000/live"), queueCapacity: 1),
				() => transport, () => new PassThroughEncoder());
		var timebase = new Timebase(denominator, numerator);
		try
		{
			Assert.True(session.TrySubmit(Sample(1, timebase, 0, 800)).Accepted);
			await (ndi ? sender.SendStarted.Task : transport.ConnectStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
			TestSample? previous = null;
			for (ulong sequence = 2; sequence <= 10_002; sequence++)
			{
				var sample = Sample(sequence, timebase, sequence * 800, 800);
				Assert.True(session.TrySubmit(sample).Accepted);
				if (previous is not null)
					Assert.True(previous.VideoLease.IsDisposed);
				previous = sample;
			}
			var saturated = session.Snapshot.Statistics;
			Assert.Equal(1, saturated.QueueDepth);
			Assert.Equal(1, saturated.MaximumQueueDepth);
			Assert.Equal(1, saturated.QueueCapacity);
			Assert.True(saturated.Backpressured);
			Assert.Equal(10_000UL, saturated.DroppedSamples);
			var field = session.GetType().GetField("_queueSignal",
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
			Assert.Equal(1, Assert.IsType<SemaphoreSlim>(field.GetValue(session)).CurrentCount);
			gate.TrySetResult();
			await WaitUntilAsync(() => session.Snapshot.Statistics.SentSamples == 2, TimeSpan.FromSeconds(2));
			var recovered = session.Snapshot.Statistics;
			Assert.Equal(0, recovered.QueueDepth);
			Assert.Equal(1, recovered.MaximumQueueDepth);
			Assert.False(recovered.Backpressured);
		}
		finally
		{
			gate.TrySetResult();
		}
	}

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
		public int DisposeCount { get; private set; }
		public ReadOnlyMemory<byte> Memory => IsDisposed ? ReadOnlyMemory<byte>.Empty : _memory;
		public void Dispose()
		{
			DisposeCount++;
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
		public int DisposeCount { get; private set; }
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
			DisposeCount++;
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

	private sealed class PassThroughEncoder(bool failInitialization = false) : ISrtPayloadEncoder
	{
		public int DisposeCount { get; private set; }
		public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken cancellationToken)
		{
			_ = configuration;
			cancellationToken.ThrowIfCancellationRequested();
			if (failInitialization) throw new IOException("Synthetic encoder initialization failure.");
			return ValueTask.CompletedTask;
		}
		public ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0x47, 0x40, 0x00, 0x10 });
		}
		public ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
		public ValueTask DisposeAsync()
		{
			DisposeCount++;
			return ValueTask.CompletedTask;
		}
	}
}
