// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;
using ClientHealthStates = rtaime.Client.OperatorHealthStates;
using ProjectionHealthStates = rtaime.ControlHost.OperatorHealthStates;

namespace rtaime.Tests.Integration;

public sealed class RuntimeHealthPerformanceHudIntegrationTests
{
	[Fact]
	public void Healthy_evidence_projects_PASS_without_inventing_GPU_telemetry()
	{
		var runtime = CreateRuntimeSnapshot();
		var providers = new[] { CreateGpuProvider(ProviderAvailabilityState.Available) };

		var health = OperatorHealthProjection.Evaluate(
			runtime,
			providers,
			null,
			controlAuthorityAvailable: true,
			new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));

		Assert.Equal(ProjectionHealthStates.Pass, health.Engine.State);
		Assert.Equal(ProjectionHealthStates.Pass, health.Control.State);
		Assert.Equal(ProjectionHealthStates.Pass, health.Runtime.State);
		Assert.Equal(ProjectionHealthStates.Pass, health.Media.State);
		Assert.Equal(ProjectionHealthStates.Pass, health.Provider.State);
		Assert.Equal(ProjectionHealthStates.Pass, health.GpuProvider.State);
		Assert.Equal("UNVERIFIED", health.GpuUtilization);
		Assert.Equal("UNVERIFIED", health.Vram);
		Assert.Equal(3UL, health.DroppedFrames);
		Assert.True(health.FrameTime > TimeSpan.Zero);
		Assert.True(health.FrameBudget > health.FrameTime);
		Assert.NotNull(health.OutputFramesPerSecond);
		Assert.Equal(49.75, health.OutputFramesPerSecond.Value, 6);
	}

	[Fact]
	public void Measured_hardware_telemetry_is_projected_without_estimation()
	{
		var runtime = CreateRuntimeSnapshot();
		runtime = runtime with
		{
			Performance = runtime.Performance! with
			{
				CpuDeviceName = "AMD Ryzen Threadripper PRO",
				CpuLogicalProcessorCount = 64,
				CpuUtilizationPercent = 42.5,
				SystemMemoryUsedBytes = 8UL * 1024 * 1024 * 1024,
				SystemMemoryTotalBytes = 32UL * 1024 * 1024 * 1024,
				SystemTelemetryEvidence = "PASS: measured.",
				PhysicalGpuDeviceName = "NVIDIA RTX PRO 6000",
				GpuUtilizationPercent = 73,
				GpuVramUsedBytes = 4UL * 1024 * 1024 * 1024,
				GpuVramTotalBytes = 24UL * 1024 * 1024 * 1024,
				GpuTelemetryEvidence = "PASS: measured."
			}
		};

		var health = OperatorHealthProjection.Evaluate(
			runtime,
			new[] { CreateGpuProvider(ProviderAvailabilityState.Available) },
			null,
			controlAuthorityAvailable: true,
			DateTimeOffset.UtcNow);

		Assert.Equal("AMD Ryzen Threadripper PRO · 64 logical", health.CpuDeviceName);
		Assert.Equal("42.5%", health.CpuUtilization);
		Assert.Equal("25% · 8.00 GiB / 32.00 GiB", health.SystemMemory);
		Assert.Equal("NVIDIA RTX PRO 6000", health.GpuDeviceName);
		Assert.Equal("73%", health.GpuUtilization);
		Assert.Equal("4.00 GiB / 24.00 GiB", health.Vram);
	}

	[Fact]
	public void Memory_and_vram_boundaries_format_without_overflow_or_estimation()
	{
		var runtime = CreateRuntimeSnapshot();
		runtime = runtime with
		{
			Performance = runtime.Performance! with
			{
				SystemMemoryUsedBytes = 0,
				SystemMemoryTotalBytes = 32UL * 1024 * 1024 * 1024,
				GpuVramUsedBytes = 24UL * 1024 * 1024 * 1024,
				GpuVramTotalBytes = 24UL * 1024 * 1024 * 1024
			}
		};

		var health = OperatorHealthProjection.Evaluate(
			runtime,
			new[] { CreateGpuProvider(ProviderAvailabilityState.Available) },
			null,
			controlAuthorityAvailable: true,
			DateTimeOffset.UtcNow);

		Assert.Equal("0% · 0.00 GiB / 32.00 GiB", health.SystemMemory);
		Assert.Equal("24.00 GiB / 24.00 GiB", health.Vram);
	}

	[Fact]
	public void Retained_runtime_observation_keeps_performance_values_without_false_green_health()
	{
		var runtime = CreateRuntimeSnapshot() with
		{
			Performance = CreateRuntimeSnapshot().Performance! with
			{
				CpuDeviceName = "Test CPU",
				CpuLogicalProcessorCount = 16,
				CpuUtilizationPercent = 31.5,
				SystemMemoryUsedBytes = 4UL * 1024 * 1024 * 1024,
				SystemMemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
				PhysicalGpuDeviceName = "Test NVIDIA GPU",
				GpuUtilizationPercent = 44,
				GpuVramUsedBytes = 2UL * 1024 * 1024 * 1024,
				GpuVramTotalBytes = 8UL * 1024 * 1024 * 1024
			}
		};

		var health = OperatorHealthProjection.Evaluate(
			runtime,
			new[] { CreateGpuProvider(ProviderAvailabilityState.Available) },
			null,
			controlAuthorityAvailable: true,
			DateTimeOffset.UtcNow,
			runtimeObservationFresh: false);

		Assert.Equal(ProjectionHealthStates.Unverified, health.Engine.State);
		Assert.Equal(ProjectionHealthStates.Unverified, health.Runtime.State);
		Assert.Equal(ProjectionHealthStates.Unverified, health.Media.State);
		Assert.Equal(ProjectionHealthStates.Unverified, health.Provider.State);
		Assert.Equal(ProjectionHealthStates.Unverified, health.GpuProvider.State);
		Assert.Equal("31.5%", health.CpuUtilization);
		Assert.Equal("25% · 4.00 GiB / 16.00 GiB", health.SystemMemory);
		Assert.Equal("44%", health.GpuUtilization);
		Assert.Equal("2.00 GiB / 8.00 GiB", health.Vram);
	}

	[Fact]
	public void Gpu_telemetry_recovery_updates_projected_values_without_restart()
	{
		var runtime = CreateRuntimeSnapshot();
		var providers = new[] { CreateGpuProvider(ProviderAvailabilityState.Available) };

		var unavailable = OperatorHealthProjection.Evaluate(
			runtime,
			providers,
			null,
			controlAuthorityAvailable: true,
			DateTimeOffset.UtcNow);

		Assert.Equal("UNVERIFIED", unavailable.GpuUtilization);
		Assert.Equal("UNVERIFIED", unavailable.Vram);

		var recoveredRuntime = runtime with
		{
			Performance = runtime.Performance! with
			{
				PhysicalGpuDeviceName = "Recovered GPU",
				GpuUtilizationPercent = 37.5,
				GpuVramUsedBytes = 6UL * 1024 * 1024 * 1024,
				GpuVramTotalBytes = 24UL * 1024 * 1024 * 1024,
				GpuTelemetryEvidence = "PASS: recovered."
			}
		};
		var recovered = OperatorHealthProjection.Evaluate(
			recoveredRuntime,
			providers,
			null,
			controlAuthorityAvailable: true,
			DateTimeOffset.UtcNow);

		Assert.Equal("Recovered GPU", recovered.GpuDeviceName);
		Assert.Equal("37.5%", recovered.GpuUtilization);
		Assert.Equal("6.00 GiB / 24.00 GiB", recovered.Vram);
	}

	[Fact]
	public void Degraded_GPU_provider_is_UNVERIFIED_and_never_presented_as_PASS()
	{
		var runtime = CreateRuntimeSnapshot();
		var providers = new[] { CreateGpuProvider(ProviderAvailabilityState.Degraded) };

		var health = OperatorHealthProjection.Evaluate(
			runtime,
			providers,
			null,
			controlAuthorityAvailable: true,
			DateTimeOffset.UtcNow);

		Assert.Equal(ProjectionHealthStates.Unverified, health.Provider.State);
		Assert.Equal(ProjectionHealthStates.Unverified, health.GpuProvider.State);
		Assert.Equal(ProjectionHealthStates.Unverified, health.Engine.State);
		Assert.Contains("reference", health.GpuProvider.Detail, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Runtime_disconnect_is_visible_as_FAIL_in_the_Operator_snapshot()
	{
		var runtimeEndpoint = $"rtaime.test.ap54.runtime.{Guid.NewGuid():N}";
		var controlEndpoint = $"rtaime.test.ap54.control.{Guid.NewGuid():N}";
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			ConnectTimeout = TimeSpan.FromMilliseconds(100),
			RequestTimeout = TimeSpan.FromSeconds(1),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		try
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(3)));
			var ready = await SynchronizeWithRetryAsync(client);
			Assert.NotEqual(ClientHealthStates.Fail, ready.Health.Control.State);

			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Degraded);

			var duringGrace = await SynchronizeWithRetryAsync(client);
			Assert.NotEqual(ClientHealthStates.Pass, duringGrace.Health.Runtime.State);

			await Task.Delay(TimeSpan.FromMilliseconds(2200));
			var disconnected = await SynchronizeWithRetryAsync(client);
			Assert.Equal(ClientHealthStates.Fail, disconnected.Health.Runtime.State);
			Assert.Equal(ClientHealthStates.Fail, disconnected.Health.Media.State);
			Assert.Equal(ClientHealthStates.Fail, disconnected.Health.Provider.State);
			Assert.Equal(ClientHealthStates.Fail, disconnected.Health.GpuProvider.State);
			Assert.Equal(ClientHealthStates.Fail, disconnected.Health.Engine.State);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			if (!runtimeRun.IsCompleted)
			{
				runtimeStop.Cancel();
				Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			}
		}
	}

	[Fact]
	public void Dropped_frame_counter_counts_missed_cadence_and_output_rejection_without_allocating_history()
	{
		var counter = new RuntimeFrameDropCounter();
		var frame = TimeSpan.FromMilliseconds(20);

		Assert.Equal(0UL, counter.Observe(TimeSpan.Zero, frame));
		Assert.Null(counter.OutputFramesPerSecond);

		Assert.Equal(0UL, counter.Observe(TimeSpan.FromMilliseconds(20), frame));
		Assert.NotNull(counter.OutputFramesPerSecond);
		Assert.Equal(50, counter.OutputFramesPerSecond.Value, 6);

		Assert.Equal(1UL, counter.Observe(TimeSpan.FromMilliseconds(60), frame));
		Assert.NotNull(counter.OutputFramesPerSecond);
		Assert.Equal(45, counter.OutputFramesPerSecond.Value, 6);

		Assert.Equal(4UL, counter.Observe(TimeSpan.FromMilliseconds(80), frame, outputBackpressure: 2, outputRejected: 1));
		Assert.NotNull(counter.OutputFramesPerSecond);
		Assert.Equal(46, counter.OutputFramesPerSecond.Value, 6);
	}

	private static RuntimeRemoteSnapshot CreateRuntimeSnapshot()
	{
		var sourceId = new MediaSourceId(Identity.Parse("74000000-0000-0000-0000-00000000000a"));
		var streamId = new AudioStreamId(Identity.Parse("74000000-0000-0000-0000-00000000000b"));
		var format = VideoFormat.Hd1080p50Rgba8;
		return new RuntimeRemoteSnapshot(
			"runtime-ap54",
			new RuntimeExecutionState(
				RuntimeContractVersion.Current,
				new ExecutionInstanceId(Identity.Parse("74000000-0000-0000-0000-000000000001")),
				new Revision(1),
				RuntimeExecutionStatus.Committed,
				null),
			Identity.Parse("74000000-0000-0000-0000-000000000002"),
			new Revision(1),
			100,
			2,
			0,
			format,
			new Dictionary<MediaSourceId, string> { [sourceId] = "VALID" },
			new RuntimeGraphicsOverlaySnapshot(false, null, 0, 0, false, 0.72, 0.06, 1),
			new Dictionary<MediaSourceId, RuntimeAudioInputSnapshot>(),
			new RuntimeAudioProgramSnapshot(sourceId, streamId, 1, false, 0.2, 0.2, 0.2, false, "HEALTHY"),
			1,
			null,
			new RuntimePerformanceSnapshot(
				TimeSpan.FromMinutes(5),
				TimeSpan.FromMilliseconds(20),
				TimeSpan.FromMilliseconds(4),
				3,
				"Qualified GPU",
				true,
				null,
				null,
				null,
				"UNVERIFIED: no qualified utilization or used-VRAM source.",
				OutputFramesPerSecond: 49.75));
	}

	private static ProviderDescriptor CreateGpuProvider(ProviderAvailabilityState state)
	{
		var providerId = new ProviderId(Identity.Parse("74000000-0000-0000-0000-000000000010"));
		Failure? failure = state == ProviderAvailabilityState.Available
			? null
			: new Failure("gpu.backend.reference_only", "Managed reference provider lacks hardware qualification evidence.");
		return new ProviderDescriptor(
			ProviderContractVersion.Current,
			providerId,
			"GPU Provider",
			new ProviderAvailability(state, failure),
			new[]
			{
				new ProviderCapabilityDescriptor(
					new CapabilityId(Identity.Parse("74000000-0000-0000-0000-000000000011")),
					"gpu.processing",
					new[] { VideoFormat.Hd1080p50Rgba8 })
			},
			new[]
			{
				new ProviderResourceDescriptor(
					new ProviderResourceId(Identity.Parse("74000000-0000-0000-0000-000000000012")),
					providerId,
					"gpu.processing",
					1,
					true)
			});
	}

	private static async Task<OperatorStatusSnapshot> SynchronizeWithRetryAsync(OperatorControlClient client)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < 10; attempt++)
		{
			try { return await client.SynchronizeAsync(); }
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
			{
				last = exception;
				await Task.Delay(50);
			}
		}
		throw new InvalidOperationException("Operator snapshot did not become available.", last);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException("Condition was not reached before the AP-54 integration-test deadline.");
			await Task.Delay(20);
		}
	}
}
