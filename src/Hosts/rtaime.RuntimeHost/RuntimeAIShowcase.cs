// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

public static class RuntimeAIShowcaseStates
{
	public const string Disabled = "DISABLED";
	public const string Armed = "ARMED";
	public const string Running = "RUNNING";
	public const string Suppressed = "SUPPRESSED";
	public const string Unavailable = "UNAVAILABLE";
	public const string Timeout = "TIMEOUT";
	public const string Failed = "FAILED";
}

public sealed record RuntimeAIShowcaseSnapshot(
	bool Enabled,
	string Feature,
	string Status,
	string Provider,
	TimeSpan InferenceTime,
	uint PersonRegionCount,
	ulong? SourceSequence,
	ulong? AppliedSequence,
	double? Confidence,
	bool EffectVisible,
	Failure? Failure,
	DateTimeOffset? UpdatedAtUtc)
{
	public static RuntimeAIShowcaseSnapshot Disabled { get; } = new(
		false,
		"Person Segmentation Highlight",
		RuntimeAIShowcaseStates.Disabled,
		"UNVERIFIED",
		TimeSpan.Zero,
		0,
		null,
		null,
		null,
		false,
		null,
		null);
}

public sealed record RuntimePersonSegmentationResult(
	bool Succeeded,
	string Status,
	string Provider,
	TimeSpan InferenceTime,
	double Left,
	double Top,
	double Right,
	double Bottom,
	uint PersonRegionCount,
	ulong SourceSequence,
	double? Confidence,
	Failure? Failure);

public interface IRuntimeAIHostTransport : IAsyncDisposable
{
	ValueTask<RuntimePersonSegmentationResult> ExecutePersonSegmentationAsync(
		FrameDescriptor frame,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Non-authoritative effect policy for the AP-55 showcase. It samples committed Program descriptors at a bounded
/// management cadence, delegates inference to AIHost, validates result/source synchronization and only then updates
/// RuntimeHost's existing dynamic visual layer. Inference never blocks the media loop.
/// </summary>
public sealed class RuntimeAIShowcaseService : IAsyncDisposable
{
	private static readonly TimeSpan MinimumInferenceInterval = TimeSpan.FromMilliseconds(200);
	private readonly object _gate = new();
	private readonly V1RuntimeHostService _runtime;
	private readonly IRuntimeAIHostTransport _transport;
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly CancellationTokenSource _stop = new();
	private RuntimeAIShowcaseSnapshot _snapshot = RuntimeAIShowcaseSnapshot.Disabled;
	private TimeSpan _lastSubmission = TimeSpan.MinValue;
	private Task? _inFlight;
	private ulong _generation;
	private V1VisualLayerMode _previousVisualMode = V1VisualLayerMode.Disabled;
	private bool _disposed;

	public RuntimeAIShowcaseService(V1RuntimeHostService runtime, IRuntimeAIHostTransport transport)
	{
		_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
	}

	public RuntimeAIShowcaseSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return _snapshot;
		}
	}

