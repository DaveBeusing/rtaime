// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ControlTransportFaultQualificationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Lost_TAKE_requires_snapshot_without_replay_before_or_after_commit(bool loseAfterCommit)
	{
		var runtimeEndpoint = Endpoint("runtime");
		var controlEndpoint = Endpoint("control");
		var root = Path.Combine(Path.GetTempPath(), "rtaime-fault-qualification", Guid.NewGuid().ToString("N"));
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await using var proxy = new LosingResponseProxy(Endpoint("proxy"), controlEndpoint, loseAfterCommit);
		try
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true);
			var transport = new NamedPipeOperatorControlTransport(proxy.Endpoint, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
			var client = new OperatorControlClient(transport);
			var initial = await client.SynchronizeAsync();
			var target = initial.Sources.First(source => source.Id != initial.Production.Routing.ProgramSourceId.ToString());
			Assert.True((await client.SelectPreviewAsync(target.Id)).Accepted);
			var before = client.Snapshot!.Production;
			var hostIdentity = transport.HostInstanceId;
			var command = new CutProgramCommand(
				new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(), before.ProductionId, before.Revision),
				before.Routing.PreviewSourceId);

			// The proxy forwards real Control/Runtime IPC, then loses exactly one TAKE.
			// Both failure positions are acknowledged by its owned session completing.
			proxy.LoseNextTake();
			await Assert.ThrowsAnyAsync<IOException>(() => transport.CutProgramAsync(command).AsTask());
			await proxy.Lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(transport.Connected);
			Assert.True(transport.RequiresFullSnapshot);
			Assert.Equal(before, client.Snapshot!.Production);
			Assert.False(control.Control!.HasPendingExecution);
			var expectedRevision = before.Revision.Value + (loseAfterCommit ? 1UL : 0UL);
			Assert.Equal(expectedRevision, control.Control.State.Revision.Value);
			Assert.Equal(loseAfterCommit ? before.Routing.PreviewSourceId : before.Routing.ProgramSourceId,
				control.Control.State.Routing.ProgramSourceId);

			// A handshake with the same host must not authorize stale UI intent.
			await Assert.ThrowsAsync<InvalidOperationException>(() => client.CutPreviewAsync().AsTask());
			Assert.Equal(1, proxy.TakeRequests);
			Assert.Equal(expectedRevision, control.Control.State.Revision.Value);
			Assert.True(transport.RequiresFullSnapshot);

			var recovered = await client.SynchronizeAsync();
			Assert.Equal(hostIdentity, transport.HostInstanceId);
			Assert.False(transport.RequiresFullSnapshot);
			Assert.Equal(expectedRevision, recovered.Production.Revision.Value);
			Assert.Equal(control.Control.State, recovered.Production);
			Assert.Equal(1, proxy.TakeRequests); // Snapshot recovery did not replay TAKE.

			if (loseAfterCommit)
			{
				// An explicit duplicate old command cannot cause a second commit.
				var duplicate = await transport.CutProgramAsync(command);
				Assert.False(duplicate.Accepted);
				Assert.Equal(expectedRevision, control.Control.State.Revision.Value);
				Assert.False(control.Control.HasPendingExecution);
			}
			else
			{
				Assert.True((await client.CutPreviewAsync()).Accepted);
				Assert.Equal(expectedRevision + 1UL, control.Control.State.Revision.Value);
			}
		}
		finally
		{
			await proxy.DisposeAsync();
			controlStop.Cancel();
			runtimeStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(10)));
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(10)));
			Assert.Equal(0, control.IpcServer!.Sessions.ActiveSessions);
			Assert.Equal(0, runtime.IpcServer!.Sessions.ActiveSessions);
			Assert.Equal(0, runtime.MonitoringServer!.Sessions.ActiveSessions);
			Assert.Equal(0, runtime.FinalRuntimeSnapshot!.ActiveGpuSurfaces);
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	private sealed class LosingResponseProxy : IAsyncDisposable
	{
		private readonly string _target;
		private readonly bool _loseAfterCommit;
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _run;
		private int _lose;
		private int _takes;
		private bool _disposed;

		public LosingResponseProxy(string endpoint, string target, bool loseAfterCommit)
		{
			Endpoint = endpoint;
			_target = target;
			_loseAfterCommit = loseAfterCommit;
			_run = RunAsync();
		}

		public string Endpoint { get; }
		public int TakeRequests => Volatile.Read(ref _takes);
		public TaskCompletionSource<bool> Lost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public void LoseNextTake() => Interlocked.Exchange(ref _lose, 1);

		private async Task RunAsync()
		{
			try
			{
				while (!_stop.IsCancellationRequested)
				{
					await using var incoming = new NamedPipeServerStream(
						Endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
					await incoming.WaitForConnectionAsync(_stop.Token);
					try { await RelayAsync(incoming); }
					catch (IOException) { } // Client fencing closes immediately after the handshake.
				}
			}
			catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
		}

		private async Task RelayAsync(Stream incoming)
		{
			await using var outgoing = new NamedPipeClientStream(".", _target, PipeDirection.InOut, PipeOptions.Asynchronous);
			await outgoing.ConnectAsync(_stop.Token);
			await WriteAsync(outgoing, await ReadAsync(incoming));
			await WriteAsync(incoming, await ReadAsync(outgoing));
			var request = await ReadAsync(incoming);
			using var document = JsonDocument.Parse(request);
			var take = document.RootElement.GetProperty("messageType").GetString() == "control.program.cut";
			if (take) Interlocked.Increment(ref _takes);
			var lose = take && Interlocked.Exchange(ref _lose, 0) == 1;
			if (lose && !_loseAfterCommit)
			{
				Lost.TrySetResult(true);
				return;
			}
			await WriteAsync(outgoing, request);
			var response = await ReadAsync(outgoing);
			if (lose)
			{
				Lost.TrySetResult(true);
				return;
			}
			await WriteAsync(incoming, response);
		}

		private async Task<byte[]> ReadAsync(Stream stream)
		{
			var prefix = new byte[4];
			await stream.ReadExactlyAsync(prefix, _stop.Token);
			var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
			if (length is 0 or > 1024 * 1024) throw new InvalidDataException("Invalid relay frame length.");
			var bytes = new byte[checked((int)length)];
			await stream.ReadExactlyAsync(bytes, _stop.Token);
			return bytes;
		}

		private async Task WriteAsync(Stream stream, byte[] bytes)
		{
			var prefix = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
			await stream.WriteAsync(prefix, _stop.Token);
			await stream.WriteAsync(bytes, _stop.Token);
			await stream.FlushAsync(_stop.Token);
		}

		public async ValueTask DisposeAsync()
		{
			if (_disposed) return;
			_disposed = true;
			_stop.Cancel();
			await _run.WaitAsync(TimeSpan.FromSeconds(5));
			_stop.Dispose();
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (!condition()) await Task.Delay(20, timeout.Token);
	}

	private static string Endpoint(string purpose) => $"rtaime.test.fault.{purpose}.{Guid.NewGuid():N}";
}
