// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public sealed class ControlHostIpcServer : IAsyncDisposable
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly string _endpoint;
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly IControlRuntimeTransportSeam _runtimeTransport;
	private readonly MediaDeckControlService? _mediaDeck;
	private readonly CancellationTokenSource _stop = new();
	private readonly SemaphoreSlim _mutationGate = new(1, 1);
	private readonly BoundedRequestCache _requestCache = new(256);
	private static readonly TimeSpan RuntimeObservationRetention = TimeSpan.FromSeconds(2);
	private readonly string _hostInstanceId = Identity.New().ToString();
	private readonly object _runtimeObservationGate = new();
	private RuntimeRemoteSnapshot? _lastRuntimeSnapshot;
	private DateTimeOffset _lastRuntimeSnapshotAtUtc;
	private RuntimeProductionCgTextDefinition? _productionCgText;
	private RetainedGraphicsAsset? _graphicsAsset;
	private RuntimeGraphicsOverlaySnapshot _graphicsOverlayState = new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
	private IReadOnlyList<RuntimeCompositingLayerSnapshot> _compositingLayers = Array.Empty<RuntimeCompositingLayerSnapshot>();
	private Task? _acceptLoop;
	private long _stateVersion = 1;
	private long _sequence;

	public ControlHostIpcServer(
		string endpoint,
		Func<ControlHostService?> controlAccessor,
		IControlRuntimeTransportSeam runtimeTransport,
		MediaDeckControlService? mediaDeck = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("ControlHost IPC endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_runtimeTransport = runtimeTransport ?? throw new ArgumentNullException(nameof(runtimeTransport));
		_mediaDeck = mediaDeck;
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

	public async ValueTask RestoreGraphicsStateAsync(CancellationToken cancellationToken = default)
	{
		if (!_runtimeTransport.IsConnected)
			return;

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_runtimeTransport.IsConnected)
				return;

			if (_graphicsAsset is { } asset)
			{
				await _runtimeTransport.LoadGraphicsOverlayAsync(
					asset.Name,
					asset.Width,
					asset.Height,
					asset.RgbaPixels,
					cancellationToken).ConfigureAwait(false);
				await _runtimeTransport.SetGraphicsOverlayAsync(
					_graphicsOverlayState.Visible,
					_graphicsOverlayState.PositionX,
					_graphicsOverlayState.PositionY,
					_graphicsOverlayState.Scale,
					cancellationToken).ConfigureAwait(false);
			}

			if (_productionCgText is { } definition)
				await _runtimeTransport.ApplyProductionCgTextAsync(definition, cancellationToken).ConfigureAwait(false);

			if (_compositingLayers.Count > 0)
			{
				foreach (var layer in _compositingLayers.Where(layer =>
					layer.LayerId is "bitmap-graphics" or "production-cg"))
				{
					await _runtimeTransport.SetCompositingLayerStateAsync(
						layer.LayerId,
						layer.Visible,
						layer.Opacity,
						cancellationToken).ConfigureAwait(false);
				}
				var current = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				var retainedOrder = _compositingLayers
					.OrderBy(layer => layer.Order)
					.Select(layer => layer.LayerId)
					.Where(layerId => current.CompositingLayers?.Any(layer => string.Equals(layer.LayerId, layerId, StringComparison.Ordinal)) == true)
					.ToArray();
				var missing = (current.CompositingLayers ?? Array.Empty<RuntimeCompositingLayerSnapshot>())
					.Select(layer => layer.LayerId)
					.Where(layerId => !retainedOrder.Contains(layerId, StringComparer.Ordinal))
					.ToArray();
				var order = retainedOrder.Concat(missing).ToArray();
				if (order.Length > 0)
					_compositingLayers = await _runtimeTransport.ReorderCompositingLayersAsync(order, cancellationToken).ConfigureAwait(false);
			}
			NotifyObservableStateChanged();
		}
		finally
		{
			_mutationGate.Release();
		}
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
			var pipe = OperatorPipeServerFactory.Create(_endpoint, PipeDirection.InOut);
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
			"control.scene.activate" => await MutateAsync(request, MutationKind.ActivateScene, cancellationToken).ConfigureAwait(false),
			"control.output.route" => await MutateAsync(request, MutationKind.RouteOutputRole, cancellationToken).ConfigureAwait(false),
			"control.program.cut" => await MutateAsync(request, MutationKind.Cut, cancellationToken).ConfigureAwait(false),
			"control.program.dissolve" => await MutateAsync(request, MutationKind.Dissolve, cancellationToken).ConfigureAwait(false),
			"control.graphics.overlay.load" => await LoadGraphicsOverlayAsync(request, cancellationToken).ConfigureAwait(false),
			"control.graphics.cg.apply" => await ApplyProductionCgTextAsync(request, cancellationToken).ConfigureAwait(false),
			"control.graphics.overlay.set" => await SetGraphicsOverlayAsync(request, cancellationToken).ConfigureAwait(false),
			"control.graphics.overlay.clear" => await ClearGraphicsOverlayAsync(request, cancellationToken).ConfigureAwait(false),
			"control.compositing.layer.set" => await SetCompositingLayerStateAsync(request, cancellationToken).ConfigureAwait(false),
			"control.compositing.layers.reorder" => await ReorderCompositingLayersAsync(request, cancellationToken).ConfigureAwait(false),
			"control.audio.input.set" => await SetAudioInputStateAsync(request, cancellationToken).ConfigureAwait(false),
			"control.audio.test_signal.set" => await SetAudioTestSignalAsync(request, cancellationToken).ConfigureAwait(false),
			"control.test_pattern.set" => await SetBroadcastTestPatternAsync(request, cancellationToken).ConfigureAwait(false),
			"control.recording.start" => await StartRecordingAsync(request, cancellationToken).ConfigureAwait(false),
			"control.recording.stop" => await StopRecordingAsync(request, cancellationToken).ConfigureAwait(false),
			"control.ai_showcase.set" => await SetAIShowcaseAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.snapshot.get" => await GetMediaDeckSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.open" => await OpenMediaDeckAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.transport" => await ApplyMediaDeckTransportAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.marker" => await ApplyMediaDeckMarkerAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.close" => await CloseMediaDeckAsync(request, cancellationToken).ConfigureAwait(false),
			_ => Error(request, "ipc.message.unknown", $"Unknown ControlHost message type '{request.MessageType}'.")
		};
	}

	private async ValueTask<WireEnvelope> SetBroadcastTestPatternAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireTestPatternState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Broadcast test pattern state payload is required.");

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			MediaSourceId sourceId;
			try { sourceId = new MediaSourceId(Identity.Parse(wire.SourceId)); }
			catch (Exception exception) when (exception is FormatException or ArgumentException)
			{
				return Error(request, "control.test_pattern.source.invalid", exception.Message);
			}

			if (!control.Specification.Sources.Any(source => source.SourceId.Value == sourceId.Value))
				return Error(request, "control.test_pattern.source.unknown", "Broadcast test pattern source must belong to the authoritative production.");

			try
			{
				var enabled = await _runtimeTransport
					.SetBroadcastTestPatternAsync(sourceId, wire.Enabled, wire.MotionTiming, cancellationToken)
					.ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(
					request,
					"control.test_pattern.response",
					new WireTestPatternState(sourceId.ToString(), enabled, enabled && wire.MotionTiming));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.test_pattern.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> SetAudioInputStateAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireAudioInputState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Audio input state payload is required.");
		if (!double.IsFinite(wire.Gain) || wire.Gain is < 0 or > 4)
			return Error(request, "control.audio.gain.invalid", "Audio gain must be finite and in the inclusive range 0..4.");

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			MediaSourceId sourceId;
			try { sourceId = new MediaSourceId(Identity.Parse(wire.SourceId)); }
			catch (Exception exception) when (exception is FormatException or ArgumentException)
			{
				return Error(request, "control.audio.source.invalid", exception.Message);
			}

			if (!control.Specification.Sources.Any(source => source.SourceId.Value == sourceId.Value))
				return Error(request, "control.audio.source.unknown", "Audio input must belong to an authoritative production source.");

			try
			{
				var snapshot = await _runtimeTransport
					.SetAudioInputStateAsync(sourceId, wire.Gain, wire.Muted, cancellationToken)
					.ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(request, "control.audio.input.response", ToWire(snapshot));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.audio.input.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> SetAudioTestSignalAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireAudioTestSignalState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Generated audio test signal payload is required.");
		if (wire.Mode is < 1 or > 5)
			return Error(request, "control.audio.test_signal.mode.invalid", "Generated audio test signal mode must be in the supported range 1..5.");
		if (!double.IsFinite(wire.FrequencyHz) || wire.FrequencyHz <= 0)
			return Error(request, "control.audio.test_signal.frequency.invalid", "Generated audio frequency must be finite and greater than zero.");
		if (!double.IsFinite(wire.PeakLevel) || wire.PeakLevel is < 0 or > 0.5)
			return Error(request, "control.audio.test_signal.level.invalid", "Generated audio peak level must be finite and in the inclusive range 0..0.5.");

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			MediaSourceId sourceId;
			try { sourceId = new MediaSourceId(Identity.Parse(wire.SourceId)); }
			catch (Exception exception) when (exception is FormatException or ArgumentException)
			{
				return Error(request, "control.audio.test_signal.source.invalid", exception.Message);
			}

			if (!control.Specification.Sources.Any(source => source.SourceId.Value == sourceId.Value))
				return Error(request, "control.audio.test_signal.source.unknown", "Generated audio test signal source must belong to the authoritative production.");

			try
			{
				var snapshot = await _runtimeTransport
					.SetAudioTestSignalAsync(sourceId, wire.Enabled, wire.Mode, wire.FrequencyHz, wire.PeakLevel, cancellationToken)
					.ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(request, "control.audio.test_signal.response", ToWire(snapshot));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.audio.test_signal.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> LoadGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireGraphicsAsset>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay asset payload is required.");
		var response = await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.LoadGraphicsOverlayAsync(wire.Name, wire.Width, wire.Height, wire.RgbaPixels, token),
			cancellationToken).ConfigureAwait(false);
		if (!string.Equals(response.MessageType, "error", StringComparison.Ordinal))
		{
			_graphicsAsset = new RetainedGraphicsAsset(wire.Name.Trim(), wire.Width, wire.Height, wire.RgbaPixels.ToArray());
			if (!_graphicsOverlayState.AssetLoaded)
				_graphicsOverlayState = new RuntimeGraphicsOverlaySnapshot(true, wire.Name.Trim(), wire.Width, wire.Height, false, 0.72, 0.06, 1.0);
			else
				_graphicsOverlayState = _graphicsOverlayState with { AssetLoaded = true, AssetName = wire.Name.Trim(), AssetWidth = wire.Width, AssetHeight = wire.Height };
		}
		return response;
	}

	private async ValueTask<WireEnvelope> ApplyProductionCgTextAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireProductionCgText>(Wire.JsonOptions)
			?? throw new InvalidDataException("Production CG text payload is required.");
		var definition = new RuntimeProductionCgTextDefinition(
			wire.Text,
			wire.Typeface,
			wire.FallbackTypeface,
			wire.FontSizePixels,
			new RuntimeCgColor(wire.Foreground.Red, wire.Foreground.Green, wire.Foreground.Blue, wire.Foreground.Alpha),
			wire.PositionX,
			wire.PositionY,
			wire.BoxWidth,
			wire.BoxHeight,
			wire.Alignment,
			wire.Anchor,
			new RuntimeCgPanel(
				wire.Panel.Enabled,
				new RuntimeCgColor(wire.Panel.Color.Red, wire.Panel.Color.Green, wire.Panel.Color.Blue, wire.Panel.Color.Alpha),
				wire.Panel.CornerRadiusPixels,
				wire.Panel.PaddingPixels),
			wire.Visible,
			wire.Layer,
			wire.ZOrder);
		var response = await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.ApplyProductionCgTextAsync(definition, token),
			cancellationToken).ConfigureAwait(false);
		if (!string.Equals(response.MessageType, "error", StringComparison.Ordinal))
			_productionCgText = definition;
		return response;
	}

	private async ValueTask<WireEnvelope> SetGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireGraphicsOverlayState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay state payload is required.");
		var response = await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.SetGraphicsOverlayAsync(wire.Visible, wire.PositionX, wire.PositionY, wire.Scale, token),
			cancellationToken).ConfigureAwait(false);
		if (!string.Equals(response.MessageType, "error", StringComparison.Ordinal))
		{
			if (_graphicsAsset is not null)
			{
				_graphicsOverlayState = _graphicsOverlayState with
				{
					Visible = wire.Visible,
					PositionX = wire.PositionX,
					PositionY = wire.PositionY,
					Scale = wire.Scale
				};
			}
			else if (_productionCgText is not null)
			{
				_productionCgText = _productionCgText with { Visible = wire.Visible };
			}
		}
		return response;
	}

	private async ValueTask<WireEnvelope> ClearGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var response = await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.ClearGraphicsOverlayAsync(token),
			cancellationToken).ConfigureAwait(false);
		if (!string.Equals(response.MessageType, "error", StringComparison.Ordinal))
		{
			_graphicsAsset = null;
			_graphicsOverlayState = new RuntimeGraphicsOverlaySnapshot(false, null, 0, 0, false, 0.72, 0.06, 1.0);
			_productionCgText = null;
			_compositingLayers = _compositingLayers
				.Where(layer => layer.LayerId is not "bitmap-graphics" and not "production-cg")
				.ToArray();
		}
		return response;
	}

	private async ValueTask<WireEnvelope> SetCompositingLayerStateAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireCompositingLayerState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Compositing layer state payload is required.");
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");
			var layers = await _runtimeTransport.SetCompositingLayerStateAsync(
				wire.LayerId,
				wire.Visible,
				wire.Opacity,
				cancellationToken).ConfigureAwait(false);
			_compositingLayers = layers;
			control.ConfirmCompositingMutation(ToProductionCompositingState(layers));
			if (string.Equals(wire.LayerId, "bitmap-graphics", StringComparison.Ordinal))
				_graphicsOverlayState = _graphicsOverlayState with { Visible = wire.Visible };
			else if (string.Equals(wire.LayerId, "production-cg", StringComparison.Ordinal) && _productionCgText is not null)
				_productionCgText = _productionCgText with { Visible = wire.Visible };
			NotifyObservableStateChanged();
			return Success(request, "control.compositing.layers.response", layers.Select(ToWire).ToArray());
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
		{
			return Error(request, "control.compositing.layer.rejected", exception.Message);
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> ReorderCompositingLayersAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireCompositingLayerOrder>(Wire.JsonOptions)
			?? throw new InvalidDataException("Compositing layer order payload is required.");
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");
			var layers = await _runtimeTransport.ReorderCompositingLayersAsync(wire.LayerIds, cancellationToken).ConfigureAwait(false);
			_compositingLayers = layers;
			control.ConfirmCompositingMutation(ToProductionCompositingState(layers));
			NotifyObservableStateChanged();
			return Success(request, "control.compositing.layers.response", layers.Select(ToWire).ToArray());
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
		{
			return Error(request, "control.compositing.reorder.rejected", exception.Message);
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask ConfirmRuntimeCompositingAuthorityAsync(
		ControlHostService control,
		CancellationToken cancellationToken)
	{
		var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		_compositingLayers = runtime.CompositingLayers ?? Array.Empty<RuntimeCompositingLayerSnapshot>();
		control.ConfirmCompositingMutation(ToProductionCompositingState(_compositingLayers));
	}

	private static ProductionCompositingState ToProductionCompositingState(
		IReadOnlyList<RuntimeCompositingLayerSnapshot> layers)
	{
		ArgumentNullException.ThrowIfNull(layers);
		return new ProductionCompositingState(
			ProductionCompositingState.CurrentVersion,
			layers
				.OrderBy(layer => layer.Order)
				.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
				.Select(layer => new ProductionCompositingLayerState(
					layer.LayerId,
					Enum.IsDefined(typeof(ProductionCompositingLayerKind), layer.Kind)
						? (ProductionCompositingLayerKind)layer.Kind
						: throw new InvalidDataException($"Runtime compositing layer kind '{layer.Kind}' is invalid."),
					layer.Order,
					layer.Visible,
					layer.Opacity,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.ContentIdentity))
				.ToArray());
	}

	private async ValueTask<WireEnvelope> MutateGraphicsAsync(
		WireEnvelope request,
		Func<CancellationToken, ValueTask<RuntimeGraphicsOverlaySnapshot>> mutation,
		CancellationToken cancellationToken)
	{
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			try
			{
				var snapshot = await mutation(cancellationToken).ConfigureAwait(false);
				await ConfirmRuntimeCompositingAuthorityAsync(control, cancellationToken).ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(request, "control.graphics.overlay.response", ToWire(snapshot));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.graphics.overlay.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> SetAIShowcaseAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireAIShowcaseState>(Wire.JsonOptions)
			?? throw new InvalidDataException("AI showcase state payload is required.");

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			try
			{
				var snapshot = await _runtimeTransport.SetAIShowcaseEnabledAsync(wire.Enabled, cancellationToken).ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(request, "control.ai_showcase.response", ToWire(snapshot));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.ai_showcase.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> StartRecordingAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireRecordingStart>(Wire.JsonOptions)
			?? throw new InvalidDataException("Recording start payload is required.");
		if (string.IsNullOrWhiteSpace(wire.DestinationDirectory))
			return Error(request, "control.recording.destination.invalid", "Recording destination directory is required.");
		if (string.IsNullOrWhiteSpace(wire.FileName))
			return Error(request, "control.recording.file_name.invalid", "Recording file name is required.");

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			try
			{
				var result = await _runtimeTransport
					.StartRecordingAsync(wire.DestinationDirectory, wire.FileName, cancellationToken)
					.ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(request, "control.recording.command.response", ToWire(result));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
			{
				return Error(request, "control.recording.start.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> StopRecordingAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			try
			{
				var result = await _runtimeTransport.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
				NotifyObservableStateChanged();
				return Success(request, "control.recording.command.response", ToWire(result));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.recording.stop.rejected", exception.Message);
			}
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> GetMediaDeckSnapshotAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return Error(request, "control.media_deck.unavailable", "Media-deck control service is not configured.");
		var snapshot = await _mediaDeck.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return Success(request, "control.media_deck.snapshot.response", ToWire(snapshot));
	}

	private async ValueTask<WireEnvelope> OpenMediaDeckAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return Error(request, "control.media_deck.unavailable", "Media-deck control service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaDeckOpen>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media-deck open payload is required.");
		var snapshot = await _mediaDeck.OpenAsync(
			new MediaDeckOpenRequest(
				CompatibilityVersion.Parse(wire.Version),
				new MediaSourceId(Identity.Parse(wire.SourceId)),
				wire.Path),
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return Success(request, "control.media_deck.snapshot.response", ToWire(snapshot));
	}

	private async ValueTask<WireEnvelope> ApplyMediaDeckTransportAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return Error(request, "control.media_deck.unavailable", "Media-deck control service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaTransportCommand>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media-deck transport payload is required.");
		var snapshot = await _mediaDeck.ApplyTransportAsync(
			new MediaTransportCommand(
				CompatibilityVersion.Parse(wire.Version),
				new MediaAssetId(Identity.Parse(wire.AssetId)),
				Enum.IsDefined(typeof(MediaTransportCommandKind), wire.Kind)
					? (MediaTransportCommandKind)wire.Kind
					: throw new InvalidDataException("Media transport command kind is invalid."),
				wire.TargetFrame,
				wire.AutoPlayOnProgram,
				wire.EndBehavior is null
					? null
					: Enum.IsDefined(typeof(MediaDeckEndBehavior), wire.EndBehavior.Value)
						? (MediaDeckEndBehavior)wire.EndBehavior.Value
						: throw new InvalidDataException("Media-deck end behavior is invalid."),
				wire.InPointFrame,
				wire.OutPointFrame),
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return Success(request, "control.media_deck.snapshot.response", ToWire(snapshot));
	}

	private async ValueTask<WireEnvelope> ApplyMediaDeckMarkerAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return Error(request, "control.media_deck.unavailable", "Media-deck control service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaMarkerCommand>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media-deck marker payload is required.");
		var snapshot = await _mediaDeck.ApplyMarkerAsync(
			new MediaMarkerCommand(
				CompatibilityVersion.Parse(wire.Version),
				new MediaAssetId(Identity.Parse(wire.AssetId)),
				Enum.IsDefined(typeof(MediaMarkerCommandKind), wire.Kind)
					? (MediaMarkerCommandKind)wire.Kind
					: throw new InvalidDataException("Media marker command kind is invalid."),
				wire.PositionFrame,
				string.IsNullOrWhiteSpace(wire.CuePointId)
					? null
					: new MediaCuePointId(Identity.Parse(wire.CuePointId)),
				wire.Name),
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return Success(request, "control.media_deck.snapshot.response", ToWire(snapshot));
	}

	private async ValueTask<WireEnvelope> CloseMediaDeckAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return Error(request, "control.media_deck.unavailable", "Media-deck control service is not configured.");
		var snapshot = await _mediaDeck.CloseAsync(cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return Success(request, "control.media_deck.snapshot.response", ToWire(snapshot));
	}

	private async ValueTask<WireEnvelope> GetSnapshotAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");

		var runtimeObservation = await ObserveRuntimeAsync(cancellationToken).ConfigureAwait(false);
		var runtime = runtimeObservation.Snapshot;
		var runtimeFresh = runtimeObservation.Fresh;
		var runtimeObservedAtUtc = runtimeObservation.ObservedAtUtc;

		MediaDeckSnapshot? mediaDeck = null;
		if (_mediaDeck is not null)
		{
			try { mediaDeck = await _mediaDeck.GetSnapshotAsync(cancellationToken).ConfigureAwait(false); }
			catch { mediaDeck = null; }
		}

		var state = control.State;
		var sources = control.Specification.Sources
			.Select(source => ToWireSource(source, runtime, mediaDeck))
			.ToArray();
		var audioInputs = runtime is null
			? Array.Empty<WireAudioInput>()
			: runtime.AudioInputs.Values
				.OrderBy(input => input.SourceId.ToString(), StringComparer.Ordinal)
				.Select(ToWire)
				.ToArray();
		var audioProgram = runtime is null
			? WireAudioProgram.Empty
			: ToWire(runtime.AudioProgram);
		var recording = runtime?.Recording is { } runtimeRecording
			? ToWire(runtimeRecording)
			: WireRecordingSnapshot.Unavailable;
		var aiShowcase = runtime?.AIShowcase is { } runtimeAI
			? ToWire(runtimeAI)
			: WireAIShowcase.Unavailable;
		var health = OperatorHealthProjection.Evaluate(
			runtime,
			runtime is null ? Array.Empty<ProviderDescriptor>() : _runtimeTransport.ProviderDescriptors,
			mediaDeck,
			control.HasAuthoritativeState,
			runtimeObservedAtUtc,
			runtimeFresh);
		var payload = new WireOperatorSnapshot(
			ToWire(state),
			sources,
			runtime is null || !runtimeFresh ? "DEGRADED" : "READY",
			runtime is null ? "UNKNOWN" : runtimeFresh ? runtime.TimingHealth.ToString() : "STALE",
			runtime is null ? "UNKNOWN" : runtimeFresh ? "VALID" : "STALE",
			aiShowcase.Status,
			recording.State,
			runtime?.CompositingLayers?.Any(layer => layer.Visible) == true,
			audioProgram.MasterPeak,
			runtime is null ? WireGraphicsOverlay.Empty : ToWire(runtime.GraphicsOverlay),
			audioInputs,
			audioProgram,
			recording,
			ToWire(health),
			aiShowcase,
			ToWire(mediaDeck ?? MediaDeckSnapshot.Unloaded),
			StateVersion,
			runtime?.ProductionCgText is null ? null : ToWire(runtime.ProductionCgText),
			control.Specification.Scenes
				.Select(scene => new WireScene(
					scene.SceneId.ToString(),
					scene.Name,
					scene.Routing.PreviewSourceId.ToString(),
					scene.Routing.ProgramSourceId.ToString(),
					ToWire(scene.CompositingState)))
				.ToArray(),
			ProjectOutputRoles(state, runtime, runtimeFresh),
			(runtime?.CompositingLayers ?? Array.Empty<RuntimeCompositingLayerSnapshot>())
				.OrderBy(layer => layer.Order)
				.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
				.Select(ToWire)
				.ToArray());
		return Success(request, "control.snapshot.response", payload);
	}


	private async ValueTask<RuntimeObservation> ObserveRuntimeAsync(CancellationToken cancellationToken)
	{
		try
		{
			var snapshot = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			var observedAtUtc = DateTimeOffset.UtcNow;
			lock (_runtimeObservationGate)
			{
				_lastRuntimeSnapshot = snapshot;
				_lastRuntimeSnapshotAtUtc = observedAtUtc;
			}
			return new RuntimeObservation(snapshot, true, observedAtUtc);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			lock (_runtimeObservationGate)
			{
				var now = DateTimeOffset.UtcNow;
				if (_lastRuntimeSnapshot is not null &&
					_lastRuntimeSnapshotAtUtc != default &&
					now - _lastRuntimeSnapshotAtUtc <= RuntimeObservationRetention)
				{
					return new RuntimeObservation(_lastRuntimeSnapshot, false, _lastRuntimeSnapshotAtUtc);
				}
			}
			return new RuntimeObservation(null, false, DateTimeOffset.UtcNow);
		}
	}

	private static WireSource ToWireSource(
		ProductionSourceSpecification source,
		RuntimeRemoteSnapshot? runtime,
		MediaDeckSnapshot? mediaDeck)
	{
		var mediaSourceId = new MediaSourceId(source.SourceId.Value);
		var isTestPattern = runtime?.BroadcastTestPatternSources?.Contains(mediaSourceId) == true;
		if (isTestPattern)
		{
			var motionTiming = runtime?.MotionTimingTestPatternSources?.Contains(mediaSourceId) == true;
			return new WireSource(
				source.SourceId.ToString(),
				source.Name,
				"TEST",
				FormatVideo(runtime!.Format),
				"VALID",
				motionTiming ? "MOTION" : "STATIC",
				null,
				null);
		}

		var isMedia = mediaDeck?.IsLoaded == true && mediaDeck.SourceId == mediaSourceId;
		if (isMedia)
		{
			var probe = mediaDeck!.Probe!;
			var transport = mediaDeck.Transport!;
			return new WireSource(
				source.SourceId.ToString(),
				source.Name,
				"MEDIA",
				FormatVideo(probe.VideoFormat),
				mediaDeck.State == MediaDeckState.Error ? "ERROR" : "READY",
				mediaDeck.State.ToString().ToUpperInvariant(),
				transport.EffectiveRemaining.Ticks,
				probe.FileName);
		}

		var health = "UNKNOWN";
		if (runtime is not null)
		{
			health = runtime.InputSignals.TryGetValue(mediaSourceId, out var signal)
				? signal
				: "VALID";
		}
		return new WireSource(
			source.SourceId.ToString(),
			source.Name,
			"LIVE",
			runtime is null ? "UNKNOWN" : FormatVideo(runtime.Format),
			health,
			"—",
			null,
			null);
	}

	private static string FormatVideo(VideoFormat format) =>
		$"{format.Width}×{format.Height} {format.FrameRate}";


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
			ControlHostOperationResult staged;
			if (kind == MutationKind.ActivateScene)
			{
				if (string.IsNullOrWhiteSpace(command.SceneId))
					throw new InvalidDataException("Scene activation requires sceneId.");
				staged = control.ActivateScene(new ActivateSceneCommand(
					metadata,
					new SceneId(Identity.Parse(command.SceneId))));
			}
			else
			{
				if (string.IsNullOrWhiteSpace(command.SourceId))
					throw new InvalidDataException("Production routing command requires sourceId.");
				var sourceId = new ProductionSourceId(Identity.Parse(command.SourceId));
				staged = kind switch
				{
					MutationKind.SelectPreview => control.SelectPreview(new SelectPreviewCommand(metadata, sourceId)),
					MutationKind.Cut => control.CutProgram(new CutProgramCommand(metadata, sourceId)),
					MutationKind.Dissolve => control.DissolveProgram(new DissolveProgramCommand(metadata, sourceId, command.DurationFrames ?? throw new InvalidDataException("DISSOLVE requires durationFrames."))),
					MutationKind.RouteOutputRole => control.RouteOutputRole(new RouteOutputRoleCommand(
						metadata,
						new OutputRoleId(command.OutputRoleId ?? throw new InvalidDataException("Output routing requires outputRoleId.")),
						sourceId)),
					_ => throw new InvalidOperationException("Unknown mutation kind.")
				};
			}

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

	private static WireAudioInput ToWire(RuntimeAudioInputSnapshot snapshot) => new(
		snapshot.SourceId.ToString(),
		snapshot.StreamId.ToString(),
		snapshot.Gain,
		snapshot.Muted,
		snapshot.LeftPeak,
		snapshot.RightPeak,
		snapshot.MasterPeak,
		snapshot.Clipping,
		snapshot.Health,
		snapshot.TestSignalEnabled,
		snapshot.TestSignalMode,
		snapshot.TestSignalActiveChannel,
		snapshot.TestSignalFrequencyHz,
		snapshot.TestSignalPeakLevel);

	private static WireAudioProgram ToWire(RuntimeAudioProgramSnapshot snapshot) => new(
		snapshot.ActiveVideoSourceId.ToString(),
		snapshot.ActiveStreamId.ToString(),
		snapshot.Gain,
		snapshot.Muted,
		snapshot.LeftPeak,
		snapshot.RightPeak,
		snapshot.MasterPeak,
		snapshot.Clipping,
		snapshot.Health);

	private static WireProductionCgTextSnapshot ToWire(RuntimeProductionCgTextSnapshot snapshot) => new(
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
		snapshot.RenderDuration.Ticks);

	private static WireCompositingLayer ToWire(RuntimeCompositingLayerSnapshot snapshot) => new(
		snapshot.LayerId,
		snapshot.Kind,
		snapshot.Order,
		snapshot.Visible,
		snapshot.Opacity,
		snapshot.PositionX,
		snapshot.PositionY,
		snapshot.Scale,
		snapshot.ContentIdentity);

	private static WireGraphicsOverlay ToWire(RuntimeGraphicsOverlaySnapshot snapshot) => new(
		snapshot.AssetLoaded,
		snapshot.AssetName,
		snapshot.AssetWidth,
		snapshot.AssetHeight,
		snapshot.Visible,
		snapshot.PositionX,
		snapshot.PositionY,
		snapshot.Scale);

	private static WireMediaDeckSnapshot ToWire(MediaDeckSnapshot snapshot) => new(
		(int)snapshot.State,
		snapshot.SourceId?.ToString(),
		snapshot.Probe is null ? null : new WireLocalMediaProbe(
			snapshot.Probe.Version.ToString(),
			snapshot.Probe.AssetId.ToString(),
			snapshot.Probe.SourceId.ToString(),
			snapshot.Probe.FileName,
			(int)snapshot.Probe.Container,
			(int)snapshot.Probe.VideoCodec,
			(int)snapshot.Probe.AudioCodec,
			snapshot.Probe.VideoFormat.Width,
			snapshot.Probe.VideoFormat.Height,
			snapshot.Probe.VideoFormat.FrameRate.ToString(),
			snapshot.Probe.Duration.Ticks),
		snapshot.Transport is null ? null : new WireMediaTransportSnapshot(
			snapshot.Transport.Version.ToString(),
			snapshot.Transport.AssetId.ToString(),
			snapshot.Transport.SourceId.ToString(),
			(int)snapshot.Transport.State,
			snapshot.Transport.Position.CurrentFrame,
			snapshot.Transport.Position.TotalFrames,
			snapshot.Transport.Position.Position.Ticks,
			snapshot.Transport.Position.Duration.Ticks,
			snapshot.Transport.Position.Remaining.Ticks,
			snapshot.Transport.Position.FrameRate.ToString(),
			snapshot.Transport.Failure is { } transportFailure ? new WireFailure(transportFailure.Code, transportFailure.Message) : null,
			snapshot.Transport.AutoPlayOnProgram,
			(int)snapshot.Transport.EndBehavior,
			snapshot.Transport.IsOnProgram,
			snapshot.Transport.EffectiveStartFrame,
			snapshot.Transport.EffectiveEndFrame,
			snapshot.Transport.EffectiveRemainingFrames,
			snapshot.Transport.EffectiveRemaining.Ticks),
		snapshot.Markers is null ? null : new WireMediaMarkerSnapshot(
			snapshot.Markers.Version.ToString(),
			snapshot.Markers.AssetId.ToString(),
			snapshot.Markers.TotalFrames,
			snapshot.Markers.InPointFrame,
			snapshot.Markers.OutPointFrame,
			snapshot.Markers.CuePoints.Select(cue => new WireCuePoint(cue.Id.ToString(), cue.Name, cue.PositionFrame)).ToArray()),
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

	private static WireRecordingSnapshot ToWire(RuntimeRecordingSnapshot snapshot) => new(
		snapshot.State,
		snapshot.Elapsed.Ticks,
		snapshot.Destination,
		snapshot.FileName,
		snapshot.FinalPath,
		snapshot.Accepted,
		snapshot.Written,
		snapshot.Dropped,
		snapshot.Rejected,
		snapshot.WriterFailures,
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

	private static WireAIShowcase ToWire(RuntimeAIShowcaseRemoteSnapshot snapshot) => new(
		snapshot.Enabled,
		snapshot.Feature,
		snapshot.Status,
		snapshot.Provider,
		snapshot.InferenceTime.Ticks,
		snapshot.PersonRegionCount,
		snapshot.SourceSequence,
		snapshot.AppliedSequence,
		snapshot.Confidence,
		snapshot.EffectVisible,
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null,
		snapshot.UpdatedAtUtc);

	private static WireRecordingCommandResult ToWire(RuntimeRecordingCommandResult result) => new(
		result.Succeeded,
		ToWire(result.Snapshot),
		result.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

	private static WireHealthSnapshot ToWire(OperatorHealthProjectionSnapshot snapshot) => new(
		ToWire(snapshot.Engine),
		ToWire(snapshot.Control),
		ToWire(snapshot.Runtime),
		ToWire(snapshot.Media),
		ToWire(snapshot.Provider),
		ToWire(snapshot.GpuProvider),
		snapshot.CurrentFormat,
		snapshot.FrameTime.Ticks,
		snapshot.FrameBudget.Ticks,
		snapshot.DroppedFrames,
		snapshot.Uptime.Ticks,
		snapshot.GpuUtilization,
		snapshot.Vram,
		snapshot.ObservedAtUtc,
		snapshot.CpuDeviceName,
		snapshot.CpuUtilization,
		snapshot.SystemMemory,
		snapshot.GpuDeviceName,
		snapshot.OutputFramesPerSecond,
		snapshot.AvSyncState,
		snapshot.AvSyncEvent,
		snapshot.AvSyncScheduledOffset,
		snapshot.AvSyncSubmitOffset,
		snapshot.AvSyncDrift,
		snapshot.AvSyncDetail);

	private static WireHealthMetric ToWire(OperatorHealthMetric metric) =>
		new(metric.State, metric.Detail);

	private static WireProductionState ToWire(AuthoritativeProductionState state) => new(
		state.Version.ToString(),
		state.ProductionId.ToString(),
		state.Revision.Value,
		state.Routing.PreviewSourceId.ToString(),
		state.Routing.ProgramSourceId.ToString(),
		state.ActiveSceneId?.ToString(),
		state.OutputRoles.Select(role => new WireOutputRoleAuthority(
			role.RoleId.ToString(),
			(int)role.Kind,
			role.SourceId.ToString(),
			role.ProviderSelector,
			role.TargetId,
			role.FormatPolicy,
			role.TimingPolicy,
			role.Enabled)).ToArray(),
		ToWire(state.CompositingState));

	private static WireCompositingState? ToWire(ProductionCompositingState? state) =>
		state is null
			? null
			: new WireCompositingState(
				state.Version.ToString(),
				state.Layers.Select(layer => new WireCompositingLayer(
					layer.LayerId,
					(int)layer.Kind,
					layer.Order,
					layer.Visible,
					layer.Opacity,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.ContentIdentity)).ToArray());

	private static WireOutputRole[] ProjectOutputRoles(
		AuthoritativeProductionState state,
		RuntimeRemoteSnapshot? runtime,
		bool runtimeFresh)
	{
		return state.OutputRoles
			.OrderBy(role => role.RoleId.ToString(), StringComparer.Ordinal)
			.Select(role =>
			{
				var runtimeRole = runtimeFresh
					? runtime?.OutputRoles?.FirstOrDefault(candidate =>
						string.Equals(candidate.RoleId, role.RoleId.ToString(), StringComparison.OrdinalIgnoreCase))
					: null;
				var sourceMatches = runtimeRole is not null && runtimeRole.SourceId.Value == role.SourceId.Value;
				var confirmed = runtimeRole is not null && runtimeRole.AuthoritativeActive && sourceMatches;
				var health = !runtimeFresh || runtimeRole is null
					? "UNVERIFIED"
					: !sourceMatches
						? "FAIL"
						: runtimeRole.HealthState switch
						{
							RuntimeOutputRoleHealthState.Healthy => "PASS",
							RuntimeOutputRoleHealthState.Faulted => "FAIL",
							_ => "UNVERIFIED"
						};
				var evidence = !runtimeFresh
					? "Runtime output evidence is stale or unavailable."
					: runtimeRole is null
						? "Runtime has not confirmed the configured output role."
						: !sourceMatches
							? "Runtime output source does not match authoritative Control configuration."
							: runtimeRole.Evidence;
				var error = runtimeRole?.Error;
				if (runtimeFresh && runtimeRole is not null && !sourceMatches)
					error = new Failure("control.output_role.source_mismatch", "Runtime output source does not match authoritative Control configuration.");

				return new WireOutputRole(
					role.RoleId.ToString(),
					role.Kind.ToString().ToUpperInvariant(),
					role.SourceId.ToString(),
					runtimeRole?.TargetId.ToString() ?? role.TargetId,
					runtimeRole?.ProviderId.ToString() ?? role.ProviderSelector,
					runtimeRole?.Format.Width,
					runtimeRole?.Format.Height,
					runtimeRole?.Format.FrameRate.ToString(),
					runtimeRole?.Format.PixelFormat.ToString().ToUpperInvariant(),
					runtimeRole is null ? null : $"{runtimeRole.Timing.Numerator}/{runtimeRole.Timing.Denominator}",
					runtimeRole?.LifecycleState.ToString().ToUpperInvariant() ?? "INACTIVE",
					confirmed,
					health,
					evidence,
					error is { } failure ? new WireFailure(failure.Code, failure.Message) : null);
			})
			.ToArray();
	}

	private readonly record struct RuntimeObservation(
		RuntimeRemoteSnapshot? Snapshot,
		bool Fresh,
		DateTimeOffset ObservedAtUtc);

	private enum MutationKind
	{
		SelectPreview = 1,
		Cut = 2,
		Dissolve = 3,
		ActivateScene = 4,
		RouteOutputRole = 5
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
	private sealed record WireCompositingLayer(string LayerId, int Kind, int Order, bool Visible, byte Opacity, double PositionX, double PositionY, double Scale, string ContentIdentity);
	private sealed record WireCompositingState(string Version, WireCompositingLayer[] Layers);
	private sealed record WireGraphicsOverlay(bool AssetLoaded, string? AssetName, uint AssetWidth, uint AssetHeight, bool Visible, double PositionX, double PositionY, double Scale)
	{
		public static WireGraphicsOverlay Empty { get; } = new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
	}
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
	private sealed record WireAudioProgram(string ActiveVideoSourceId, string ActiveStreamId, double Gain, bool Muted, double LeftPeak, double RightPeak, double MasterPeak, bool Clipping, string Health)
	{
		public static WireAudioProgram Empty { get; } = new(string.Empty, string.Empty, 1, false, 0, 0, 0, false, "UNKNOWN");
	}
	private sealed record WireRecordingStart(string DestinationDirectory, string FileName);
	private sealed record WireRecordingSnapshot(string State, long ElapsedTicks, string? Destination, string? FileName, string? FinalPath, ulong Accepted, ulong Written, ulong Dropped, ulong Rejected, ulong WriterFailures, WireFailure? Failure)
	{
		public static WireRecordingSnapshot Unavailable { get; } = new("UNAVAILABLE", 0, null, null, null, 0, 0, 0, 0, 0, null);
	}
	private sealed record WireRecordingCommandResult(bool Succeeded, WireRecordingSnapshot Snapshot, WireFailure? Failure);
	private sealed record WireAIShowcaseState(bool Enabled);
	private sealed record WireAIShowcase(bool Enabled, string Feature, string Status, string Provider, long InferenceTimeTicks, uint PersonRegionCount, ulong? SourceSequence, ulong? AppliedSequence, double? Confidence, bool EffectVisible, WireFailure? Failure, DateTimeOffset? UpdatedAtUtc)
	{
		public static WireAIShowcase Unavailable { get; } = new(false, "Person Segmentation Highlight", "UNAVAILABLE", "UNVERIFIED", 0, 0, null, null, null, false, null, null);
	}
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
	private sealed record WireOperatorSnapshot(WireProductionState Production, WireSource[] Sources, string RuntimeStatus, string TimingStatus, string InputStatus, string AIStatus, string RecordingStatus, bool VisualLayerEnabled, double AudioPeakLevel, WireGraphicsOverlay GraphicsOverlay, WireAudioInput[] AudioInputs, WireAudioProgram AudioProgram, WireRecordingSnapshot Recording, WireHealthSnapshot Health, WireAIShowcase AIShowcase, WireMediaDeckSnapshot MediaDeck, ulong StateVersion, WireProductionCgTextSnapshot? ProductionCgText = null, WireScene[]? Scenes = null, WireOutputRole[]? OutputRoles = null, WireCompositingLayer[]? CompositingLayers = null);
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
	private sealed record RetainedGraphicsAsset(string Name, uint Width, uint Height, byte[] RgbaPixels);

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
