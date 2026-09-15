using System.Globalization;
using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Inference;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class V1EndToEndProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V1_reference_workload_executes_end_to_end_for_both_development_formats(bool fractionalRate)
    {
        var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        await using var harness = await V1Harness.CreateAsync(format);

        Assert.Equal(harness.SourceAProductionId, harness.Client.Snapshot!.Production.Routing.ProgramSourceId);
        Assert.Equal(Revision.Initial, harness.Client.Snapshot.Production.Revision);

        var recordingStart = await harness.Runtime.StartRecordingAsync(
            new RecordingSessionId(Identity.Parse("71000000-0000-0000-0000-000000000001")),
            new RecordingOutputId(Identity.Parse("72000000-0000-0000-0000-000000000001")));
        Assert.True(recordingStart.Succeeded);

        var baseline = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(0UL, baseline.SequenceNumber);
        Assert.Equal(harness.SourceAMediaId, baseline.CommittedProgramSourceId);
        Assert.True(baseline.Audio.Emitted);
        Assert.Equal(harness.SourceAMediaId, baseline.Audio.VideoSourceId);
        Assert.Equal(format.FrameRate.Denominator, baseline.ProgramFrame.Timing.Timebase.Numerator);
        Assert.Equal(format.FrameRate.Numerator, baseline.ProgramFrame.Timing.Timebase.Denominator);

        var preview = await harness.Client.SelectPreviewAsync(harness.SourceBProductionId.ToString());
        Assert.True(preview.Accepted);
        Assert.Equal(harness.SourceBProductionId, harness.Client.Snapshot!.Production.Routing.PreviewSourceId);
        Assert.Equal(harness.SourceAProductionId, harness.Client.Snapshot.Production.Routing.ProgramSourceId);

        var beforeCut = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(harness.SourceAMediaId, beforeCut.CommittedProgramSourceId);

        var cut = await harness.Client.CutPreviewAsync();
        Assert.True(cut.Accepted);
        var cutFrame = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(RuntimeProgramTransitionKind.Cut, cutFrame.TransitionKind);
        Assert.Equal(byte.MaxValue, cutFrame.BlendWeight);
        Assert.Equal(harness.SourceBMediaId, cutFrame.CommittedProgramSourceId);
        Assert.Equal(harness.SourceBMediaId, cutFrame.Audio.VideoSourceId);
        Assert.NotEqual(beforeCut.PixelProbe, cutFrame.PixelProbe);

        harness.Runtime.SetVisualLayerMode(V1VisualLayerMode.Static);
        var staticLayerFrame = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(V1VisualLayerMode.Static, staticLayerFrame.VisualLayerMode);
        Assert.NotEqual(cutFrame.PixelProbe, staticLayerFrame.PixelProbe);

        harness.Runtime.SetVisualLayerMode(V1VisualLayerMode.Disabled);
        var previewBack = await harness.Client.SelectPreviewAsync(harness.SourceAProductionId.ToString());
        Assert.True(previewBack.Accepted);
        var dissolve = await harness.Client.DissolvePreviewAsync(3);
        Assert.True(dissolve.Accepted);

        var dissolve1 = harness.Runtime.ProcessNextBoundary();
        var dissolve2 = harness.Runtime.ProcessNextBoundary();
        var dissolve3 = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(RuntimeProgramTransitionKind.Dissolve, dissolve1.TransitionKind);
        Assert.Equal(RuntimeProgramTransitionKind.Dissolve, dissolve2.TransitionKind);
        Assert.Equal(RuntimeProgramTransitionKind.Dissolve, dissolve3.TransitionKind);
        Assert.Equal((byte)85, dissolve1.BlendWeight);
        Assert.Equal((byte)170, dissolve2.BlendWeight);
        Assert.Equal(byte.MaxValue, dissolve3.BlendWeight);
        Assert.Equal(harness.SourceAMediaId, dissolve1.CommittedProgramSourceId);
        Assert.Equal(harness.SourceAMediaId, dissolve1.Audio.VideoSourceId);
        Assert.True(dissolve1.PixelProbe.Red > dissolve2.PixelProbe.Red);
        Assert.True(dissolve2.PixelProbe.Red > dissolve3.PixelProbe.Red);
        Assert.True(dissolve1.PixelProbe.Blue < dissolve2.PixelProbe.Blue);
        Assert.True(dissolve2.PixelProbe.Blue < dissolve3.PixelProbe.Blue);

        var cleanForAI = harness.Runtime.ProcessNextBoundary();
        var aiResult = await harness.AI.ExecuteAsync(CreateInferenceRequest(cleanForAI.ProgramFrame));
        var useDecision = AIResultUsePolicy.Evaluate(
            aiResult,
            cleanForAI.ProgramFrame.Surface.SurfaceId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            0.9);
        Assert.True(useDecision.Usable);
        ApplyReferenceSegmentationEffect(harness.Runtime, aiResult);

        var aiVisible = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(V1VisualLayerMode.Dynamic, aiVisible.VisualLayerMode);
        Assert.NotEqual(cleanForAI.PixelProbe, aiVisible.PixelProbe);

        var unavailableAI = new AIHostService(new GovernedInferenceRuntime(
            new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider(InferenceProviderState.Unavailable) },
            InferenceRuntimeLimits.ReferenceV1));
        var unavailable = await unavailableAI.ExecuteAsync(CreateInferenceRequest(aiVisible.ProgramFrame));
        var fallback = AIResultUsePolicy.Evaluate(
            unavailable,
            aiVisible.ProgramFrame.Surface.SurfaceId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            0.9);
        Assert.False(fallback.Usable);
        harness.Runtime.SetVisualLayerMode(V1VisualLayerMode.Disabled);

        var cleanFallback = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(harness.SourceAMediaId, cleanFallback.CommittedProgramSourceId);
        Assert.True(cleanFallback.Audio.Emitted);
        Assert.Equal(V1VisualLayerMode.Disabled, cleanFallback.VisualLayerMode);

        var recordingStop = await harness.Runtime.StopRecordingAsync();
        Assert.Equal(RecordingStopStatus.Stopped, recordingStop.Status);
        Assert.Equal(harness.Runtime.Snapshot.Recording.Statistics.Written, (ulong)harness.Writer.Samples.Count);
        Assert.True(harness.Writer.Samples.Count >= 9);

        var sequenceBeforeDisconnect = harness.Runtime.Snapshot.NextSequenceNumber;
        harness.Client.Disconnect();
        Assert.False(harness.Client.Connected);
        var withoutOperator = harness.Runtime.ProcessNextBoundary();
        Assert.Equal(sequenceBeforeDisconnect, withoutOperator.SequenceNumber);
        Assert.True(withoutOperator.Audio.Emitted);
        await harness.Client.SynchronizeAsync();
        Assert.True(harness.Client.Connected);
        Assert.Equal(harness.Control.State.Revision, harness.Client.Snapshot!.Production.Revision);

        await harness.Journal.FlushAsync();
        Assert.Contains(harness.Journal.Entries, entry => entry.Event.Code == "planning.prepared");
        Assert.Contains(harness.Journal.Entries, entry => entry.Event.Code == "runtime.commit.observed");
        Assert.Contains(harness.Journal.Entries, entry => entry.Event.Code == "control.authoritative.committed");
        Assert.Equal(0UL, harness.Journal.Statistics.Dropped);

        var outputSequences = harness.Runtime.ProgramFrames.Select(frame => frame.Frame.Timing.SequenceNumber).ToArray();
        Assert.Equal(Enumerable.Range(0, outputSequences.Length).Select(value => (ulong)value), outputSequences);
        Assert.Equal(0, harness.Runtime.Snapshot.ActiveGpuSurfaces);
    }

    [Fact]
    public async Task Lost_input_has_defined_black_fallback_and_program_continues()
    {
        await using var harness = await V1Harness.CreateAsync(VideoFormat.Hd1080p50Rgba8);
        var healthy = harness.Runtime.ProcessNextBoundary();
        Assert.NotEqual(new ProgramPixelProbe(0, 0, 0, 255), healthy.PixelProbe);

        harness.Runtime.SetInputSignalState(harness.SourceAMediaId, V1InputSignalState.Lost);
        var fallback = harness.Runtime.ProcessNextBoundary();

        Assert.Equal(harness.SourceAMediaId, fallback.CommittedProgramSourceId);
        Assert.Equal(new ProgramPixelProbe(0, 0, 0, 255), fallback.PixelProbe);
        Assert.Contains(harness.Runtime.Observations, value => value.StartsWith("input.fallback.black:", StringComparison.Ordinal));
        Assert.Equal(RuntimeExecutionStatus.Committed, harness.Runtime.Snapshot.Runtime.Status);
    }

    private static GovernedInferenceExecutionRequest CreateInferenceRequest(FrameDescriptor frame)
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
            frame,
            new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(5)),
            Array.Empty<InferenceParameter>());
        return new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            request,
            new InferenceRequestContext(
                Identity.Parse("73000000-0000-0000-0000-000000000001"),
                new InferenceProductionTime(frame.Timing.PresentationTimestamp, frame.Timing.Timebase),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
    }

    private static void ApplyReferenceSegmentationEffect(
        V1RuntimeHostService runtime,
        GovernedInferenceExecutionResult result)
    {
        var region = result.Result.Outputs.Single(output => output.Name == "mask.region.normalized").Value;
        var parts = region.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, parts.Length);
        runtime.UpdateDynamicLayerRegion(parts[0], parts[1], parts[2], parts[3]);
        runtime.SetVisualLayerMode(V1VisualLayerMode.Dynamic);
    }

    private sealed class V1Harness : IAsyncDisposable
    {
        private V1Harness(
            ProductionSourceId sourceAProductionId,
            ProductionSourceId sourceBProductionId,
            MediaSourceId sourceAMediaId,
            MediaSourceId sourceBMediaId,
            CollectingRecordingWriter writer,
            BoundedProductionJournal journal,
            ControlHostService control,
            V1RuntimeHostService runtime,
            AIHostService ai,
            OperatorControlClient client)
        {
            SourceAProductionId = sourceAProductionId;
            SourceBProductionId = sourceBProductionId;
            SourceAMediaId = sourceAMediaId;
            SourceBMediaId = sourceBMediaId;
            Writer = writer;
            Journal = journal;
            Control = control;
            Runtime = runtime;
            AI = ai;
            Client = client;
        }

        public ProductionSourceId SourceAProductionId { get; }
        public ProductionSourceId SourceBProductionId { get; }
        public MediaSourceId SourceAMediaId { get; }
        public MediaSourceId SourceBMediaId { get; }
        public CollectingRecordingWriter Writer { get; }
        public BoundedProductionJournal Journal { get; }
        public ControlHostService Control { get; }
        public V1RuntimeHostService Runtime { get; }
        public AIHostService AI { get; }
        public OperatorControlClient Client { get; }

        public static async ValueTask<V1Harness> CreateAsync(VideoFormat format)
        {
            var productionId = new ProductionId(Identity.Parse("70000000-0000-0000-0000-000000000001"));
            var sourceA = new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000a"));
            var sourceB = new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000b"));
            var mediaA = new MediaSourceId(sourceA.Value);
            var mediaB = new MediaSourceId(sourceB.Value);
            var specification = new ProductionSpecification(
                ControlContractVersion.Current,
                productionId,
                "V1 Architecture Proof",
                new[]
                {
                    new ProductionSourceSpecification(sourceA, "Input 1"),
                    new ProductionSourceSpecification(sourceB, "Input 2")
                },
                new ProductionRoutingState(sourceA, sourceA));

            var writer = new CollectingRecordingWriter();
            var runtime = new V1RuntimeHostService(mediaA, mediaB, format, writer);
            var journal = new BoundedProductionJournal(256);
            var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
            var ai = AIHostService.CreateManagedReference();
            var transport = new InProcessOperatorTransport(control, runtime, ai, specification);
            await transport.InitializeAsync();
            var client = new OperatorControlClient(transport);
            await client.SynchronizeAsync();

            return new V1Harness(sourceA, sourceB, mediaA, mediaB, writer, journal, control, runtime, ai, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class InProcessOperatorTransport : IOperatorControlTransport
    {
        private readonly ControlHostService _control;
        private readonly V1RuntimeHostService _runtime;
        private readonly AIHostService _ai;
        private readonly ProductionSpecification _specification;

        public InProcessOperatorTransport(
            ControlHostService control,
            V1RuntimeHostService runtime,
            AIHostService ai,
            ProductionSpecification specification)
        {
            _control = control;
            _runtime = runtime;
            _ai = ai;
            _specification = specification;
        }

        public async ValueTask InitializeAsync()
        {
            var staged = _control.Initialize();
            var response = Apply(staged);
            if (!response.Accepted)
                throw new InvalidOperationException(response.Failure?.Message ?? "Initial Runtime commit failed.");
            await Task.CompletedTask;
        }

        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = _runtime.Snapshot;
            var sources = _specification.Sources
                .Select(source => new OperatorSourceDescriptor(source.SourceId.ToString(), source.Name))
                .ToArray();
            var inputStatus = string.Join(",", runtime.InputSignals.OrderBy(pair => pair.Key.ToString()).Select(pair => pair.Value.ToString()));
            return ValueTask.FromResult(new OperatorStatusSnapshot(
                _control.State,
                sources,
                runtime.Runtime.Status.ToString(),
                runtime.TimingHealth.ToString(),
                inputStatus,
                _ai.Snapshot.State.ToString(),
                runtime.Recording.State.ToString(),
                runtime.VisualLayerMode != V1VisualLayerMode.Disabled,
                runtime.Audio.LastPeakLevel));
        }

        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Apply(_control.SelectPreview(command)));

        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Apply(_control.CutProgram(command)));

        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Apply(_control.DissolveProgram(command)));

        private OperatorMutationResponse Apply(ControlHostOperationResult staged)
        {
            if (!staged.Accepted || staged.Execution is null)
                return new OperatorMutationResponse(false, staged.State, staged.Failure ?? new Failure("control.operation.rejected", "Control operation was rejected."));

            var execution = staged.Execution;
            var runtime = _runtime.ApplyExecution(
                execution.PreparedExecution,
                execution.ProgramSinkId,
                execution.ProgramTransition);
            var runtimeCommit = runtime.Commit ?? new RuntimeCommitResult(
                RuntimeContractVersion.Current,
                RuntimeCommitStatus.Rejected,
                null,
                _runtime.Snapshot.Runtime.ExecutionRevision,
                runtime.Prepare.Failure ?? new Failure("runtime.prepare.rejected", "Runtime prepare was rejected."));
            var confirmed = _control.ConfirmRuntimeCommit(execution.PreparedExecution.PreparedExecutionId, runtimeCommit);
            if (!confirmed.Committed || confirmed.State is null)
            {
                var state = confirmed.State ?? staged.State;
                return new OperatorMutationResponse(false, state, confirmed.Failure ?? new Failure("control.commit.rejected", "Cross-host commit was rejected."));
            }

            return new OperatorMutationResponse(true, confirmed.State, null);
        }
    }

    private sealed class CollectingRecordingWriter : IProgramRecordingWriter
    {
        private readonly List<RecordingProgramSample> _samples = new();
        public IReadOnlyList<RecordingProgramSample> Samples => _samples;

        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_samples)
                _samples.Add(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
