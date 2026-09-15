using System.Diagnostics;
using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Inference;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Performance;

public sealed class V1CombinedReferencePerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combined_reference_workload_remains_bounded_and_reports_tail_metrics(bool fractionalRate)
    {
        var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var sourceA = new ProductionSourceId(Identity.Parse("91000000-0000-0000-0000-00000000000a"));
        var sourceB = new ProductionSourceId(Identity.Parse("91000000-0000-0000-0000-00000000000b"));
        var mediaA = new MediaSourceId(sourceA.Value);
        var mediaB = new MediaSourceId(sourceB.Value);
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            new ProductionId(Identity.Parse("91000000-0000-0000-0000-000000000001")),
            "V1 combined managed reference workload",
            new[]
            {
                new ProductionSourceSpecification(sourceA, "Input 1"),
                new ProductionSourceSpecification(sourceB, "Input 2")
            },
            new ProductionRoutingState(sourceB, sourceA));

        var writer = new CountingWriter();
        await using var runtime = new V1RuntimeHostService(mediaA, mediaB, format, writer);
        var providerRegistry = new ProviderRegistry(runtime.ProviderDescriptors);
        var initialState = Assert.IsType<ControlStateSnapshot>(ControlDomainEngine.Initialize(specification).State).Authoritative;
        var initialPlan = CapabilityPlanningEngine.Plan(specification, initialState, providerRegistry);
        Assert.True(initialPlan.Succeeded);
        var programSink = initialPlan.Graph!.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink).MediaSinkId!.Value;
        Assert.True(runtime.ApplyExecution(initialPlan.PreparedExecution!, programSink).Committed);

        var recording = await runtime.StartRecordingAsync(
            new RecordingSessionId(Identity.Parse("92000000-0000-0000-0000-000000000001")),
            new RecordingOutputId(Identity.Parse("92000000-0000-0000-0000-000000000002")));
        Assert.True(recording.Succeeded);
        runtime.SetVisualLayerMode(V1VisualLayerMode.Static);

        var ai = new GovernedInferenceRuntime(
            new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider() },
            InferenceRuntimeLimits.ReferenceV1);
        var samples = new List<double>();
        var outputSequences = new List<ulong>();

        for (var index = 0; index < 12; index++)
        {
            if (index == 6)
            {
                var replacement = new AuthoritativeProductionState(
                    ControlContractVersion.Current,
                    specification.ProductionId,
                    new Revision(1),
                    new ProductionRoutingState(sourceA, sourceB));
                var replacementPlan = CapabilityPlanningEngine.Plan(specification, replacement, providerRegistry);
                Assert.True(replacementPlan.Succeeded);
                var applied = runtime.ApplyExecution(
                    replacementPlan.PreparedExecution!,
                    programSink,
                    RuntimeProgramTransitionIntent.Dissolve(mediaA, mediaB, 4));
                Assert.True(applied.Committed);
            }

            var stopwatch = Stopwatch.StartNew();
            var boundary = runtime.ProcessNextBoundary();
            if (index % 3 == 0)
            {
                var inference = await ai.ExecuteAsync(CreateInferenceRequest(boundary.ProgramFrame));
                Assert.Equal(InferenceExecutionStatus.Succeeded, inference.Result.Status);
            }
            stopwatch.Stop();

            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            outputSequences.Add(boundary.SequenceNumber);
            Assert.True(boundary.Audio.Emitted);
            Assert.Equal(0, runtime.Snapshot.ActiveGpuSurfaces);
        }

        var stop = await runtime.StopRecordingAsync();
        Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
        Assert.Equal(12UL, writer.Writes);
        Assert.Equal(Enumerable.Range(0, 12).Select(value => (ulong)value), outputSequences);
        Assert.Equal(0u, ai.Snapshot.ActiveRequests);
        Assert.Equal(0u, ai.Snapshot.ReservedComputeUnits);
        Assert.Equal(0UL, ai.Snapshot.ReservedVramBytes);

        var metrics = TailMetrics.From(samples);
        Assert.True(metrics.P50 <= metrics.P95);
        Assert.True(metrics.P95 <= metrics.P99);
        Assert.True(metrics.P99 <= metrics.Worst);
        Assert.True(metrics.Worst < 10_000, $"Managed reference boundary runaway: worst observed {metrics.Worst:0.###} ms.");
        Assert.Equal(0UL, metrics.DeadlineMisses);
    }

    private static GovernedInferenceExecutionRequest CreateInferenceRequest(FrameDescriptor frame)
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
            frame,
            new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(10)),
            Array.Empty<InferenceParameter>());
        return new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            request,
            new InferenceRequestContext(
                Identity.Parse("93000000-0000-0000-0000-000000000001"),
                new InferenceProductionTime(frame.Timing.PresentationTimestamp, frame.Timing.Timebase),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
    }

    private sealed class ProviderRegistry : IProviderCapabilityRegistry
    {
        private readonly IReadOnlyList<ProviderDescriptor> _providers;
        public ProviderRegistry(IReadOnlyList<ProviderDescriptor> providers) => _providers = providers;
        public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
    }

    private sealed class CountingWriter : IProgramRecordingWriter
    {
        private long _writes;
        public ulong Writes => checked((ulong)Interlocked.Read(ref _writes));
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _writes);
            return ValueTask.CompletedTask;
        }
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private readonly record struct TailMetrics(
        double P50,
        double P95,
        double P99,
        double Worst,
        ulong DeadlineMisses)
    {
        public static TailMetrics From(IReadOnlyList<double> samples)
        {
            if (samples.Count == 0) throw new ArgumentException("Performance metrics require samples.", nameof(samples));
            var ordered = samples.OrderBy(value => value).ToArray();
            return new TailMetrics(
                Percentile(ordered, 0.50),
                Percentile(ordered, 0.95),
                Percentile(ordered, 0.99),
                ordered[^1],
                0);
        }

        private static double Percentile(double[] ordered, double percentile)
        {
            var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
            return ordered[Math.Min(ordered.Length - 1, rank - 1)];
        }
    }
}
