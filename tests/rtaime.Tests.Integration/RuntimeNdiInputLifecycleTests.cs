// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Ndi;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeNdiInputLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly VideoFormat Format = VideoFormat.Hd1080p50Rgba8;
    private static readonly NdiDiscoveredSourceEndpoint First = new("Fixture (First)", "ndi://fixture/first");
    private static readonly NdiDiscoveredSourceEndpoint Second = new("Fixture (Second)", "ndi://fixture/second");

    [Theory]
    [InlineData(NdiInputMediaKind.Video, false)]
    [InlineData(NdiInputMediaKind.Audio, false)]
    [InlineData(NdiInputMediaKind.Video, true)]
    [InlineData(NdiInputMediaKind.Audio, true)]
    public async Task Removal_and_replacement_retire_paused_publication_without_stalling_Program(
        NdiInputMediaKind kind, bool replace)
    {
        using var publication = new PausePoint();
        await using var fixture = new Fixture((_, _) => publication.WaitOnce());
        Assert.True(fixture.Apply(First).Committed);
        var owner = fixture.Owner;
        var receiver = fixture.Receiver(First);
        receiver.Publish(Sample(kind));
        Task<RuntimeHostApplyResult>? mutation = null;
        try
        {
            await publication.Entered.Task.WaitAsync(Deadline);
            mutation = Task.Run(() => fixture.Apply(replace ? Second : null));
            await WaitForRevisionAsync(fixture.Runtime, 2);

            // Retirement is waiting for the deliberately paused pump, after releasing
            // all Program and Runtime locks. A real boundary must still complete.
            Assert.False(mutation.IsCompleted);
            using (var boundary = await Task.Run(fixture.Runtime.ProcessNextBoundary).WaitAsync(Deadline))
            {
                Assert.Equal(0UL, boundary.SequenceNumber);
                Assert.Equal(fixture.ProgramSource, boundary.CommittedProgramSourceId);
            }
            Assert.False(fixture.Runtime.IsNdiInputCurrent(owner));

            publication.Release();
            Assert.True((await mutation.WaitAsync(Deadline)).Committed);
            Assert.Equal(1, receiver.DisposalCount);
            Assert.False(fixture.Runtime.TrySetNdiInputContent(owner, VideoPixels()));
            Assert.False(fixture.Runtime.TrySetNdiAudioInput(owner, AudioPayload()));
            Assert.False(fixture.Runtime.TrySetNdiInputHealth(owner, RuntimeInputSignalState.Valid));

            if (replace)
            {
                var current = fixture.Owner;
                Assert.NotSame(owner, current);
                var replacement = fixture.Receiver(Second);
                replacement.Publish(Sample(NdiInputMediaKind.Video));
                replacement.Publish(Sample(NdiInputMediaKind.Audio));
                await WaitUntilAsync(() => fixture.Runtime.Snapshot.InputSignals[fixture.InputSource] == RuntimeInputSignalState.Valid &&
                    fixture.Runtime.Snapshot.AudioInputs[fixture.InputSource].LeftPeak > 0);
                Assert.False(fixture.Runtime.TrySetNdiInputHealth(owner, RuntimeInputSignalState.Lost));
                Assert.Equal(RuntimeInputSignalState.Valid, fixture.Runtime.Snapshot.InputSignals[fixture.InputSource]);
                Assert.Equal(NdiDiscoveryService.CreateStableId(Second.NdiName),
                    Assert.Single(fixture.Runtime.NdiInputHealth).DiscoveredSourceId);
            }
            else
            {
                Assert.Empty(fixture.Runtime.NdiInputHealth);
                Assert.Equal(RuntimeInputSignalState.Lost, fixture.Runtime.Snapshot.InputSignals[fixture.InputSource]);
                Assert.Equal(0, fixture.Runtime.Snapshot.AudioInputs[fixture.InputSource].LeftPeak);
            }

            using var next = fixture.Runtime.ProcessNextBoundary();
            Assert.Equal(1UL, next.SequenceNumber);
        }
        finally
        {
            publication.Release();
            if (mutation is not null)
                await mutation.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task Unchanged_binding_reuses_owner_and_receiver_while_publication_is_paused()
    {
        using var publication = new PausePoint();
        await using var fixture = new Fixture((_, _) => publication.WaitOnce());
        Assert.True(fixture.Apply(First).Committed);
        var owner = fixture.Owner;
        fixture.Receiver(First).Publish(Sample(NdiInputMediaKind.Video));
        try
        {
            await publication.Entered.Task.WaitAsync(Deadline);
            var result = await Task.Run(() => fixture.Apply(First)).WaitAsync(Deadline);
            Assert.True(result.Committed);
            Assert.Same(owner, fixture.Owner);
            Assert.Single(fixture.Receivers);
            Assert.Equal(0, fixture.Receiver(First).DisposalCount);
            publication.Release();
            await WaitUntilAsync(() => fixture.Runtime.Snapshot.InputSignals[fixture.InputSource] == RuntimeInputSignalState.Valid);
        }
        finally
        {
            publication.Release();
        }
    }

    [Fact]
    public async Task New_external_Program_source_is_registered_before_discovery_and_replacement_clears_old_pixels()
    {
        using var discovery = new PausePoint();
        using var rediscovery = new PausePoint();
        await using var fixture = new Fixture();
        var externalSource = MediaSourceId.New();
        fixture.BeforeDiscovery = discovery.WaitOnce;
        var activation = Task.Run(() => fixture.ApplyExternalProgramSource(First, externalSource));
        Task<RuntimeHostApplyResult>? replacement = null;
        try
        {
            await discovery.Entered.Task.WaitAsync(Deadline);
            var snapshot = await Task.Run(() => fixture.Runtime.Snapshot).WaitAsync(Deadline);
            Assert.Equal(1UL, snapshot.Runtime.ExecutionRevision.Value);
            Assert.Equal(RuntimeInputSignalState.Recovering, snapshot.InputSignals[externalSource]);
            Assert.True(snapshot.AudioInputs.ContainsKey(externalSource));
            using (var pending = await Task.Run(fixture.Runtime.ProcessNextBoundary).WaitAsync(Deadline))
            {
                Assert.Equal(0UL, pending.SequenceNumber);
                Assert.Equal(externalSource, pending.CommittedProgramSourceId);
                Assert.Equal(new ProgramPixelProbe(0, 0, 0, 255), pending.PixelProbe);
            }

            discovery.Release();
            Assert.True((await activation.WaitAsync(Deadline)).Committed);
            fixture.Receiver(First).Publish(Sample(NdiInputMediaKind.Video));
            await WaitUntilAsync(() => fixture.Runtime.Snapshot.InputSignals[externalSource] == RuntimeInputSignalState.Valid);
            using (var live = fixture.Runtime.ProcessNextBoundary())
            {
                Assert.Equal(1UL, live.SequenceNumber);
                Assert.Equal(new ProgramPixelProbe(80, 40, 20, 255), live.PixelProbe);
            }

            fixture.BeforeDiscovery = rediscovery.WaitOnce;
            replacement = Task.Run(() => fixture.ApplyExternalProgramSource(Second, externalSource));
            await rediscovery.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(1, fixture.Receiver(First).DisposalCount);
            using (var replaced = await Task.Run(fixture.Runtime.ProcessNextBoundary).WaitAsync(Deadline))
            {
                Assert.Equal(2UL, replaced.SequenceNumber);
                Assert.Equal(externalSource, replaced.CommittedProgramSourceId);
                Assert.Equal(new ProgramPixelProbe(0, 0, 0, 255), replaced.PixelProbe);
            }
            rediscovery.Release();
            Assert.True((await replacement.WaitAsync(Deadline)).Committed);
        }
        finally
        {
            discovery.Release();
            rediscovery.Release();
            await activation.WaitAsync(Deadline);
            if (replacement is not null)
                await replacement.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task Registration_and_native_lifecycle_run_without_Runtime_or_boundary_locks()
    {
        await using var fixture = new Fixture();
        var lockFreeCalls = 0;
        fixture.BeforeDiscovery = () => CheckLocks();
        fixture.BeforeCreation = _ => CheckLocks();
        fixture.BeforeReceiverDisposal = () => CheckLocks();
        void CheckLocks()
        {
            foreach (var name in new[] { "_gate", "_boundaryCaptureGate", "_boundaryExecutionGate" })
                Assert.False(Monitor.IsEntered(GetField<object>(fixture.Runtime, name)), name);
            Interlocked.Increment(ref lockFreeCalls);
        }

        Assert.True((await Task.Run(() => fixture.Apply(First)).WaitAsync(Deadline)).Committed);
        fixture.Receiver(First).Publish(Sample(NdiInputMediaKind.Video));
        await WaitUntilAsync(() => fixture.Runtime.Snapshot.InputSignals[fixture.InputSource] == RuntimeInputSignalState.Valid);
        Assert.True((await Task.Run(() => fixture.Apply(null)).WaitAsync(Deadline)).Committed);
        Assert.True(lockFreeCalls >= 3);
        Assert.Equal(1, fixture.Receiver(First).DisposalCount);
        using var boundary = fixture.Runtime.ProcessNextBoundary();
        Assert.Equal(fixture.ProgramSource, boundary.CommittedProgramSourceId);
    }

    [Fact]
    public async Task Superseded_registration_disposes_its_receiver_and_cannot_restore_an_old_binding()
    {
        using var creation = new PausePoint();
        await using var fixture = new Fixture();
        fixture.BeforeCreation = _ => creation.WaitOnce();
        var first = Task.Run(() => fixture.Apply(First));
        Task<RuntimeHostApplyResult>? replacement = null;
        try
        {
            await creation.Entered.Task.WaitAsync(Deadline);
            var oldOwner = fixture.Owner;
            replacement = Task.Run(() => fixture.Apply(Second));
            await WaitForRevisionAsync(fixture.Runtime, 2);
            Assert.False(fixture.Runtime.IsNdiInputCurrent(oldOwner));
            using (var boundary = await Task.Run(fixture.Runtime.ProcessNextBoundary).WaitAsync(Deadline))
                Assert.Equal(0UL, boundary.SequenceNumber);

            creation.Release();
            Assert.True((await first.WaitAsync(Deadline)).Committed);
            Assert.True((await replacement.WaitAsync(Deadline)).Committed);
            Assert.Equal(1, fixture.Receiver(First).DisposalCount);
            Assert.Equal(0, fixture.Receiver(Second).DisposalCount);
            var current = fixture.Owner;
            var bridge = GetField<RuntimeNdiInputBridge>(fixture.Runtime, "_ndiInputBridge");
            bridge.SynchronizeCommittedBindings(new RuntimeNdiBindingSnapshot(1, [oldOwner]));
            Assert.Same(current, fixture.Owner);
            Assert.Equal(2, fixture.Receivers.Count);
            Assert.False(fixture.Runtime.TryRegisterNdiSource(oldOwner));
            Assert.Equal(NdiDiscoveryService.CreateStableId(Second.NdiName),
                Assert.Single(fixture.Runtime.NdiInputHealth).DiscoveredSourceId);
        }
        finally
        {
            creation.Release();
            await first.WaitAsync(Deadline);
            if (replacement is not null)
                await replacement.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(NdiInputMediaKind.Video)]
    [InlineData(NdiInputMediaKind.Audio)]
    public async Task Shutdown_joins_paused_publication_once_and_rejects_late_media(NdiInputMediaKind kind)
    {
        using var publication = new PausePoint();
        await using var fixture = new Fixture((_, _) => publication.WaitOnce());
        Assert.True(fixture.Apply(First).Committed);
        var owner = fixture.Owner;
        fixture.Receiver(First).Publish(Sample(kind));
        try
        {
            await publication.Entered.Task.WaitAsync(Deadline);
            var shutdown = (await Task.Run(() => fixture.Runtime.DisposeAsync()).WaitAsync(Deadline)).AsTask();
            var repeated = (await Task.Run(() => fixture.Runtime.DisposeAsync()).WaitAsync(Deadline)).AsTask();
            Assert.Same(shutdown, repeated);
            Assert.False(shutdown.IsCompleted);
            Assert.False(fixture.Runtime.IsNdiInputCurrent(owner));
            publication.Release();
            await shutdown.WaitAsync(Deadline);
            Assert.Equal(1, fixture.Receiver(First).DisposalCount);
            Assert.False(fixture.Runtime.TrySetNdiInputContent(owner, VideoPixels()));
            Assert.False(fixture.Runtime.TrySetNdiAudioInput(owner, AudioPayload()));
        }
        finally
        {
            publication.Release();
        }
    }

    private static async Task WaitForRevisionAsync(RuntimeProgramExecutionService runtime, ulong revision) =>
        await WaitUntilAsync(() => runtime.Snapshot.Runtime.ExecutionRevision.Value == revision);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var limit = DateTimeOffset.UtcNow + Deadline;
        while (!await Task.Run(predicate).WaitAsync(Deadline))
        {
            if (DateTimeOffset.UtcNow >= limit)
                throw new TimeoutException("Expected bounded NDI lifecycle progress was not observed.");
            await Task.Delay(5);
        }
    }

    private static byte[] VideoPixels()
    {
        var pixels = new byte[checked((int)(Format.Width * Format.Height * 4))];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 80;
            pixels[index + 1] = 40;
            pixels[index + 2] = 20;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static byte[] AudioPayload() => MemoryMarshal.AsBytes(new float[] { .8f, -.4f }.AsSpan()).ToArray();

    private static NdiInputMediaSample Sample(NdiInputMediaKind kind) => kind == NdiInputMediaKind.Video
        ? NdiInputMediaSample.FromVideo(new NdiInputVideoFrame(Format, 1, VideoPixels()))
        : NdiInputMediaSample.FromAudio(new NdiInputAudioFrame(AudioFormat.Stereo48kFloat32, 1, 1, AudioPayload()));

    private static T GetField<T>(object target, string name) where T : class =>
        Assert.IsType<T>(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));

    private sealed class PausePoint : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _armed = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void WaitOnce()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
                return;
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("NDI lifecycle barrier was not released.");
        }
        public void Release() => _release.Set();
        public void Dispose() { Release(); _release.Dispose(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PreparedExecutionContract _baseline;
        private readonly ProviderDescriptor _ndi;
        private readonly MediaSinkId _sink;
        private long _revision;
        public RuntimeProgramExecutionService Runtime { get; }
        public MediaSourceId InputSource { get; } = MediaSourceId.New();
        public MediaSourceId ProgramSource { get; } = MediaSourceId.New();
        public ConcurrentDictionary<DiscoveredMediaSourceId, Receiver> Receivers { get; } = new();
        public Action? BeforeDiscovery { get; set; }
        public Action<NdiInputConfiguration>? BeforeCreation { get; set; }
        public Action? BeforeReceiverDisposal { get; set; }

        public Fixture(Action<MediaSourceId, NdiInputMediaKind>? beforePublication = null)
        {
            Runtime = new RuntimeProgramExecutionService(InputSource, ProgramSource, Format,
                new NullRecordingWriter(), null, null, null, null, null,
                new Discovery(this), CreateSession, beforePublication);
            _ndi = Assert.Single(Runtime.ProviderDescriptors,
                provider => provider.Capabilities.Any(capability => capability.Kind == MediaSourceCapabilityKinds.Input));
            var input = new ProductionSourceId(InputSource.Value);
            var program = new ProductionSourceId(ProgramSource.Value);
            var specification = new ProductionSpecification(ControlContractVersion.Current,
                new ProductionId(Identity.New()), "NDI lifecycle",
                [new ProductionSourceSpecification(input, "NDI input"), new ProductionSourceSpecification(program, "Program")],
                new ProductionRoutingState(input, program));
            var initialized = ControlDomainEngine.Initialize(specification);
            Assert.True(initialized.Succeeded);
            var plan = CapabilityPlanningEngine.Plan(specification, initialized.State!.Authoritative,
                new Registry(Runtime.ProviderDescriptors));
            Assert.True(plan.Succeeded);
            _baseline = plan.PreparedExecution!;
            _sink = Assert.Single(_baseline.Bindings, binding => binding.OutputRoleId == "program").MediaSinkId!.Value;
        }

        public RuntimeNdiInputBinding Owner =>
            GetField<Dictionary<MediaSourceId, RuntimeNdiInputBinding>>(Runtime, "_committedNdiInputs")[InputSource];

        public Receiver Receiver(NdiDiscoveredSourceEndpoint endpoint) => Receivers[NdiDiscoveryService.CreateStableId(endpoint.NdiName)];

        public RuntimeHostApplyResult Apply(NdiDiscoveredSourceEndpoint? endpoint)
        {
            var bindings = _baseline.Bindings.Select(binding => endpoint is not null &&
                binding.MediaSourceId == InputSource && binding.MediaSinkId is null
                ? new PreparedExecutionBinding(binding.LogicalNodeId,
                    Assert.Single(_ndi.Capabilities, capability => capability.Kind == MediaSourceCapabilityKinds.Input).CapabilityId,
                    Assert.Single(_ndi.Resources, resource => resource.Kind == MediaSourceCapabilityKinds.Input),
                    InputSource, null, externalSourceId: NdiDiscoveryService.CreateStableId(endpoint.NdiName).Value,
                    safeSourceIdentity: "ndi://" + endpoint.NdiName)
                : binding).ToArray();
            return ApplyBindings(bindings);
        }

        public RuntimeHostApplyResult ApplyExternalProgramSource(NdiDiscoveredSourceEndpoint endpoint, MediaSourceId sourceId)
        {
            var bindings = _baseline.Bindings.Select(binding => binding.OutputRoleId == "program"
                ? new PreparedExecutionBinding(binding.LogicalNodeId, binding.CapabilityId, binding.Resource,
                    sourceId, binding.MediaSinkId, binding.OutputRoleId, binding.AudioBusId)
                : binding).Append(new PreparedExecutionBinding(Identity.New(),
                    Assert.Single(_ndi.Capabilities, capability => capability.Kind == MediaSourceCapabilityKinds.Input).CapabilityId,
                    Assert.Single(_ndi.Resources, resource => resource.Kind == MediaSourceCapabilityKinds.Input),
                    sourceId, null, externalSourceId: NdiDiscoveryService.CreateStableId(endpoint.NdiName).Value,
                    safeSourceIdentity: "ndi://" + endpoint.NdiName)).ToArray();
            return ApplyBindings(bindings);
        }

        private RuntimeHostApplyResult ApplyBindings(IReadOnlyList<PreparedExecutionBinding> bindings)
        {
            var revision = checked((ulong)Interlocked.Increment(ref _revision));
            var prepared = new PreparedExecutionContract(RuntimeContractVersion.Current, PreparedExecutionId.New(),
                new AuthoritySnapshotReference(_baseline.AuthoritySnapshot.StateId, new Revision(revision)),
                new Generation(revision), bindings, _baseline.CompositingState);
            return Runtime.ApplyExecution(prepared, _sink);
        }

        private NdiInputSession CreateSession(NdiInputConfiguration configuration)
        {
            BeforeCreation?.Invoke(configuration);
            var receiver = new Receiver(() => BeforeReceiverDisposal?.Invoke());
            Assert.True(Receivers.TryAdd(configuration.DiscoveredSourceId, receiver));
            return new NdiInputSession(configuration, () => receiver);
        }

        public async ValueTask DisposeAsync() =>
            await Task.Run(() => Runtime.DisposeAsync().AsTask()).WaitAsync(Deadline);

        private sealed class Discovery(Fixture fixture) : INdiDiscoveryBackend
        {
            public IReadOnlyList<NdiDiscoveredSourceEndpoint> GetCurrentSources(TimeSpan waitForChange)
            {
                fixture.BeforeDiscovery?.Invoke();
                return [First, Second];
            }
            public void Dispose() { }
        }
    }

    private sealed class Receiver(Action beforeDisposal) : INdiReceiveBackend
    {
        private readonly ConcurrentQueue<NdiInputMediaSample> _samples = new();
        private readonly AutoResetEvent _available = new(false);
        private int _disposalCount;
        public int DisposalCount => Volatile.Read(ref _disposalCount);
        public int ConnectionCount => 1;
        public void Publish(NdiInputMediaSample sample) { _samples.Enqueue(sample); _available.Set(); }
        public NdiCaptureResult Capture(TimeSpan timeout)
        {
            if (_samples.TryDequeue(out var sample))
                return new NdiCaptureResult(NdiCaptureStatus.Media, sample);
            _available.WaitOne(TimeSpan.FromMilliseconds(5));
            return new NdiCaptureResult(NdiCaptureStatus.None);
        }
        public ValueTask DisposeAsync()
        {
            beforeDisposal();
            Interlocked.Increment(ref _disposalCount);
            _available.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Registry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
    {
        public IReadOnlyList<ProviderDescriptor> GetProviders() => providers;
    }

    private sealed class NullRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
