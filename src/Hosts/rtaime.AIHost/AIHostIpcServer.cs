// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.AIHost;

public sealed class AIHostIpcServer : IAsyncDisposable
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly string _endpoint;
	private readonly Func<AIHostService?> _serviceAccessor;
	private readonly CancellationTokenSource _stop = new();
	private readonly string _hostInstanceId = Identity.New().ToString();
	private Task? _acceptLoop;
	private ulong _sequence;
	private ulong _stateVersion = 1;

	public AIHostIpcServer(string endpoint, Func<AIHostService?> serviceAccessor)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("AIHost IPC endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_serviceAccessor = serviceAccessor ?? throw new ArgumentNullException(nameof(serviceAccessor));
	}

	public string Endpoint => _endpoint;
	public string HostInstanceId => _hostInstanceId;
	public bool Running => _acceptLoop is { IsCompleted: false };

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_acceptLoop is not null) throw new InvalidOperationException("AIHost IPC server has already been started.");
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
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.protocol.unsupported", "AIHost supports IPC protocol 1.0 only.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (hello.Role is not "ControlHost" and not "RuntimeHost")
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.role.invalid", "AIHost accepts ControlHost or RuntimeHost roles.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!hello.ContractVersions.TryGetValue("ai", out var aiVersion) || aiVersion != AIContractVersion.Current.ToString() ||
					!hello.ContractVersions.TryGetValue("media", out var mediaVersion) || mediaVersion != MediaContractVersion.Current.ToString())
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.contract.unsupported", "AI or Media contract version is incompatible.", cancellationToken).ConfigureAwait(false);
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
							"AIHost",
							_hostInstanceId,
							new Dictionary<string, string>(StringComparer.Ordinal)
							{
								["ai"] = AIContractVersion.Current.ToString(),
								["media"] = MediaContractVersion.Current.ToString()
							})),
					cancellationToken).ConfigureAwait(false);

				while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
				{
					WireEnvelope request;
					try { request = await Wire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false); }
					catch (EndOfStreamException) { return; }
					catch (IOException) { return; }
					var response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
					await Wire.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
			catch (IOException) { }
			catch (InvalidDataException) { }
		}
	}

	private async ValueTask<WireEnvelope> DispatchAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var service = _serviceAccessor();
		if (service is null)
			return Error(request, "ai.unavailable", "AIHost service is not available.");

		try
		{
			switch (request.MessageType)
			{
				case "ai.ping":
					return Success(request, "ai.ping.response", new { status = service.Snapshot.State.ToString() });
				case "ai.capabilities.get":
					return Success(request, "ai.capabilities.response", service.Capabilities.Select(ToWire).ToArray());
				case "ai.snapshot.get":
					return Success(request, "ai.snapshot.response", ToWire(service.Snapshot));
				case "ai.inference.execute":
				{
					var execution = request.Payload.Deserialize<WireExecutionRequest>(Wire.JsonOptions)
						?? throw new InvalidDataException("AI execution request payload is required.");
					var result = await service.ExecuteAsync(FromWire(execution), cancellationToken).ConfigureAwait(false);
					_stateVersion++;
					return Success(request, "ai.inference.execute.response", ToWire(result));
				}
				default:
					return Error(request, "ipc.message.unknown", $"Unknown AIHost message type '{request.MessageType}'.");
			}
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException)
		{
			return Error(request, "ai.request.rejected", exception.Message);
		}
	}

	private WireEnvelope Success(WireEnvelope request, string messageType, object payload) =>
		Wire.Create(messageType, request.RequestId, request.CorrelationId, _hostInstanceId, _stateVersion, NextSequence(), payload);

	private WireEnvelope Error(WireEnvelope request, string code, string message) =>
		Wire.Create("error", request.RequestId, request.CorrelationId, _hostInstanceId, _stateVersion, NextSequence(), new WireFailure(code, message));

	private Task WriteErrorAsync(Stream stream, WireEnvelope request, string code, string message, CancellationToken cancellationToken) =>
		Wire.WriteAsync(stream, Error(request, code, message), cancellationToken);

	private ulong NextSequence() => _sequence == ulong.MaxValue
		? throw new InvalidOperationException("AIHost IPC sequence exhausted.")
		: ++_sequence;

	private static WireCapability ToWire(InferenceCapabilityDescriptor capability) =>
		new(capability.Version.ToString(), capability.CapabilityId.ToString(), capability.Kind, capability.AcceptsVideoFrames);

	private static WireAISnapshot ToWire(rtaime.AI.AIExecutionSnapshot snapshot) => new(
		(int)snapshot.State,
		snapshot.ActiveRequests,
		snapshot.ReservedComputeUnits,
		snapshot.ReservedVramBytes,
		snapshot.Completed,
		snapshot.Rejected,
		snapshot.TimedOut,
		snapshot.Cancelled,
		snapshot.Failed);

	private static GovernedInferenceExecutionRequest FromWire(WireExecutionRequest execution)
	{
		var frame = execution.Request.InputFrame;
		var format = new VideoFormat(
			frame.Format.Width,
			frame.Format.Height,
			FrameRate.Parse(frame.Format.FrameRate),
			(Enum.IsDefined(typeof(PixelFormat), frame.Format.PixelFormat) ? (PixelFormat)frame.Format.PixelFormat : throw new InvalidDataException("Pixel format is invalid.")),
			(Enum.IsDefined(typeof(ScanMode), frame.Format.ScanMode) ? (ScanMode)frame.Format.ScanMode : throw new InvalidDataException("Scan mode is invalid.")));
		var descriptor = new FrameDescriptor(
			MediaContractVersion.Current,
			new MediaSourceId(Identity.Parse(frame.SourceId)),
			new SurfaceDescriptor(
				new SurfaceId(Identity.Parse(frame.SurfaceId)),
				format,
				(Enum.IsDefined(typeof(SurfaceStorageDomain), frame.StorageDomain) ? (SurfaceStorageDomain)frame.StorageDomain : throw new InvalidDataException("Surface storage domain is invalid.")),
				(Enum.IsDefined(typeof(SurfaceOwnership), frame.Ownership) ? (SurfaceOwnership)frame.Ownership : throw new InvalidDataException("Surface ownership is invalid.")),
				new SurfaceLifetimeDescriptor(new Generation(frame.Generation), string.IsNullOrWhiteSpace(frame.LeaseId) ? null : Identity.Parse(frame.LeaseId)),
				string.IsNullOrWhiteSpace(frame.HandleKind) || string.IsNullOrWhiteSpace(frame.HandleValue) ? null : new OpaqueSurfaceHandle(frame.HandleKind, frame.HandleValue)),
			new FrameTiming(frame.SequenceNumber, frame.PresentationTimestamp, Timebase.Parse(frame.Timebase)));
		var request = new GovernedInferenceRequest(
			CompatibilityVersion.Parse(execution.Version),
			new InferenceRequestId(Identity.Parse(execution.Request.RequestId)),
			new InferenceCapabilityId(Identity.Parse(execution.Request.CapabilityId)),
			descriptor,
			UtcTimestamp.Parse(execution.Request.DeadlineUtc),
			execution.Request.Parameters.Select(parameter => new InferenceParameter(parameter.Name, parameter.Value)).ToArray());
		var context = new InferenceRequestContext(
			Identity.Parse(execution.Context.TimingDomainId),
			new InferenceProductionTime(execution.Context.ProductionTimestamp, Timebase.Parse(execution.Context.ProductionTimebase)),
			new InferenceResourceBudget(execution.Context.ComputeUnits, execution.Context.VramBytes, execution.Context.MaxInferenceRatePerSecond),
			new InferenceResourceHandle(execution.Context.InputResourceKind, execution.Context.InputResourceValue));
		return new GovernedInferenceExecutionRequest(CompatibilityVersion.Parse(execution.Version), request, context);
	}

	private static WireExecutionResult ToWire(GovernedInferenceExecutionResult result) => new(
		result.Version.ToString(),
		new WireAdmission(
			result.Admission.RequestId.ToString(),
			(int)result.Admission.Status,
			result.Admission.ProviderId?.ToString(),
			result.Admission.Failure is { } admissionFailure ? new WireFailure(admissionFailure.Code, admissionFailure.Message) : null),
		new WireInferenceResult(
			result.Result.RequestId.ToString(),
			(int)result.Result.Status,
			result.Result.Outputs.Select(output => new WireNameValue(output.Name, output.Value)).ToArray(),
			result.Result.Failure is { } resultFailure ? new WireFailure(resultFailure.Code, resultFailure.Message) : null),
		result.Metadata is null ? null : new WireResultMetadata(
			result.Metadata.ResultId.ToString(),
			result.Metadata.CapabilityId.ToString(),
			result.Metadata.SourceFrameId.ToString(),
			result.Metadata.ModelId.ToString(),
			result.Metadata.ModelVersion,
			result.Metadata.ProviderId.ToString(),
			result.Metadata.ObservationTime.ToString(),
			result.Metadata.ProductionTime.Timestamp,
			result.Metadata.ProductionTime.Timebase.ToString(),
			result.Metadata.Freshness.Ticks,
			result.Metadata.Confidence,
			result.Metadata.Uncertainty,
			result.Metadata.PayloadDescriptor.Kind,
			result.Metadata.PayloadDescriptor.MediaType,
			result.Metadata.ResourceHandle.Kind,
			result.Metadata.ResourceHandle.Value));

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireCapability(string Version, string CapabilityId, string Kind, bool AcceptsVideoFrames);
	private sealed record WireAISnapshot(int State, uint ActiveRequests, uint ReservedComputeUnits, ulong ReservedVramBytes, ulong Completed, ulong Rejected, ulong TimedOut, ulong Cancelled, ulong Failed);
	private sealed record WireVideoFormat(uint Width, uint Height, string FrameRate, int PixelFormat, int ScanMode);
	private sealed record WireFrame(string SourceId, string SurfaceId, WireVideoFormat Format, int StorageDomain, int Ownership, ulong Generation, string? LeaseId, string? HandleKind, string? HandleValue, ulong SequenceNumber, long PresentationTimestamp, string Timebase);
	private sealed record WireNameValue(string Name, string Value);
	private sealed record WireInferenceRequest(string RequestId, string CapabilityId, WireFrame InputFrame, string DeadlineUtc, WireNameValue[] Parameters);
	private sealed record WireContext(string TimingDomainId, long ProductionTimestamp, string ProductionTimebase, uint ComputeUnits, ulong VramBytes, uint MaxInferenceRatePerSecond, string InputResourceKind, string InputResourceValue);
	private sealed record WireExecutionRequest(string Version, WireInferenceRequest Request, WireContext Context);
	private sealed record WireAdmission(string RequestId, int Status, string? ProviderId, WireFailure? Failure);
	private sealed record WireInferenceResult(string RequestId, int Status, WireNameValue[] Outputs, WireFailure? Failure);
	private sealed record WireResultMetadata(string ResultId, string CapabilityId, string SourceFrameId, string ModelId, string ModelVersion, string ProviderId, string ObservationTimeUtc, long ProductionTimestamp, string ProductionTimebase, long FreshnessTicks, double Confidence, double Uncertainty, string PayloadKind, string PayloadMediaType, string ResourceKind, string ResourceValue);
	private sealed record WireExecutionResult(string Version, WireAdmission Admission, WireInferenceResult Result, WireResultMetadata? Metadata);

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
