// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

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

		Assert.Equal(OperatorHealthStates.Pass, health.Engine.State);
		Assert.Equal(OperatorHealthStates.Pass, health.Control.State);
		Assert.Equal(OperatorHealthStates.Pass, health.Runtime.State);
		Assert.Equal(OperatorHealthStates.Pass, health.Media.State);
		Assert.Equal(OperatorHealthStates.Pass, health.Provider.State);
		Assert.Equal(OperatorHealthStates.Pass, health.GpuProvider.State);
		Assert.Equal("UNVERIFIED", health.GpuUtilization);
		Assert.Equal("UNVERIFIED", health.Vram);
		Assert.Equal(3UL, health.DroppedFrames);
		Assert.True(health.FrameTime > TimeSpan.Zero);
		Assert.True(health.FrameBudget > health.FrameTime);
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

		Assert.Equal(OperatorHealthStates.Unverified, health.Provider.State);
		Assert.Equal(OperatorHealthStates.Unverified, health.GpuProvider.State);
		Assert.Equal(OperatorHealthStates.Unverified, health.Engine.State);
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
			Assert.NotEqual(OperatorHealthStates.Fail, ready.Health.Control.State);

			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Degraded);

			var disconnected = await SynchronizeWithRetryAsync(client);
			Assert.Equal(OperatorHealthStates.Fail, disconnected.Health.Runtime.State);
			Assert.Equal(OperatorHealthStates.Fail, disconnected.Health.Media.State);
			Assert.Equal(OperatorHealthStates.Fail, disconnected.Health.Provider.State);
			Assert.Equal(OperatorHealthStates.Fail, disconnected.Health.GpuProvider.State);
			Assert.Equal(OperatorHealthStates.Fail, disconnected.Health.Engine.State);
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
		Assert.Equal(0UL, counter.Observe(TimeSpan.FromMilliseconds(20), frame));
		Assert.Equal(1UL, counter.Observe(TimeSpan.FromMilliseconds(60), frame));
		Assert.Equal(4UL, counter.Observe(TimeSpan.FromMilliseconds(80), frame, outputBackpressure: 2, outputRejected: 1));
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
				"UNVERIFIED: no qualified utilization or used-VRAM source."));
	}

	private static ProviderDescriptor CreateGpuProvider(ProviderAvailabilityState state)
	{
		var providerId = new ProviderId(Identity.Parse("74000000-0000-0000-0000-000000000010"));
		var failure = state == ProviderAvailabilityState.Available
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
