// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ExecutableHostLifecycleTests
{
	[Fact]
	public async Task ControlHost_composes_as_degraded_without_AP14_transport_and_drains_journal()
	{
		using var stop = new CancellationTokenSource();
		var process = new ControlHostProcess(ControlHostProcessOptions.Default);

		var run = process.RunAsync(stop.Token);

		Assert.Equal(ControlHostProcessState.Degraded, process.Lifecycle.State);
		Assert.Equal(ControlHostHealthState.Degraded, process.Lifecycle.Health);
		Assert.NotNull(process.Control);
		Assert.NotNull(process.Journal);
		Assert.NotNull(process.RuntimeTransport);
		Assert.False(process.RuntimeTransport!.IsConnected);

		stop.Cancel();
		var exit = await run;

		Assert.Equal(ControlHostExitCode.Success, exit);
		Assert.Equal(ControlHostProcessState.Stopped, process.Lifecycle.State);
		Assert.Equal(ControlHostHealthState.Stopped, process.Lifecycle.Health);
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await process.Journal!.FlushAsync());
	}

	[Fact]
	public async Task RuntimeHost_starts_cancels_and_releases_runtime_resources()
	{
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default);

		var run = process.RunAsync(stop.Token);

		Assert.Equal(RuntimeHostProcessState.Ready, process.Lifecycle.State);
		Assert.Equal(RuntimeHostHealthState.Healthy, process.Lifecycle.Health);
		Assert.NotNull(process.Runtime);

		stop.Cancel();
		var exit = await run;

		Assert.Equal(RuntimeHostExitCode.Success, exit);
		Assert.True(process.RuntimeDisposed);
		Assert.NotNull(process.FinalRuntimeSnapshot);
		Assert.Equal(0, process.FinalRuntimeSnapshot!.ActiveGpuSurfaces);
		Assert.Equal(0, process.Runtime!.Snapshot.ActiveGpuSurfaces);
		Assert.Equal(RuntimeHostProcessState.Stopped, process.Lifecycle.State);
	}

	[Fact]
	public async Task RuntimeHost_repeated_process_start_stop_is_clean()
	{
		for (var attempt = 0; attempt < 2; attempt++)
		{
			using var stop = new CancellationTokenSource();
			var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default);
			var run = process.RunAsync(stop.Token);
			Assert.Equal(RuntimeHostProcessState.Ready, process.Lifecycle.State);

			stop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await run);
			Assert.True(process.RuntimeDisposed);
			Assert.Equal(0, process.FinalRuntimeSnapshot!.ActiveGpuSurfaces);
		}
	}

	[Fact]
	public async Task AIHost_shutdown_cancels_inflight_inference_and_releases_admission()
	{
		var sourceA = new MediaSourceId(Identity.Parse("74000000-0000-0000-0000-00000000000a"));
		var sourceB = new MediaSourceId(Identity.Parse("74000000-0000-0000-0000-00000000000b"));
		var media = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
		var frame = media.SourceA.GenerateFrame(0);

		using var stop = new CancellationTokenSource();
		var process = new AIHostProcess(
			AIHostProcessOptions.Default,
			limits => new AIHostService(new GovernedInferenceRuntime(
				new IInferenceProvider[]
				{
					new ManagedReferencePersonSegmentationProvider(executionDelay: TimeSpan.FromSeconds(30))
				},
				limits)));

		var run = process.RunAsync(stop.Token);
		Assert.Equal(AIHostProcessState.Ready, process.Lifecycle.State);
		Assert.NotNull(process.Service);

		var execution = process.Service!.ExecuteAsync(CreateInferenceRequest(frame)).AsTask();
		Assert.Equal(1, process.Service.ActiveExecutions);
		Assert.Equal(1U, process.Service.Snapshot.ActiveRequests);

		stop.Cancel();
		var exit = await run;
		var result = await execution;

		Assert.Equal(AIHostExitCode.Success, exit);
		Assert.Equal(InferenceExecutionStatus.Cancelled, result.Result.Status);
		Assert.True(process.ServiceDisposed);
		Assert.NotNull(process.FinalExecutionSnapshot);
		Assert.Equal(0U, process.FinalExecutionSnapshot!.ActiveRequests);
		Assert.Equal(0U, process.FinalExecutionSnapshot.ReservedComputeUnits);
		Assert.Equal(0UL, process.FinalExecutionSnapshot.ReservedVramBytes);
		Assert.Equal(AIHostProcessState.Stopped, process.Lifecycle.State);
	}

	[Fact]
	public async Task Invalid_configuration_fails_closed_before_host_run_loop()
	{
		var defaults = RuntimeHostProcessOptions.Default;
		var invalid = defaults with { SourceBId = defaults.SourceAId };
		var process = new RuntimeHostProcess(invalid);

		var exit = await process.RunAsync(CancellationToken.None);

		Assert.Equal(RuntimeHostExitCode.ConfigurationError, exit);
		Assert.Equal(RuntimeHostProcessState.Failed, process.Lifecycle.State);
		Assert.Equal(RuntimeHostHealthState.Unhealthy, process.Lifecycle.Health);
	}

	[Fact]
	public async Task Subsystem_startup_failure_has_explicit_exit_semantics()
	{
		var process = new RuntimeHostProcess(
			RuntimeHostProcessOptions.Default,
			runtimeFactory: (_, _) => throw new InvalidOperationException("Synthetic startup failure."));

		var exit = await process.RunAsync(CancellationToken.None);

		Assert.Equal(RuntimeHostExitCode.StartupFailure, exit);
		Assert.Equal(RuntimeHostProcessState.Failed, process.Lifecycle.State);
		Assert.Contains("Synthetic startup failure", process.Lifecycle.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public void Configuration_loading_uses_command_line_over_environment_and_supports_V1_formats()
	{
		var environment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["RTAIME_RUNTIME_FORMAT"] = "1080p50",
			["RTAIME_RUNTIME_SHUTDOWN_TIMEOUT_MS"] = "2500"
		};

		var options = RuntimeHostProcessOptions.Load(
			new[] { "--format=1080p59.94", "--shutdown-timeout-ms=5000" },
			name => environment.TryGetValue(name, out var value) ? value : null);

		Assert.Equal(VideoFormat.Hd1080p59_94Rgba8, options.Format);
		Assert.Equal(TimeSpan.FromSeconds(5), options.ShutdownTimeout);
	}

	[Fact]
	public async Task Process_instance_cannot_be_started_twice()
	{
		using var stop = new CancellationTokenSource();
		var process = new AIHostProcess(AIHostProcessOptions.Default);
		var run = process.RunAsync(stop.Token);
		Assert.Equal(AIHostProcessState.Ready, process.Lifecycle.State);

		await Assert.ThrowsAsync<InvalidOperationException>(() => process.RunAsync(CancellationToken.None));
		stop.Cancel();
		Assert.Equal(AIHostExitCode.Success, await run);
	}

	private static GovernedInferenceExecutionRequest CreateInferenceRequest(FrameDescriptor frame)
	{
		var request = new GovernedInferenceRequest(
			AIContractVersion.Current,
			InferenceRequestId.New(),
			ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
			frame,
			new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(20)),
			Array.Empty<InferenceParameter>());

		return new GovernedInferenceExecutionRequest(
			AIContractVersion.Current,
			request,
			new InferenceRequestContext(
				Identity.Parse("75000000-0000-0000-0000-000000000001"),
				new InferenceProductionTime(frame.Timing.PresentationTimestamp, frame.Timing.Timebase),
				new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
				new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
	}
}
