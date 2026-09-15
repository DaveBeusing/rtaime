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

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

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
