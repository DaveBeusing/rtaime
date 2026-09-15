// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Client;

public sealed class NamedPipeOperatorControlTransport : IOperatorControlTransport
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly object _gate = new();
	private readonly string _endpoint;
	private readonly TimeSpan _connectTimeout;
	private readonly TimeSpan _requestTimeout;
	private readonly string _clientInstanceId = Identity.New().ToString();
	private readonly RemoteStateSynchronizer _synchronizer = new();
	private string? _hostInstanceId;
	private string? _previousHostInstanceId;
	private bool _connected;
	private bool _requiresFullSnapshot;

	public NamedPipeOperatorControlTransport(
		string endpoint = "rtaime.v1.control.default",
		TimeSpan? connectTimeout = null,
		TimeSpan? requestTimeout = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("ControlHost endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
		_requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
		if (_connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
		if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
	}

	public bool Connected
	{
		get { lock (_gate) return _connected; }
	}

	public string? HostInstanceId
	{
		get { lock (_gate) return _hostInstanceId; }
	}

	public bool RequiresFullSnapshot
	{
		get { lock (_gate) return _requiresFullSnapshot; }
	}

	public ulong StateVersion => _synchronizer.StateVersion;

	public async ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("control.snapshot.get", new { }, cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireOperatorSnapshot>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost snapshot payload is required.");
		var snapshot = FromWire(wire);
		_synchronizer.AcceptFullSnapshot(response.HostInstanceId, wire.StateVersion);
		lock (_gate)
		{
			_requiresFullSnapshot = false;
			_previousHostInstanceId = null;
			_connected = true;
		}
		return snapshot;
	}

	public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) =>
		MutateAsync("control.preview.select", command.Metadata, command.SourceId, null, cancellationToken);

	public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) =>
		MutateAsync("control.program.cut", command.Metadata, command.SourceId, null, cancellationToken);

	public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) =>
		MutateAsync("control.program.dissolve", command.Metadata, command.SourceId, command.DurationFrames, cancellationToken);

	private async ValueTask<OperatorMutationResponse> MutateAsync(
		string messageType,
		ControlCommandMetadata metadata,
		ProductionSourceId sourceId,
		uint? durationFrames,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		var response = await ExchangeAsync(
			messageType,
			new WireControlCommand(
				metadata.Version.ToString(),
				metadata.CommandId.ToString(),
				metadata.ProductionId.ToString(),
				metadata.ExpectedRevision.Value,
				sourceId.ToString(),
				durationFrames),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireMutationResponse>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost mutation response is required.");
		_synchronizer.AcceptFullSnapshot(response.HostInstanceId, wire.StateVersion);
		return new OperatorMutationResponse(
			wire.Accepted,
			FromWire(wire.State),
			wire.Failure is null ? null : new Failure(wire.Failure.Code, wire.Failure.Message));
	}

	private async Task<WireEnvelope> ExchangeAsync(string messageType, object payload, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_requestTimeout);
		await using var pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			using var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
			connect.CancelAfter(_connectTimeout);
			await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);

			var correlationId = Identity.New().ToString();
			var helloId = Identity.New().ToString();
			await Wire.WriteAsync(
				pipe,
				Wire.Create(
					"client.hello",
					helloId,
					correlationId,
					_clientInstanceId,
					_synchronizer.StateVersion,
					0,
					new ClientHello(
						ProtocolVersion,
						"OperatorClient",
						_clientInstanceId,
						new Dictionary<string, string>(StringComparer.Ordinal)
						{
							["control"] = ControlContractVersion.Current.ToString()
						})),
				timeout.Token).ConfigureAwait(false);

			var hello = await Wire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
			EnsureNotError(hello);
			if (!string.Equals(hello.MessageType, "server.hello", StringComparison.Ordinal))
				throw new InvalidDataException("ControlHost did not complete the IPC handshake.");
			var serverHello = hello.Payload.Deserialize<ServerHello>(Wire.JsonOptions)
				?? throw new InvalidDataException("ControlHost ServerHello is required.");
			if (!string.Equals(serverHello.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) || !string.Equals(serverHello.Role, "ControlHost", StringComparison.Ordinal))
				throw new InvalidDataException("ControlHost handshake role or protocol is incompatible.");
			var previous = MarkConnected(serverHello.HostInstanceId);
			if (previous is not null)
				_synchronizer.Reset();

			if (!string.Equals(messageType, "control.snapshot.get", StringComparison.Ordinal) && RequiresFullSnapshot)
			{
				string oldHost;
				string currentHost;
				lock (_gate)
				{
					oldHost = _previousHostInstanceId ?? previous ?? "unknown";
					currentHost = _hostInstanceId ?? serverHello.HostInstanceId;
				}
				throw new RemoteHostSessionChangedException(oldHost, currentHost);
			}

			var requestId = Identity.New().ToString();
			await Wire.WriteAsync(
				pipe,
				Wire.Create(messageType, requestId, correlationId, _clientInstanceId, hello.StateVersion, 0, payload),
				timeout.Token).ConfigureAwait(false);
			var response = await Wire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
			if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) || !string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal))
				throw new InvalidDataException("ControlHost response correlation is invalid.");
			EnsureNotError(response);
			var responsePrevious = MarkConnected(response.HostInstanceId);
			if (responsePrevious is not null)
				throw new InvalidDataException("ControlHost process identity changed within one IPC request.");
			return response;
		}
		catch
		{
			MarkDisconnected();
			throw;
		}
	}

	private static OperatorStatusSnapshot FromWire(WireOperatorSnapshot wire) => new(
		FromWire(wire.Production),
		wire.Sources.Select(source => new OperatorSourceDescriptor(source.Id, source.Name)).ToArray(),
		wire.RuntimeStatus,
		wire.TimingStatus,
		wire.InputStatus,
		wire.AIStatus,
		wire.RecordingStatus,
		wire.VisualLayerEnabled,
		wire.AudioPeakLevel);

	private static AuthoritativeProductionState FromWire(WireProductionState state) => new(
		CompatibilityVersion.Parse(state.Version),
		new ProductionId(Identity.Parse(state.ProductionId)),
		new Revision(state.Revision),
		new ProductionRoutingState(
			new ProductionSourceId(Identity.Parse(state.PreviewSourceId)),
			new ProductionSourceId(Identity.Parse(state.ProgramSourceId))));

	private static void EnsureNotError(WireEnvelope envelope)
	{
		if (!string.Equals(envelope.MessageType, "error", StringComparison.Ordinal)) return;
		var failure = envelope.Payload.Deserialize<WireFailure>(Wire.JsonOptions);
		throw new InvalidOperationException(failure is null ? "Remote IPC request failed." : $"{failure.Code}: {failure.Message}");
	}

	private string? MarkConnected(string hostInstanceId)
	{
		if (string.IsNullOrWhiteSpace(hostInstanceId)) throw new InvalidDataException("ControlHost instance identity is required.");
		lock (_gate)
		{
			string? previous = null;
			if (_hostInstanceId is not null && !string.Equals(_hostInstanceId, hostInstanceId, StringComparison.Ordinal))
			{
				previous = _hostInstanceId;
				_previousHostInstanceId = previous;
				_requiresFullSnapshot = true;
			}
			_connected = true;
			_hostInstanceId = hostInstanceId;
			return previous;
		}
	}

	private void MarkDisconnected()
	{
		lock (_gate) _connected = false;
	}

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireSource(string Id, string Name);
	private sealed record WireProductionState(string Version, string ProductionId, ulong Revision, string PreviewSourceId, string ProgramSourceId);
	private sealed record WireOperatorSnapshot(WireProductionState Production, WireSource[] Sources, string RuntimeStatus, string TimingStatus, string InputStatus, string AIStatus, string RecordingStatus, bool VisualLayerEnabled, double AudioPeakLevel, ulong StateVersion);
	private sealed record WireControlCommand(string Version, string CommandId, string ProductionId, ulong ExpectedRevision, string SourceId, uint? DurationFrames);
	private sealed record WireMutationResponse(bool Accepted, WireProductionState State, WireFailure? Failure, ulong StateVersion);

	private static class Wire
	{
		public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

		public static WireEnvelope Create(string messageType, string requestId, string correlationId, string hostInstanceId, ulong stateVersion, ulong sequence, object payload) =>
			new(ProtocolVersion, messageType, requestId, correlationId, hostInstanceId, DateTimeOffset.UtcNow, stateVersion, sequence, JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions));

		public static async Task WriteAsync(Stream stream, WireEnvelope envelope, CancellationToken cancellationToken)
		{
			var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
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
