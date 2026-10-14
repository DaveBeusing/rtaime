// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Provider.Ndi;
using rtaime.Provider.Srt;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeConsumerIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_consumers_preserve_Program_cadence_audio_ownership_and_join_shutdown(bool use5994) =>
        await RunMixedAsync(use5994);

    internal async Task RunMixedAsync(bool use5994, Action<object, IReadOnlyList<double>>? observe = null)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var root = Path.Combine(Path.GetTempPath(), "rtaime-consumer-isolation-" + Guid.NewGuid().ToString("N"));
        var recordingWriter = new StalledWriter();
        var replayWriter = new StalledWriter();
        var ndiSender = new StalledNdiSender();
        var transport = new StalledTransport();
        var aiTransport = new TimedOutAI();
        var network = new NetworkRegistry(ndiSender, transport);
        var targets = new[]
        {
            new RuntimeNetworkOutputTarget("program", new NetworkOutputConfiguration("isolated-ndi",
                NetworkOutputProtocolFamily.Ndi, format, AudioFormat.Stereo48kFloat32,
                new NdiNetworkOutputSettings("Isolation"), queueCapacity: 2)),
            new RuntimeNetworkOutputTarget("aux", new NetworkOutputConfiguration("isolated-srt",
                new Uri("srt://127.0.0.1:9000/aux"), NetworkOutputProtocolFamily.Srt,
                NetworkOutputConnectionMode.Caller, format, AudioFormat.Stereo48kFloat32,
                NetworkOutputVideoCodec.H264, NetworkOutputAudioCodec.AacLc, 12_000_000, 192_000,
                120, queueCapacity: 2))
        };
        var replay = new RuntimeReplayService(root, format, writerFactory: () => replayWriter, queueCapacity: 2);
        var period = TimeSpan.FromSeconds((double)format.FrameRate.Denominator / format.FrameRate.Numerator);
        var probe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(period,
            TimeSpan.FromTicks(period.Ticks / 4), period, 4096));
        var a = new ProductionSourceId(Identity.New());
        var b = new ProductionSourceId(Identity.New());
        var mediaA = new MediaSourceId(a.Value);
        var mediaB = new MediaSourceId(b.Value);
        var runtime = new V1RuntimeHostService(mediaA, mediaB, format, recordingWriter,
            new ManagedReferenceGpuBackend(), targets, replay, probe, network);
        var ai = new RuntimeAIShowcaseService(runtime, aiTransport);
        RuntimeMonitoringSubscription? monitor = null;
        var specification = new ProductionSpecification(ControlContractVersion.Current,
            new ProductionId(Identity.New()), "Consumer isolation", new[]
            {
                new ProductionSourceSpecification(a, "Media A"), new ProductionSourceSpecification(b, "Media B")
            }, new ProductionRoutingState(b, a), initialOutputRoles: new[]
            {
                ProductionOutputRoleState.Program(a), ProductionOutputRoleState.Aux(b)
            });
        var state = ControlDomainEngine.Initialize(specification).State!.Authoritative;
        var plan = CapabilityPlanningEngine.Plan(specification, state, new Registry(runtime.ProviderDescriptors));
        Assert.True(plan.Succeeded);
        var prepared = plan.PreparedExecution!;
        var sink = plan.Graph!.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink).MediaSinkId!.Value;
        var sourcePixels = RgbaFrameBuffer.Solid(format, 22, 44, 66);
        var retainedHash = Array.Empty<byte>();
        Task? shutdown = null;
        Task? repeatedShutdown = null;
        try
        {
            Assert.True(runtime.ApplyExecution(prepared, sink).Committed);
            runtime.SetExternalInputContent(mediaA, sourcePixels);
            runtime.SetVisualLayerMode(V1VisualLayerMode.Static);
            runtime.LoadGraphicsOverlay("Isolation graphic", 1, 1, new byte[] { 200, 10, 20, 160 });
            runtime.SetGraphicsOverlay(true, .25, .25, 1);
            runtime.SetAudioProductionConfiguration(new AudioProductionConfiguration(1,
                new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1, false) },
                new[]
                {
                    new AudioProductionSourceConfiguration(mediaA, .5, false, false, new[] { AudioBusId.Program }),
                    new AudioProductionSourceConfiguration(mediaB, .5, false, false, new[] { AudioBusId.Program })
                }));
            monitor = runtime.MonitoringHub.Subscribe(capacity: 2);
            Assert.True((await runtime.StartRecordingAsync(RecordingSessionId.New(), RecordingOutputId.New())).Succeeded);
            ai.SetEnabled(true);
            const int boundaries = ProgramRecorder.DefaultQueueCapacity + 16;
            const int warmup = 3;
            var trace = new List<object>(boundaries);
            var durations = new List<double>(boundaries - warmup);
            var clock = Stopwatch.StartNew();
            var schedule = new RationalFrameSchedule(format.FrameRate, TimeSpan.Zero);
            ulong missedSlots = 0;
            ulong monitorDrops = 0;
            ulong audioFrames = 0;
            var maximumReadbackBuffers = 0;
            var maximumMonitoringDepth = 0;
            var transitions = new HashSet<RuntimeProgramTransitionKind>();
            for (var index = 0; index < boundaries; index++)
            {
                if (index == 8 || index == 16)
                {
                    var target = index == 8 ? mediaB : mediaA;
                    var from = index == 8 ? mediaA : mediaB;
                    var bindings = prepared.Bindings.Select(binding => binding.OutputRoleId == "program"
                        ? new PreparedExecutionBinding(binding.LogicalNodeId, binding.CapabilityId, binding.Resource,
                            target, binding.MediaSinkId, binding.OutputRoleId, binding.AudioBusId,
                            binding.ExternalSourceId, binding.SafeSourceIdentity)
                        : binding).ToArray();
                    prepared = new PreparedExecutionContract(RuntimeContractVersion.Current, PreparedExecutionId.New(),
                        new AuthoritySnapshotReference(prepared.AuthoritySnapshot.StateId, prepared.AuthoritySnapshot.Revision.Next()),
                        prepared.PlanGeneration.Next(), bindings, prepared.CompositingState);
                    Assert.True(runtime.ApplyExecution(prepared, sink, index == 8
                        ? RuntimeProgramTransitionIntent.Dissolve(from, target, 4)
                        : RuntimeProgramTransitionIntent.Cut(from, target)).Committed);
                }
                if (index == 12)
                {
                    runtime.SetExternalInputContent(mediaA, RgbaFrameBuffer.Solid(format, 88, 77, 66));
                    await monitor.DisposeAsync();
                    monitorDrops += monitor.DroppedFrames;
                    monitor = runtime.MonitoringHub.Subscribe(capacity: 1);
                }
                if (index == ProgramRecorder.DefaultQueueCapacity + 2)
                {
                    Assert.False(recordingWriter.Release.Task.IsCompleted);
                    Assert.False(replayWriter.Release.Task.IsCompleted);
                    Assert.False(ndiSender.Release.Task.IsCompleted);
                    Assert.False(transport.Release.Task.IsCompleted);
                    Assert.True(runtime.Snapshot.Recording.Statistics.Dropped > 0);
                    Assert.True(replay.Snapshot.Statistics.DroppedSamples > 0);
                    Assert.True(network.Ndi!.Snapshot.Statistics.DroppedSamples > 0);
                    Assert.True(network.Srt!.Snapshot.Statistics.DroppedSamples > 0);
                    Assert.Equal(retainedHash, recordingWriter.RetainedHash());
                    recordingWriter.Release.TrySetResult();
                    replayWriter.Release.TrySetResult();
                    ndiSender.Release.TrySetResult();
                    transport.Release.TrySetResult();
                    aiTransport.Release.TrySetResult();
                }
                FrameScheduleOpportunity opportunity;
                while (!schedule.TryTake(clock.Elapsed, out opportunity))
                {
                    var wait = schedule.NextDeadline - clock.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait);
                }
                missedSlots += opportunity.MissedSlots;
                var started = Stopwatch.GetTimestamp();
                using (var boundary = runtime.ProcessNextBoundary())
                {
                    ai.ObserveProgramBoundary(boundary.ProgramFrame);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    if (index >= warmup) durations.Add(elapsed.TotalMilliseconds);
                    Assert.Equal((ulong)index, boundary.SequenceNumber);
                    Assert.Equal((ulong)index, boundary.ProgramFrame.Timing.SequenceNumber);
                    var window = AudioVideoTimingRelationship.GetSampleWindow(format.FrameRate, 48_000, (ulong)index);
                    Assert.Equal(window.SamplePosition, boundary.ProgramAudioBuffer.Timing.SamplePosition);
                    Assert.Equal(window.SampleCount, boundary.ProgramAudioBuffer.Timing.SampleCount);
                    Assert.Equal(window.PresentationTimestamp, boundary.ProgramAudioBuffer.Timing.PresentationTimestamp);
                    Assert.Equal(window.Timebase, boundary.ProgramAudioBuffer.Timing.Timebase);
                    Assert.Equal(audioFrames, window.SamplePosition);
                    audioFrames += window.SampleCount;
                    Assert.Equal(0, boundary.ActiveGpuSurfacesAfterBoundary);
                    if (boundary.TransitionKind is { } kind) transitions.Add(kind);
                    maximumReadbackBuffers = Math.Max(maximumReadbackBuffers, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
                    maximumMonitoringDepth = Math.Max(maximumMonitoringDepth, monitor.QueueStatistics.QueueDepth);
                    Assert.InRange(runtime.ProgramReadbackPoolStatistics.ActiveBuffers, 0, runtime.ProgramReadbackPoolStatistics.Capacity);
                    Assert.InRange(monitor.QueueStatistics.QueueDepth, 0, monitor.QueueStatistics.QueueCapacity);
                    probe.TryObserveStage(RuntimePipelineTimingStage.SchedulerStartLateness, boundary.SequenceNumber, opportunity.StartLateness);
                    var lateness = clock.Elapsed - opportunity.PresentationDeadline;
                    probe.TryObserveStage(RuntimePipelineTimingStage.PresentationLateness, boundary.SequenceNumber,
                        lateness > TimeSpan.Zero ? lateness : TimeSpan.Zero);
                    trace.Add(new { sequence = boundary.SequenceNumber, source = boundary.ProgramFrame.SourceId.ToString(),
                        videoTiming = boundary.ProgramFrame.Timing, audioTiming = boundary.ProgramAudioBuffer.Timing,
                        virtualOutputAccepted = true, schedulerSlot = opportunity.Slot,
                        opportunity.MissedSlots, startLatenessTicks = opportunity.StartLateness.Ticks,
                        presentationLatenessTicks = Math.Max(0, lateness.Ticks) });
                    if (index == 0) retainedHash = SHA256.HashData(boundary.ProgramPixels.Span);
                }
                if (index == warmup - 1) probe.ResetPipelineSamples();
                if (index == 0)
                {
                    await Task.WhenAll(recordingWriter.Started.Task, replayWriter.Started.Task,
                        ndiSender.Started.Task, transport.Started.Task, aiTransport.Started.Task)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            await UntilAsync(() => network.Ndi!.Snapshot.Statistics.SentSamples > 0 &&
                network.Srt!.Snapshot.Statistics.SentSamples > 0 && ai.Snapshot.Status == RuntimeAIShowcaseStates.Timeout);
            Assert.True(network.Ndi!.Snapshot.Statistics.ReconnectCount > 0);
            Assert.Equal(0UL, network.Ndi.Snapshot.Statistics.RejectedSamples);
            Assert.Equal(0UL, network.Srt!.Snapshot.Statistics.RejectedSamples);
            Assert.Contains(RuntimeProgramTransitionKind.Cut, transitions);
            Assert.Contains(RuntimeProgramTransitionKind.Dissolve, transitions);
            Assert.Equal((ulong)boundaries, runtime.Snapshot.NextSequenceNumber);
            var recordingStats = runtime.Snapshot.Recording.Statistics;
            var replayStats = replay.QueueStatistics;
            Assert.InRange(recordingStats.MaximumQueueDepth, 1, recordingStats.QueueCapacity);
            Assert.InRange(replayStats.MaximumQueueDepth, 1, replayStats.QueueCapacity);
            Assert.Equal((ulong)(boundaries * 2), runtime.GpuMemoryTransfers.ReadbackOperations); // One Program readback plus distinct Aux.
            await monitor.DisposeAsync();
            monitorDrops += monitor.DroppedFrames;
            monitor = null;
            await ai.DisposeAsync();
            shutdown = runtime.DisposeAsync().AsTask();
            await replayWriter.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            repeatedShutdown = runtime.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.False(repeatedShutdown.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => runtime.ProcessNextBoundary());
            replayWriter.FinalizeRelease.TrySetResult();
            await recordingWriter.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(repeatedShutdown.IsCompleted);
            recordingWriter.FinalizeRelease.TrySetResult();
            await Task.WhenAll(shutdown, repeatedShutdown).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
            Assert.Equal(0, replay.QueueStatistics.QueueDepth);
            Assert.Equal(0, network.Ndi.Snapshot.Statistics.QueueDepth);
            Assert.Equal(0, network.Srt.Snapshot.Statistics.QueueDepth);
            Assert.Equal(1, recordingWriter.Finalizations);
            Assert.Equal(1, replayWriter.Finalizations);
            var ordered = durations.Order().ToArray();
            double P(double rank) => ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * rank) - 1, 0, ordered.Length - 1)];
            var evidence = new
            {
                scope = "V1_SERVICE_WITH_SIMULATED_CONSUMER_BACKENDS_AND_GOVERNED_AI_OBSERVATION",
                measurementKind = "CPU_WALL", warmupBoundaries = warmup, measuredBoundaries = ordered.Length,
                p50Milliseconds = P(.5), p95Milliseconds = P(.95), p99Milliseconds = P(.99), maximumMilliseconds = ordered[^1],
                pipelineTiming = probe.PipelineSnapshot(), missedSchedulerSlots = missedSlots,
                virtualOutputAccepted = boundaries, audioFrames, audioDriftSamples = 0,
                recording = recordingStats, replay = replayStats,
                ndi = network.Ndi.Snapshot.Statistics, srt = network.Srt.Snapshot.Statistics,
                maximumReadbackBuffers, maximumMonitoringDepth, monitorDrops,
                singleProgramReadback = true, nativeTransport = "UNVERIFIED", physicalOutput = "UNVERIFIED",
                gpuAndLongSoak = "UNVERIFIED", trace,
                replayDroppedSamples = replay.Snapshot.Statistics.DroppedSamples
            };
            if (observe is null) WriteEvidence(use5994, evidence);
            else observe(evidence, ordered);
        }
        finally
        {
            recordingWriter.Release.TrySetResult();
            replayWriter.Release.TrySetResult();
            recordingWriter.FinalizeRelease.TrySetResult();
            replayWriter.FinalizeRelease.TrySetResult();
            ndiSender.Release.TrySetResult();
            transport.Release.TrySetResult();
            aiTransport.Release.TrySetResult();
            if (monitor is not null) await monitor.DisposeAsync();
            await ai.DisposeAsync();
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (shutdown is not null) await shutdown;
            if (repeatedShutdown is not null) await repeatedShutdown;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_failure_still_drains_other_sessions_and_repeat_shutdown_reports_same_failure(bool throughRuntime)
    {
        var format = VideoFormat.Hd1080p50Rgba8;
        var targets = new[]
        {
            new RuntimeNetworkOutputTarget("program", new NetworkOutputConfiguration("cleanup-a",
                NetworkOutputProtocolFamily.Ndi, format, AudioFormat.Stereo48kFloat32, new NdiNetworkOutputSettings("A"))),
            new RuntimeNetworkOutputTarget("aux", new NetworkOutputConfiguration("cleanup-b",
                NetworkOutputProtocolFamily.Ndi, format, AudioFormat.Stereo48kFloat32, new NdiNetworkOutputSettings("B")))
        };
        var registry = new CleanupRegistry();
        var writer = new StalledWriter();
        writer.Release.TrySetResult();
        writer.FinalizeRelease.TrySetResult();
        var runtime = throughRuntime ? new V1RuntimeHostService(MediaSourceId.New(), MediaSourceId.New(),
            format, writer, new ManagedReferenceGpuBackend(), targets, null, null, registry) : null;
        var bridge = throughRuntime ? null : new RuntimeNetworkOutputBridge(targets, registry);
        async Task DisposeAsync()
        {
            if (runtime is not null) await runtime.DisposeAsync();
            else await bridge!.DisposeAsync();
        }
        var first = await Assert.ThrowsAsync<IOException>(DisposeAsync);
        var repeated = await Assert.ThrowsAsync<IOException>(DisposeAsync);
        Assert.Same(first, repeated);
        Assert.Equal(2, registry.Sessions.Count);
        Assert.All(registry.Sessions, session => Assert.Equal(1, session.Disposals));
        if (runtime is not null)
        {
            Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
            Assert.Throws<ObjectDisposedException>(() => runtime.ProcessNextBoundary());
        }
    }

    private sealed class CleanupRegistry : IRuntimeNetworkOutputProviderRegistry
    {
        public List<CleanupSession> Sessions { get; } = new();
        public IReadOnlyList<ProviderDescriptor> ProviderDescriptorsFor(IReadOnlyList<RuntimeNetworkOutputTarget> targets) =>
            Array.Empty<ProviderDescriptor>();
        public INetworkOutputSession CreateSession(RuntimeNetworkOutputTarget target)
        {
            var session = new CleanupSession(target.Configuration, Sessions.Count == 0);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class CleanupSession(NetworkOutputConfiguration configuration, bool fail) : INetworkOutputSession
    {
        public NetworkOutputConfiguration Configuration => configuration;
        public NetworkOutputHealthSnapshot Snapshot => throw new NotSupportedException();
        public int Disposals { get; private set; }
        public NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (fail) throw new IOException("Native session cleanup failed.");
            return ValueTask.CompletedTask;
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Consumers did not recover.");
            await Task.Delay(10);
        }
    }

    private static void WriteEvidence(bool use5994, object evidence)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        Console.WriteLine(JsonSerializer.Serialize(evidence, options));
        var root = Environment.GetEnvironmentVariable("RTAIME_PIPELINE_BASELINE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        var source = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT");
        if (source is null || source.Length != 40 || source.Any(character => !Uri.IsHexDigit(character)) ||
            source != Environment.GetEnvironmentVariable("RTAIME_BUILD_COMMIT") ||
            Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK") != "10.0.401")
            throw new InvalidOperationException("Consumer evidence requires matching source/build SHA and pinned SDK.");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, $"consumer-isolation-{use5994}.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0", sourceCommit = source, buildCommit = source, sdk = "10.0.401",
            format = use5994 ? "1080p59.94" : "1080p50", evidence
        }, options) + Environment.NewLine);
    }

    private sealed class Registry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
    {
        public IReadOnlyList<ProviderDescriptor> GetProviders() => providers;
    }

    private sealed class NetworkRegistry(StalledNdiSender firstSender, StalledTransport transport) : IRuntimeNetworkOutputProviderRegistry
    {
        private int _senders;
        public NdiNetworkOutputSession? Ndi { get; private set; }
        public SrtNetworkOutputSession? Srt { get; private set; }
        public IReadOnlyList<ProviderDescriptor> ProviderDescriptorsFor(IReadOnlyList<RuntimeNetworkOutputTarget> targets) =>
            Array.Empty<ProviderDescriptor>(); // Simulated adapters do not advertise native provider qualification.
        public INetworkOutputSession CreateSession(RuntimeNetworkOutputTarget target) =>
            target.Configuration.Protocol == NetworkOutputProtocolFamily.Ndi
                ? Ndi = new NdiNetworkOutputSession(target.Configuration, _ =>
                    Interlocked.Increment(ref _senders) == 1 ? firstSender : new StalledNdiSender(released: true))
                : Srt = new SrtNetworkOutputSession(target.Configuration, () => transport, () => new Encoder());
    }

    private sealed class StalledNdiSender : INdiSender
    {
        private readonly bool _failFirst;
        public StalledNdiSender(bool released = false)
        {
            _failFirst = !released;
            if (released) Release.TrySetResult();
        }
        public bool IsReady => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            if (_failFirst) throw new IOException("NDI disconnect.");
            return new NdiSendResult(1, (ulong)sample.Video.Memory.Length);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StalledTransport : ISrtTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected { get; private set; }
        public ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken token)
        {
            IsConnected = true;
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return payload.Length;
        }
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }

    private sealed class Encoder : ISrtPayloadEncoder
    {
        public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken token) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0x47 });
        public ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken token) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimedOutAI : IRuntimeAIHostTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<RuntimePersonSegmentationResult> ExecutePersonSegmentationAsync(FrameDescriptor frame, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            throw new TimeoutException("AI request deadline expired.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StalledWriter : IReplaySegmentWriter
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, IProgramRecordingPayloadLease> _staged = new();
        private IProgramRecordingPayloadLease? _current;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finalizing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalizeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Finalizations { get; private set; }
        public string? FinalPath { get; private set; }
        public string ConfigureTarget(string directory, string name) { FinalPath = Path.Combine(directory, name); return name; }
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken token) => ValueTask.CompletedTask;
        public void StagePayload(ulong sequence, IProgramRecordingPayloadLease payload, ReadOnlyMemory<byte> audio)
        {
            lock (_gate) _staged.Add(sequence, payload);
        }
        public byte[] RetainedHash() { lock (_gate) return SHA256.HashData(_current!.Memory.Span); }
        public void DiscardPayload(ulong sequence)
        {
            lock (_gate) { if (_staged.Remove(sequence, out var lease)) lease.Dispose(); }
        }
        public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken token)
        {
            IProgramRecordingPayloadLease payload;
            lock (_gate) { payload = _staged[sample.SequenceNumber]; _staged.Remove(sample.SequenceNumber); _current = payload; }
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            finally { lock (_gate) { _current = null; payload.Dispose(); } }
        }
        public async ValueTask FinalizeAsync(CancellationToken token)
        {
            Finalizing.TrySetResult();
            await FinalizeRelease.Task.WaitAsync(token);
            Finalizations++;
            if (FinalPath is not null) File.WriteAllBytes(FinalPath, new byte[] { 1 });
        }
        public ValueTask AbortAsync(CancellationToken token)
        {
            lock (_gate) { foreach (var lease in _staged.Values) lease.Dispose(); _staged.Clear(); }
            return ValueTask.CompletedTask;
        }
    }
}
