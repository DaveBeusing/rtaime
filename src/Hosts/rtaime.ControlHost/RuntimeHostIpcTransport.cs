// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public sealed record RuntimeGraphicsOverlaySnapshot(
	bool AssetLoaded,
	string? AssetName,
	uint AssetWidth,
	uint AssetHeight,
	bool Visible,
	double PositionX,
	double PositionY,
	double Scale);

public sealed record RuntimeCompositingLayerSnapshot(
	string LayerId,
	int Kind,
	int Order,
	bool Visible,
	byte Opacity,
	double PositionX,
	double PositionY,
	double Scale,
	string ContentIdentity);

public readonly record struct RuntimeCgColor(byte Red, byte Green, byte Blue, byte Alpha);

public sealed record RuntimeCgPanel(
	bool Enabled,
	RuntimeCgColor Color,
	float CornerRadiusPixels,
	uint PaddingPixels);

public sealed record RuntimeProductionCgTextDefinition(
	string Text,
	string Typeface,
	string? FallbackTypeface,
	float FontSizePixels,
	RuntimeCgColor Foreground,
	double PositionX,
	double PositionY,
	uint BoxWidth,
	uint BoxHeight,
	int Alignment,
	int Anchor,
	RuntimeCgPanel Panel,
	bool Visible,
	int Layer,
	int ZOrder);

public sealed record RuntimeProductionCgTextSnapshot(
	bool Active,
	string? Text,
	string? Typeface,
	string? ResolvedTypeface,
	float FontSizePixels,
	uint BoxWidth,
	uint BoxHeight,
	int Alignment,
	int Anchor,
	bool PanelEnabled,
	bool Visible,
	int Layer,
	int ZOrder,
	bool CacheHit,
	TimeSpan RenderDuration);

public sealed record RuntimeAudioInputSnapshot(
	MediaSourceId SourceId,
	AudioStreamId StreamId,
	double Gain,
	bool Muted,
	double LeftPeak,
	double RightPeak,
	double MasterPeak,
	bool Clipping,
	string Health,
	bool TestSignalEnabled = false,
	int? TestSignalMode = null,
	string? TestSignalActiveChannel = null,
	double? TestSignalFrequencyHz = null,
	double? TestSignalPeakLevel = null);

public sealed record RuntimeAudioProgramSnapshot(
	MediaSourceId ActiveVideoSourceId,
	AudioStreamId ActiveStreamId,
	double Gain,
	bool Muted,
	double LeftPeak,
	double RightPeak,
	double MasterPeak,
	bool Clipping,
	string Health);

public sealed record RuntimeRecordingSnapshot(
	string State,
	TimeSpan Elapsed,
	string? Destination,
	string? FileName,
	string? FinalPath,
	ulong Accepted,
	ulong Written,
	ulong Dropped,
	ulong Rejected,
	ulong WriterFailures,
	Failure? Failure);

public sealed record RuntimePerformanceSnapshot(
	TimeSpan Uptime,
	TimeSpan FrameBudget,
	TimeSpan LastFrameProcessingTime,
	ulong DroppedFrames,
	string GpuDeviceName,
	bool GpuHardwareAccelerated,
	double? GpuUtilizationPercent,
	ulong? GpuVramUsedBytes,
	ulong? GpuVramTotalBytes,
	string GpuTelemetryEvidence,
	string CpuDeviceName = "UNVERIFIED",
	int CpuLogicalProcessorCount = 0,
	double? CpuUtilizationPercent = null,
	ulong? SystemMemoryUsedBytes = null,
	ulong? SystemMemoryTotalBytes = null,
	string SystemTelemetryEvidence = "UNVERIFIED",
	string PhysicalGpuDeviceName = "UNVERIFIED",
	double? OutputFramesPerSecond = null);

public sealed record RuntimeAvSyncDiagnosticsSnapshot(
	bool Enabled,
	string State,
	ulong? EventId,
	string? ExpectedMediaTime,
	ulong? TargetVideoFrameSequence,
	ulong? TargetAudioSamplePosition,
	double? ScheduledVideoOffsetMilliseconds,
	double? SubmitOffsetMilliseconds,
	double? DriftFromBaselineMilliseconds,
	string Detail);

public sealed record RuntimeAIShowcaseRemoteSnapshot(
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
	DateTimeOffset? UpdatedAtUtc);

public sealed record RuntimeRecordingCommandResult(
	bool Succeeded,
	RuntimeRecordingSnapshot Snapshot,
	Failure? Failure);

