// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public sealed class RuntimeHostIpcServer : IAsyncDisposable
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly string _endpoint;
	private readonly Func<V1RuntimeHostService?> _runtimeAccessor;
	private readonly CancellationTokenSource _stop = new();
	private readonly BoundedRequestCache _requestCache = new(256);
	private readonly string _hostInstanceId = Identity.New().ToString();
	private Task? _acceptLoop;
	private ulong _stateVersion = 1;
	private ulong _sequence;
	private Identity? _committedAuthorityStateId;
	private Revision? _committedAuthorityRevision;

	public RuntimeHostIpcServer(string endpoint, Func<V1RuntimeHostService?> runtimeAccessor)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("RuntimeHost IPC endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_runtimeAccessor = runtimeAccessor ?? throw new ArgumentNullException(nameof(runtimeAccessor));
	}

	public string Endpoint => _endpoint;
	public string HostInstanceId => _hostInstanceId;
	public bool Running => _acceptLoop is { IsCompleted: false };

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_acceptLoop is not null) throw new InvalidOperationException("RuntimeHost IPC server has already been started.");
		var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
		_acceptLoop = AcceptLoopAsync(linked.Token);
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync()
	{
		_stop.Cancel();
		if (_acceptLoop is not null)
		{
			try { await _acceptLoop.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_stop.Dispose();
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			var pipe = new NamedPipeServerStream(
				_endpoint,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			try
			{
				await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
				_ = HandleConnectionAsync(pipe, cancellationToken);
			}
			catch
			{
				pipe.Dispose();
				if (!cancellationToken.IsCancellationRequested) throw;
			}
		}
	}

	private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
	{
		await using (pipe.ConfigureAwait(false))
		{
			try
			{
				var helloEnvelope = await Wire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
				if (!string.Equals(helloEnvelope.MessageType, "client.hello", StringComparison.Ordinal))
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.handshake.required", "ClientHello must be the first message.", cancellationToken).ConfigureAwait(false);
					return;
				}

				var hello = helloEnvelope.Payload.Deserialize<ClientHello>(Wire.JsonOptions)
					?? throw new InvalidDataException("ClientHello payload is required.");
				if (!string.Equals(hello.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.protocol.unsupported", "RuntimeHost supports IPC protocol 1.0 only.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!string.Equals(hello.Role, "ControlHost", StringComparison.Ordinal))
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.role.invalid", "RuntimeHost accepts the ControlHost role on this endpoint.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!hello.ContractVersions.TryGetValue("runtime", out var runtimeVersion) || runtimeVersion != RuntimeContractVersion.Current.ToString() ||
					!hello.ContractVersions.TryGetValue("provider", out var providerVersion) || providerVersion != ProviderContractVersion.Current.ToString())
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.contract.unsupported", "Runtime or Provider contract version is incompatible.", cancellationToken).ConfigureAwait(false);
					return;
				}

				await Wire.WriteAsync(
					pipe,
					Wire.Create(
						"server.hello",
						helloEnvelope.RequestId,
						helloEnvelope.CorrelationId,
						_hostInstanceId,
						_stateVersion,
						NextSequence(),
						new ServerHello(
							ProtocolVersion,
							"RuntimeHost",
							_hostInstanceId,
							new Dictionary<string, string>(StringComparer.Ordinal)
							{
								["runtime"] = RuntimeContractVersion.Current.ToString(),
								["provider"] = ProviderContractVersion.Current.ToString()
							})),
					cancellationToken).ConfigureAwait(false);

				while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
				{
					WireEnvelope request;
					try { request = await Wire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false); }
					catch (EndOfStreamException) { return; }
					catch (IOException) { return; }

					var canonical = request.MessageType + "\n" + request.Payload.GetRawText();
					if (_requestCache.TryGet(request.RequestId, canonical, out var cached, out var conflict))
					{
						if (conflict)
							await WriteErrorAsync(pipe, request, "ipc.request_id_conflict", "RequestId was reused with a different request payload.", cancellationToken).ConfigureAwait(false);
						else
							await Wire.WriteRawAsync(pipe, cached!, cancellationToken).ConfigureAwait(false);
						continue;
					}

					var response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
					var serialized = Wire.Serialize(response);
					_requestCache.Add(request.RequestId, canonical, serialized);
					await Wire.WriteRawAsync(pipe, serialized, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
			catch (IOException) { }
			catch (InvalidDataException) { }
		}
	}

	private ValueTask<WireEnvelope> DispatchAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var runtime = _runtimeAccessor();
		if (runtime is null)
			return ValueTask.FromResult(Error(request, "runtime.unavailable", "RuntimeHost service is not available."));

		try
		{
			return request.MessageType switch
			{
				"runtime.ping" => ValueTask.FromResult(Success(request, "runtime.ping.response", new { status = "ready" })),
				"runtime.providers.get" => ValueTask.FromResult(Success(request, "runtime.providers.response", runtime.ProviderDescriptors.Select(ToWire).ToArray())),
				"runtime.snapshot.get" => ValueTask.FromResult(Success(request, "runtime.snapshot.response", ToWire(runtime.Snapshot))),
				"runtime.execution.apply" => ValueTask.FromResult(ApplyExecution(request, runtime)),
				_ => ValueTask.FromResult(Error(request, "ipc.message.unknown", $"Unknown RuntimeHost message type '{request.MessageType}'."))
			};
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
		{
			return ValueTask.FromResult(Error(request, "runtime.request.rejected", exception.Message));
		}
	}

	private WireEnvelope ApplyExecution(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireApplyRequest>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime apply payload is required.");
		var prepared = FromWire(wire.PreparedExecution);
		var sink = new MediaSinkId(Identity.Parse(wire.ProgramSinkId));
		var transition = wire.Transition is null
			? null
			: new RuntimeProgramTransitionIntent(
				RuntimeContractVersion.Current,
				(Enum.IsDefined(typeof(RuntimeProgramTransitionKind), wire.Transition.Kind)
					? (RuntimeProgramTransitionKind)wire.Transition.Kind
					: throw new InvalidDataException("Transition kind is invalid.")),
				new MediaSourceId(Identity.Parse(wire.Transition.FromSourceId)),
				new MediaSourceId(Identity.Parse(wire.Transition.ToSourceId)),
				wire.Transition.DurationFrames);

		var result = runtime.ApplyExecution(prepared, sink, transition);
		if (result.Committed)
		{
			_committedAuthorityStateId = prepared.AuthoritySnapshot.StateId;
			_committedAuthorityRevision = prepared.AuthoritySnapshot.Revision;
			_stateVersion++;
		}
		return Success(request, "runtime.execution.apply.response", ToWire(result));
	}

	private WireEnvelope Success(WireEnvelope request, string messageType, object payload) =>
		Wire.Create(messageType, request.RequestId, request.CorrelationId, _hostInstanceId, _stateVersion, NextSequence(), payload);

	private WireEnvelope Error(WireEnvelope request, string code, string message) =>
		Wire.Create("error", request.RequestId, request.CorrelationId, _hostInstanceId, _stateVersion, NextSequence(), new WireFailure(code, message));

	private Task WriteErrorAsync(Stream stream, WireEnvelope request, string code, string message, CancellationToken cancellationToken) =>
		Wire.WriteAsync(stream, Error(request, code, message), cancellationToken);

	private ulong NextSequence() => _sequence == ulong.MaxValue
		? throw new InvalidOperationException("RuntimeHost IPC sequence exhausted.")
		: ++_sequence;

	private static WireProvider ToWire(ProviderDescriptor provider) => new(
		provider.Version.ToString(),
		provider.ProviderId.ToString(),
		provider.Name,
		(int)provider.Availability.State,
		provider.Availability.Failure is { } availabilityFailure ? new WireFailure(availabilityFailure.Code, availabilityFailure.Message) : null,
		provider.Capabilities.Select(capability => new WireCapability(
			capability.CapabilityId.ToString(),
			capability.Kind,
			capability.VideoFormats.Select(format => new WireVideoFormat(format.Width, format.Height, format.FrameRate.ToString(), (int)format.PixelFormat, (int)format.ScanMode)).ToArray())).ToArray(),
		provider.Resources.Select(resource => new WireResource(resource.ResourceId.ToString(), resource.ProviderId.ToString(), resource.Kind, resource.CapacityUnits, resource.Reservable)).ToArray());

	private WireRuntimeSnapshot ToWire(V1RuntimeHostSnapshot snapshot) => new(
		snapshot.Runtime.Version.ToString(),
		snapshot.Runtime.ActiveExecutionId?.ToString(),
		snapshot.Runtime.ExecutionRevision.Value,
		(int)snapshot.Runtime.Status,
		snapshot.Runtime.Failure is { } runtimeFailure ? new WireFailure(runtimeFailure.Code, runtimeFailure.Message) : null,
		_committedAuthorityStateId?.ToString(),
		_committedAuthorityRevision?.Value,
		snapshot.NextSequenceNumber,
		(int)snapshot.TimingHealth,
		snapshot.ActiveGpuSurfaces);

	private static WireApplyResponse ToWire(RuntimeHostApplyResult result) => new(
		new WirePrepareResult(
			result.Prepare.Version.ToString(),
			result.Prepare.PreparedExecutionId.ToString(),
			(int)result.Prepare.Status,
			result.Prepare.ReservationId?.ToString(),
			result.Prepare.Failure is { } prepareFailure ? new WireFailure(prepareFailure.Code, prepareFailure.Message) : null),
		result.Commit is null ? null : new WireCommitResult(
			result.Commit.Version.ToString(),
			(int)result.Commit.Status,
			result.Commit.ExecutionInstanceId?.ToString(),
			result.Commit.ExecutionRevision.Value,
			result.Commit.Failure is { } commitFailure ? new WireFailure(commitFailure.Code, commitFailure.Message) : null),
		result.ActivationSequence);

	private static PreparedExecutionContract FromWire(WirePreparedExecution prepared) => new(
		CompatibilityVersion.Parse(prepared.Version),
		new PreparedExecutionId(Identity.Parse(prepared.PreparedExecutionId)),
		new AuthoritySnapshotReference(Identity.Parse(prepared.AuthorityStateId), new Revision(prepared.AuthorityRevision)),
		new Generation(prepared.PlanGeneration),
		prepared.Bindings.Select(binding => new PreparedExecutionBinding(
			Identity.Parse(binding.LogicalNodeId),
			new CapabilityId(Identity.Parse(binding.CapabilityId)),
			new ProviderResourceDescriptor(
				new ProviderResourceId(Identity.Parse(binding.Resource.ResourceId)),
				new ProviderId(Identity.Parse(binding.Resource.ProviderId)),
				binding.Resource.Kind,
				binding.Resource.CapacityUnits,
				binding.Resource.Reservable),
			string.IsNullOrWhiteSpace(binding.MediaSourceId) ? null : new MediaSourceId(Identity.Parse(binding.MediaSourceId)),
			string.IsNullOrWhiteSpace(binding.MediaSinkId) ? null : new MediaSinkId(Identity.Parse(binding.MediaSinkId)))).ToArray());

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireVideoFormat(uint Width, uint Height, string FrameRate, int PixelFormat, int ScanMode);
	private sealed record WireCapability(string CapabilityId, string Kind, WireVideoFormat[] VideoFormats);
	private sealed record WireResource(string ResourceId, string ProviderId, string Kind, uint CapacityUnits, bool Reservable);
	private sealed record WireProvider(string Version, string ProviderId, string Name, int AvailabilityState, WireFailure? Failure, WireCapability[] Capabilities, WireResource[] Resources);
	private sealed record WirePreparedBinding(string LogicalNodeId, string CapabilityId, WireResource Resource, string? MediaSourceId, string? MediaSinkId);
	private sealed record WirePreparedExecution(string Version, string PreparedExecutionId, string AuthorityStateId, ulong AuthorityRevision, ulong PlanGeneration, WirePreparedBinding[] Bindings);
	private sealed record WireTransition(int Kind, string FromSourceId, string ToSourceId, uint DurationFrames);
	private sealed record WireApplyRequest(WirePreparedExecution PreparedExecution, string ProgramSinkId, WireTransition? Transition);
	private sealed record WirePrepareResult(string Version, string PreparedExecutionId, int Status, string? ReservationId, WireFailure? Failure);
	private sealed record WireCommitResult(string Version, int Status, string? ExecutionInstanceId, ulong ExecutionRevision, WireFailure? Failure);
	private sealed record WireApplyResponse(WirePrepareResult Prepare, WireCommitResult? Commit, ulong? ActivationSequence);
	private sealed record WireRuntimeSnapshot(
		string Version,
		string? ActiveExecutionId,
		ulong ExecutionRevision,
		int Status,
		WireFailure? Failure,
		string? AuthorityStateId,
		ulong? AuthorityRevision,
		ulong NextSequenceNumber,
		int TimingHealth,
		int ActiveGpuSurfaces);

	private sealed class BoundedRequestCache
	{
		private readonly int _capacity;
		private readonly object _gate = new();
		private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
		private readonly Queue<string> _order = new();

		public BoundedRequestCache(int capacity) => _capacity = capacity;

		public bool TryGet(string requestId, string canonical, out byte[]? response, out bool conflict)
		{
			lock (_gate)
			{
				if (!_entries.TryGetValue(requestId, out var entry))
				{
					response = null;
					conflict = false;
					return false;
				}
				response = entry.Response;
				conflict = !string.Equals(entry.Canonical, canonical, StringComparison.Ordinal);
				return true;
			}
		}

		public void Add(string requestId, string canonical, byte[] response)
		{
			lock (_gate)
			{
				if (_entries.ContainsKey(requestId)) return;
				_entries.Add(requestId, new Entry(canonical, response));
				_order.Enqueue(requestId);
				while (_entries.Count > _capacity)
					_entries.Remove(_order.Dequeue());
			}
		}

		private sealed record Entry(string Canonical, byte[] Response);
	}

	private static class Wire
	{
		public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

		public static WireEnvelope Create(string messageType, string requestId, string correlationId, string hostInstanceId, ulong stateVersion, ulong sequence, object payload)
		{
			var payloadElement = JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions);
			return new WireEnvelope(ProtocolVersion, messageType, requestId, correlationId, hostInstanceId, DateTimeOffset.UtcNow, stateVersion, sequence, payloadElement);
		}

		public static byte[] Serialize(WireEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

		public static Task WriteAsync(Stream stream, WireEnvelope envelope, CancellationToken cancellationToken) =>
			WriteRawAsync(stream, Serialize(envelope), cancellationToken);

		public static async Task WriteRawAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
		{
			if (payload.Length > MaxFrameBytes) throw new InvalidDataException("IPC frame exceeds the configured maximum size.");
			var prefix = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)payload.Length));
			await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
			await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
			await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}

		public static async Task<WireEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
		{
			var prefix = new byte[4];
			await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
			var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
			if (length == 0 || length > MaxFrameBytes) throw new InvalidDataException("IPC frame length is invalid or exceeds the configured maximum.");
			var payload = new byte[length];
			await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
			var envelope = JsonSerializer.Deserialize<WireEnvelope>(payload, JsonOptions)
				?? throw new InvalidDataException("IPC envelope is required.");
			if (!string.Equals(envelope.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
				throw new InvalidDataException("IPC protocol version is unsupported.");
			if (string.IsNullOrWhiteSpace(envelope.MessageType) || string.IsNullOrWhiteSpace(envelope.RequestId) || string.IsNullOrWhiteSpace(envelope.CorrelationId))
				throw new InvalidDataException("IPC envelope identifiers are required.");
			return envelope;
		}

		private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
		{
			var offset = 0;
			while (offset < buffer.Length)
			{
				var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
				if (read == 0) throw new EndOfStreamException();
				offset += read;
			}
		}
	}

	private sealed record WireEnvelope(
		string ProtocolVersion,
		string MessageType,
		string RequestId,
		string CorrelationId,
		string HostInstanceId,
		DateTimeOffset SentAtUtc,
		ulong StateVersion,
		ulong Sequence,
		JsonElement Payload);
}
