// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.ControlHost;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ProductionIpcRecoveryTests
{
	[Fact]
	public async Task ControlHost_starts_degraded_and_becomes_ready_when_RuntimeHost_appears()
	{
		var runtimeEndpoint = Endpoint("runtime-late");
		var controlEndpoint = Endpoint("control-first");
		using var controlStop = new CancellationTokenSource();
		using var runtimeStop = new CancellationTokenSource();
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			ConnectTimeout = TimeSpan.FromMilliseconds(100),
			RequestTimeout = TimeSpan.FromSeconds(1),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Degraded);
		Assert.False(control.Control!.HasAuthoritativeState);

		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control.HasAuthoritativeState);

		Assert.True(control.RuntimeTransport!.IsConnected);
		Assert.False(string.IsNullOrWhiteSpace(control.RuntimeTransport.HostInstanceId));

		controlStop.Cancel();
		runtimeStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	[Fact]
	public async Task Runtime_loss_rejects_operator_mutation_without_advancing_authority()
	{
		var runtimeEndpoint = Endpoint("runtime-loss");
		var controlEndpoint = Endpoint("control-loss");
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
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
		var client = new OperatorControlClient(transport);
		var snapshot = await client.SynchronizeAsync();
		var revisionBefore = snapshot.Production.Revision;

		runtimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Degraded);

		var response = await client.SelectPreviewAsync(snapshot.Sources[1].Id);
		Assert.False(response.Accepted);
		Assert.NotNull(response.Failure);
		Assert.Equal(revisionBefore, control.Control!.State.Revision);
		Assert.False(control.Control.HasPendingExecution);

		controlStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
	}

	[Fact]
	public async Task Operator_disconnect_requires_and_accepts_full_snapshot_resynchronization()
	{
		var runtimeEndpoint = Endpoint("runtime-operator-reconnect");
		var controlEndpoint = Endpoint("control-operator-reconnect");
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
		var client = new OperatorControlClient(transport);
		var first = await client.SynchronizeAsync();
		var stateVersionBefore = transport.StateVersion;
		var hostInstanceBefore = transport.HostInstanceId;

		client.Disconnect();
		Assert.False(client.Connected);
		var resynchronized = await client.SynchronizeAsync();

		Assert.True(client.Connected);
		Assert.Equal(first.Production, resynchronized.Production);
		Assert.Equal(hostInstanceBefore, transport.HostInstanceId);
		Assert.True(transport.StateVersion >= stateVersionBefore);

		controlStop.Cancel();
		runtimeStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException("Condition was not reached before the integration-test deadline.");
			await Task.Delay(20);
		}
	}

	private static string Endpoint(string purpose) => $"rtaime.test.{purpose}.{Guid.NewGuid():N}";
}
