// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ProductionIpcIntegrationTests
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task RuntimeHost_named_pipe_exposes_provider_and_runtime_snapshot()
	{
		var endpoint = Endpoint("runtime");
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);

		var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		await RetryAsync(() => transport.ConnectAsync().AsTask());
		var providers = await transport.GetProviderDescriptorsAsync();
		var snapshot = await transport.GetSnapshotAsync();

		Assert.True(transport.IsConnected);
		Assert.False(string.IsNullOrWhiteSpace(transport.HostInstanceId));
		Assert.NotEmpty(providers);
		Assert.Equal(RuntimeExecutionStatus.Idle, snapshot.Runtime.Status);
		Assert.Equal(Revision.Initial, snapshot.Runtime.ExecutionRevision);

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task Operator_commands_cross_ControlHost_and_RuntimeHost_process_boundaries()
	{
		var runtimeEndpoint = Endpoint("runtime");
		var controlEndpoint = Endpoint("control");
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

		var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		var client = new OperatorControlClient(transport);
		var initial = await client.SynchronizeAsync();
		var sourceA = initial.Sources[0];
		var sourceB = initial.Sources[1];

		var preview = await client.SelectPreviewAsync(sourceB.Id);
		Assert.True(preview.Accepted, preview.Failure?.ToString());
		var cut = await client.CutAsync(sourceB.Id);
		Assert.True(cut.Accepted, cut.Failure?.ToString());
		var previewBack = await client.SelectPreviewAsync(sourceA.Id);
		Assert.True(previewBack.Accepted, previewBack.Failure?.ToString());
		var dissolve = await client.DissolveAsync(sourceA.Id, 12);
		Assert.True(dissolve.Accepted, dissolve.Failure?.ToString());

		Assert.Equal(sourceA.Id, client.Snapshot!.Production.Routing.ProgramSourceId.ToString());
		Assert.True(client.Snapshot.Production.Revision.Value >= 4);
		Assert.True(transport.Connected);
		Assert.True(transport.StateVersion > 1);
		Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime!.Snapshot.Runtime.Status);

		controlStop.Cancel();
		runtimeStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	[Fact]
	public async Task Media_deck_crosses_operator_control_and_runtime_boundaries_with_persistent_markers()
	{
		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var runtimeEndpoint = Endpoint("runtime-deck");
		var controlEndpoint = Endpoint("control-deck");
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

		var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		var client = new OperatorControlClient(transport);
		var production = await client.SynchronizeAsync();
		var sourceId = new MediaSourceId(Identity.Parse(production.Sources[0].Id));
		await using var deck = new MediaDeckController(client);

		var opened = await deck.OpenAsync(referenceAsset.Path, sourceId);
		Assert.True(opened.IsLoaded, opened.Failure?.Message);
		Assert.Equal(Path.GetFileName(referenceAsset.Path), opened.Probe!.FileName);
		Assert.Equal(MediaDeckState.Ready, opened.State);
		Assert.Equal(50, opened.Transport!.Position.TotalFrames);

		var playing = await deck.PlayAsync();
		Assert.Equal(MediaDeckState.Playing, playing.State);
		await Task.Delay(120);
		await deck.RefreshAsync();
		Assert.True(deck.Snapshot.Transport!.Position.CurrentFrame > 0);

		await deck.PauseAsync();
		await deck.Timeline.SeekToFrameAsync(10);
		Assert.Equal(10, deck.Timeline.State.ConfirmedFrame);
		Assert.True(await deck.Markers.SetInAtCurrentFrameAsync());
		var cueId = await deck.Markers.AddCueAtCurrentFrameAsync("Decision");
		Assert.True(cueId.HasValue);
		Assert.Equal(10, deck.Snapshot.Markers!.InPointFrame);
		Assert.Single(deck.Snapshot.Markers.CuePoints);

		await deck.CloseAsync();
		var reopened = await deck.OpenAsync(referenceAsset.Path, sourceId);
		Assert.Equal(10, reopened.Markers!.InPointFrame);
		Assert.Equal("Decision", reopened.Markers.CuePoints.Single().Name);

		controlStop.Cancel();
		runtimeStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	[Fact]
	public async Task RuntimeHost_restart_resynchronizes_without_advancing_authoritative_revision()
	{
		var runtimeEndpoint = Endpoint("runtime-restart");
		var controlEndpoint = Endpoint("control-restart");
		using var firstRuntimeStop = new CancellationTokenSource();
		using var secondRuntimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var firstRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			ConnectTimeout = TimeSpan.FromMilliseconds(150),
			RequestTimeout = TimeSpan.FromSeconds(2),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var firstRun = firstRuntime.RunAsync(firstRuntimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
		var initial = await client.SynchronizeAsync();
		Assert.True((await client.SelectPreviewAsync(initial.Sources[1].Id)).Accepted);
		var revisionBeforeRestart = control.Control!.State.Revision;
		var firstInstance = control.RuntimeTransport!.HostInstanceId;

		firstRuntimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await firstRun);

		var secondRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var secondRun = secondRuntime.RunAsync(secondRuntimeStop.Token);
		await WaitUntilAsync(() =>
			control.Lifecycle.State == ControlHostProcessState.Ready &&
			!string.IsNullOrWhiteSpace(control.RuntimeTransport?.HostInstanceId) &&
			!string.Equals(firstInstance, control.RuntimeTransport!.HostInstanceId, StringComparison.Ordinal));

		Assert.Equal(revisionBeforeRestart, control.Control.State.Revision);
		Assert.Equal(RuntimeExecutionStatus.Committed, secondRuntime.Runtime!.Snapshot.Runtime.Status);

		controlStop.Cancel();
		secondRuntimeStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		Assert.Equal(RuntimeHostExitCode.Success, await secondRun);
	}

	[Fact]
	public async Task AIHost_executes_managed_reference_inference_over_named_pipe()
	{
		var endpoint = Endpoint("ai");
		using var stop = new CancellationTokenSource();
		var process = new AIHostProcess(AIHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);
		await WaitUntilAsync(() => process.Lifecycle.State == AIHostProcessState.Ready);

		var sourceA = new MediaSourceId(Identity.Parse("76000000-0000-0000-0000-00000000000a"));
		var sourceB = new MediaSourceId(Identity.Parse("76000000-0000-0000-0000-00000000000b"));
		var media = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
		var frame = media.SourceA.GenerateFrame(0);
		var payload = CreateAiExecutionPayload(frame);

		using var response = await ExchangeRawAsync(
			endpoint,
			"ControlHost",
			new Dictionary<string, string>
			{
				["ai"] = AIContractVersion.Current.ToString(),
				["media"] = MediaContractVersion.Current.ToString()
			},
			"ai.inference.execute",
			payload);

		Assert.Equal("ai.inference.execute.response", response.RootElement.GetProperty("messageType").GetString());
		var result = response.RootElement.GetProperty("payload").GetProperty("result");
		Assert.Equal((int)InferenceExecutionStatus.Succeeded, result.GetProperty("status").GetInt32());
		Assert.Equal(0U, process.Service!.Snapshot.ActiveRequests);

		stop.Cancel();
		Assert.Equal(AIHostExitCode.Success, await run);
	}

	[Fact]
	public async Task RuntimeHost_rejects_wrong_handshake_role_fail_closed()
	{
		var endpoint = Endpoint("runtime-role");
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);

		using var response = await ExchangeRawAsync(
			endpoint,
			"OperatorClient",
			new Dictionary<string, string>
			{
				["runtime"] = RuntimeContractVersion.Current.ToString(),
				["provider"] = "1.0"
			},
			messageType: null,
			payload: null,
			expectHandshakeError: true);

		Assert.Equal("error", response.RootElement.GetProperty("messageType").GetString());
		Assert.Equal("ipc.role.invalid", response.RootElement.GetProperty("payload").GetProperty("code").GetString());

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task RuntimeHost_can_be_addressed_as_a_separate_OS_process()
	{
		var endpoint = Endpoint("runtime-process");
		var repo = FindRepositoryRoot();
		var assembly = Path.Combine(repo, "src", "Hosts", "rtaime.RuntimeHost", "bin", "Release", "net10.0", "rtaime.RuntimeHost.dll");
		Assert.True(File.Exists(assembly), $"RuntimeHost build output was not found at '{assembly}'.");

		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = $"\"{assembly}\" --listen-endpoint={endpoint}",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		}) ?? throw new InvalidOperationException("Failed to start RuntimeHost process.");

		try
		{
			var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
			await RetryAsync(() => transport.ConnectAsync().AsTask(), attempts: 20, delay: TimeSpan.FromMilliseconds(100));
			var providers = await transport.GetProviderDescriptorsAsync();

			Assert.True(transport.IsConnected);
			Assert.NotEmpty(providers);
			Assert.False(process.HasExited);
		}
		finally
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
		}
	}

	[Fact]
	public void Remote_state_synchronizer_requires_exact_base_version_and_host_identity()
	{
		var synchronizer = new RemoteStateSynchronizer();
		synchronizer.AcceptFullSnapshot("host-a", 5);

		Assert.False(synchronizer.TryApplyDelta("host-b", 5, 6));
		Assert.False(synchronizer.TryApplyDelta("host-a", 4, 6));
		Assert.True(synchronizer.TryApplyDelta("host-a", 5, 6));
		Assert.Equal(6UL, synchronizer.StateVersion);
	}

	private static object CreateAiExecutionPayload(FrameDescriptor frame)
	{
		return new
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
	}

	private static async Task<JsonDocument> ExchangeRawAsync(
		string endpoint,
		string role,
		Dictionary<string, string> contractVersions,
		string? messageType,
		object? payload,
		bool expectHandshakeError = false)
	{
		await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await pipe.ConnectAsync(3000);
		var correlationId = Identity.New().ToString();
		await WriteEnvelopeAsync(pipe, "client.hello", Identity.New().ToString(), correlationId, new
		{
			protocolVersion = "1.0",
			role,
			hostInstanceId = Identity.New().ToString(),
			contractVersions
		});
		var hello = await ReadEnvelopeAsync(pipe);
		if (expectHandshakeError)
			return hello;

		Assert.Equal("server.hello", hello.RootElement.GetProperty("messageType").GetString());
		hello.Dispose();
		await WriteEnvelopeAsync(pipe, messageType!, Identity.New().ToString(), correlationId, payload!);
		return await ReadEnvelopeAsync(pipe);
	}

	private static async Task WriteEnvelopeAsync(Stream stream, string messageType, string requestId, string correlationId, object payload)
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
			if (read == 0) throw new EndOfStreamException();
			offset += read;
		}
	}

	private static async Task RetryAsync(Func<Task> action, int attempts = 10, TimeSpan? delay = null)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < attempts; attempt++)
		{
			try
			{
				await action();
				return;
			}
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
			{
				last = exception;
				await Task.Delay(delay ?? TimeSpan.FromMilliseconds(50));
			}
		}
		throw new InvalidOperationException("IPC endpoint did not become available.", last);
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

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new DirectoryNotFoundException("Repository root containing rtaime.slnx was not found.");
	}
}