	public RuntimeAIShowcaseSnapshot SetEnabled(bool enabled)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_snapshot.Enabled == enabled)
				return _snapshot;

			_generation++;
			if (!enabled)
			{
				RestoreVisualModeUnsafe();
				_snapshot = RuntimeAIShowcaseSnapshot.Disabled;
				return _snapshot;
			}

			_previousVisualMode = _runtime.Snapshot.VisualLayerMode;
			_snapshot = RuntimeAIShowcaseSnapshot.Disabled with
			{
				Enabled = true,
				Status = RuntimeAIShowcaseStates.Armed,
				UpdatedAtUtc = DateTimeOffset.UtcNow
			};
			_lastSubmission = TimeSpan.MinValue;
			return _snapshot;
		}
	}

	public void ObserveProgramBoundary(FrameDescriptor programFrame)
	{
		ArgumentNullException.ThrowIfNull(programFrame);
		lock (_gate)
		{
			if (_disposed || !_snapshot.Enabled || _inFlight is { IsCompleted: false })
				return;

			var now = _clock.Elapsed;
			if (_lastSubmission != TimeSpan.MinValue && now - _lastSubmission < MinimumInferenceInterval)
				return;

			_lastSubmission = now;
			var generation = _generation;
			_inFlight = ExecuteAsync(programFrame, generation, _stop.Token);
		}
	}

	private async Task ExecuteAsync(FrameDescriptor frame, ulong generation, CancellationToken cancellationToken)
	{
		RuntimePersonSegmentationResult result;
		try
		{
			result = await _transport.ExecutePersonSegmentationAsync(frame, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		catch (TimeoutException exception)
		{
			result = FailureResult(RuntimeAIShowcaseStates.Timeout, frame, exception.Message);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
		{
			result = FailureResult(RuntimeAIShowcaseStates.Unavailable, frame, exception.Message);
		}
		catch (Exception exception)
		{
			result = FailureResult(RuntimeAIShowcaseStates.Failed, frame, $"{exception.GetType().Name}: {exception.Message}");
		}

		lock (_gate)
		{
			if (_disposed || !_snapshot.Enabled || generation != _generation)
				return;

			if (!result.Succeeded)
			{
				RestoreVisualModeUnsafe();
				_snapshot = new RuntimeAIShowcaseSnapshot(
					true,
					"Person Segmentation Highlight",
					result.Status,
					result.Provider,
					result.InferenceTime,
					0,
					result.SourceSequence,
					null,
					result.Confidence,
					false,
					result.Failure,
					DateTimeOffset.UtcNow);
				return;
			}

			if (result.SourceSequence != frame.Timing.SequenceNumber)
			{
				RestoreVisualModeUnsafe();
				_snapshot = new RuntimeAIShowcaseSnapshot(
					true,
					"Person Segmentation Highlight",
					RuntimeAIShowcaseStates.Failed,
					result.Provider,
					result.InferenceTime,
					0,
					result.SourceSequence,
					null,
					result.Confidence,
					false,
					new Failure("ai.showcase.source_mismatch", "AI result source sequence does not match the submitted Program frame."),
					DateTimeOffset.UtcNow);
				return;
			}

			if (result.Confidence is not { } confidence || confidence < 0.90)
			{
				RestoreVisualModeUnsafe();
				_snapshot = new RuntimeAIShowcaseSnapshot(
					true,
					"Person Segmentation Highlight",
					RuntimeAIShowcaseStates.Failed,
					result.Provider,
					result.InferenceTime,
					0,
					result.SourceSequence,
					null,
					result.Confidence,
					false,
					new Failure("ai.showcase.confidence_low", "Person segmentation confidence is below the V1 showcase threshold."),
					DateTimeOffset.UtcNow);
				return;
			}

			if (_runtime.Snapshot.GraphicsOverlay.Visible)
			{
				RestoreVisualModeUnsafe();
				_snapshot = new RuntimeAIShowcaseSnapshot(
					true,
					"Person Segmentation Highlight",
					RuntimeAIShowcaseStates.Suppressed,
					result.Provider,
					result.InferenceTime,
					result.PersonRegionCount,
					result.SourceSequence,
					null,
					result.Confidence,
					false,
					new Failure("ai.showcase.graphics_overlay_active", "Operator graphics overlay is active; hide it to make the AI highlight visible."),
					DateTimeOffset.UtcNow);
				return;
			}

			_runtime.UpdateDynamicLayerRegion(result.Left, result.Top, result.Right, result.Bottom);
			_runtime.SetVisualLayerMode(V1VisualLayerMode.Dynamic);
			var appliedSequence = _runtime.Snapshot.NextSequenceNumber;
			_snapshot = new RuntimeAIShowcaseSnapshot(
				true,
				"Person Segmentation Highlight",
				RuntimeAIShowcaseStates.Running,
				result.Provider,
				result.InferenceTime,
				result.PersonRegionCount,
				result.SourceSequence,
				appliedSequence,
				result.Confidence,
				true,
				null,
				DateTimeOffset.UtcNow);
		}
	}

	private RuntimePersonSegmentationResult FailureResult(string status, FrameDescriptor frame, string message) =>
		new(
			false,
			status,
			"UNVERIFIED",
			TimeSpan.Zero,
			0,
			0,
			0,
			0,
			0,
			frame.Timing.SequenceNumber,
			null,
			new Failure("ai.showcase.transport_failed", message));

	private void RestoreVisualModeUnsafe()
	{
		if (_runtime.Snapshot.VisualLayerMode == V1VisualLayerMode.Dynamic)
			_runtime.SetVisualLayerMode(_previousVisualMode);
	}

	public async ValueTask DisposeAsync()
	{
		Task? inFlight;
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			_generation++;
			RestoreVisualModeUnsafe();
			_stop.Cancel();
			inFlight = _inFlight;
		}

		if (inFlight is not null)
		{
			try { await inFlight.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		await _transport.DisposeAsync().ConfigureAwait(false);
		_stop.Dispose();
	}
}

/// <summary>
/// Private RuntimeHost-to-AIHost AP-55 transport. It intentionally uses the already-versioned AIHost named-pipe
/// protocol without adding a host-to-host project reference. Only frame descriptors and inference metadata cross it.
/// </summary>
public sealed class NamedPipeRuntimeAIHostTransport : IRuntimeAIHostTransport
{
	private const string ProtocolVersion = "1.0";
	private const string AIContractVersion = "1.0";
	private const string CapabilityKind = "ai.person-segmentation";
	private const int SucceededStatus = 1;
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly string _endpoint;
	private readonly TimeSpan _connectTimeout;
	private readonly TimeSpan _requestTimeout;
	private readonly string _clientInstanceId = Identity.New().ToString();

	public NamedPipeRuntimeAIHostTransport(
		string endpoint,
		TimeSpan? connectTimeout = null,
		TimeSpan? requestTimeout = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint))
			throw new ArgumentException("AIHost endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(250);
		_requestTimeout = requestTimeout ?? TimeSpan.FromMilliseconds(500);
		if (_connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
		if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
	}

	public async ValueTask<RuntimePersonSegmentationResult> ExecutePersonSegmentationAsync(
		FrameDescriptor frame,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(frame);
		var stopwatch = Stopwatch.StartNew();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_requestTimeout);
		await using var pipe = new NamedPipeClientStream(
			".",
			_endpoint,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

		try
		{
			using var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
			connect.CancelAfter(_connectTimeout);
			await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
			var correlationId = Identity.New().ToString();
			await HandshakeAsync(pipe, correlationId, timeout.Token).ConfigureAwait(false);

			var capabilityResponse = await ExchangeAsync(
				pipe,
				"ai.capabilities.get",
				new { },
				correlationId,
				timeout.Token).ConfigureAwait(false);
			var capabilities = capabilityResponse.Payload.Deserialize<WireCapability[]>(Wire.JsonOptions)
				?? throw new InvalidDataException("AIHost capability response is required.");
			var capability = capabilities.SingleOrDefault(item => string.Equals(item.Kind, CapabilityKind, StringComparison.Ordinal))
				?? throw new InvalidOperationException("AIHost does not expose the person-segmentation capability.");

			var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(100, _requestTimeout.TotalMilliseconds * 0.70));
			var execution = new WireExecutionRequest(
				AIContractVersion,
				new WireInferenceRequest(
					Identity.New().ToString(),
					capability.CapabilityId,
					ToWire(frame),
					deadline.ToString("O", CultureInfo.InvariantCulture),
					Array.Empty<WireNameValue>()),
				new WireContext(
					"75000000-0000-0000-0000-000000000001",
					frame.Timing.PresentationTimestamp,
					frame.Timing.Timebase.ToString(),
					10,
					64UL * 1024 * 1024,
					10,
					"surface.descriptor",
					frame.Surface.SurfaceId.ToString()));

			var response = await ExchangeAsync(
				pipe,
				"ai.inference.execute",
				execution,
				correlationId,
				timeout.Token).ConfigureAwait(false);
			stopwatch.Stop();
			var result = response.Payload.Deserialize<WireExecutionResult>(Wire.JsonOptions)
				?? throw new InvalidDataException("AIHost inference response is required.");

			if (result.Result.Status != SucceededStatus)
			{
				var failure = result.Result.Failure ?? result.Admission.Failure;
				return new RuntimePersonSegmentationResult(
					false,
					result.Result.Status == 3 ? RuntimeAIShowcaseStates.Timeout : RuntimeAIShowcaseStates.Unavailable,
					result.Metadata?.ProviderName ?? "UNVERIFIED",
					stopwatch.Elapsed,
					0, 0, 0, 0, 0,
					frame.Timing.SequenceNumber,
					result.Metadata?.Confidence,
					failure is null ? new Failure("ai.showcase.inference_failed", "AIHost returned a non-success inference result.") : new Failure(failure.Code, failure.Message));
			}

			var metadata = result.Metadata
				?? throw new InvalidDataException("Successful AIHost result requires metadata.");
			if (!string.Equals(metadata.SourceFrameId, frame.Surface.SurfaceId.ToString(), StringComparison.Ordinal))
				throw new InvalidDataException("AIHost result references a different source frame.");

			var semantic = result.Result.Outputs.SingleOrDefault(output => string.Equals(output.Name, "mask.semantic", StringComparison.Ordinal));
			if (semantic is null || !string.Equals(semantic.Value, "person", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("AIHost result does not contain the expected person segmentation semantic.");

			var sequenceOutput = result.Result.Outputs.SingleOrDefault(output => string.Equals(output.Name, "source.sequence", StringComparison.Ordinal));
			if (sequenceOutput is null || !ulong.TryParse(sequenceOutput.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceSequence))
				throw new InvalidDataException("AIHost result does not contain a valid source sequence.");

			var regionOutput = result.Result.Outputs.SingleOrDefault(output => string.Equals(output.Name, "mask.region.normalized", StringComparison.Ordinal))
				?? throw new InvalidDataException("AIHost result does not contain a normalized person region.");
			var region = ParseRegion(regionOutput.Value);

			return new RuntimePersonSegmentationResult(
				true,
				RuntimeAIShowcaseStates.Running,
				metadata.ProviderName,
				stopwatch.Elapsed,
				region.Left,
				region.Top,
				region.Right,
				region.Bottom,
				1,
				sourceSequence,
				metadata.Confidence,
				null);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("AIHost person-segmentation request exceeded its bounded request deadline.");
		}
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	private async Task HandshakeAsync(Stream pipe, string correlationId, CancellationToken cancellationToken)
	{
		var requestId = Identity.New().ToString();
		await Wire.WriteAsync(
			pipe,
			Wire.Create(
				"client.hello",
				requestId,
				correlationId,
				_clientInstanceId,
				new ClientHello(
					ProtocolVersion,
					"RuntimeHost",
					_clientInstanceId,
					new Dictionary<string, string>(StringComparer.Ordinal)
					{
						["ai"] = AIContractVersion,
						["media"] = MediaContractVersion.Current.ToString()
					})),
			cancellationToken).ConfigureAwait(false);
		var response = await Wire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
		EnsureNotError(response);
		if (!string.Equals(response.MessageType, "server.hello", StringComparison.Ordinal))
			throw new InvalidDataException("AIHost did not complete the IPC handshake.");
		var hello = response.Payload.Deserialize<ServerHello>(Wire.JsonOptions)
			?? throw new InvalidDataException("AIHost ServerHello is required.");
		if (!string.Equals(hello.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
			!string.Equals(hello.Role, "AIHost", StringComparison.Ordinal))
			throw new InvalidDataException("AIHost handshake role or protocol is incompatible.");
	}

	private async Task<WireEnvelope> ExchangeAsync(
		Stream pipe,
		string messageType,
		object payload,
		string correlationId,
		CancellationToken cancellationToken)
	{
		var requestId = Identity.New().ToString();
		await Wire.WriteAsync(
			pipe,
			Wire.Create(messageType, requestId, correlationId, _clientInstanceId, payload),
			cancellationToken).ConfigureAwait(false);
		var response = await Wire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
		if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
			!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal))
			throw new InvalidDataException("AIHost response correlation is invalid.");
		EnsureNotError(response);
		return response;
	}

	private static WireFrame ToWire(FrameDescriptor frame) => new(
		frame.SourceId.ToString(),
		frame.Surface.SurfaceId.ToString(),
		new WireVideoFormat(
			frame.Surface.Format.Width,
			frame.Surface.Format.Height,
			frame.Surface.Format.FrameRate.ToString(),
			(int)frame.Surface.Format.PixelFormat,
			(int)frame.Surface.Format.ScanMode),
		(int)frame.Surface.StorageDomain,
		(int)frame.Surface.Ownership,
		frame.Surface.Lifetime.Generation.Value,
		frame.Surface.Lifetime.LeaseId?.ToString(),
		frame.Surface.Handle?.Kind,
		frame.Surface.Handle?.Value,
		frame.Timing.SequenceNumber,
		frame.Timing.PresentationTimestamp,
		frame.Timing.Timebase.ToString());

	private static (double Left, double Top, double Right, double Bottom) ParseRegion(string value)
	{
		var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 4)
			throw new InvalidDataException("AIHost normalized person region requires four values.");
		var values = parts
			.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
				? parsed
				: throw new InvalidDataException("AIHost normalized person region contains an invalid number."))
			.ToArray();
		if (!(values[0] >= 0 && values[0] < values[2] && values[2] <= 1 &&
			values[1] >= 0 && values[1] < values[3] && values[3] <= 1))
			throw new InvalidDataException("AIHost normalized person region is outside the 0..1 frame bounds.");
		return (values[0], values[1], values[2], values[3]);
	}

	private static void EnsureNotError(WireEnvelope envelope)
	{
		if (!string.Equals(envelope.MessageType, "error", StringComparison.Ordinal))
			return;
		var failure = envelope.Payload.Deserialize<WireFailure>(Wire.JsonOptions);
		throw new InvalidOperationException(failure is null
			? "AIHost IPC request failed."
			: $"{failure.Code}: {failure.Message}");
	}

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireCapability(string Version, string CapabilityId, string Kind, bool AcceptsVideoFrames);
	private sealed record WireVideoFormat(uint Width, uint Height, string FrameRate, int PixelFormat, int ScanMode);
	private sealed record WireFrame(string SourceId, string SurfaceId, WireVideoFormat Format, int StorageDomain, int Ownership, ulong Generation, string? LeaseId, string? HandleKind, string? HandleValue, ulong SequenceNumber, long PresentationTimestamp, string Timebase);
	private sealed record WireNameValue(string Name, string Value);
	private sealed record WireInferenceRequest(string RequestId, string CapabilityId, WireFrame InputFrame, string DeadlineUtc, WireNameValue[] Parameters);
	private sealed record WireContext(string TimingDomainId, long ProductionTimestamp, string ProductionTimebase, uint ComputeUnits, ulong VramBytes, uint MaxInferenceRatePerSecond, string InputResourceKind, string InputResourceValue);
	private sealed record WireExecutionRequest(string Version, WireInferenceRequest Request, WireContext Context);
	private sealed record WireAdmission(string RequestId, int Status, string? ProviderId, WireFailure? Failure);
	private sealed record WireInferenceResult(string RequestId, int Status, WireNameValue[] Outputs, WireFailure? Failure);
	private sealed record WireResultMetadata(string ResultId, string CapabilityId, string SourceFrameId, string ModelId, string ModelVersion, string ProviderId, string ProviderName, string ObservationTimeUtc, long ProductionTimestamp, string ProductionTimebase, long FreshnessTicks, double Confidence, double Uncertainty, string PayloadKind, string PayloadMediaType, string ResourceKind, string ResourceValue);
	private sealed record WireExecutionResult(string Version, WireAdmission Admission, WireInferenceResult Result, WireResultMetadata? Metadata);

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

	private static class Wire
	{
		public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

		public static WireEnvelope Create(
			string messageType,
			string requestId,
			string correlationId,
			string hostInstanceId,
			object payload) =>
			new(
				ProtocolVersion,
				messageType,
				requestId,
				correlationId,
				hostInstanceId,
				DateTimeOffset.UtcNow,
				0,
				0,
				JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions));

		public static async Task WriteAsync(Stream stream, WireEnvelope envelope, CancellationToken cancellationToken)
		{
			var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
			if (payload.Length == 0 || payload.Length > MaxFrameBytes)
				throw new InvalidDataException("AIHost IPC frame length is invalid.");
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
			if (length == 0 || length > MaxFrameBytes)
				throw new InvalidDataException("AIHost IPC frame length is invalid.");
			var payload = new byte[length];
			await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
			return JsonSerializer.Deserialize<WireEnvelope>(payload, JsonOptions)
				?? throw new InvalidDataException("AIHost IPC envelope is required.");
		}

		private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
		{
			var offset = 0;
			while (offset < buffer.Length)
			{
				var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
				if (read == 0)
					throw new EndOfStreamException();
				offset += read;
			}
		}
	}
}
