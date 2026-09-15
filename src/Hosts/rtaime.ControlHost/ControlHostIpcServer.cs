// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public sealed class ControlHostIpcServer : IAsyncDisposable
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly string _endpoint;
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly IControlRuntimeTransportSeam _runtimeTransport;
	private readonly CancellationTokenSource _stop = new();
	private readonly SemaphoreSlim _mutationGate = new(1, 1);
	private readonly BoundedRequestCache _requestCache = new(256);
	private readonly string _hostInstanceId = Identity.New().ToString();
	private Task? _acceptLoop;
	private long _stateVersion = 1;
	private long _sequence;

	public ControlHostIpcServer(
		string endpoint,
		Func<ControlHostService?> controlAccessor,
		IControlRuntimeTransportSeam runtimeTransport)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("ControlHost IPC endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_runtimeTransport = runtimeTransport ?? throw new ArgumentNullException(nameof(runtimeTransport));
	}

	public string Endpoint => _endpoint;
	public string HostInstanceId => _hostInstanceId;
	public ulong StateVersion => checked((ulong)Interlocked.Read(ref _stateVersion));
	public bool Running => _acceptLoop is { IsCompleted: false };

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_acceptLoop is not null) throw new InvalidOperationException("ControlHost IPC server has already been started.");
		var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
		_acceptLoop = AcceptLoopAsync(linked.Token);
		return Task.CompletedTask;
	}

	public void NotifyObservableStateChanged()
	{
		if (Interlocked.Read(ref _stateVersion) == long.MaxValue)
			throw new InvalidOperationException("ControlHost remote StateVersion is exhausted.");
		Interlocked.Increment(ref _stateVersion);
	}

	public async ValueTask DisposeAsync()
	{
		_stop.Cancel();
		if (_acceptLoop is not null)
		{
			try { await _acceptLoop.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_mutationGate.Dispose();
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
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.protocol.unsupported", "ControlHost supports IPC protocol 1.0 only.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!string.Equals(hello.Role, "OperatorClient", StringComparison.Ordinal))
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.role.invalid", "ControlHost accepts the OperatorClient role on this endpoint.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!hello.ContractVersions.TryGetValue("control", out var controlVersion) || controlVersion != ControlContractVersion.Current.ToString())
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.contract.unsupported", "Control contract version is incompatible.", cancellationToken).ConfigureAwait(false);
					return;
				}

				await Wire.WriteAsync(
					pipe,
					Wire.Create(
						"server.hello",
						helloEnvelope.RequestId,
						helloEnvelope.CorrelationId,
						_hostInstanceId,
						StateVersion,
						NextSequence(),
						new ServerHello(
							ProtocolVersion,
							"ControlHost",
							_hostInstanceId,
							new Dictionary<string, string>(StringComparer.Ordinal)
							{
								["control"] = ControlContractVersion.Current.ToString()
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

	private async ValueTask<WireEnvelope> DispatchAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		return request.MessageType switch
		{
			"control.ping" => Success(request, "control.ping.response", new { status = _runtimeTransport.IsConnected ? "ready" : "degraded" }),
			"control.snapshot.get" => await GetSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
			"control.preview.select" => await MutateAsync(request, MutationKind.SelectPreview, cancellationToken).ConfigureAwait(false),
			"control.program.cut" => await MutateAsync(request, MutationKind.Cut, cancellationToken).ConfigureAwait(false),
			"control.program.dissolve" => await MutateAsync(request, MutationKind.Dissolve, cancellationToken).ConfigureAwait(false),
			_ => Error(request, "ipc.message.unknown", $"Unknown ControlHost message type '{request.MessageType}'.")
		};
	}

	private async ValueTask<WireEnvelope> GetSnapshotAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");

		RuntimeRemoteSnapshot? runtime = null;
		try { runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false); }
		catch { runtime = null; }

		var state = control.State;
		var payload = new WireOperatorSnapshot(
			ToWire(state),
			control.Specification.Sources.Select(source => new WireSource(source.SourceId.ToString(), source.Name)).ToArray(),
			runtime is null ? "DEGRADED" : "READY",
			runtime is null ? "UNKNOWN" : runtime.TimingHealth.ToString(),
			runtime is null ? "UNKNOWN" : "VALID",
			"AVAILABLE",
			"IDLE",
			false,
			0.0,
			StateVersion);
		return Success(request, "control.snapshot.response", payload);
	}

	private async ValueTask<WireEnvelope> MutateAsync(WireEnvelope request, MutationKind kind, CancellationToken cancellationToken)
	{
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");

			try
			{
				var providers = await _runtimeTransport.GetProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
				control.RefreshProviderSnapshot(providers);
			}
			catch (Exception exception)
			{
				return MutationResponse(
					request,
					false,
					control.State,
					new Failure("runtime.transport.failed", $"Runtime provider refresh failed before command staging: {exception.GetType().Name}."));
			}

			var command = request.Payload.Deserialize<WireControlCommand>(Wire.JsonOptions)
				?? throw new InvalidDataException("Control command payload is required.");
			var metadata = new ControlCommandMetadata(
				CompatibilityVersion.Parse(command.Version),
				new CommandId(Identity.Parse(command.CommandId)),
				new ProductionId(Identity.Parse(command.ProductionId)),
				new Revision(command.ExpectedRevision));
			var sourceId = new ProductionSourceId(Identity.Parse(command.SourceId));

			ControlHostOperationResult staged = kind switch
			{
				MutationKind.SelectPreview => control.SelectPreview(new SelectPreviewCommand(metadata, sourceId)),
				MutationKind.Cut => control.CutProgram(new CutProgramCommand(metadata, sourceId)),
				MutationKind.Dissolve => control.DissolveProgram(new DissolveProgramCommand(metadata, sourceId, command.DurationFrames ?? throw new InvalidDataException("DISSOLVE requires durationFrames."))),
				_ => throw new InvalidOperationException("Unknown mutation kind.")
			};

			if (!staged.Accepted || staged.Execution is null)
				return MutationResponse(request, false, staged.State, staged.Failure ?? new Failure("control.command.rejected", "Control command was rejected."));

			RuntimeRemoteApplyResult remote;
			try
			{
				remote = await _runtimeTransport.ApplyExecutionAsync(
					staged.Execution.PreparedExecution,
					staged.Execution.ProgramSinkId,
					staged.Execution.ProgramTransition,
					cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				var failure = new Failure("runtime.transport.failed", $"Runtime transport failed before commit confirmation: {exception.GetType().Name}.");
				var rejection = control.RejectRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId, failure);
				return MutationResponse(request, false, rejection.State ?? staged.State, failure);
			}

			if (remote.Commit is null)
			{
				var failure = remote.Prepare.Failure ?? new Failure("runtime.prepare.rejected", "Runtime did not produce a commit result.");
				var rejection = control.RejectRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId, failure);
				return MutationResponse(request, false, rejection.State ?? staged.State, failure);
			}

			var confirmation = control.ConfirmRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId, remote.Commit);
			if (!confirmation.Committed || confirmation.State is null)
				return MutationResponse(request, false, confirmation.State ?? staged.State, confirmation.Failure ?? new Failure("control.commit.rejected", "ControlHost did not confirm the Runtime commit."));

			NotifyObservableStateChanged();
			return MutationResponse(request, true, confirmation.State, null);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException)
		{
			var control = _controlAccessor();
			if (control is not null && control.HasAuthoritativeState)
				return MutationResponse(request, false, control.State, new Failure("control.request.rejected", exception.Message));
			return Error(request, "control.request.rejected", exception.Message);
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private WireEnvelope MutationResponse(WireEnvelope request, bool accepted, AuthoritativeProductionState state, Failure? failure) =>
		Success(
			request,
			"control.mutation.response",
			new WireMutationResponse(accepted, ToWire(state), failure is { } value ? new WireFailure(value.Code, value.Message) : null, StateVersion));

	private WireEnvelope Success(WireEnvelope request, string messageType, object payload) =>
		Wire.Create(messageType, request.RequestId, request.CorrelationId, _hostInstanceId, StateVersion, NextSequence(), payload);

	private WireEnvelope Error(WireEnvelope request, string code, string message) =>
		Wire.Create("error", request.RequestId, request.CorrelationId, _hostInstanceId, StateVersion, NextSequence(), new WireFailure(code, message));

	private Task WriteErrorAsync(Stream stream, WireEnvelope request, string code, string message, CancellationToken cancellationToken) =>
		Wire.WriteAsync(stream, Error(request, code, message), cancellationToken);

	private ulong NextSequence()
	{
		if (Interlocked.Read(ref _sequence) == long.MaxValue) throw new InvalidOperationException("ControlHost IPC sequence exhausted.");
		return checked((ulong)Interlocked.Increment(ref _sequence));
	}

	private static WireProductionState ToWire(AuthoritativeProductionState state) => new(
		state.Version.ToString(),
		state.ProductionId.ToString(),
		state.Revision.Value,
		state.Routing.PreviewSourceId.ToString(),
		state.Routing.ProgramSourceId.ToString());

	private enum MutationKind
	{
		SelectPreview = 1,
		Cut = 2,
		Dissolve = 3
	}

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireSource(string Id, string Name);
	private sealed record WireProductionState(string Version, string ProductionId, ulong Revision, string PreviewSourceId, string ProgramSourceId);
	private sealed record WireOperatorSnapshot(WireProductionState Production, WireSource[] Sources, string RuntimeStatus, string TimingStatus, string InputStatus, string AIStatus, string RecordingStatus, bool VisualLayerEnabled, double AudioPeakLevel, ulong StateVersion);
	private sealed record WireControlCommand(string Version, string CommandId, string ProductionId, ulong ExpectedRevision, string SourceId, uint? DurationFrames);
	private sealed record WireMutationResponse(bool Accepted, WireProductionState State, WireFailure? Failure, ulong StateVersion);

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

		public static WireEnvelope Create(string messageType, string requestId, string correlationId, string hostInstanceId, ulong stateVersion, ulong sequence, object payload) =>
			new(ProtocolVersion, messageType, requestId, correlationId, hostInstanceId, DateTimeOffset.UtcNow, stateVersion, sequence, JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions));

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
