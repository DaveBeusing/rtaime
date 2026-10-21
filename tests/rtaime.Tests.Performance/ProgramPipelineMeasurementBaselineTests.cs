// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Performance;

public sealed class ProgramPipelineMeasurementBaselineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Program_service_load_matrix_reports_consumers_separately_from_physical_output(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var reports = new List<object>();
        for (var mask = 0; mask < 8; mask++)
        {
            var monitoringEnabled = (mask & 1) != 0;
            var recordingEnabled = (mask & 2) != 0;
            var outputCopyEnabled = (mask & 4) != 0;
            var period = TimeSpan.FromSeconds((double)format.FrameRate.Denominator / format.FrameRate.Numerator);
            var probe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(period,
                TimeSpan.FromTicks(period.Ticks / 4), period, 2048));
            var a = new ProductionSourceId(Identity.New());
            var b = new ProductionSourceId(Identity.New());
            var spec = new ProductionSpecification(ControlContractVersion.Current, new ProductionId(Identity.New()),
                "Program measurement", new[] { new ProductionSourceSpecification(a, "Input A"),
                    new ProductionSourceSpecification(b, "Input B") }, new ProductionRoutingState(b, a));
            await using var runtime = new RuntimeProgramExecutionService(new MediaSourceId(a.Value), new MediaSourceId(b.Value),
                format, new RecordingWriter(), new ManagedReferenceGpuBackend(), pipelineTimingProbe: probe);
            var state = ControlDomainEngine.Initialize(spec).State!.Authoritative;
            var plan = CapabilityPlanningEngine.Plan(spec, state, new Registry(runtime.ProviderDescriptors));
            Assert.True(plan.Succeeded);
            var sink = plan.Graph!.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink).MediaSinkId!.Value;
            Assert.True(runtime.ApplyExecution(plan.PreparedExecution!, sink).Committed);
            runtime.SetVisualLayerMode(RuntimeVisualLayerMode.Static);
            await using var monitoring = monitoringEnabled
                ? runtime.MonitoringHub.Subscribe(capacity: 2, requiresCpuFallback: true) : null;
            if (recordingEnabled)
                Assert.True((await runtime.StartRecordingAsync(RecordingSessionId.New(), RecordingOutputId.New())).Succeeded);
            var outputBuffer = outputCopyEnabled ? new byte[RgbaFrameBuffer.RequiredByteLength(format)] : null;
            void Boundary()
            {
                using var boundary = runtime.ProcessNextBoundary();
                if (outputBuffer is not null)
                {
                    var started = Stopwatch.GetTimestamp();
                    boundary.ProgramPixels.Span.CopyTo(outputBuffer);
                    probe.TryObserveStage(RuntimePipelineTimingStage.OutputSubmission, boundary.SequenceNumber, Stopwatch.GetElapsedTime(started));
                }
            }
            const int warmup = 3;
            const int count = 12;
            for (var index = 0; index < warmup; index++) Boundary();
            probe.ResetPipelineSamples();
            // Observe both the caller's direct allocations and approximate process-wide
            // allocations separately: monitoring/recording may execute on other threads.
            // No GC.Collect or artificial NoGCRegion alters the real pipeline workload.
            var measurementThread = Environment.CurrentManagedThreadId;
            var allocatedOnThreadBefore = GC.GetAllocatedBytesForCurrentThread();
            var totalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var gc0Before = GC.CollectionCount(0);
            var gc1Before = GC.CollectionCount(1);
            var gc2Before = GC.CollectionCount(2);
            var pauseBefore = GC.GetTotalPauseDuration();
            var samples = new double[count];
            for (var index = 0; index < count; index++)
            {
                var started = Stopwatch.GetTimestamp();
                Boundary();
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var allocatedOnThreadAfter = GC.GetAllocatedBytesForCurrentThread();
            var totalAllocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
            var pauseAfter = GC.GetTotalPauseDuration();
            var gen0Collections = GC.CollectionCount(0) - gc0Before;
            var gen1Collections = GC.CollectionCount(1) - gc1Before;
            var gen2Collections = GC.CollectionCount(2) - gc2Before;
            var gcInfo = GC.GetGCMemoryInfo();
            Assert.Equal(measurementThread, Environment.CurrentManagedThreadId);
            Assert.True(allocatedOnThreadAfter >= allocatedOnThreadBefore);
            Assert.True(totalAllocatedAfter >= totalAllocatedBefore);
            var threadAllocationBytes = allocatedOnThreadAfter - allocatedOnThreadBefore;
            var totalAllocationBytes = totalAllocatedAfter - totalAllocatedBefore;
            if (recordingEnabled)
                Assert.Equal(RecordingStopStatus.Stopped, (await runtime.StopRecordingAsync()).Status);
            var drain = Stopwatch.StartNew();
            while (runtime.ProgramReadbackPoolStatistics.ActiveBuffers != 0 && drain.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            var monitoringStatistics = runtime.MonitoringHub.Statistics;
            var monitoringQueueStatistics = runtime.MonitoringHub.QueueStatistics;
            Assert.True(monitoringQueueStatistics.QueueDepth <= monitoringQueueStatistics.QueueCapacity);
            Assert.True(monitoringQueueStatistics.SumSubscriberMaximumQueueDepth <= monitoringQueueStatistics.QueueCapacity);
            if (monitoring is not null) await monitoring.DisposeAsync();
            var final = runtime.Snapshot;
            Assert.Equal((ulong)(count + warmup), final.NextSequenceNumber);
            Assert.Equal(0, final.ActiveGpuSurfaces);
            Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
            Assert.True(final.Recording.Statistics.MaximumQueueDepth <= final.Recording.Statistics.QueueCapacity);
            var summary = probe.PipelineSnapshot();
            Assert.Contains(summary.Stages, stage => stage.Stage == RuntimePipelineTimingStage.ServiceBoundaryAttempt);
            Assert.Equal(outputCopyEnabled, summary.Stages.Any(stage => stage.Stage == RuntimePipelineTimingStage.OutputSubmission));
            var ordered = samples.Order().ToArray();
            double P(double rank) => ordered[Math.Clamp((int)Math.Ceiling(count * rank) - 1, 0, count - 1)];
            var report = new
            {
                format = use5994 ? "1080p59.94" : "1080p50", monitoringEnabled, recordingEnabled, outputCopyEnabled,
                warmupBoundaries = warmup, samples = count, measurementKind = "CPU_WALL",
                boundaryScope = "V1_SERVICE_AND_OPTIONAL_SYNTHETIC_OUTPUT_COPY",
                p50Milliseconds = P(.50), p95Milliseconds = P(.95), p99Milliseconds = P(.99), maximumMilliseconds = ordered[^1],
                allocation = new
                {
                    measuredThread = measurementThread,
                    measuredThreadBytes = threadAllocationBytes,
                    measuredThreadBytesPerBoundary = threadAllocationBytes / (double)count,
                    approximateProcessBytes = totalAllocationBytes,
                    approximateProcessBytesPerBoundary = totalAllocationBytes / (double)count,
                    processTotalIncludesAsynchronousConsumers = true
                },
                garbageCollection = new
                {
                    gen0Collections, gen1Collections, gen2Collections,
                    pauseMilliseconds = (pauseAfter - pauseBefore).TotalMilliseconds,
                    heapSizeBytesAtLastGc = gcInfo.HeapSizeBytes,
                    fragmentedBytesAtLastGc = gcInfo.FragmentedBytes,
                    committedBytesAtLastGc = gcInfo.TotalCommittedBytes,
                    pinnedObjectCountAtLastGc = gcInfo.PinnedObjectsCount,
                    largeObjectHeapBytesAtLastGc = gcInfo.GenerationInfo.Length > 3 ? gcInfo.GenerationInfo[3].SizeAfterBytes : (long?)null,
                    pinnedObjectHeapBytesAtLastGc = gcInfo.GenerationInfo.Length > 4 ? gcInfo.GenerationInfo[4].SizeAfterBytes : (long?)null
                },
                pipelineTiming = summary,
                queues = new { recording = final.Recording.Statistics, monitoring = monitoringStatistics, monitoringQueues = monitoringQueueStatistics },
                transfers = runtime.GpuMemoryTransfers,
                physicalOutput = "UNVERIFIED", gpuEventTiming = "UNVERIFIED"
            };
            reports.Add(report);
            Console.WriteLine(JsonSerializer.Serialize(report, ProgramPipelineEvidence.Options));
        }
        ProgramPipelineEvidence.Write($"program-load-{use5994}", reports);
    }

    private sealed class Registry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
    {
        public IReadOnlyList<ProviderDescriptor> GetProviders() => providers;
    }

    private sealed class RecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

internal static class ProgramPipelineEvidence
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void Write(string name, object profiles)
    {
        var root = Environment.GetEnvironmentVariable("RTAIME_PIPELINE_BASELINE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        var source = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT");
        var build = Environment.GetEnvironmentVariable("RTAIME_BUILD_COMMIT");
        if (source is null || source.Length != 40 || source.Any(character => !Uri.IsHexDigit(character)) || build != source)
            throw new InvalidOperationException("Pipeline evidence requires matching exact source and build commit SHAs.");
        var sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK");
        if (sdk != "10.0.401") throw new InvalidOperationException("Pipeline evidence must bind the qualified pinned SDK.");
        Directory.CreateDirectory(root);
        var report = new
        {
            schemaVersion = "1.0", qualification = "program-pipeline-software-baseline",
            sourceCommit = source, buildCommit = build, sdk,
            capturedAtUtc = DateTimeOffset.UtcNow,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "UNAVAILABLE",
            logicalProcessors = Environment.ProcessorCount,
            gpu = "ManagedReferenceGpuBackend", gpuDriver = "NOT_APPLICABLE",
            physicalGpuAndOutput = "UNVERIFIED", profiles
        };
        File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(report, Options) + Environment.NewLine);
    }
}
