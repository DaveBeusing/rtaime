// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Provider.Inference;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class AIShowcaseIntegrationTests
{
	[Fact]
	public async Task Operator_enable_runs_AIHost_segmentation_and_Runtime_applies_visible_synchronized_highlight()
	{
		await using var fixture = await ProcessFixture.StartAsync();
		var client = fixture.Client;
		var initial = await SynchronizeWithRetryAsync(client);
		Assert.False(initial.AIShowcase.Enabled);

		var enabled = await client.SetAIShowcaseEnabledAsync(true);
		Assert.True(enabled.Enabled);

		var running = await WaitForAIAsync(
			client,
			snapshot => snapshot.Enabled && snapshot.Status == RuntimeAIShowcaseStates.Running);

		Assert.Equal("Person Segmentation Highlight", running.Feature);
		Assert.Equal("Managed Reference Person Segmentation", running.Provider);
		Assert.Equal(1U, running.PersonRegionCount);
		Assert.True(running.InferenceTime > TimeSpan.Zero);
		Assert.NotNull(running.SourceSequence);
		Assert.NotNull(running.AppliedSequence);
		Assert.True(running.AppliedSequence >= running.SourceSequence);
		Assert.True(running.Confidence >= 0.90);
		Assert.True(running.EffectVisible);
		Assert.Equal(RuntimeExecutionStatus.Committed, fixture.Runtime.Runtime!.Snapshot.Runtime.Status);
		Assert.Equal(V1VisualLayerMode.Dynamic, fixture.Runtime.Runtime.Snapshot.VisualLayerMode);

		await client.SetAIShowcaseEnabledAsync(false);
		var disabled = await WaitForAIAsync(client, snapshot => !snapshot.Enabled && snapshot.Status == RuntimeAIShowcaseStates.Disabled);
		Assert.False(disabled.EffectVisible);
		Assert.NotEqual(V1VisualLayerMode.Dynamic, fixture.Runtime.Runtime.Snapshot.VisualLayerMode);
		Assert.Equal(RuntimeExecutionStatus.Committed, fixture.Runtime.Runtime.Snapshot.Runtime.Status);
	}

	[Fact]
	public async Task Provider_unavailable_falls_back_to_clean_Program_without_losing_Runtime_commit()
	{
		await using var fixture = await ProcessFixture.StartAsync(limits => new AIHostService(
			new GovernedInferenceRuntime(
				new IInferenceProvider[]
				{
					new ManagedReferencePersonSegmentationProvider(InferenceProviderState.Unavailable)
				},
				limits)));

		await SynchronizeWithRetryAsync(fixture.Client);
		await fixture.Client.SetAIShowcaseEnabledAsync(true);
		var unavailable = await WaitForAIAsync(
			fixture.Client,
			snapshot => snapshot.Enabled && snapshot.Status == RuntimeAIShowcaseStates.Unavailable);

		Assert.False(unavailable.EffectVisible);
		Assert.Equal(0U, unavailable.PersonRegionCount);
		Assert.NotNull(unavailable.Failure);
		Assert.NotEqual(V1VisualLayerMode.Dynamic, fixture.Runtime.Runtime!.Snapshot.VisualLayerMode);
		Assert.Equal(RuntimeExecutionStatus.Committed, fixture.Runtime.Runtime.Snapshot.Runtime.Status);
		var before = fixture.Runtime.Runtime.Snapshot.NextSequenceNumber;
		await Task.Delay(100);
		Assert.True(fixture.Runtime.Runtime.Snapshot.NextSequenceNumber > before);
	}

	[Fact]
	public async Task Inference_timeout_is_bounded_and_Program_continues()
	{
		await using var fixture = await ProcessFixture.StartAsync(limits => new AIHostService(
			new GovernedInferenceRuntime(
				new IInferenceProvider[]
				{
					new ManagedReferencePersonSegmentationProvider(executionDelay: TimeSpan.FromSeconds(2))
				},
				limits)));

		await SynchronizeWithRetryAsync(fixture.Client);
		await fixture.Client.SetAIShowcaseEnabledAsync(true);
		var timedOut = await WaitForAIAsync(
			fixture.Client,
			snapshot => snapshot.Enabled && snapshot.Status == RuntimeAIShowcaseStates.Timeout,
			timeoutMilliseconds: 5000);

		Assert.False(timedOut.EffectVisible);
		Assert.NotNull(timedOut.Failure);
		Assert.NotEqual(V1VisualLayerMode.Dynamic, fixture.Runtime.Runtime!.Snapshot.VisualLayerMode);
		Assert.Equal(RuntimeExecutionStatus.Committed, fixture.Runtime.Runtime.Snapshot.Runtime.Status);
		var before = fixture.Runtime.Runtime.Snapshot.NextSequenceNumber;
		await Task.Delay(100);
		Assert.True(fixture.Runtime.Runtime.Snapshot.NextSequenceNumber > before);
	}

	private static async Task<OperatorAIShowcaseDescriptor> WaitForAIAsync(
		OperatorControlClient client,
		Func<OperatorAIShowcaseDescriptor, bool> predicate,
		int timeoutMilliseconds = 4000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (DateTime.UtcNow < deadline)
		{
			var snapshot = await client.SynchronizeAsync();
			if (predicate(snapshot.AIShowcase))
				return snapshot.AIShowcase;
			await Task.Delay(50);
		}
		throw new TimeoutException("AI showcase state did not reach the expected condition.");
	}

	private static async Task<OperatorStatusSnapshot> SynchronizeWithRetryAsync(OperatorControlClient client)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < 20; attempt++)
		{
			try { return await client.SynchronizeAsync(); }
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
			{
				last = exception;
				await Task.Delay(50);
			}
		}
		throw new InvalidOperationException("Operator snapshot did not become available.", last);
	}

	private sealed class ProcessFixture : IAsyncDisposable
	{
		private readonly CancellationTokenSource _aiStop;
		private readonly CancellationTokenSource _runtimeStop;
		private readonly CancellationTokenSource _controlStop;
		private readonly Task<AIHostExitCode> _aiRun;
		private readonly Task<RuntimeHostExitCode> _runtimeRun;
		private readonly Task<ControlHostExitCode> _controlRun;

		private ProcessFixture(
			AIHostProcess ai,
			RuntimeHostProcess runtime,
			ControlHostProcess control,
			OperatorControlClient client,
			CancellationTokenSource aiStop,
			CancellationTokenSource runtimeStop,
			CancellationTokenSource controlStop,
			Task<AIHostExitCode> aiRun,
			Task<RuntimeHostExitCode> runtimeRun,
			Task<ControlHostExitCode> controlRun)
		{
			AI = ai;
			Runtime = runtime;
			Control = control;
			Client = client;
			_aiStop = aiStop;
			_runtimeStop = runtimeStop;
			_controlStop = controlStop;
			_aiRun = aiRun;
			_runtimeRun = runtimeRun;
			_controlRun = controlRun;
		}

		public AIHostProcess AI { get; }
		public RuntimeHostProcess Runtime { get; }
		public ControlHostProcess Control { get; }
		public OperatorControlClient Client { get; }

		public static async Task<ProcessFixture> StartAsync(
			Func<InferenceRuntimeLimits, AIHostService>? aiFactory = null)
		{
			var aiEndpoint = Endpoint("ai");
			var runtimeEndpoint = Endpoint("runtime");
			var controlEndpoint = Endpoint("control");
			var aiStop = new CancellationTokenSource();
			var runtimeStop = new CancellationTokenSource();
			var controlStop = new CancellationTokenSource();

			var ai = new AIHostProcess(
				AIHostProcessOptions.Default with { ListenEndpoint = aiEndpoint },
				aiFactory);
			var runtime = new RuntimeHostProcess(
				RuntimeHostProcessOptions.Default with
				{
					ListenEndpoint = runtimeEndpoint,
					AIEndpoint = aiEndpoint
				});
			var control = new ControlHostProcess(
				ControlHostProcessOptions.Default with
				{
					ListenEndpoint = controlEndpoint,
					RuntimeEndpoint = runtimeEndpoint,
					ConnectTimeout = TimeSpan.FromMilliseconds(100),
					RequestTimeout = TimeSpan.FromSeconds(2),
					RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
				});

			var aiRun = ai.RunAsync(aiStop.Token);
			await WaitUntilAsync(() => ai.Lifecycle.State is AIHostProcessState.Ready or AIHostProcessState.Degraded);
			var runtimeRun = runtime.RunAsync(runtimeStop.Token);
			await WaitUntilAsync(() => runtime.Lifecycle.State == RuntimeHostProcessState.Ready);
			var controlRun = control.RunAsync(controlStop.Token);
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(3)));
			return new ProcessFixture(ai, runtime, control, client, aiStop, runtimeStop, controlStop, aiRun, runtimeRun, controlRun);
		}

		public async ValueTask DisposeAsync()
		{
			_controlStop.Cancel();
			_runtimeStop.Cancel();
			_aiStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await _controlRun);
			Assert.Equal(RuntimeHostExitCode.Success, await _runtimeRun);
			Assert.Equal(AIHostExitCode.Success, await _aiRun);
			_controlStop.Dispose();
			_runtimeStop.Dispose();
			_aiStop.Dispose();
		}

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (!condition())
			{
				if (DateTime.UtcNow >= deadline)
					throw new TimeoutException("AP-55 process fixture did not reach the expected state.");
				await Task.Delay(20);
			}
		}

		private static string Endpoint(string purpose) =>
			$"rtaime.test.ap55.{purpose}.{Guid.NewGuid():N}";
	}
}