public sealed record RuntimeRemoteSnapshot(
	string HostInstanceId,
	RuntimeExecutionState Runtime,
	Identity? AuthorityStateId,
	Revision? AuthorityRevision,
	ulong NextSequenceNumber,
	int TimingHealth,
	int ActiveGpuSurfaces,
	VideoFormat Format,
	IReadOnlyDictionary<MediaSourceId, string> InputSignals,
	RuntimeGraphicsOverlaySnapshot GraphicsOverlay,
	IReadOnlyDictionary<MediaSourceId, RuntimeAudioInputSnapshot> AudioInputs,
	RuntimeAudioProgramSnapshot AudioProgram,
	ulong StateVersion,
	RuntimeRecordingSnapshot? Recording = null,
	RuntimePerformanceSnapshot? Performance = null,
	RuntimeAIShowcaseRemoteSnapshot? AIShowcase = null,
	IReadOnlyCollection<MediaSourceId>? BroadcastTestPatternSources = null,
	IReadOnlyCollection<MediaSourceId>? MotionTimingTestPatternSources = null,
	RuntimeAvSyncDiagnosticsSnapshot? AvSyncDiagnostics = null,
	RuntimeProductionCgTextSnapshot? ProductionCgText = null,
	IReadOnlyList<RuntimeOutputRoleSnapshot>? OutputRoles = null,
	IReadOnlyList<RuntimeCompositingLayerSnapshot>? CompositingLayers = null);

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
	private readonly SemaphoreSlim _requestGate = new(1, 1);
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
		if ((string.IsNullOrWhiteSpace(snapshot.AuthorityStateId)) != (snapshot.AuthorityRevision is null))
			throw new InvalidDataException("Runtime authority snapshot identity and revision must either both be present or both be absent.");

		Identity? authorityStateId = string.IsNullOrWhiteSpace(snapshot.AuthorityStateId)
			? null
			: Identity.Parse(snapshot.AuthorityStateId);
		Revision? authorityRevision = snapshot.AuthorityRevision is null
			? null
			: new Revision(snapshot.AuthorityRevision.Value);

		return new RuntimeRemoteSnapshot(
			response.HostInstanceId,
			FromWire(snapshot),
			authorityStateId,
			authorityRevision,
			snapshot.NextSequenceNumber,
			snapshot.TimingHealth,
			snapshot.ActiveGpuSurfaces,
			new VideoFormat(
				snapshot.Format.Width,
				snapshot.Format.Height,
				FrameRate.Parse(snapshot.Format.FrameRate),
				Enum.IsDefined(typeof(PixelFormat), snapshot.Format.PixelFormat) ? (PixelFormat)snapshot.Format.PixelFormat : throw new InvalidDataException("Runtime pixel format is invalid."),
				Enum.IsDefined(typeof(ScanMode), snapshot.Format.ScanMode) ? (ScanMode)snapshot.Format.ScanMode : throw new InvalidDataException("Runtime scan mode is invalid.")),
			snapshot.InputSignals.ToDictionary(
				signal => new MediaSourceId(Identity.Parse(signal.SourceId)),
				signal => string.IsNullOrWhiteSpace(signal.Health) ? "UNKNOWN" : signal.Health.Trim(),
				EqualityComparer<MediaSourceId>.Default),
			FromWire(snapshot.GraphicsOverlay),
			snapshot.AudioInputs.ToDictionary(
				input => new MediaSourceId(Identity.Parse(input.SourceId)),
				FromWire,
				EqualityComparer<MediaSourceId>.Default),
			FromWire(snapshot.AudioProgram),
			response.StateVersion,
			FromWire(snapshot.Recording),
			FromWire(snapshot.Performance),
			FromWire(snapshot.AIShowcase),
			Array.AsReadOnly((snapshot.BroadcastTestPatternSourceIds ?? Array.Empty<string>())
				.Select(sourceId => new MediaSourceId(Identity.Parse(sourceId)))
				.ToArray()),
			Array.AsReadOnly((snapshot.MotionTimingTestPatternSourceIds ?? Array.Empty<string>())
				.Select(sourceId => new MediaSourceId(Identity.Parse(sourceId)))
				.ToArray()),
			snapshot.AvSyncDiagnostics is null ? null : FromWire(snapshot.AvSyncDiagnostics),
			snapshot.ProductionCgText is null ? null : FromWire(snapshot.ProductionCgText),
			Array.AsReadOnly((snapshot.OutputRoles ?? Array.Empty<WireOutputRole>()).Select(FromWire).ToArray()),
			Array.AsReadOnly((snapshot.CompositingLayers ?? Array.Empty<WireCompositingLayer>()).Select(FromWire).ToArray()));
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

	public async ValueTask<RuntimeAudioInputSnapshot> SetAudioInputStateAsync(
		MediaSourceId sourceId,
		double gain,
		bool muted,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"runtime.audio.input.set",
			new WireAudioInputState(sourceId.ToString(), gain, muted),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireAudioInput>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime audio input response is required.");
		return FromWire(wire);
	}

	public async ValueTask<RuntimeAudioInputSnapshot> SetAudioTestSignalAsync(
		MediaSourceId sourceId,
		bool enabled,
		int mode,
		double frequencyHz,
		double peakLevel,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"runtime.audio.test_signal.set",
			new WireAudioTestSignalState(sourceId.ToString(), enabled, mode, frequencyHz, peakLevel),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireAudioInput>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime generated audio test signal response is required.");
		return FromWire(wire);
	}

	public ValueTask<bool> SetBroadcastTestPatternAsync(
		MediaSourceId sourceId,
		bool enabled,
		CancellationToken cancellationToken = default) =>
		SetBroadcastTestPatternAsync(sourceId, enabled, false, cancellationToken);

	public async ValueTask<bool> SetBroadcastTestPatternAsync(
		MediaSourceId sourceId,
		bool enabled,
		bool motionTiming,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"runtime.test_pattern.set",
			new WireTestPatternState(sourceId.ToString(), enabled, motionTiming),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireTestPatternState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime broadcast test pattern response is required.");
		if (!string.Equals(wire.SourceId, sourceId.ToString(), StringComparison.Ordinal))
			throw new InvalidDataException("Runtime broadcast test pattern response source does not match the request.");
		return wire.Enabled;
	}

	public async ValueTask<RuntimeGraphicsOverlaySnapshot> LoadGraphicsOverlayAsync(
		string assetName,
		uint width,
		uint height,
		byte[] rgbaPixels,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(assetName)) throw new ArgumentException("Graphics asset name is required.", nameof(assetName));
		ArgumentNullException.ThrowIfNull(rgbaPixels);
		var response = await ExchangeAsync(
			"runtime.graphics.overlay.load",
			new WireGraphicsAsset(assetName.Trim(), width, height, rgbaPixels),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireGraphicsOverlay>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime graphics overlay response is required.");
		return FromWire(wire);
	}

	public async ValueTask<RuntimeGraphicsOverlaySnapshot> ApplyProductionCgTextAsync(
		RuntimeProductionCgTextDefinition definition,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definition);
		var response = await ExchangeAsync(
			"runtime.graphics.cg.apply",
			new WireProductionCgText(
				definition.Text,
				definition.Typeface,
				definition.FallbackTypeface,
				definition.FontSizePixels,
				new WireCgColor(definition.Foreground.Red, definition.Foreground.Green, definition.Foreground.Blue, definition.Foreground.Alpha),
				definition.PositionX,
				definition.PositionY,
				definition.BoxWidth,
				definition.BoxHeight,
				definition.Alignment,
				definition.Anchor,
				new WireCgPanel(
					definition.Panel.Enabled,
					new WireCgColor(definition.Panel.Color.Red, definition.Panel.Color.Green, definition.Panel.Color.Blue, definition.Panel.Color.Alpha),
					definition.Panel.CornerRadiusPixels,
					definition.Panel.PaddingPixels),
				definition.Visible,
				definition.Layer,
				definition.ZOrder),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireGraphicsOverlay>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime graphics overlay response is required.");
		return FromWire(wire);
	}

	public async ValueTask<RuntimeGraphicsOverlaySnapshot> SetGraphicsOverlayAsync(
		bool visible,
		double positionX,
		double positionY,
		double scale,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"runtime.graphics.overlay.set",
			new WireGraphicsOverlayState(visible, positionX, positionY, scale),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireGraphicsOverlay>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime graphics overlay response is required.");
		return FromWire(wire);
	}

	public async ValueTask<RuntimeGraphicsOverlaySnapshot> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("runtime.graphics.overlay.clear", new { }, cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireGraphicsOverlay>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime graphics overlay response is required.");
		return FromWire(wire);
	}

	public async ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerStateAsync(
		string layerId,
		bool visible,
		byte opacity,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(layerId)) throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
		var response = await ExchangeAsync(
			"runtime.compositing.layer.set",
			new WireCompositingLayerState(layerId.Trim(), visible, opacity),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireCompositingLayer[]>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime compositing layer response is required.");
		return Array.AsReadOnly(wire.Select(FromWire).ToArray());
	}

	public async ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> ReorderCompositingLayersAsync(
		IReadOnlyList<string> orderedLayerIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(orderedLayerIds);
		var response = await ExchangeAsync(
			"runtime.compositing.layers.reorder",
			new WireCompositingLayerOrder(orderedLayerIds.ToArray()),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireCompositingLayer[]>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime compositing layer response is required.");
		return Array.AsReadOnly(wire.Select(FromWire).ToArray());
	}

	public async ValueTask<RuntimeRecordingCommandResult> StartRecordingAsync(
		string destinationDirectory,
		string fileName,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ArgumentException("Recording destination is required.", nameof(destinationDirectory));
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("Recording file name is required.", nameof(fileName));

		var response = await ExchangeAsync(
			"runtime.recording.start",
			new WireRecordingStart(
				Identity.New().ToString(),
				Identity.New().ToString(),
				destinationDirectory.Trim(),
				fileName.Trim()),
			cancellationToken).ConfigureAwait(false);
		return ReadRecordingCommandResult(response);
	}

	public async ValueTask<RuntimeRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("runtime.recording.stop", new { }, cancellationToken).ConfigureAwait(false);
		return ReadRecordingCommandResult(response);
	}

	public async ValueTask<RuntimeAIShowcaseRemoteSnapshot> SetAIShowcaseEnabledAsync(
		bool enabled,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"runtime.ai_showcase.set",
			new WireAIShowcaseState(enabled),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireAIShowcase>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime AI showcase response is required.");
		return FromWire(wire);
	}

	public async ValueTask<MediaDeckRuntimeSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("runtime.media_deck.snapshot.get", new { }, cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireMediaDeckRuntimeSnapshot>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime media-deck snapshot response is required.");
		return FromWire(wire);
	}

	public async ValueTask<MediaDeckRuntimeSnapshot> OpenMediaDeckAsync(
		MediaDeckOpenRequest request,
		PreparedExecutionContract preparedExecution,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(preparedExecution);
		var payload = new WireMediaDeckOpen(
			request.Version.ToString(),
			request.SourceId.ToString(),
			request.Path,
			ToWire(preparedExecution));
		var response = await ExchangeAsync("runtime.media_deck.open", payload, cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireMediaDeckRuntimeSnapshot>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime media-deck open response is required.");
		return FromWire(wire);
	}

	public async ValueTask<MediaTransportCommandResult> ApplyMediaDeckTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		var response = await ExchangeAsync(
			"runtime.media_deck.transport",
			new WireMediaTransportCommand(
				command.Version.ToString(),
				command.AssetId.ToString(),
				(int)command.Kind,
				command.TargetFrame,
				command.AutoPlayOnProgram,
				command.EndBehavior is null ? null : (int)command.EndBehavior.Value,
				command.InPointFrame,
				command.OutPointFrame),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireMediaTransportResult>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime media-deck transport response is required.");
		return new MediaTransportCommandResult(
			wire.Succeeded,
			FromWire(wire.Snapshot),
			wire.Failure is null ? null : new Failure(wire.Failure.Code, wire.Failure.Message));
	}

	public async ValueTask<MediaDeckRuntimeSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("runtime.media_deck.close", new { }, cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireMediaDeckRuntimeSnapshot>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime media-deck close response is required.");
		return FromWire(wire);
	}

	public ValueTask DisconnectAsync()
	{
		ResetBinding();
		return ValueTask.CompletedTask;
	}

	private async Task<WireEnvelope> ExchangeAsync(string messageType, object payload, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_requestTimeout);
		await _requestGate.WaitAsync(timeout.Token).ConfigureAwait(false);
		try
		{
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
		finally
		{
			_requestGate.Release();
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
			if (_hostInstanceId is not null && !string.Equals(_hostInstanceId, hostInstanceId, StringComparison.Ordinal))
				throw new InvalidDataException($"RuntimeHost process identity changed from '{_hostInstanceId}' to '{hostInstanceId}' during an established transport binding.");
			_connected = true;
			_hostInstanceId = hostInstanceId;
		}
	}

	private void MarkDisconnected()
	{
		lock (_gate) _connected = false;
	}

	private void ResetBinding()
	{
		lock (_gate)
		{
			_connected = false;
			_hostInstanceId = null;
			_providers = Array.Empty<ProviderDescriptor>();
		}
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

	private static RuntimeAudioInputSnapshot FromWire(WireAudioInput snapshot) => new(
		new MediaSourceId(Identity.Parse(snapshot.SourceId)),
		new AudioStreamId(Identity.Parse(snapshot.StreamId)),
		snapshot.Gain,
		snapshot.Muted,
		snapshot.LeftPeak,
		snapshot.RightPeak,
		snapshot.MasterPeak,
		snapshot.Clipping,
		AudioHealth(snapshot.Health),
		snapshot.TestSignalEnabled,
		snapshot.TestSignalMode,
		snapshot.TestSignalActiveChannel,
		snapshot.TestSignalFrequencyHz,
		snapshot.TestSignalPeakLevel);

	private static RuntimeAudioProgramSnapshot FromWire(WireAudioProgram snapshot) => new(
		new MediaSourceId(Identity.Parse(snapshot.ActiveVideoSourceId)),
		new AudioStreamId(Identity.Parse(snapshot.ActiveStreamId)),
		snapshot.Gain,
		snapshot.Muted,
		snapshot.LeftPeak,
		snapshot.RightPeak,
		snapshot.MasterPeak,
		snapshot.Clipping,
		AudioHealth(snapshot.Health));

	private static RuntimeRecordingSnapshot FromWire(WireRecordingSnapshot snapshot) => new(
		string.IsNullOrWhiteSpace(snapshot.State) ? "UNKNOWN" : snapshot.State.Trim().ToUpperInvariant(),
		TimeSpan.FromTicks(Math.Max(0, snapshot.ElapsedTicks)),
		snapshot.Destination,
		snapshot.FileName,
		snapshot.FinalPath,
		snapshot.Accepted,
		snapshot.Written,
		snapshot.Dropped,
		snapshot.Rejected,
		snapshot.WriterFailures,
		snapshot.Failure is null ? null : new Failure(snapshot.Failure.Code, snapshot.Failure.Message));

	private static RuntimePerformanceSnapshot FromWire(WireRuntimePerformance snapshot) => new(
		TimeSpan.FromTicks(Math.Max(0, snapshot.UptimeTicks)),
		TimeSpan.FromTicks(Math.Max(0, snapshot.FrameBudgetTicks)),
		TimeSpan.FromTicks(Math.Max(0, snapshot.LastFrameProcessingTicks)),
		snapshot.DroppedFrames,
		string.IsNullOrWhiteSpace(snapshot.GpuDeviceName) ? "UNKNOWN" : snapshot.GpuDeviceName.Trim(),
		snapshot.GpuHardwareAccelerated,
		snapshot.GpuUtilizationPercent,
		snapshot.GpuVramUsedBytes,
		snapshot.GpuVramTotalBytes,
		string.IsNullOrWhiteSpace(snapshot.GpuTelemetryEvidence) ? "UNVERIFIED" : snapshot.GpuTelemetryEvidence.Trim(),
		string.IsNullOrWhiteSpace(snapshot.CpuDeviceName) ? "UNVERIFIED" : snapshot.CpuDeviceName.Trim(),
		Math.Max(0, snapshot.CpuLogicalProcessorCount),
		snapshot.CpuUtilizationPercent,
		snapshot.SystemMemoryUsedBytes,
		snapshot.SystemMemoryTotalBytes,
		string.IsNullOrWhiteSpace(snapshot.SystemTelemetryEvidence) ? "UNVERIFIED" : snapshot.SystemTelemetryEvidence.Trim(),
		string.IsNullOrWhiteSpace(snapshot.PhysicalGpuDeviceName) ? "UNVERIFIED" : snapshot.PhysicalGpuDeviceName.Trim(),
		snapshot.OutputFramesPerSecond is { } framesPerSecond && double.IsFinite(framesPerSecond) && framesPerSecond > 0
			? framesPerSecond
			: null);

	private static RuntimeAvSyncDiagnosticsSnapshot FromWire(WireAvSyncDiagnostics snapshot) => new(
		snapshot.Enabled,
		string.IsNullOrWhiteSpace(snapshot.State) ? "UNAVAILABLE" : snapshot.State.Trim().ToUpperInvariant(),
		snapshot.EventId,
		snapshot.ExpectedMediaTime,
		snapshot.TargetVideoFrameSequence,
		snapshot.TargetAudioSamplePosition,
		snapshot.ScheduledVideoOffsetMilliseconds,
		snapshot.SubmitOffsetMilliseconds,
		snapshot.DriftFromBaselineMilliseconds,
		string.IsNullOrWhiteSpace(snapshot.Detail) ? "A/V sync diagnostics detail is unavailable." : snapshot.Detail.Trim());

	private static RuntimeAIShowcaseRemoteSnapshot FromWire(WireAIShowcase snapshot) => new(
		snapshot.Enabled,
		snapshot.Feature,
		snapshot.Status,
		snapshot.Provider,
		TimeSpan.FromTicks(Math.Max(0, snapshot.InferenceTimeTicks)),
		snapshot.PersonRegionCount,
		snapshot.SourceSequence,
		snapshot.AppliedSequence,
		snapshot.Confidence,
		snapshot.EffectVisible,
		snapshot.Failure is null ? null : new Failure(snapshot.Failure.Code, snapshot.Failure.Message),
		snapshot.UpdatedAtUtc);

	private static RuntimeRecordingCommandResult ReadRecordingCommandResult(WireEnvelope response)
	{
		var wire = response.Payload.Deserialize<WireRecordingCommandResult>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime recording command response is required.");
		return new RuntimeRecordingCommandResult(
			wire.Succeeded,
			FromWire(wire.Snapshot),
			wire.Failure is null ? null : new Failure(wire.Failure.Code, wire.Failure.Message));
	}

	private static string AudioHealth(int health) => health switch
	{
		1 => "HEALTHY",
		2 => "MUTED",
		3 => "SILENCE",
		4 => "CLIPPING",
		5 => "UNDERRUN",
		6 => "ERROR",
		_ => throw new InvalidDataException("Runtime audio health state is invalid.")
	};

	private static RuntimeOutputRoleSnapshot FromWire(WireOutputRole snapshot) => new(
		snapshot.RoleId,
		snapshot.RoleKind,
		new MediaSourceId(Identity.Parse(snapshot.SourceId)),
		new MediaSinkId(Identity.Parse(snapshot.TargetId)),
		new VideoFormat(snapshot.Format.Width, snapshot.Format.Height, FrameRate.Parse(snapshot.Format.FrameRate), Enum.IsDefined(typeof(PixelFormat), snapshot.Format.PixelFormat) ? (PixelFormat)snapshot.Format.PixelFormat : throw new InvalidDataException("Output role pixel format is invalid."), Enum.IsDefined(typeof(ScanMode), snapshot.Format.ScanMode) ? (ScanMode)snapshot.Format.ScanMode : throw new InvalidDataException("Output role scan mode is invalid.")),
		new Timebase(snapshot.TimingNumerator, snapshot.TimingDenominator),
		new ProviderId(Identity.Parse(snapshot.ProviderId)),
		Enum.IsDefined(typeof(RuntimeOutputRoleLifecycleState), snapshot.LifecycleState) ? (RuntimeOutputRoleLifecycleState)snapshot.LifecycleState : throw new InvalidDataException("Output role lifecycle state is invalid."),
		snapshot.AuthoritativeActive,
		Enum.IsDefined(typeof(RuntimeOutputRoleHealthState), snapshot.HealthState) ? (RuntimeOutputRoleHealthState)snapshot.HealthState : throw new InvalidDataException("Output role health state is invalid."),
		snapshot.Evidence,
		snapshot.Error is null ? null : new Failure(snapshot.Error.Code, snapshot.Error.Message));

	private static RuntimeProductionCgTextSnapshot FromWire(WireProductionCgTextSnapshot snapshot) => new(
		snapshot.Active,
		snapshot.Text,
		snapshot.Typeface,
		snapshot.ResolvedTypeface,
		snapshot.FontSizePixels,
		snapshot.BoxWidth,
		snapshot.BoxHeight,
		snapshot.Alignment,
		snapshot.Anchor,
		snapshot.PanelEnabled,
		snapshot.Visible,
		snapshot.Layer,
		snapshot.ZOrder,
		snapshot.CacheHit,
		TimeSpan.FromTicks(snapshot.RenderDurationTicks));

	private static RuntimeCompositingLayerSnapshot FromWire(WireCompositingLayer snapshot)
	{
		if (string.IsNullOrWhiteSpace(snapshot.LayerId))
			throw new InvalidDataException("Runtime compositing layer identity is required.");
		if (snapshot.Kind is < 1 or > 3)
			throw new InvalidDataException("Runtime compositing layer kind is invalid.");
		if (snapshot.Order is < 0 or >= 8)
			throw new InvalidDataException("Runtime compositing layer order is outside the supported bound.");
		if (!double.IsFinite(snapshot.PositionX) || snapshot.PositionX is < 0 or > 1 ||
			!double.IsFinite(snapshot.PositionY) || snapshot.PositionY is < 0 or > 1 ||
			!double.IsFinite(snapshot.Scale) || snapshot.Scale is < 0.05 or > 4.0)
		{
			throw new InvalidDataException("Runtime compositing layer transform is invalid.");
		}
		if (string.IsNullOrWhiteSpace(snapshot.ContentIdentity))
			throw new InvalidDataException("Runtime compositing layer content identity is required.");
		return new RuntimeCompositingLayerSnapshot(
			snapshot.LayerId.Trim(),
			snapshot.Kind,
			snapshot.Order,
			snapshot.Visible,
			snapshot.Opacity,
			snapshot.PositionX,
			snapshot.PositionY,
			snapshot.Scale,
			snapshot.ContentIdentity.Trim());
	}

	private static RuntimeGraphicsOverlaySnapshot FromWire(WireGraphicsOverlay snapshot) => new(
		snapshot.AssetLoaded,
		snapshot.AssetName,
		snapshot.AssetWidth,
		snapshot.AssetHeight,
		snapshot.Visible,
		snapshot.PositionX,
		snapshot.PositionY,
		snapshot.Scale);

	private static MediaDeckRuntimeSnapshot FromWire(WireMediaDeckRuntimeSnapshot snapshot)
	{
		var state = Enum.IsDefined(typeof(MediaDeckState), snapshot.State)
			? (MediaDeckState)snapshot.State
			: throw new InvalidDataException("Media-deck state is invalid.");
		MediaSourceId? sourceId = string.IsNullOrWhiteSpace(snapshot.SourceId)
			? null
			: new MediaSourceId(Identity.Parse(snapshot.SourceId));
		return new MediaDeckRuntimeSnapshot(
			MediaContractVersion.Current,
			state,
			sourceId,
			snapshot.Probe is null ? null : FromWire(snapshot.Probe),
			snapshot.Transport is null ? null : FromWire(snapshot.Transport),
			snapshot.Failure is null ? null : new Failure(snapshot.Failure.Code, snapshot.Failure.Message));
	}

	private static LocalMediaProbe FromWire(WireLocalMediaProbe probe) => new(
		CompatibilityVersion.Parse(probe.Version),
		new MediaAssetId(Identity.Parse(probe.AssetId)),
		new MediaSourceId(Identity.Parse(probe.SourceId)),
		probe.FileName,
		Enum.IsDefined(typeof(MediaContainerFormat), probe.Container)
			? (MediaContainerFormat)probe.Container
			: throw new InvalidDataException("Media container format is invalid."),
		Enum.IsDefined(typeof(MediaVideoCodec), probe.VideoCodec)
			? (MediaVideoCodec)probe.VideoCodec
			: throw new InvalidDataException("Media video codec is invalid."),
		Enum.IsDefined(typeof(MediaAudioCodec), probe.AudioCodec)
			? (MediaAudioCodec)probe.AudioCodec
			: throw new InvalidDataException("Media audio codec is invalid."),
		new VideoFormat(
			probe.Width,
			probe.Height,
			FrameRate.Parse(probe.FrameRate),
			PixelFormat.Rgba8,
			ScanMode.Progressive),
		AudioFormat.Stereo48kFloat32,
		TimeSpan.FromTicks(probe.DurationTicks));

	private static MediaTransportSnapshot FromWire(WireMediaTransportSnapshot snapshot) => new(
		CompatibilityVersion.Parse(snapshot.Version),
		new MediaAssetId(Identity.Parse(snapshot.AssetId)),
		new MediaSourceId(Identity.Parse(snapshot.SourceId)),
		Enum.IsDefined(typeof(MediaTransportState), snapshot.State)
			? (MediaTransportState)snapshot.State
			: throw new InvalidDataException("Media transport state is invalid."),
		new MediaTransportPosition(
			snapshot.CurrentFrame,
			snapshot.TotalFrames,
			TimeSpan.FromTicks(snapshot.PositionTicks),
			TimeSpan.FromTicks(snapshot.DurationTicks),
			TimeSpan.FromTicks(snapshot.RemainingTicks),
			FrameRate.Parse(snapshot.FrameRate)),
		snapshot.Failure is null ? null : new Failure(snapshot.Failure.Code, snapshot.Failure.Message),
		snapshot.AutoPlayOnProgram,
		Enum.IsDefined(typeof(MediaDeckEndBehavior), snapshot.EndBehavior)
			? (MediaDeckEndBehavior)snapshot.EndBehavior
			: throw new InvalidDataException("Media-deck end behavior is invalid."),
		snapshot.IsOnProgram,
		snapshot.EffectiveStartFrame,
		snapshot.EffectiveEndFrame,
		snapshot.EffectiveRemainingFrames,
		TimeSpan.FromTicks(snapshot.EffectiveRemainingTicks));

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
			binding.MediaSinkId?.ToString(),
			binding.OutputRoleId)).ToArray());

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireVideoFormat(uint Width, uint Height, string FrameRate, int PixelFormat, int ScanMode);
	private sealed record WireInputSignal(string SourceId, string Health);
	private sealed record WireTestPatternState(string SourceId, bool Enabled, bool MotionTiming = false);
	private sealed record WireGraphicsAsset(string Name, uint Width, uint Height, byte[] RgbaPixels);
	private sealed record WireCgColor(byte Red, byte Green, byte Blue, byte Alpha);
	private sealed record WireCgPanel(bool Enabled, WireCgColor Color, float CornerRadiusPixels, uint PaddingPixels);
	private sealed record WireProductionCgText(string Text, string Typeface, string? FallbackTypeface, float FontSizePixels, WireCgColor Foreground, double PositionX, double PositionY, uint BoxWidth, uint BoxHeight, int Alignment, int Anchor, WireCgPanel Panel, bool Visible, int Layer, int ZOrder);
	private sealed record WireProductionCgTextSnapshot(bool Active, string? Text, string? Typeface, string? ResolvedTypeface, float FontSizePixels, uint BoxWidth, uint BoxHeight, int Alignment, int Anchor, bool PanelEnabled, bool Visible, int Layer, int ZOrder, bool CacheHit, long RenderDurationTicks);
	private sealed record WireGraphicsOverlayState(bool Visible, double PositionX, double PositionY, double Scale);
	private sealed record WireCompositingLayerState(string LayerId, bool Visible, byte Opacity);
	private sealed record WireCompositingLayerOrder(string[] LayerIds);
	private sealed record WireGraphicsOverlay(bool AssetLoaded, string? AssetName, uint AssetWidth, uint AssetHeight, bool Visible, double PositionX, double PositionY, double Scale);
	private sealed record WireCompositingLayer(string LayerId, int Kind, int Order, bool Visible, byte Opacity, double PositionX, double PositionY, double Scale, string ContentIdentity);
	private sealed record WireAudioInputState(string SourceId, double Gain, bool Muted);
	private sealed record WireAudioTestSignalState(string SourceId, bool Enabled, int Mode, double FrequencyHz, double PeakLevel);
	private sealed record WireAudioInput(
		string SourceId,
		string StreamId,
		double Gain,
		bool Muted,
		double LeftPeak,
		double RightPeak,
		double MasterPeak,
		bool Clipping,
		int Health,
		bool TestSignalEnabled = false,
		int? TestSignalMode = null,
		string? TestSignalActiveChannel = null,
		double? TestSignalFrequencyHz = null,
		double? TestSignalPeakLevel = null);
	private sealed record WireAudioProgram(string ActiveVideoSourceId, string ActiveStreamId, double Gain, bool Muted, double LeftPeak, double RightPeak, double MasterPeak, bool Clipping, int Health);
	private sealed record WireCapability(string CapabilityId, string Kind, WireVideoFormat[] VideoFormats);
	private sealed record WireResource(string ResourceId, string ProviderId, string Kind, uint CapacityUnits, bool Reservable);
	private sealed record WireProvider(string Version, string ProviderId, string Name, int AvailabilityState, WireFailure? Failure, WireCapability[] Capabilities, WireResource[] Resources);
	private sealed record WirePreparedBinding(string LogicalNodeId, string CapabilityId, WireResource Resource, string? MediaSourceId, string? MediaSinkId, string? OutputRoleId = null);
	private sealed record WirePreparedExecution(string Version, string PreparedExecutionId, string AuthorityStateId, ulong AuthorityRevision, ulong PlanGeneration, WirePreparedBinding[] Bindings);
	private sealed record WireTransition(int Kind, string FromSourceId, string ToSourceId, uint DurationFrames);
	private sealed record WireApplyRequest(WirePreparedExecution PreparedExecution, string ProgramSinkId, WireTransition? Transition);
	private sealed record WirePrepareResult(string Version, string PreparedExecutionId, int Status, string? ReservationId, WireFailure? Failure);
	private sealed record WireCommitResult(string Version, int Status, string? ExecutionInstanceId, ulong ExecutionRevision, WireFailure? Failure);
	private sealed record WireApplyResponse(WirePrepareResult Prepare, WireCommitResult? Commit, ulong? ActivationSequence);
	private sealed record WireRecordingStart(string SessionId, string OutputId, string DestinationDirectory, string FileName);
	private sealed record WireRecordingSnapshot(string State, long ElapsedTicks, string? Destination, string? FileName, string? FinalPath, ulong Accepted, ulong Written, ulong Dropped, ulong Rejected, ulong WriterFailures, WireFailure? Failure);
	private sealed record WireRuntimePerformance(long UptimeTicks, long FrameBudgetTicks, long LastFrameProcessingTicks, ulong DroppedFrames, string GpuDeviceName, bool GpuHardwareAccelerated, double? GpuUtilizationPercent, ulong? GpuVramUsedBytes, ulong? GpuVramTotalBytes, string GpuTelemetryEvidence, string CpuDeviceName, int CpuLogicalProcessorCount, double? CpuUtilizationPercent, ulong? SystemMemoryUsedBytes, ulong? SystemMemoryTotalBytes, string SystemTelemetryEvidence, string PhysicalGpuDeviceName, double? OutputFramesPerSecond);
	private sealed record WireAIShowcaseState(bool Enabled);
	private sealed record WireAIShowcase(bool Enabled, string Feature, string Status, string Provider, long InferenceTimeTicks, uint PersonRegionCount, ulong? SourceSequence, ulong? AppliedSequence, double? Confidence, bool EffectVisible, WireFailure? Failure, DateTimeOffset? UpdatedAtUtc);
	private sealed record WireAvSyncDiagnostics(bool Enabled, string State, ulong? EventId, string? ExpectedMediaTime, ulong? TargetVideoFrameSequence, ulong? TargetAudioSamplePosition, double? ScheduledVideoOffsetMilliseconds, double? SubmitOffsetMilliseconds, double? DriftFromBaselineMilliseconds, string Detail);
	private sealed record WireOutputRole(string RoleId, string RoleKind, string SourceId, string TargetId, WireVideoFormat Format, long TimingNumerator, long TimingDenominator, string ProviderId, int LifecycleState, bool AuthoritativeActive, int HealthState, string Evidence, WireFailure? Error);
	private sealed record WireRecordingCommandResult(bool Succeeded, WireRecordingSnapshot Snapshot, WireFailure? Failure);
	private sealed record WireMediaDeckOpen(string Version, string SourceId, string Path, WirePreparedExecution PreparedExecution);
	private sealed record WireMediaTransportCommand(string Version, string AssetId, int Kind, long? TargetFrame, bool? AutoPlayOnProgram, int? EndBehavior, long? InPointFrame, long? OutPointFrame);
	private sealed record WireLocalMediaProbe(string Version, string AssetId, string SourceId, string FileName, int Container, int VideoCodec, int AudioCodec, uint Width, uint Height, string FrameRate, long DurationTicks);
	private sealed record WireMediaTransportSnapshot(string Version, string AssetId, string SourceId, int State, long CurrentFrame, long TotalFrames, long PositionTicks, long DurationTicks, long RemainingTicks, string FrameRate, WireFailure? Failure, bool AutoPlayOnProgram, int EndBehavior, bool IsOnProgram, long EffectiveStartFrame, long EffectiveEndFrame, long EffectiveRemainingFrames, long EffectiveRemainingTicks);
	private sealed record WireMediaDeckRuntimeSnapshot(int State, string? SourceId, WireLocalMediaProbe? Probe, WireMediaTransportSnapshot? Transport, WireFailure? Failure);
	private sealed record WireMediaTransportResult(bool Succeeded, WireMediaTransportSnapshot Snapshot, WireFailure? Failure);

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
		int ActiveGpuSurfaces,
		WireVideoFormat Format,
		WireInputSignal[] InputSignals,
		string[]? BroadcastTestPatternSourceIds,
		string[]? MotionTimingTestPatternSourceIds,
		WireGraphicsOverlay GraphicsOverlay,
		WireAudioInput[] AudioInputs,
		WireAudioProgram AudioProgram,
		WireRecordingSnapshot Recording,
		WireRuntimePerformance Performance,
		WireAIShowcase AIShowcase,
		WireAvSyncDiagnostics? AvSyncDiagnostics = null,
		WireProductionCgTextSnapshot? ProductionCgText = null,
		WireOutputRole[]? OutputRoles = null,
		WireCompositingLayer[]? CompositingLayers = null);

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
