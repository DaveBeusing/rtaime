// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.AIHost;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class SupportSnapshotIntegrationTests
{
	[Fact]
	public void Host_support_snapshot_projections_share_schema_and_exclude_bulk_media()
	{
		var captured = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
		var snapshots = new[]
		{
			ControlHostDiagnostics.Capture(new ControlHostProcess(ControlHostProcessOptions.Default), captured),
			RuntimeHostDiagnostics.Capture(new RuntimeHostProcess(RuntimeHostProcessOptions.Default), captured),
			AIHostDiagnostics.Capture(new AIHostProcess(AIHostProcessOptions.Default), captured)
		};

		Assert.Equal(new[] { "ControlHost", "RuntimeHost", "AIHost" }, snapshots.Select(snapshot => snapshot.Host));
		foreach (var snapshot in snapshots)
		{
			Assert.Equal(SupportSnapshotBuilder.CurrentSchemaVersion, snapshot.SchemaVersion);
			Assert.Equal(captured, snapshot.CapturedAtUtc);
			var json = SupportSnapshotSerializer.Serialize(snapshot);
			Assert.DoesNotContain("pixels", json, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
		}
	}

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_process_loop_publishes_complete_boundary_lateness_and_stage_evidence(bool use5994)
    {
        var options = RuntimeHostProcessOptions.Default with
        {
            Format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8,
            ListenEndpoint = $"rtaime.pipeline.{Guid.NewGuid():N}",
            AIEndpoint = $"rtaime.pipeline.ai.{Guid.NewGuid():N}"
        };
        var process = new RuntimeHostProcess(options, runtimeFactory: (configuration, writer) =>
            new V1RuntimeHostService(configuration.SourceAId, configuration.SourceBId, configuration.Format,
                writer, new ManagedReferenceGpuBackend()));
        using var stop = new CancellationTokenSource();
        var run = process.RunAsync(stop.Token);
        try
        {
            var a = new ProductionSourceId(options.SourceAId.Value);
            var b = new ProductionSourceId(options.SourceBId.Value);
            var spec = new ProductionSpecification(ControlContractVersion.Current, new ProductionId(Identity.New()),
                "Process timing", new[] { new ProductionSourceSpecification(a, "A"), new ProductionSourceSpecification(b, "B") },
                new ProductionRoutingState(b, a));
            var runtime = process.Runtime!;
            var plan = CapabilityPlanningEngine.Plan(spec, ControlDomainEngine.Initialize(spec).State!.Authoritative,
                new Registry(runtime.ProviderDescriptors));
            Assert.True(plan.Succeeded);
            var sink = plan.Graph!.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink).MediaSinkId!.Value;
            Assert.True(runtime.ApplyExecution(plan.PreparedExecution!, sink).Committed);
            var wait = Stopwatch.StartNew();
            while (process.ScheduleStatistics.CommittedBoundaries < 4 && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            Assert.True(process.ScheduleStatistics.CommittedBoundaries >= 4);
            var snapshot = process.PipelineTiming;
            foreach (var stage in new[] { RuntimePipelineTimingStage.CompleteProgramBoundary,
                RuntimePipelineTimingStage.InputAdmission, RuntimePipelineTimingStage.OutputSubmission,
                RuntimePipelineTimingStage.SchedulerStartLateness, RuntimePipelineTimingStage.PresentationLateness,
                RuntimePipelineTimingStage.ServiceBoundaryAttempt, RuntimePipelineTimingStage.Readback })
                Assert.Contains(snapshot.Stages, summary => summary.Stage == stage);
            Assert.All(snapshot.Samples, sample => Assert.True(double.IsFinite(sample.Milliseconds) && sample.Milliseconds >= 0));
            var json = RuntimeHostDiagnostics.CaptureJson(process);
            Assert.Contains("timing.pipeline.window", json);
            Assert.Contains("BOUNDED_RETAINED_SAMPLES", json);
            Assert.Contains("timing.pipeline.CompleteProgramBoundary.p99Ms", json);
            Assert.Contains("timing.pipeline.contentionDropped", json);
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(RuntimeHostExitCode.Success, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(0, process.FinalRuntimeSnapshot!.ActiveGpuSurfaces);
    }

    private sealed class Registry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
    {
        public IReadOnlyList<ProviderDescriptor> GetProviders() => providers;
    }
}
