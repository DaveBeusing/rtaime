// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class HostIpcSessionLifecycleIntegrationTests
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task RuntimeHost_shutdown_drains_primary_and_monitoring_sessions_before_runtime_disposal()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var endpoint = Endpoint("runtime-drain");
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with
		{
			ListenEndpoint = endpoint
		});
		var run = process.RunAsync(stop.Token);

		await WaitUntilAsync(() =>
			process.Lifecycle.State == RuntimeHostProcessState.Ready &&
			process.IpcServer?.Running == true &&
			process.MonitoringServer?.Running == true);

		await using var primary = await OpenSessionAsync(
			endpoint,
			"ControlHost",
			new Dictionary<string, string>
			{
				["runtime"] = RuntimeContractVersion.Current.ToString(),
				["provider"] = ProviderContractVersion.Current.ToString()
			});
		await using var monitoring = new NamedPipeClientStream(
			".",
			$"{endpoint}.monitor",
			PipeDirection.In,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await monitoring.ConnectAsync(3000);

		await WaitUntilAsync(() =>
			process.IpcServer?.Sessions.ActiveSessions == 1 &&
			process.MonitoringServer?.Sessions.ActiveSessions == 1 &&
			process.Runtime?.MonitoringHub.Statistics.Subscribers == 1);

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);

		Assert.True(process.RuntimeDisposed);
		Assert.NotNull(process.IpcServer);
		Assert.NotNull(process.MonitoringServer);
		Assert.Equal(0, process.IpcServer!.Sessions.ActiveSessions);
		Assert.Equal(0, process.MonitoringServer!.Sessions.ActiveSessions);
		Assert.True(process.IpcServer.Sessions.StopRequested);
		Assert.True(process.MonitoringServer.Sessions.StopRequested);
		Assert.False(process.IpcServer.Sessions.DrainTimedOut);
		Assert.False(process.MonitoringServer.Sessions.DrainTimedOut);
	}

	[Fact]
	public async Task ControlHost_shutdown_drains_active_operator_session_before_server_dependencies()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var endpoint = Endpoint("control-drain");
		var durabilityRoot = Path.Combine(Path.GetTempPath(), "rtaime-tests", Guid.NewGuid().ToString("N"));
		using var stop = new CancellationTokenSource();
		var process = new ControlHostProcess(
			ControlHostProcessOptions.Default with
			{
				ListenEndpoint = endpoint,
				DurabilityRoot = durabilityRoot,
				RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
			},
			() => new UnboundControlRuntimeTransportSeam());
		var run = process.RunAsync(stop.Token);

		try
		{
			await WaitUntilAsync(() =>
				process.IpcServer?.Running == true &&
				process.Lifecycle.State == ControlHostProcessState.Degraded);

			await using var session = await OpenSessionAsync(
				endpoint,
				"OperatorClient",
				new Dictionary<string, string>
				{
					["control"] = ControlContractVersion.Current.ToString()
				});

			await WaitUntilAsync(() => process.IpcServer?.Sessions.ActiveSessions == 1);
			stop.Cancel();

			Assert.Equal(ControlHostExitCode.Success, await run);
			Assert.NotNull(process.IpcServer);
			Assert.Equal(0, process.IpcServer!.Sessions.ActiveSessions);
			Assert.True(process.IpcServer.Sessions.StopRequested);
			Assert.False(process.IpcServer.Sessions.DrainTimedOut);
		}
		finally
		{
			stop.Cancel();
			if (Directory.Exists(durabilityRoot))
				Directory.Delete(durabilityRoot, recursive: true);
		}
	}

	[Fact]
	public async Task AIHost_shutdown_drains_active_session_before_service_disposal()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var endpoint = Endpoint("ai-drain");
		using var stop = new CancellationTokenSource();
		var process = new AIHostProcess(AIHostProcessOptions.Default with
		{
			ListenEndpoint = endpoint
		});
		var run = process.RunAsync(stop.Token);

		await WaitUntilAsync(() =>
			process.Lifecycle.State == AIHostProcessState.Ready &&
			process.IpcServer?.Running == true);

		await using var session = await OpenSessionAsync(
			endpoint,
			"ControlHost",
			new Dictionary<string, string>
			{
				["ai"] = AIContractVersion.Current.ToString(),
				["media"] = MediaContractVersion.Current.ToString()
			});

		await WaitUntilAsync(() => process.IpcServer?.Sessions.ActiveSessions == 1);
		stop.Cancel();

		Assert.Equal(AIHostExitCode.Success, await run);
		Assert.True(process.ServiceDisposed);
		Assert.NotNull(process.IpcServer);
		Assert.Equal(0, process.IpcServer!.Sessions.ActiveSessions);
		Assert.True(process.IpcServer.Sessions.StopRequested);
		Assert.False(process.IpcServer.Sessions.DrainTimedOut);
	}

	[Fact]
	public async Task Runtime_listener_non_cooperative_session_hits_bounded_drain_timeout()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var endpoint = Endpoint("runtime-timeout");
		var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var listener = new RuntimePipeListener(
			endpoint,
			"timeout-test",
			() => CreateServerPipe(endpoint, PipeDirection.InOut),
			async (pipe, _) =>
			{
				await using (pipe.ConfigureAwait(false))
				{
					started.TrySetResult(true);
					await release.Task;
				}
			},
			TimeSpan.FromMilliseconds(50));

		await listener.StartAsync();
		await using var client = new NamedPipeClientStream(
			".",
			endpoint,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await client.ConnectAsync(3000);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

		var timeout = await Assert.ThrowsAsync<TimeoutException>(() => listener.DisposeAsync().AsTask());
		Assert.Contains("did not drain", timeout.Message, StringComparison.Ordinal);
		Assert.Equal(RuntimePipeListenerState.Faulted, listener.Snapshot.State);
		Assert.True(listener.Sessions.DrainTimedOut);
		Assert.Equal(1, listener.Sessions.ActiveSessions);

		release.TrySetResult(true);
		await WaitUntilAsync(() => listener.Sessions.ActiveSessions == 0);
		await listener.DisposeAsync();
	}

	[Fact]
	public async Task Concurrent_RuntimeHost_clients_allocate_unique_sequences_and_preserve_all_state_version_increments()
	{
		if (!OperatingSystem.IsWindows())
			return;

		const int clients = 12;
		var endpoint = Endpoint("runtime-concurrency");
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with
		{
			ListenEndpoint = endpoint
		});
		var run = process.RunAsync(stop.Token);

		try
		{
			await WaitUntilAsync(() =>
				process.Lifecycle.State == RuntimeHostProcessState.Ready &&
				process.IpcServer?.Running == true);

			var exchanges = Enumerable.Range(0, clients)
				.Select(_ => ExchangeAsync(
					endpoint,
					"ControlHost",
					new Dictionary<string, string>
					{
						["runtime"] = RuntimeContractVersion.Current.ToString(),
						["provider"] = ProviderContractVersion.Current.ToString()
					},
					"runtime.graphics.overlay.clear",
					new { }))
				.ToArray();
			var responses = await Task.WhenAll(exchanges);

			try
			{
				var sequences = responses
					.Select(response => response.RootElement.GetProperty("sequence").GetUInt64())
					.ToArray();
				Assert.Equal(clients, sequences.Distinct().Count());
				Assert.All(responses, response =>
					Assert.Equal(
						"runtime.graphics.overlay.response",
						response.RootElement.GetProperty("messageType").GetString()));
				Assert.Equal(checked(1UL + (ulong)clients), process.IpcServer!.StateVersion);
			}
			finally
			{
				foreach (var response in responses)
					response.Dispose();
			}
		}
		finally
		{
			stop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await run);
		}
	}

	[Fact]
	public async Task Concurrent_AIHost_clients_allocate_unique_sequences_and_preserve_all_state_version_increments()
	{
		if (!OperatingSystem.IsWindows())
			return;

		const int clients = 8;
		var endpoint = Endpoint("ai-concurrency");
		using var stop = new CancellationTokenSource();
		var process = new AIHostProcess(AIHostProcessOptions.Default with
		{
			ListenEndpoint = endpoint
		});
		var run = process.RunAsync(stop.Token);

		try
		{
			await WaitUntilAsync(() =>
				process.Lifecycle.State == AIHostProcessState.Ready &&
				process.IpcServer?.Running == true);

			var sourceA = new MediaSourceId(Identity.New());
			var sourceB = new MediaSourceId(Identity.New());
			var media = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
			var frame = media.SourceA.GenerateFrame(0);
			var exchanges = Enumerable.Range(0, clients)
				.Select(_ => ExchangeAsync(
					endpoint,
					"ControlHost",
					new Dictionary<string, string>
					{
						["ai"] = AIContractVersion.Current.ToString(),
						["media"] = MediaContractVersion.Current.ToString()
					},
					"ai.inference.execute",
					CreateAiExecutionPayload(frame)))
				.ToArray();
			var responses = await Task.WhenAll(exchanges);

			try
			{
				var sequences = responses
					.Select(response => response.RootElement.GetProperty("sequence").GetUInt64())
					.ToArray();
				Assert.Equal(clients, sequences.Distinct().Count());
				Assert.All(responses, response =>
					Assert.Equal(
						"ai.inference.execute.response",
						response.RootElement.GetProperty("messageType").GetString()));
				Assert.Equal(checked(1UL + (ulong)clients), process.IpcServer!.StateVersion);
			}
			finally
			{
				foreach (var response in responses)
					response.Dispose();
			}
		}
		finally
		{
			stop.Cancel();
			Assert.Equal(AIHostExitCode.Success, await run);
		}
	}

	private static async Task<NamedPipeClientStream> OpenSessionAsync(
		string endpoint,
		string role,
		Dictionary<string, string> contractVersions)
	{
		var pipe = new NamedPipeClientStream(
			".",
			endpoint,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			await pipe.ConnectAsync(3000);
			var correlationId = Identity.New().ToString();
			await WriteEnvelopeAsync(
				pipe,
				"client.hello",
				Identity.New().ToString(),
				correlationId,
				new
				{
					protocolVersion = "1.0",
					role,
					hostInstanceId = Identity.New().ToString(),
					contractVersions
				});
			using var hello = await ReadEnvelopeAsync(pipe);
			Assert.Equal("server.hello", hello.RootElement.GetProperty("messageType").GetString());
			return pipe;
		}
		catch
		{
			await pipe.DisposeAsync();
			throw;
		}
	}

	private static async Task<JsonDocument> ExchangeAsync(
		string endpoint,
		string role,
		Dictionary<string, string> contractVersions,
		string messageType,
		object payload)
	{
		await using var pipe = await OpenSessionAsync(endpoint, role, contractVersions);
		var correlationId = Identity.New().ToString();
		await WriteEnvelopeAsync(pipe, messageType, Identity.New().ToString(), correlationId, payload);
		return await ReadEnvelopeAsync(pipe);
	}

	private static async Task WriteEnvelopeAsync(
		Stream stream,
		string messageType,
		string requestId,
		string correlationId,
		object payload)
	{
		var envelope = new
		{
			protocolVersion = "1.0",
			messageType,
			requestId,
			correlationId,
			hostInstanceId = Identity.New().ToString(),
			sentAtUtc = DateTimeOffset.UtcNow,
			stateVersion = 0UL,
			sequence = 0UL,
			payload
		};
		var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
		var prefix = new byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
		await stream.WriteAsync(prefix);
		await stream.WriteAsync(bytes);
		await stream.FlushAsync();
	}

	private static async Task<JsonDocument> ReadEnvelopeAsync(Stream stream)
	{
		var prefix = new byte[4];
		await ReadExactlyAsync(stream, prefix);
		var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
		Assert.InRange(length, 1U, 1024U * 1024U);
		var bytes = new byte[checked((int)length)];
		await ReadExactlyAsync(stream, bytes);
		return JsonDocument.Parse(bytes);
	}

	private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
	{
		var offset = 0;
		while (offset < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer[offset..]);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
	}

	private static NamedPipeServerStream CreateServerPipe(string endpoint, PipeDirection direction) =>
		new(
			endpoint,
			direction,
			NamedPipeServerStream.MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

	private static object CreateAiExecutionPayload(FrameDescriptor frame) =>
		new
		{
			version = AIContractVersion.Current.ToString(),
			request = new
			{
				requestId = InferenceRequestId.New().ToString(),
				capabilityId = ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId.ToString(),
				inputFrame = new
				{
					sourceId = frame.SourceId.ToString(),
					surfaceId = frame.Surface.SurfaceId.ToString(),
					format = new
					{
						width = frame.Surface.Format.Width,
						height = frame.Surface.Format.Height,
						frameRate = frame.Surface.Format.FrameRate.ToString(),
						pixelFormat = (int)frame.Surface.Format.PixelFormat,
						scanMode = (int)frame.Surface.Format.ScanMode
					},
					storageDomain = (int)frame.Surface.StorageDomain,
					ownership = (int)frame.Surface.Ownership,
					generation = frame.Surface.Lifetime.Generation.Value,
					leaseId = frame.Surface.Lifetime.LeaseId?.ToString(),
					handleKind = frame.Surface.Handle?.Kind,
					handleValue = frame.Surface.Handle?.Value,
					sequenceNumber = frame.Timing.SequenceNumber,
					presentationTimestamp = frame.Timing.PresentationTimestamp,
					timebase = frame.Timing.Timebase.ToString()
				},
				deadlineUtc = new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(10)).ToString(),
				parameters = Array.Empty<object>()
			},
			context = new
			{
				timingDomainId = Identity.New().ToString(),
				productionTimestamp = frame.Timing.PresentationTimestamp,
				productionTimebase = frame.Timing.Timebase.ToString(),
				computeUnits = 10U,
				vramBytes = 64UL * 1024 * 1024,
				maxInferenceRatePerSecond = 25U,
				inputResourceKind = "surface.descriptor",
				inputResourceValue = frame.Surface.SurfaceId.ToString()
			}
		};

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
	{
		var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
		while (!condition())
		{
			if (DateTimeOffset.UtcNow >= deadline)
				throw new TimeoutException("Expected IPC lifecycle condition was not reached.");
			await Task.Delay(10);
		}
	}

	private static string Endpoint(string role) =>
		$"rtaime.test.ipc-session.{role}.{Guid.NewGuid():N}";
}
