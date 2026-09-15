// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public sealed record RuntimeRemoteSnapshot(
	string HostInstanceId,
	RuntimeExecutionState Runtime,
	ulong NextSequenceNumber,
	int TimingHealth,
	int ActiveGpuSurfaces,
	ulong StateVersion);

public sealed record RuntimeRemoteApplyResult(
	string HostInstanceId,
	RuntimePrepareResult Prepare,
	RuntimeCommitResult? Commit,
	ulong? ActivationSequence,
	ulong StateVersion)
{
	public bool Committed => Commit?.Status == RuntimeCommitStatus.Committed;
}

public sealed class NamedPipeRuntimeHostTransport : IControlRuntimeTransportSeam
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly object _gate = new();
	private readonly string _endpoint;
	private readonly TimeSpan _connectTimeout;
	private readonly TimeSpan _requestTimeout;
	private readonly string _clientInstanceId = Identity.New().ToString();
	private ProviderDescriptor[] _providers = Array.Empty<ProviderDescriptor>();
	private bool _connected;
	private string? _hostInstanceId;

	public NamedPipeRuntimeHostTransport(string endpoint, TimeSpan connectTimeout, TimeSpan requestTimeout)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("RuntimeHost endpoint is required.", nameof(endpoint));
		if (connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
		if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
		_endpoint = endpoint.Trim();
		_connectTimeout = connectTimeout;
		_requestTimeout = requestTimeout;
	}

	public bool IsConnected
	{
		get { lock (_gate) return _connected; }
	}

	public string? HostInstanceId
	{
		get { lock (_gate) return _hostInstanceId; }
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors
	{
		get { lock (_gate) return Array.AsReadOnly(_providers.ToArray()); }
	}

	public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
	{
		await ExchangeAsync("runtime.ping", new { }, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("runtime.providers.get", new { }, cancellationToken).ConfigureAwait(false);
		var providers = response.Payload.Deserialize<WireProvider[]>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime provider response is required.");
		var mapped = providers.Select(FromWire).ToArray();
		lock (_gate) _providers = mapped;
		return Array.AsReadOnly(mapped);
	}

	public async ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("runtime.snapshot.get", new { }, cancellationToken).ConfigureAwait(false);
		var snapshot = response.Payload.Deserialize<WireRuntimeSnapshot>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime snapshot response is required.");
		return new RuntimeRemoteSnapshot(
			response.HostInstanceId,
			FromWire(snapshot),
			snapshot.NextSequenceNumber,
			snapshot.TimingHealth,
			snapshot.ActiveGpuSurfaces,
			response.StateVersion);
	}

	public async ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(preparedExecution);
		var request = new WireApplyRequest(
			ToWire(preparedExecution),
			programSinkId.ToString(),
			transition is null ? null : new WireTransition((int)transition.Kind, transition.FromSourceId.ToString(), transition.ToSourceId.ToString(), transition.DurationFrames));
		var response = await ExchangeAsync("runtime.execution.apply", request, cancellationToken).ConfigureAwait(false);
		var apply = response.Payload.Deserialize<WireApplyResponse>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime apply response is required.");
		return new RuntimeRemoteApplyResult(
			response.HostInstanceId,
			FromWire(apply.Prepare),
			apply.Commit is null ? null : FromWire(apply.Commit),
			apply.ActivationSequence,
			response.StateVersion);
	}

	public ValueTask DisconnectAsync()
	{
		MarkDisconnected();
		return ValueTask.CompletedTask;
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
					0,
					0,
					new ClientHello(
						ProtocolVersion,
						"ControlHost",
						_clientInstanceId,
						new Dictionary<string, string>(StringComparer.Ordinal)
						{
							["runtime"] = RuntimeContractVersion.Current.ToString(),
							["provider"] = ProviderContractVersion.Current.ToString()
						})),
				timeout.Token).ConfigureAwait(false);

			var hello = await Wire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
			EnsureNotError(hello);
			if (!string.Equals(hello.MessageType, "server.hello", StringComparison.Ordinal))
				throw new InvalidDataException("RuntimeHost did not complete the IPC handshake.");
			var serverHello = hello.Payload.Deserialize<ServerHello>(Wire.JsonOptions)
				?? throw new InvalidDataException("RuntimeHost ServerHello is required.");
			if (!string.Equals(serverHello.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) || !string.Equals(serverHello.Role, "RuntimeHost", StringComparison.Ordinal))
				throw new InvalidDataException("RuntimeHost handshake role or protocol is incompatible.");
			MarkConnected(serverHello.HostInstanceId);

			var requestId = Identity.New().ToString();
			await Wire.WriteAsync(
				pipe,
				Wire.Create(messageType, requestId, correlationId, _clientInstanceId, hello.StateVersion, 0, payload),
				timeout.Token).ConfigureAwait(false);
			var response = await Wire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
			if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) || !string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal))
				throw new InvalidDataException("RuntimeHost response correlation is invalid.");
			EnsureNotError(response);
			MarkConnected(response.HostInstanceId);
			return response;
		}
		catch
		{
			MarkDisconnected();
			throw;
		}
	}

	private static void EnsureNotError(WireEnvelope envelope)
	{
		if (!string.Equals(envelope.MessageType, "error", StringComparison.Ordinal)) return;
		var failure = envelope.Payload.Deserialize<WireFailure>(Wire.JsonOptions);
		throw new InvalidOperationException(failure is null ? "Remote IPC request failed." : $"{failure.Code}: {failure.Message}");
	}

	private void MarkConnected(string hostInstanceId)
	{
		if (string.IsNullOrWhiteSpace(hostInstanceId)) throw new InvalidDataException("RuntimeHost instance identity is required.");
		lock (_gate)
		{
			_connected = true;
			_hostInstanceId = hostInstanceId;
		}
	}

	private void MarkDisconnected()
	{
		lock (_gate) _connected = false;
	}

	private static ProviderDescriptor FromWire(WireProvider provider) => new(
		CompatibilityVersion.Parse(provider.Version),
		new ProviderId(Identity.Parse(provider.ProviderId)),
		provider.Name,
		new ProviderAvailability(
			(Enum.IsDefined(typeof(ProviderAvailabilityState), provider.AvailabilityState)
				? (ProviderAvailabilityState)provider.AvailabilityState
				: throw new InvalidDataException("Provider availability state is invalid.")),
			provider.Failure is null ? null : new Failure(provider.Failure.Code, provider.Failure.Message)),
		provider.Capabilities.Select(capability => new ProviderCapabilityDescriptor(
			new CapabilityId(Identity.Parse(capability.CapabilityId)),
			capability.Kind,
			capability.VideoFormats.Select(format => new VideoFormat(
				format.Width,
				format.Height,
				FrameRate.Parse(format.FrameRate),
				(Enum.IsDefined(typeof(PixelFormat), format.PixelFormat) ? (PixelFormat)format.PixelFormat : throw new InvalidDataException("Pixel format is invalid.")),
				(Enum.IsDefined(typeof(ScanMode), format.ScanMode) ? (ScanMode)format.ScanMode : throw new InvalidDataException("Scan mode is invalid.")))).ToArray())).ToArray(),
		provider.Resources.Select(resource => new ProviderResourceDescriptor(
			new ProviderResourceId(Identity.Parse(resource.ResourceId)),
			new ProviderId(Identity.Parse(resource.ProviderId)),
			resource.Kind,
			resource.CapacityUnits,
			resource.Reservable)).ToArray());

	private static RuntimeExecutionState FromWire(WireRuntimeSnapshot snapshot) => new(
		CompatibilityVersion.Parse(snapshot.Version),
		string.IsNullOrWhiteSpace(snapshot.ActiveExecutionId) ? null : new ExecutionInstanceId(Identity.Parse(snapshot.ActiveExecutionId)),
		new Revision(snapshot.ExecutionRevision),
		(Enum.IsDefined(typeof(RuntimeExecutionStatus), snapshot.Status)
			? (RuntimeExecutionStatus)snapshot.Status
			: throw new InvalidDataException("Runtime execution status is invalid.")),
		snapshot.Failure is null ? null : new Failure(snapshot.Failure.Code, snapshot.Failure.Message));

	private static RuntimePrepareResult FromWire(WirePrepareResult result) => new(
		CompatibilityVersion.Parse(result.Version),
		new PreparedExecutionId(Identity.Parse(result.PreparedExecutionId)),
		(Enum.IsDefined(typeof(RuntimePrepareStatus), result.Status)
			? (RuntimePrepareStatus)result.Status
			: throw new InvalidDataException("Runtime prepare status is invalid.")),
		string.IsNullOrWhiteSpace(result.ReservationId) ? null : Identity.Parse(result.ReservationId),
		result.Failure is null ? null : new Failure(result.Failure.Code, result.Failure.Message));

	private static RuntimeCommitResult FromWire(WireCommitResult result) => new(
		CompatibilityVersion.Parse(result.Version),
		(Enum.IsDefined(typeof(RuntimeCommitStatus), result.Status)
			? (RuntimeCommitStatus)result.Status
			: throw new InvalidDataException("Runtime commit status is invalid.")),
		string.IsNullOrWhiteSpace(result.ExecutionInstanceId) ? null : new ExecutionInstanceId(Identity.Parse(result.ExecutionInstanceId)),
		new Revision(result.ExecutionRevision),
		result.Failure is null ? null : new Failure(result.Failure.Code, result.Failure.Message));

	private static WirePreparedExecution ToWire(PreparedExecutionContract prepared) => new(
		prepared.Version.ToString(),
		prepared.PreparedExecutionId.ToString(),
		prepared.AuthoritySnapshot.StateId.ToString(),
		prepared.AuthoritySnapshot.Revision.Value,
		prepared.PlanGeneration.Value,
		prepared.Bindings.Select(binding => new WirePreparedBinding(
			binding.LogicalNodeId.ToString(),
			binding.CapabilityId.ToString(),
			new WireResource(binding.Resource.ResourceId.ToString(), binding.Resource.ProviderId.ToString(), binding.Resource.Kind, binding.Resource.CapacityUnits, binding.Resource.Reservable),
			binding.MediaSourceId?.ToString(),
			binding.MediaSinkId?.ToString())).ToArray());

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
	private sealed record WireRuntimeSnapshot(string Version, string? ActiveExecutionId, ulong ExecutionRevision, int Status, WireFailure? Failure, ulong NextSequenceNumber, int TimingHealth, int ActiveGpuSurfaces);

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
