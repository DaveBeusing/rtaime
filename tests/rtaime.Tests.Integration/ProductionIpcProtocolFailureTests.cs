// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Core;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ProductionIpcProtocolFailureTests
{
	private const int MaxFrameBytes = 1024 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);


	[Fact]
	public async Task Bare_connect_disconnect_stress_keeps_RuntimeHost_ready_and_accepting()
	{
		var endpoint = Endpoint();
		using var stop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = runtime.RunAsync(stop.Token);

		for (var attempt = 0; attempt < 200; attempt++)
		{
			await using var pipe = new NamedPipeClientStream(
				".",
				endpoint,
				PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await pipe.ConnectAsync(3000);
		}

		Assert.Equal(RuntimeHostProcessState.Ready, runtime.Lifecycle.State);
		Assert.Equal(RuntimeHostHealthState.Healthy, runtime.Lifecycle.Health);
		Assert.True(runtime.IpcServer?.Running);
		Assert.Equal(RuntimePipeListenerState.Running, runtime.IpcServer?.Listener.State);

		await AssertValidPingAsync(endpoint);

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task Recoverable_accept_IO_failures_recreate_listener_and_preserve_availability()
	{
		var endpoint = Endpoint();
		var attempts = 0;
		await using var server = new RuntimeHostIpcServer(
			endpoint,
			() => null,
			null,
			null,
			() =>
			{
				if (Interlocked.Increment(ref attempts) <= 2)
					throw new IOException("Synthetic recoverable accept failure.");
				return CreateServerPipe(endpoint);
			});

		await server.StartAsync();
		await using var connection = await ConnectRuntimeAsync(endpoint);

		Assert.True(attempts >= 3);
		Assert.True(server.Running);
		Assert.Equal(RuntimePipeListenerState.Running, server.Listener.State);
		Assert.Null(server.Listener.FailureDetail);
	}

	[Fact]
	public async Task Duplicate_RequestId_replays_same_response_and_conflicting_reuse_fails_closed()
	{
		var endpoint = Endpoint();
		using var stop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = runtime.RunAsync(stop.Token);

		await using var pipe = await ConnectRuntimeAsync(endpoint);
		var requestId = Identity.New().ToString();
		var correlationId = Identity.New().ToString();
		var payload = new { probe = 1 };

		await WriteEnvelopeAsync(pipe, "runtime.ping", requestId, correlationId, payload);
		using var first = await ReadEnvelopeAsync(pipe);
		await WriteEnvelopeAsync(pipe, "runtime.ping", requestId, correlationId, payload);
		using var replay = await ReadEnvelopeAsync(pipe);

		Assert.Equal("runtime.ping.response", first.RootElement.GetProperty("messageType").GetString());
		Assert.Equal(first.RootElement.GetProperty("sequence").GetUInt64(), replay.RootElement.GetProperty("sequence").GetUInt64());
		Assert.Equal(first.RootElement.GetProperty("sentAtUtc").GetDateTimeOffset(), replay.RootElement.GetProperty("sentAtUtc").GetDateTimeOffset());

		await WriteEnvelopeAsync(pipe, "runtime.snapshot.get", requestId, correlationId, new { });
		using var conflict = await ReadEnvelopeAsync(pipe);
		Assert.Equal("error", conflict.RootElement.GetProperty("messageType").GetString());
		Assert.Equal("ipc.request_id_conflict", conflict.RootElement.GetProperty("payload").GetProperty("code").GetString());

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task Oversize_frame_is_rejected_before_payload_allocation()
	{
		var endpoint = Endpoint();
		using var stop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = runtime.RunAsync(stop.Token);
		await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await pipe.ConnectAsync(3000);

		var prefix = new byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(prefix, MaxFrameBytes + 1U);
		await pipe.WriteAsync(prefix);
		await pipe.FlushAsync();

		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		var buffer = new byte[1];
		var read = await pipe.ReadAsync(buffer, deadline.Token);
		Assert.Equal(0, read);
		await AssertValidPingAsync(endpoint);

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task Unsupported_protocol_version_closes_connection_without_dispatching_request()
	{
		var endpoint = Endpoint();
		using var stop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = runtime.RunAsync(stop.Token);
		await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await pipe.ConnectAsync(3000);

		var envelope = new
		{
			protocolVersion = "2.0",
			messageType = "client.hello",
			requestId = Identity.New().ToString(),
			correlationId = Identity.New().ToString(),
			hostInstanceId = Identity.New().ToString(),
			sentAtUtc = DateTimeOffset.UtcNow,
			stateVersion = 0UL,
			sequence = 0UL,
			payload = new
			{
				protocolVersion = "2.0",
				role = "ControlHost",
				hostInstanceId = Identity.New().ToString(),
				contractVersions = new Dictionary<string, string>
				{
					["runtime"] = RuntimeContractVersion.Current.ToString(),
					["provider"] = ProviderContractVersion.Current.ToString()
				}
			}
		};
		await WriteRawAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));

		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		var buffer = new byte[1];
		var read = await pipe.ReadAsync(buffer, deadline.Token);
		Assert.Equal(0, read);
		await AssertValidPingAsync(endpoint);

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	private static async Task AssertValidPingAsync(string endpoint)
	{
		await using var pipe = await ConnectRuntimeAsync(endpoint);
		await WriteEnvelopeAsync(
			pipe,
			"runtime.ping",
			Identity.New().ToString(),
			Identity.New().ToString(),
			new { });
		using var response = await ReadEnvelopeAsync(pipe);
		Assert.Equal("runtime.ping.response", response.RootElement.GetProperty("messageType").GetString());
	}

	private static NamedPipeServerStream CreateServerPipe(string endpoint) =>
		new(
			endpoint,
			PipeDirection.InOut,
			NamedPipeServerStream.MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

	private static async Task<NamedPipeClientStream> ConnectRuntimeAsync(string endpoint)
	{
		var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await pipe.ConnectAsync(3000);
		var correlationId = Identity.New().ToString();
		await WriteEnvelopeAsync(pipe, "client.hello", Identity.New().ToString(), correlationId, new
		{
			protocolVersion = "1.0",
			role = "ControlHost",
			hostInstanceId = Identity.New().ToString(),
			contractVersions = new Dictionary<string, string>
			{
				["runtime"] = RuntimeContractVersion.Current.ToString(),
				["provider"] = ProviderContractVersion.Current.ToString()
			}
		});
		using var hello = await ReadEnvelopeAsync(pipe);
		Assert.Equal("server.hello", hello.RootElement.GetProperty("messageType").GetString());
		return pipe;
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
		await WriteRawAsync(stream, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
	}

	private static async Task WriteRawAsync(Stream stream, byte[] bytes)
	{
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

	private static string Endpoint() => $"rtaime.test.protocol.{Guid.NewGuid():N}";
}
