// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

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

	public ValueTask<OperatorMutationResponse> ActivateSceneAsync(ActivateSceneCommand command, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		return MutateSceneAsync(command.Metadata, command.SceneId, cancellationToken);
	}

	public ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(RouteOutputRoleCommand command, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		return MutateOutputRoleAsync(command.Metadata, command.RoleId, command.SourceId, cancellationToken);
	}

	public async ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(
		string sourceId,
		double gain,
		bool muted,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourceId))
			throw new ArgumentException("Audio source id is required.", nameof(sourceId));
		var response = await ExchangeAsync(
			"control.audio.input.set",
			new WireAudioInputState(sourceId.Trim(), gain, muted),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireAudioInput>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost audio input payload is required.");
		return FromWire(wire);
	}

	public async ValueTask<OperatorAudioInputDescriptor> SetAudioTestSignalAsync(
		string sourceId,
		bool enabled,
		int mode,
		double frequencyHz,
		double peakLevel,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourceId))
			throw new ArgumentException("Audio source id is required.", nameof(sourceId));
		var response = await ExchangeAsync(
			"control.audio.test_signal.set",
			new WireAudioTestSignalState(sourceId.Trim(), enabled, mode, frequencyHz, peakLevel),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireAudioInput>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost generated audio test signal payload is required.");
		return FromWire(wire);
	}

	public ValueTask<bool> SetBroadcastTestPatternAsync(
		string sourceId,
		bool enabled,
		CancellationToken cancellationToken = default) =>
		SetBroadcastTestPatternAsync(sourceId, enabled, false, cancellationToken);

	public async ValueTask<bool> SetBroadcastTestPatternAsync(
		string sourceId,
		bool enabled,
		bool motionTiming,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourceId))
			throw new ArgumentException("Broadcast test pattern source id is required.", nameof(sourceId));

		var response = await ExchangeAsync(
			"control.test_pattern.set",
			new WireTestPatternState(sourceId.Trim(), enabled, motionTiming),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireTestPatternState>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost broadcast test pattern payload is required.");
		if (!string.Equals(wire.SourceId, sourceId.Trim(), StringComparison.Ordinal))
			throw new InvalidDataException("ControlHost broadcast test pattern response source does not match the request.");
		return wire.Enabled;
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> LoadGraphicsOverlayAsync(
		OperatorGraphicsAsset asset,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(asset);
		var response = await ExchangeAsync(
			"control.graphics.overlay.load",
			new WireGraphicsAsset(asset.Name, asset.Width, asset.Height, asset.RgbaPixels),
			cancellationToken).ConfigureAwait(false);
		return ReadGraphicsOverlay(response);
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> ApplyProductionCgTextAsync(
		OperatorProductionCgText definition,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definition);
		var response = await ExchangeAsync(
			"control.graphics.cg.apply",
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
				(int)definition.Alignment,
				(int)definition.Anchor,
				new WireCgPanel(
					definition.Panel.Enabled,
					new WireCgColor(definition.Panel.Color.Red, definition.Panel.Color.Green, definition.Panel.Color.Blue, definition.Panel.Color.Alpha),
					definition.Panel.CornerRadiusPixels,
					definition.Panel.PaddingPixels),
				definition.Visible,
				(int)definition.Layer,
				definition.ZOrder),
			cancellationToken).ConfigureAwait(false);
		return ReadGraphicsOverlay(response);
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> SetGraphicsOverlayAsync(
		bool visible,
		double positionX,
		double positionY,
		double scale,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"control.graphics.overlay.set",
			new WireGraphicsOverlayState(visible, positionX, positionY, scale),
			cancellationToken).ConfigureAwait(false);
		return ReadGraphicsOverlay(response);
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("control.graphics.overlay.clear", new { }, cancellationToken).ConfigureAwait(false);
		return ReadGraphicsOverlay(response);
	}

	public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerStateAsync(
		string layerId,
		bool visible,
		byte opacity,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(layerId)) throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
		var response = await ExchangeAsync(
			"control.compositing.layer.set",
			new WireCompositingLayerState(layerId.Trim(), visible, opacity),
			cancellationToken).ConfigureAwait(false);
		return ReadCompositingLayers(response);
	}

	public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> ReorderCompositingLayersAsync(
		IReadOnlyList<string> orderedLayerIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(orderedLayerIds);
		var response = await ExchangeAsync(
			"control.compositing.layers.reorder",
			new WireCompositingLayerOrder(orderedLayerIds.ToArray()),
			cancellationToken).ConfigureAwait(false);
		return ReadCompositingLayers(response);
	}

	public async ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
		string destinationDirectory,
		string fileName,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ArgumentException("Recording destination directory is required.", nameof(destinationDirectory));
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("Recording file name is required.", nameof(fileName));

		var response = await ExchangeAsync(
			"control.recording.start",
			new WireRecordingStart(destinationDirectory.Trim(), fileName.Trim()),
			cancellationToken).ConfigureAwait(false);
		return ReadRecordingCommandResult(response);
	}

	public async ValueTask<OperatorRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("control.recording.stop", new { }, cancellationToken).ConfigureAwait(false);
		return ReadRecordingCommandResult(response);
	}

	public async ValueTask<OperatorAIShowcaseDescriptor> SetAIShowcaseEnabledAsync(
		bool enabled,
		CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync(
			"control.ai_showcase.set",
			new WireAIShowcaseState(enabled),
			cancellationToken).ConfigureAwait(false);
		var wire = response.Payload.Deserialize<WireAIShowcase>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost AI showcase payload is required.");
		return FromWire(wire);
	}

	public async ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("control.media_deck.snapshot.get", new { }, cancellationToken).ConfigureAwait(false);
		return ReadMediaDeckSnapshot(response);
	}

	public async ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(
		MediaDeckOpenRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var response = await ExchangeAsync(
			"control.media_deck.open",
			new WireMediaDeckOpen(request.Version.ToString(), request.SourceId.ToString(), request.Path),
			cancellationToken).ConfigureAwait(false);
		return ReadMediaDeckSnapshot(response);
	}

	public async ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		var response = await ExchangeAsync(
			"control.media_deck.transport",
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
		return ReadMediaDeckSnapshot(response);
	}

	public async ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(
		MediaMarkerCommand command,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		var response = await ExchangeAsync(
			"control.media_deck.marker",
			new WireMediaMarkerCommand(
				command.Version.ToString(),
				command.AssetId.ToString(),
				(int)command.Kind,
				command.PositionFrame,
				command.CuePointId?.ToString(),
				command.Name),
			cancellationToken).ConfigureAwait(false);
		return ReadMediaDeckSnapshot(response);
	}

	public async ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default)
	{
		var response = await ExchangeAsync("control.media_deck.close", new { }, cancellationToken).ConfigureAwait(false);
		return ReadMediaDeckSnapshot(response);
	}

	private async ValueTask<OperatorMutationResponse> MutateAsync(
		string messageType,
		ControlCommandMetadata metadata,
		ProductionSourceId sourceId,
		uint? durationFrames,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		return await MutateCoreAsync(
			messageType,
			new WireControlCommand(
				metadata.Version.ToString(),
				metadata.CommandId.ToString(),
				metadata.ProductionId.ToString(),
				metadata.ExpectedRevision.Value,
				sourceId.ToString(),
				durationFrames),
			cancellationToken).ConfigureAwait(false);
	}

	private ValueTask<OperatorMutationResponse> MutateSceneAsync(
		ControlCommandMetadata metadata,
		SceneId sceneId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		return MutateCoreAsync(
			"control.scene.activate",
			new WireControlCommand(
				metadata.Version.ToString(),
				metadata.CommandId.ToString(),
				metadata.ProductionId.ToString(),
				metadata.ExpectedRevision.Value,
				null,
				null,
				sceneId.ToString()),
			cancellationToken);
	}

	private ValueTask<OperatorMutationResponse> MutateOutputRoleAsync(
		ControlCommandMetadata metadata,
		OutputRoleId roleId,
		ProductionSourceId sourceId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		return MutateCoreAsync(
			"control.output.route",
			new WireControlCommand(
				metadata.Version.ToString(),
				metadata.CommandId.ToString(),
				metadata.ProductionId.ToString(),
				metadata.ExpectedRevision.Value,
				sourceId.ToString(),
				null,
				null,
				roleId.ToString()),
			cancellationToken);
	}

	private async ValueTask<OperatorMutationResponse> MutateCoreAsync(
		string messageType,
		WireControlCommand command,
		CancellationToken cancellationToken)
	{
		var response = await ExchangeAsync(messageType, command, cancellationToken).ConfigureAwait(false);
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
		await using var pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
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
							["control"] = ControlContractVersion.Current.ToString(),
							["media"] = MediaContractVersion.Current.ToString()
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

	private static OperatorRecordingCommandResult ReadRecordingCommandResult(WireEnvelope response)
	{
		var wire = response.Payload.Deserialize<WireRecordingCommandResult>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost recording command response is required.");
		return new OperatorRecordingCommandResult(
			wire.Succeeded,
			FromWire(wire.Snapshot),
			wire.Failure is null ? null : new Failure(wire.Failure.Code, wire.Failure.Message));
	}

	private static IReadOnlyList<OperatorCompositingLayerDescriptor> ReadCompositingLayers(WireEnvelope response)
	{
		var wire = response.Payload.Deserialize<WireCompositingLayer[]>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost compositing layer payload is required.");
		return Array.AsReadOnly(wire.Select(layer => new OperatorCompositingLayerDescriptor(
			layer.LayerId,
			layer.Kind,
			layer.Order,
			layer.Visible,
			layer.Opacity,
			layer.PositionX,
			layer.PositionY,
			layer.Scale,
			layer.ContentIdentity)).ToArray());
	}

	private static OperatorGraphicsOverlayDescriptor ReadGraphicsOverlay(WireEnvelope response)
	{
		var wire = response.Payload.Deserialize<WireGraphicsOverlay>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost graphics overlay payload is required.");
		return FromWire(wire);
	}

	private static MediaDeckSnapshot ReadMediaDeckSnapshot(WireEnvelope response)
	{
		var wire = response.Payload.Deserialize<WireMediaDeckSnapshot>(Wire.JsonOptions)
			?? throw new InvalidDataException("ControlHost media-deck snapshot payload is required.");
		return FromWire(wire);
	}

	private static MediaDeckSnapshot FromWire(WireMediaDeckSnapshot wire)
	{
		var state = Enum.IsDefined(typeof(MediaDeckState), wire.State)
			? (MediaDeckState)wire.State
			: throw new InvalidDataException("Media-deck state is invalid.");

		MediaSourceId? sourceId = string.IsNullOrWhiteSpace(wire.SourceId)
			? null
			: new MediaSourceId(Identity.Parse(wire.SourceId));
		var probe = wire.Probe is null ? null : new LocalMediaProbe(
			CompatibilityVersion.Parse(wire.Probe.Version),
			new MediaAssetId(Identity.Parse(wire.Probe.AssetId)),
			new MediaSourceId(Identity.Parse(wire.Probe.SourceId)),
			wire.Probe.FileName,
			Enum.IsDefined(typeof(MediaContainerFormat), wire.Probe.Container)
				? (MediaContainerFormat)wire.Probe.Container
				: throw new InvalidDataException("Media container format is invalid."),
			Enum.IsDefined(typeof(MediaVideoCodec), wire.Probe.VideoCodec)
				? (MediaVideoCodec)wire.Probe.VideoCodec
				: throw new InvalidDataException("Media video codec is invalid."),
			Enum.IsDefined(typeof(MediaAudioCodec), wire.Probe.AudioCodec)
				? (MediaAudioCodec)wire.Probe.AudioCodec
				: throw new InvalidDataException("Media audio codec is invalid."),
			new VideoFormat(
				wire.Probe.Width,
				wire.Probe.Height,
				FrameRate.Parse(wire.Probe.FrameRate),
				PixelFormat.Rgba8,
				ScanMode.Progressive),
			AudioFormat.Stereo48kFloat32,
			TimeSpan.FromTicks(wire.Probe.DurationTicks));

		var transport = wire.Transport is null ? null : new MediaTransportSnapshot(
			CompatibilityVersion.Parse(wire.Transport.Version),
			new MediaAssetId(Identity.Parse(wire.Transport.AssetId)),
			new MediaSourceId(Identity.Parse(wire.Transport.SourceId)),
			Enum.IsDefined(typeof(MediaTransportState), wire.Transport.State)
				? (MediaTransportState)wire.Transport.State
				: throw new InvalidDataException("Media transport state is invalid."),
			new MediaTransportPosition(
				wire.Transport.CurrentFrame,
				wire.Transport.TotalFrames,
				TimeSpan.FromTicks(wire.Transport.PositionTicks),
				TimeSpan.FromTicks(wire.Transport.DurationTicks),
				TimeSpan.FromTicks(wire.Transport.RemainingTicks),
				FrameRate.Parse(wire.Transport.FrameRate)),
			wire.Transport.Failure is null ? null : new Failure(wire.Transport.Failure.Code, wire.Transport.Failure.Message),
			wire.Transport.AutoPlayOnProgram,
			Enum.IsDefined(typeof(MediaDeckEndBehavior), wire.Transport.EndBehavior)
				? (MediaDeckEndBehavior)wire.Transport.EndBehavior
				: throw new InvalidDataException("Media-deck end behavior is invalid."),
			wire.Transport.IsOnProgram,
			wire.Transport.EffectiveStartFrame,
			wire.Transport.EffectiveEndFrame,
			wire.Transport.EffectiveRemainingFrames,
			TimeSpan.FromTicks(wire.Transport.EffectiveRemainingTicks));

		var markers = wire.Markers is null ? null : new MediaMarkerSnapshot(
			CompatibilityVersion.Parse(wire.Markers.Version),
			new MediaAssetId(Identity.Parse(wire.Markers.AssetId)),
			wire.Markers.TotalFrames,
			wire.Markers.InPointFrame,
			wire.Markers.OutPointFrame,
			wire.Markers.CuePoints.Select(cue => new MediaCuePoint(
				new MediaCuePointId(Identity.Parse(cue.Id)),
				cue.Name,
				cue.PositionFrame)).ToArray());

		return new MediaDeckSnapshot(
			MediaContractVersion.Current,
			state,
			sourceId,
			probe,
			transport,
			markers,
			wire.Failure is null ? null : new Failure(wire.Failure.Code, wire.Failure.Message));
	}

	private static OperatorStatusSnapshot FromWire(WireOperatorSnapshot wire) => new(
		FromWire(wire.Production),
		wire.Sources.Select(source => new OperatorSourceDescriptor(
			source.Id,
			source.Name,
			source.Type,
			source.Format,
			source.Health,
			source.MediaState,
			source.RemainingTicks is null ? null : TimeSpan.FromTicks(source.RemainingTicks.Value),
			source.MediaFileName)).ToArray(),
		wire.RuntimeStatus,
		wire.TimingStatus,
		wire.InputStatus,
		wire.AIStatus,
		wire.RecordingStatus,
		wire.VisualLayerEnabled,
		wire.AudioPeakLevel,
		FromWire(wire.GraphicsOverlay),
		wire.AudioInputs.Select(FromWire).ToArray(),
		FromWire(wire.AudioProgram),
		FromWire(wire.Recording),
		FromWire(wire.Health),
		FromWire(wire.AIShowcase),
		wire.MediaDeck is null ? MediaDeckSnapshot.Unloaded : FromWire(wire.MediaDeck),
		wire.ProductionCgText is null ? OperatorProductionCgTextDescriptor.Empty : FromWire(wire.ProductionCgText),
		(wire.Scenes ?? Array.Empty<WireScene>())
			.Select(scene => new OperatorSceneDescriptor(
				scene.Id,
				scene.Name,
				scene.PreviewSourceId,
				scene.ProgramSourceId,
				(scene.CompositingState?.Layers ?? Array.Empty<WireCompositingLayer>())
					.Select(layer => FromWire(layer))
					.ToArray()))
			.ToArray(),
		(wire.OutputRoles ?? Array.Empty<WireOutputRole>())
			.Select(output => new OperatorOutputRoleDescriptor(
				output.RoleId, output.RoleKind, output.SourceId, output.TargetId, output.ProviderId,
				output.Width, output.Height, output.FrameRate, output.PixelFormat, output.Timing,
				output.LifecycleState, output.AuthoritativeActive, output.HealthState, output.Evidence,
				output.Error is null ? null : new Failure(output.Error.Code, output.Error.Message)))
			.ToArray(),
		(wire.CompositingLayers ?? Array.Empty<WireCompositingLayer>())
			.Select(layer => new OperatorCompositingLayerDescriptor(
				layer.LayerId,
				layer.Kind,
				layer.Order,
				layer.Visible,
				layer.Opacity,
				layer.PositionX,
				layer.PositionY,
				layer.Scale,
				layer.ContentIdentity))
			.ToArray());

	private static OperatorProductionCgTextDescriptor FromWire(WireProductionCgTextSnapshot snapshot) => new(
		snapshot.Active,
		snapshot.Text,
		snapshot.Typeface,
		snapshot.ResolvedTypeface,
		snapshot.FontSizePixels,
		snapshot.BoxWidth,
		snapshot.BoxHeight,
		Enum.IsDefined(typeof(OperatorCgTextAlignment), snapshot.Alignment)
			? (OperatorCgTextAlignment)snapshot.Alignment
			: throw new InvalidDataException("Production CG alignment is invalid."),
		Enum.IsDefined(typeof(OperatorCgAnchor), snapshot.Anchor)
			? (OperatorCgAnchor)snapshot.Anchor
			: throw new InvalidDataException("Production CG anchor is invalid."),
		snapshot.PanelEnabled,
		snapshot.Visible,
		Enum.IsDefined(typeof(OperatorCgLayer), snapshot.Layer)
			? (OperatorCgLayer)snapshot.Layer
			: throw new InvalidDataException("Production CG layer is invalid."),
		snapshot.ZOrder,
		snapshot.CacheHit,
		TimeSpan.FromTicks(Math.Max(0, snapshot.RenderDurationTicks)));

	private static OperatorAudioInputDescriptor FromWire(WireAudioInput input) => new(
		input.SourceId,
		input.StreamId,
		input.Gain,
		input.Muted,
		input.LeftPeak,
		input.RightPeak,
		input.MasterPeak,
		input.Clipping,
		input.Health,
		input.TestSignalEnabled,
		input.TestSignalMode,
		input.TestSignalActiveChannel,
		input.TestSignalFrequencyHz,
		input.TestSignalPeakLevel);

	private static OperatorAudioProgramDescriptor FromWire(WireAudioProgram program) => new(
		program.ActiveVideoSourceId,
		program.ActiveStreamId,
		program.Gain,
		program.Muted,
		program.LeftPeak,
		program.RightPeak,
		program.MasterPeak,
		program.Clipping,
		program.Health);

	private static OperatorRecordingDescriptor FromWire(WireRecordingSnapshot recording) => new(
		recording.State,
		TimeSpan.FromTicks(Math.Max(0, recording.ElapsedTicks)),
		recording.Destination,
		recording.FileName,
		recording.FinalPath,
		recording.Accepted,
		recording.Written,
		recording.Dropped,
		recording.Rejected,
		recording.WriterFailures,
		recording.Failure is null ? null : new Failure(recording.Failure.Code, recording.Failure.Message));

	private static OperatorAIShowcaseDescriptor FromWire(WireAIShowcase showcase) => new(
		showcase.Enabled,
		showcase.Feature,
		showcase.Status,
		showcase.Provider,
		TimeSpan.FromTicks(Math.Max(0, showcase.InferenceTimeTicks)),
		showcase.PersonRegionCount,
		showcase.SourceSequence,
		showcase.AppliedSequence,
		showcase.Confidence,
		showcase.EffectVisible,
		showcase.Failure is null ? null : new Failure(showcase.Failure.Code, showcase.Failure.Message),
		showcase.UpdatedAtUtc);

	private static OperatorHealthDescriptor FromWire(WireHealthSnapshot health) => new(
		FromWire(health.Engine),
		FromWire(health.Control),
		FromWire(health.Runtime),
		FromWire(health.Media),
		FromWire(health.Provider),
		FromWire(health.GpuProvider),
		health.CurrentFormat,
		TimeSpan.FromTicks(Math.Max(0, health.FrameTimeTicks)),
		TimeSpan.FromTicks(Math.Max(0, health.FrameBudgetTicks)),
		health.DroppedFrames,
		TimeSpan.FromTicks(Math.Max(0, health.UptimeTicks)),
		health.GpuUtilization,
		health.Vram,
		health.ObservedAtUtc,
		health.CpuDeviceName,
		health.CpuUtilization,
		health.SystemMemory,
		health.GpuDeviceName,
		health.OutputFramesPerSecond,
		health.AvSyncState,
		health.AvSyncEvent,
		health.AvSyncScheduledOffset,
		health.AvSyncSubmitOffset,
		health.AvSyncDrift,
		health.AvSyncDetail);

	private static OperatorHealthMetricDescriptor FromWire(WireHealthMetric metric) =>
		new(metric.State, metric.Detail);

	private static OperatorGraphicsOverlayDescriptor FromWire(WireGraphicsOverlay overlay) => new(
		overlay.AssetLoaded,
		overlay.AssetName,
		overlay.AssetWidth,
		overlay.AssetHeight,
		overlay.Visible,
		overlay.PositionX,
		overlay.PositionY,
		overlay.Scale);

	private static AuthoritativeProductionState FromWire(WireProductionState state) => new(
		CompatibilityVersion.Parse(state.Version),
		new ProductionId(Identity.Parse(state.ProductionId)),
		new Revision(state.Revision),
		new ProductionRoutingState(
			new ProductionSourceId(Identity.Parse(state.PreviewSourceId)),
			new ProductionSourceId(Identity.Parse(state.ProgramSourceId))),
		string.IsNullOrWhiteSpace(state.ActiveSceneId)
			? null
			: new SceneId(Identity.Parse(state.ActiveSceneId)),
		(state.OutputRoles ?? Array.Empty<WireOutputRoleAuthority>())
			.Select(role => new ProductionOutputRoleState(
				new OutputRoleId(role.RoleId),
				Enum.IsDefined(typeof(OutputRoleKind), role.Kind) ? (OutputRoleKind)role.Kind : throw new InvalidDataException("Output role kind is invalid."),
				new ProductionSourceId(Identity.Parse(role.SourceId)),
				role.ProviderSelector, role.TargetId, role.FormatPolicy, role.TimingPolicy, role.Enabled))
			.ToArray(),
		state.CompositingState is null
			? null
			: new ProductionCompositingState(
				CompatibilityVersion.Parse(state.CompositingState.Version),
				state.CompositingState.Layers.Select(layer => new ProductionCompositingLayerState(
					layer.LayerId,
					Enum.IsDefined(typeof(ProductionCompositingLayerKind), layer.Kind)
						? (ProductionCompositingLayerKind)layer.Kind
						: throw new InvalidDataException("Compositing layer kind is invalid."),
					layer.Order,
					layer.Visible,
					layer.Opacity,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.ContentIdentity)).ToArray()));

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
	private sealed record WireSource(string Id, string Name, string Type, string Format, string Health, string MediaState, long? RemainingTicks, string? MediaFileName);
	private sealed record WireScene(string Id, string Name, string PreviewSourceId, string ProgramSourceId, WireCompositingState? CompositingState = null);
	private sealed record WireOutputRoleAuthority(string RoleId, int Kind, string SourceId, string ProviderSelector, string TargetId, string FormatPolicy, string TimingPolicy, bool Enabled);
	private sealed record WireOutputRole(string RoleId, string RoleKind, string SourceId, string TargetId, string ProviderId, uint? Width, uint? Height, string? FrameRate, string? PixelFormat, string? Timing, string LifecycleState, bool AuthoritativeActive, string HealthState, string Evidence, WireFailure? Error);
	private sealed record WireProductionState(string Version, string ProductionId, ulong Revision, string PreviewSourceId, string ProgramSourceId, string? ActiveSceneId = null, WireOutputRoleAuthority[]? OutputRoles = null, WireCompositingState? CompositingState = null);
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
	private sealed record WireCompositingState(string Version, WireCompositingLayer[] Layers);
	private sealed record WireAudioInputState(string SourceId, double Gain, bool Muted);
	private sealed record WireAudioTestSignalState(string SourceId, bool Enabled, int Mode, double FrequencyHz, double PeakLevel);
	private sealed record WireTestPatternState(string SourceId, bool Enabled, bool MotionTiming = false);
	private sealed record WireAudioInput(
		string SourceId,
		string StreamId,
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
	private sealed record WireAudioProgram(string ActiveVideoSourceId, string ActiveStreamId, double Gain, bool Muted, double LeftPeak, double RightPeak, double MasterPeak, bool Clipping, string Health);
	private sealed record WireRecordingStart(string DestinationDirectory, string FileName);
	private sealed record WireRecordingSnapshot(string State, long ElapsedTicks, string? Destination, string? FileName, string? FinalPath, ulong Accepted, ulong Written, ulong Dropped, ulong Rejected, ulong WriterFailures, WireFailure? Failure);
	private sealed record WireRecordingCommandResult(bool Succeeded, WireRecordingSnapshot Snapshot, WireFailure? Failure);
	private sealed record WireAIShowcaseState(bool Enabled);
	private sealed record WireAIShowcase(bool Enabled, string Feature, string Status, string Provider, long InferenceTimeTicks, uint PersonRegionCount, ulong? SourceSequence, ulong? AppliedSequence, double? Confidence, bool EffectVisible, WireFailure? Failure, DateTimeOffset? UpdatedAtUtc);
	private sealed record WireHealthMetric(string State, string Detail);
	private sealed record WireHealthSnapshot(
		WireHealthMetric Engine,
		WireHealthMetric Control,
		WireHealthMetric Runtime,
		WireHealthMetric Media,
		WireHealthMetric Provider,
		WireHealthMetric GpuProvider,
		string CurrentFormat,
		long FrameTimeTicks,
		long FrameBudgetTicks,
		ulong DroppedFrames,
		long UptimeTicks,
		string GpuUtilization,
		string Vram,
		DateTimeOffset ObservedAtUtc,
		string CpuDeviceName,
		string CpuUtilization,
		string SystemMemory,
		string GpuDeviceName,
		double? OutputFramesPerSecond,
		string AvSyncState = "UNAVAILABLE",
		string AvSyncEvent = "UNAVAILABLE",
		string AvSyncScheduledOffset = "UNAVAILABLE",
		string AvSyncSubmitOffset = "UNAVAILABLE",
		string AvSyncDrift = "UNAVAILABLE",
		string AvSyncDetail = "A/V sync diagnostics are unavailable.");
	private sealed record WireOperatorSnapshot(WireProductionState Production, WireSource[] Sources, string RuntimeStatus, string TimingStatus, string InputStatus, string AIStatus, string RecordingStatus, bool VisualLayerEnabled, double AudioPeakLevel, WireGraphicsOverlay GraphicsOverlay, WireAudioInput[] AudioInputs, WireAudioProgram AudioProgram, WireRecordingSnapshot Recording, WireHealthSnapshot Health, WireAIShowcase AIShowcase, WireMediaDeckSnapshot? MediaDeck, ulong StateVersion, WireProductionCgTextSnapshot? ProductionCgText = null, WireScene[]? Scenes = null, WireOutputRole[]? OutputRoles = null, WireCompositingLayer[]? CompositingLayers = null);
	private sealed record WireMediaDeckOpen(string Version, string SourceId, string Path);
	private sealed record WireMediaTransportCommand(string Version, string AssetId, int Kind, long? TargetFrame, bool? AutoPlayOnProgram, int? EndBehavior, long? InPointFrame, long? OutPointFrame);
	private sealed record WireMediaMarkerCommand(string Version, string AssetId, int Kind, long? PositionFrame, string? CuePointId, string? Name);
	private sealed record WireLocalMediaProbe(string Version, string AssetId, string SourceId, string FileName, int Container, int VideoCodec, int AudioCodec, uint Width, uint Height, string FrameRate, long DurationTicks);
	private sealed record WireMediaTransportSnapshot(string Version, string AssetId, string SourceId, int State, long CurrentFrame, long TotalFrames, long PositionTicks, long DurationTicks, long RemainingTicks, string FrameRate, WireFailure? Failure, bool AutoPlayOnProgram, int EndBehavior, bool IsOnProgram, long EffectiveStartFrame, long EffectiveEndFrame, long EffectiveRemainingFrames, long EffectiveRemainingTicks);
	private sealed record WireCuePoint(string Id, string Name, long PositionFrame);
	private sealed record WireMediaMarkerSnapshot(string Version, string AssetId, long TotalFrames, long? InPointFrame, long? OutPointFrame, WireCuePoint[] CuePoints);
	private sealed record WireMediaDeckSnapshot(int State, string? SourceId, WireLocalMediaProbe? Probe, WireMediaTransportSnapshot? Transport, WireMediaMarkerSnapshot? Markers, WireFailure? Failure);
	private sealed record WireControlCommand(string Version, string CommandId, string ProductionId, ulong ExpectedRevision, string? SourceId, uint? DurationFrames, string? SceneId = null, string? OutputRoleId = null);
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
