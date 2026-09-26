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
	private readonly ShowControlCoordinator? _showControl;
	private readonly RundownCoordinator? _rundown;
	private readonly MediaAssetCatalogService? _mediaAssetCatalog;
	private readonly ShowProjectPersistenceStore? _showProjectStore;
	private PersistedShowProject? _showProject;
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
	private DurableBitmapGraphicsReference? _durableBitmapReference;
	private RuntimeGraphicsOverlaySnapshot _graphicsOverlayState = new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
	private IReadOnlyList<RuntimeCompositingLayerSnapshot> _compositingLayers = Array.Empty<RuntimeCompositingLayerSnapshot>();
	private DurableAudioRoutingState _durableAudioRouting = DurableAudioRoutingState.FollowVideo;
	private string _showProjectState = "UNAVAILABLE";
	private string _showProjectDetail = "Durable show project persistence is not configured.";
	private Task? _acceptLoop;
	private long _stateVersion = 1;
	private long _sequence;

	public ControlHostIpcServer(
		string endpoint,
		Func<ControlHostService?> controlAccessor,
		IControlRuntimeTransportSeam runtimeTransport,
		MediaDeckControlService? mediaDeck = null,
		ShowControlPersistenceStore? showControlPersistence = null,
		MediaAssetCatalogService? mediaAssetCatalog = null,
		ShowProjectPersistenceStore? showProjectStore = null,
		PersistedShowProject? showProject = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("ControlHost IPC endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_runtimeTransport = runtimeTransport ?? throw new ArgumentNullException(nameof(runtimeTransport));
		_mediaDeck = mediaDeck;
		_mediaAssetCatalog = mediaAssetCatalog;
		if ((showProjectStore is null) != (showProject is null))
			throw new ArgumentException("Durable show-project store and snapshot must be configured together.");
		_showProjectStore = showProjectStore;
		_showProject = showProject;
		if (showProject is not null)
		{
			_productionCgText = showProject.Graphics.ProductionCgText;
			_durableBitmapReference = showProject.Graphics.Bitmap;
			if (_durableBitmapReference is { } bitmap)
			{
				_graphicsOverlayState = new RuntimeGraphicsOverlaySnapshot(
					true,
					bitmap.Name,
					bitmap.Width,
					bitmap.Height,
					bitmap.Visible,
					bitmap.PositionX,
					bitmap.PositionY,
					bitmap.Scale);
			}
			_compositingLayers = ToRuntimeCompositingLayers(showProject.Graphics.CompositingState);
			_durableAudioRouting = showProject.AudioRouting ?? DurableAudioRoutingState.FollowVideo;
			_showProjectState = "LOADED";
			_showProjectDetail = $"Durable show project '{showProject.Name}' ({showProject.ProjectId}) is loaded.";
		}
		_showControl = showControlPersistence is null
			? null
			: new ShowControlCoordinator(
				_controlAccessor,
				showControlPersistence,
				ExecuteShowControlActionAsync,
				ObserveShowControlFrameAsync,
				NotifyObservableStateChanged);
		_rundown = _showControl is not null && _showProjectStore is not null
			? new RundownCoordinator(
				_controlAccessor,
				_showProjectStore,
				_showControl,
				_mediaAssetCatalog,
				_mediaDeck,
				NotifyObservableStateChanged)
			: null;
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

	public ValueTask RestoreGraphicsStateAsync(CancellationToken cancellationToken = default) =>
		RunSerializedMutationAsync(RestoreGraphicsStateCoreAsync, cancellationToken);

	public ValueTask RestoreAudioRoutingStateAsync(CancellationToken cancellationToken = default) =>
		RunSerializedMutationAsync(RestoreAudioRoutingStateCoreAsync, cancellationToken);

	internal async ValueTask RunSerializedMutationAsync(
		Func<CancellationToken, ValueTask> operation,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(operation);
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await operation(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	internal ValueTask RestoreGraphicsStateWithinMutationAsync(CancellationToken cancellationToken = default) =>
		RestoreGraphicsStateCoreAsync(cancellationToken);

	internal ValueTask RestoreAudioRoutingStateWithinMutationAsync(CancellationToken cancellationToken = default) =>
		RestoreAudioRoutingStateCoreAsync(cancellationToken);

	private async ValueTask RestoreAudioRoutingStateCoreAsync(CancellationToken cancellationToken)
	{
		if (!_runtimeTransport.IsConnected)
			return;

		await _runtimeTransport.SetAudioRoutingAsync(
			_durableAudioRouting.Mode,
			_durableAudioRouting.BreakawaySourceId,
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
	}

	private async ValueTask RestoreGraphicsStateCoreAsync(CancellationToken cancellationToken)
	{
		if (!_runtimeTransport.IsConnected)
			return;

		if (_graphicsAsset is null && _durableBitmapReference is { } durableBitmap && _showProjectStore is not null)
		{
			try
			{
				var rgbaPixels = await _showProjectStore.LoadBitmapAssetAsync(durableBitmap, cancellationToken).ConfigureAwait(false);
				_graphicsAsset = new RetainedGraphicsAsset(durableBitmap.Name, durableBitmap.Width, durableBitmap.Height, rgbaPixels);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
			{
				SetShowProjectState("RECOVERY_REQUIRED", $"Durable bitmap graphics could not be restored: {exception.Message}");
				throw;
			}
		}

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
				await _runtimeTransport.SetCompositingLayerTransformAsync(
					layer.LayerId,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.RotationDegrees,
					layer.AnchorX,
					layer.AnchorY,
					layer.CropLeft,
					layer.CropTop,
					layer.CropRight,
					layer.CropBottom,
					cancellationToken).ConfigureAwait(false);
				await _runtimeTransport.SetCompositingLayerProcessingNodeAsync(
					layer.LayerId,
					layer.ProcessingNode,
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
		if (_showProject is not null && _showProjectState != "RECOVERY_REQUIRED")
		{
			SetShowProjectState(
				"RESTORED",
				$"Durable show project '{_showProject.Name}' was restored through Runtime confirmation paths.");
		}
		NotifyObservableStateChanged();
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
		if (_rundown is not null)
			await _rundown.DisposeAsync().ConfigureAwait(false);
		if (_showControl is not null)
			await _showControl.DisposeAsync().ConfigureAwait(false);
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
			"control.compositing.layer.transform" => await SetCompositingLayerTransformAsync(request, cancellationToken).ConfigureAwait(false),
			"control.compositing.layer.processing" => await SetCompositingLayerProcessingAsync(request, cancellationToken).ConfigureAwait(false),
			"control.compositing.layers.reorder" => await ReorderCompositingLayersAsync(request, cancellationToken).ConfigureAwait(false),
			"control.audio.input.set" => await SetAudioInputStateAsync(request, cancellationToken).ConfigureAwait(false),
			"control.audio.routing.set" => await SetAudioRoutingAsync(request, cancellationToken).ConfigureAwait(false),
			"control.audio.test_signal.set" => await SetAudioTestSignalAsync(request, cancellationToken).ConfigureAwait(false),
			"control.test_pattern.set" => await SetBroadcastTestPatternAsync(request, cancellationToken).ConfigureAwait(false),
			"control.recording.start" => await StartRecordingAsync(request, cancellationToken).ConfigureAwait(false),
			"control.recording.stop" => await StopRecordingAsync(request, cancellationToken).ConfigureAwait(false),
			"control.ai_showcase.set" => await SetAIShowcaseAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_asset_catalog.snapshot.get" => await GetMediaAssetCatalogAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_asset_catalog.import" => await ImportMediaAssetsAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_asset_catalog.relink" => await RelinkMediaAssetAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_asset_catalog.remove" => await RemoveMediaAssetAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_asset_catalog.availability.refresh" => await RefreshMediaAssetAvailabilityAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.snapshot.get" => await GetMediaDeckSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.open" => await OpenMediaDeckAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.transport" => await ApplyMediaDeckTransportAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.marker" => await ApplyMediaDeckMarkerAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.close" => await CloseMediaDeckAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.snapshot.get" => await GetShowControlSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.cue_list.save" => await SaveShowControlCueListAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.cue_list.select" => await SelectShowControlCueListAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.arm" => await ArmShowControlAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.go" => await GoShowControlAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.cancel" => await CancelShowControlAsync(request, cancellationToken).ConfigureAwait(false),
			"control.show_control.recovery.acknowledge" => await AcknowledgeShowControlRecoveryAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.snapshot.get" => await GetRundownSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.save" => await SaveRundownAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.prepare" => await PrepareRundownAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.go" => await GoRundownAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.next" => await NextRundownAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.previous" => await PreviousRundownAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.hold" => await HoldRundownAsync(request, cancellationToken).ConfigureAwait(false),
			"control.rundown.recovery.acknowledge" => await AcknowledgeRundownRecoveryAsync(request, cancellationToken).ConfigureAwait(false),
			_ => Error(request, "ipc.message.unknown", $"Unknown ControlHost message type '{request.MessageType}'.")
		};
	}

	private async ValueTask<WireEnvelope> GetRundownSnapshotAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_rundown is null)
			return Error(request, "control.rundown.unavailable", "Rundown persistence and Show Control must be configured.");
		try
		{
			return Success(request, "control.rundown.snapshot.response", ToWire(await _rundown.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)));
		}
		catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or FormatException or NotSupportedException)
		{
			return Error(request, "control.rundown.snapshot.rejected", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> SaveRundownAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_rundown is null)
			return Error(request, "control.rundown.unavailable", "Rundown persistence and Show Control must be configured.");
		var wire = request.Payload.Deserialize<WireRundownSave>(Wire.JsonOptions)
			?? throw new InvalidDataException("Rundown save payload is required.");
		try
		{
			var rundown = RundownCanonicalSerializer.Deserialize(wire.RundownJson);
			var snapshot = await _rundown.SaveAsync(rundown, wire.ExpectedStorageVersion, cancellationToken).ConfigureAwait(false);
			return Success(request, "control.rundown.snapshot.response", ToWire(snapshot));
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or FormatException or NotSupportedException)
		{
			return Error(request, "control.rundown.save.rejected", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> PrepareRundownAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_rundown is null)
			return Error(request, "control.rundown.unavailable", "Rundown persistence and Show Control must be configured.");
		var wire = request.Payload.Deserialize<WireRundownItemRequest>(Wire.JsonOptions)
			?? throw new InvalidDataException("Rundown item payload is required.");
		try
		{
			var itemId = new RundownItemId(Identity.Parse(wire.ItemId));
			return Success(request, "control.rundown.snapshot.response", ToWire(await _rundown.PrepareAsync(itemId, cancellationToken).ConfigureAwait(false)));
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or FormatException or KeyNotFoundException or NotSupportedException)
		{
			return Error(request, "control.rundown.prepare.rejected", exception.Message);
		}
	}

	private ValueTask<WireEnvelope> GoRundownAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		ApplyRundownOperationAsync(request, "go", coordinator => coordinator.GoAsync(cancellationToken));

	private ValueTask<WireEnvelope> NextRundownAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		ApplyRundownOperationAsync(request, "next", coordinator => coordinator.NextAsync(cancellationToken));

	private ValueTask<WireEnvelope> PreviousRundownAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		ApplyRundownOperationAsync(request, "previous", coordinator => coordinator.PreviousAsync(cancellationToken));

	private ValueTask<WireEnvelope> HoldRundownAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		ApplyRundownOperationAsync(request, "hold", coordinator => coordinator.HoldAsync(cancellationToken));

	private async ValueTask<WireEnvelope> AcknowledgeRundownRecoveryAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_rundown is null)
			return Error(request, "control.rundown.unavailable", "Rundown persistence and Show Control must be configured.");
		var wire = request.Payload.Deserialize<WireRundownRecoveryAcknowledge>(Wire.JsonOptions)
			?? throw new InvalidDataException("Rundown recovery acknowledgement payload is required.");
		try
		{
			return Success(
				request,
				"control.rundown.snapshot.response",
				ToWire(await _rundown.AcknowledgeRecoveryAsync(wire.Resume, cancellationToken).ConfigureAwait(false)));
		}
		catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or FormatException or NotSupportedException)
		{
			return Error(request, "control.rundown.recovery.rejected", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> ApplyRundownOperationAsync(
		WireEnvelope request,
		string operation,
		Func<RundownCoordinator, ValueTask<RundownWorkspaceSnapshot>> action)
	{
		if (_rundown is null)
			return Error(request, "control.rundown.unavailable", "Rundown persistence and Show Control must be configured.");
		try
		{
			return Success(request, "control.rundown.snapshot.response", ToWire(await action(_rundown).ConfigureAwait(false)));
		}
		catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or FormatException or KeyNotFoundException or NotSupportedException)
		{
			return Error(request, $"control.rundown.{operation}.rejected", exception.Message);
		}
	}

	private static WireRundownWorkspace ToWire(RundownWorkspaceSnapshot snapshot) => new(
		snapshot.Rundown is null ? null : RundownCanonicalSerializer.Serialize(snapshot.Rundown),
		(int)snapshot.Execution.State,
		snapshot.Execution.RundownId?.ToString(),
		snapshot.Execution.SelectedItemId?.ToString(),
		snapshot.Execution.PreparedItemId?.ToString(),
		snapshot.Execution.CurrentItemId?.ToString(),
		snapshot.Execution.NextItemId?.ToString(),
		snapshot.Execution.Revision,
		snapshot.Execution.CausalActionId?.ToString(),
		snapshot.Execution.AutoAdvanceArmed,
		snapshot.Execution.RequiresAcknowledgement,
		snapshot.Execution.Failure?.Code,
		snapshot.Execution.Failure?.Message,
		snapshot.StorageVersion);

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

	private async ValueTask<WireEnvelope> SetAudioRoutingAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireAudioRoutingState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Audio routing state payload is required.");
		if (wire.Mode is not (DurableAudioRoutingState.FollowVideoMode or DurableAudioRoutingState.BreakawayMode))
			return Error(request, "control.audio.routing.mode.invalid", "Audio routing mode must be FOLLOW_VIDEO or BREAKAWAY.");

		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			MediaSourceId? breakawaySourceId = null;
			if (wire.Mode == DurableAudioRoutingState.BreakawayMode)
			{
				if (string.IsNullOrWhiteSpace(wire.BreakawaySourceId))
					return Error(request, "control.audio.routing.source.required", "Breakaway audio routing requires an explicit source.");
				try { breakawaySourceId = new MediaSourceId(Identity.Parse(wire.BreakawaySourceId)); }
				catch (Exception exception) when (exception is FormatException or ArgumentException)
				{
					return Error(request, "control.audio.routing.source.invalid", exception.Message);
				}
				if (!control.Specification.Sources.Any(source => source.SourceId.Value == breakawaySourceId.Value.Value))
					return Error(request, "control.audio.routing.source.unknown", "Breakaway audio source must belong to the authoritative production.");
			}
			else if (!string.IsNullOrWhiteSpace(wire.BreakawaySourceId))
			{
				return Error(request, "control.audio.routing.source.unexpected", "FOLLOW_VIDEO routing must not declare a breakaway source.");
			}

			RuntimeRemoteSnapshot currentRuntime;
			try
			{
				currentRuntime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				return Error(request, "control.audio.routing.snapshot.unavailable", exception.Message);
			}
			if (currentRuntime.AudioProgram.RoutingRevision != wire.ExpectedRoutingRevision)
			{
				return Error(
					request,
					"control.audio.routing.revision_conflict",
					$"Expected audio routing revision {wire.ExpectedRoutingRevision}, current revision is {currentRuntime.AudioProgram.RoutingRevision}.");
			}

			var requested = new DurableAudioRoutingState(wire.Mode, breakawaySourceId);
			var previous = _durableAudioRouting;
			try
			{
				var snapshot = await _runtimeTransport
					.SetAudioRoutingAsync(requested.Mode, requested.BreakawaySourceId, cancellationToken)
					.ConfigureAwait(false);

				if (_showProjectStore is not null && _showProject is not null)
				{
					_showProject = await _showProjectStore
						.UpdateAudioRoutingAsync(control.Specification, requested, cancellationToken)
						.ConfigureAwait(false);
					_showProjectState = "SAVED";
					_showProjectDetail = $"Audio routing is persisted at show-project storage version {_showProject.StorageVersion}.";
				}
				_durableAudioRouting = requested;
				NotifyObservableStateChanged();
				return Success(request, "control.audio.routing.response", ToWire(snapshot));
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
			{
				try
				{
					await _runtimeTransport.SetAudioRoutingAsync(previous.Mode, previous.BreakawaySourceId, cancellationToken).ConfigureAwait(false);
				}
				catch
				{
					_showProjectState = "DEGRADED";
					_showProjectDetail = "Audio routing mutation failed and Runtime rollback could not be confirmed.";
				}
				return Error(request, "control.audio.routing.rejected", exception.Message);
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
		var retained = new RetainedGraphicsAsset(wire.Name.Trim(), wire.Width, wire.Height, wire.RgbaPixels.ToArray());
		return await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.LoadGraphicsOverlayAsync(wire.Name, wire.Width, wire.Height, wire.RgbaPixels, token),
			async (snapshot, _, token) =>
			{
				_graphicsAsset = retained;
				_graphicsOverlayState = snapshot;
				await StoreBitmapAndPersistAsync(retained, snapshot, token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
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
		return await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.ApplyProductionCgTextAsync(definition, token),
			async (_, _, token) =>
			{
				_productionCgText = definition;
				await TryPersistDurableGraphicsAsync(token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<WireEnvelope> SetGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireGraphicsOverlayState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay state payload is required.");
		return await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.SetGraphicsOverlayAsync(wire.Visible, wire.PositionX, wire.PositionY, wire.Scale, token),
			async (snapshot, _, token) =>
			{
				if (_graphicsAsset is not null)
				{
					_graphicsOverlayState = snapshot;
					if (_durableBitmapReference is { } bitmap)
					{
						_durableBitmapReference = bitmap with
						{
							Visible = snapshot.Visible,
							PositionX = snapshot.PositionX,
							PositionY = snapshot.PositionY,
							Scale = snapshot.Scale
						};
					}
				}
				else if (_productionCgText is not null)
				{
					_productionCgText = _productionCgText with { Visible = wire.Visible };
				}
				await TryPersistDurableGraphicsAsync(token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<WireEnvelope> ClearGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		return await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.ClearGraphicsOverlayAsync(token),
			async (_, _, token) =>
			{
				var previousBitmap = _durableBitmapReference;
				_graphicsAsset = null;
				_durableBitmapReference = null;
				_graphicsOverlayState = new RuntimeGraphicsOverlaySnapshot(false, null, 0, 0, false, 0.72, 0.06, 1.0);
				_productionCgText = null;
				_compositingLayers = _compositingLayers
					.Where(layer => layer.LayerId is not "bitmap-graphics" and not "production-cg")
					.Select((layer, order) => layer with { Order = order })
					.ToArray();
				var persisted = await TryPersistDurableGraphicsAsync(token).ConfigureAwait(false);
				if (persisted && _showProjectStore is not null)
					await _showProjectStore.DeleteBitmapAssetAsync(previousBitmap, token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
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
			await SynchronizeCompositingAuthorityAsync(control, layers, cancellationToken).ConfigureAwait(false);
			if (string.Equals(wire.LayerId, "bitmap-graphics", StringComparison.Ordinal))
			{
				_graphicsOverlayState = _graphicsOverlayState with { Visible = wire.Visible };
				if (_durableBitmapReference is { } bitmap)
					_durableBitmapReference = bitmap with { Visible = wire.Visible };
			}
			else if (string.Equals(wire.LayerId, "production-cg", StringComparison.Ordinal) && _productionCgText is not null)
				_productionCgText = _productionCgText with { Visible = wire.Visible };
			await TryPersistDurableGraphicsAsync(cancellationToken).ConfigureAwait(false);
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

	private async ValueTask<WireEnvelope> SetCompositingLayerTransformAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireCompositingLayerTransform>(Wire.JsonOptions)
			?? throw new InvalidDataException("Compositing layer transform payload is required.");
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			var layers = await _runtimeTransport.SetCompositingLayerTransformAsync(
				wire.LayerId,
				wire.PositionX,
				wire.PositionY,
				wire.Scale,
				wire.RotationDegrees,
				wire.AnchorX,
				wire.AnchorY,
				wire.CropLeft,
				wire.CropTop,
				wire.CropRight,
				wire.CropBottom,
				cancellationToken).ConfigureAwait(false);
			_compositingLayers = layers;
			await SynchronizeCompositingAuthorityAsync(control, layers, cancellationToken).ConfigureAwait(false);

			if (string.Equals(wire.LayerId, "bitmap-graphics", StringComparison.Ordinal))
			{
				_graphicsOverlayState = _graphicsOverlayState with
				{
					PositionX = wire.PositionX,
					PositionY = wire.PositionY,
					Scale = wire.Scale
				};
				if (_durableBitmapReference is { } bitmap)
				{
					_durableBitmapReference = bitmap with
					{
						PositionX = wire.PositionX,
						PositionY = wire.PositionY,
						Scale = wire.Scale
					};
				}
			}

			await TryPersistDurableGraphicsAsync(cancellationToken).ConfigureAwait(false);
			NotifyObservableStateChanged();
			return Success(request, "control.compositing.layers.response", layers.Select(ToWire).ToArray());
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
		{
			return Error(request, "control.compositing.transform.rejected", exception.Message);
		}
		finally
		{
			_mutationGate.Release();
		}
	}

	private async ValueTask<WireEnvelope> SetCompositingLayerProcessingAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireCompositingLayerProcessing>(Wire.JsonOptions)
			?? throw new InvalidDataException("Compositing layer processing payload is required.");
		await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = _controlAccessor();
			if (control is null || !control.HasAuthoritativeState)
				return Error(request, "control.not_ready", "ControlHost has no committed authoritative state yet.");
			if (!_runtimeTransport.IsConnected)
				return Error(request, "runtime.unavailable", "RuntimeHost is not connected.");

			PreparedCompositingProcessingNodeState? node = wire.ProcessingNode is null
				? null
				: new PreparedCompositingProcessingNodeState(
					wire.ProcessingNode.NodeId,
					Enum.IsDefined(typeof(PreparedCompositingProcessingNodeKind), wire.ProcessingNode.Kind)
						? (PreparedCompositingProcessingNodeKind)wire.ProcessingNode.Kind
						: throw new InvalidDataException("Compositing processing node kind is invalid."),
					wire.ProcessingNode.Enabled,
					new PreparedColorGradeSettings(
						wire.ProcessingNode.ColorGrade.Brightness,
						wire.ProcessingNode.ColorGrade.Contrast,
						wire.ProcessingNode.ColorGrade.Saturation));

			var layers = await _runtimeTransport
				.SetCompositingLayerProcessingNodeAsync(wire.LayerId, node, cancellationToken)
				.ConfigureAwait(false);
			_compositingLayers = layers;
			await SynchronizeCompositingAuthorityAsync(control, layers, cancellationToken).ConfigureAwait(false);
			await TryPersistDurableGraphicsAsync(cancellationToken).ConfigureAwait(false);
			NotifyObservableStateChanged();
			return Success(request, "control.compositing.layers.response", layers.Select(ToWire).ToArray());
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException)
		{
			return Error(request, "control.compositing.processing.rejected", exception.Message);
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
			await SynchronizeCompositingAuthorityAsync(control, layers, cancellationToken).ConfigureAwait(false);
			await TryPersistDurableGraphicsAsync(cancellationToken).ConfigureAwait(false);
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

	private static ProductionCompositingState ToProductionCompositingState(
		IReadOnlyList<RuntimeCompositingLayerSnapshot> layers)
	{
		ArgumentNullException.ThrowIfNull(layers);
		return new ProductionCompositingState(
			ProductionCompositingState.CurrentVersion,
			layers
				.OrderBy(layer => layer.Order)
				.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
				.Select((layer, order) => new ProductionCompositingLayerState(
					layer.LayerId,
					Enum.IsDefined(typeof(ProductionCompositingLayerKind), layer.Kind)
						? (ProductionCompositingLayerKind)layer.Kind
						: throw new InvalidDataException($"Runtime compositing layer kind '{layer.Kind}' is invalid."),
					order,
					layer.Visible,
					layer.Opacity,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.ContentIdentity,
					layer.RotationDegrees,
					layer.AnchorX,
					layer.AnchorY,
					layer.CropLeft,
					layer.CropTop,
					layer.CropRight,
					layer.CropBottom,
					layer.ProcessingNode is null
						? null
						: new ProductionCompositingProcessingNodeState(
							layer.ProcessingNode.NodeId,
							(ProductionCompositingProcessingNodeKind)(int)layer.ProcessingNode.Kind,
							layer.ProcessingNode.Enabled,
							new ProductionColorGradeSettings(
								layer.ProcessingNode.ColorGrade.Brightness,
								layer.ProcessingNode.ColorGrade.Contrast,
								layer.ProcessingNode.ColorGrade.Saturation))))
				.ToArray());
	}

	private async ValueTask StoreBitmapAndPersistAsync(
		RetainedGraphicsAsset asset,
		RuntimeGraphicsOverlaySnapshot snapshot,
		CancellationToken cancellationToken)
	{
		if (_showProjectStore is null || _showProject is null)
			return;

		var previous = _durableBitmapReference;
		DurableBitmapGraphicsReference? candidate = null;
		try
		{
			candidate = await _showProjectStore.StoreBitmapAssetAsync(
				asset.Name,
				asset.Width,
				asset.Height,
				asset.RgbaPixels,
				snapshot.Visible,
				snapshot.PositionX,
				snapshot.PositionY,
				snapshot.Scale,
				cancellationToken).ConfigureAwait(false);
			_durableBitmapReference = candidate;
			var persisted = await TryPersistDurableGraphicsAsync(cancellationToken).ConfigureAwait(false);
			if (!persisted)
			{
				_durableBitmapReference = previous;
				await _showProjectStore.DeleteBitmapAssetAsync(candidate, CancellationToken.None).ConfigureAwait(false);
				return;
			}

			if (previous is not null && previous.AssetId != candidate.AssetId)
				await _showProjectStore.DeleteBitmapAssetAsync(previous, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			_durableBitmapReference = previous;
			if (candidate is not null)
			{
				try { await _showProjectStore.DeleteBitmapAssetAsync(candidate, CancellationToken.None).ConfigureAwait(false); }
				catch { }
			}
			MarkShowProjectPersistenceStale(exception);
		}
	}

	private async ValueTask<bool> TryPersistDurableGraphicsAsync(CancellationToken cancellationToken)
	{
		if (_showProjectStore is null || _showProject is null)
			return true;

		try
		{
			var control = _controlAccessor()
				?? throw new InvalidOperationException("ControlHost service is unavailable while persisting durable graphics.");
			var compositing = _compositingLayers.Count == 0
				? null
				: ToProductionCompositingState(_compositingLayers);
			_showProject = await _showProjectStore
				.UpdateGraphicsAsync(
					control.Specification,
					new DurableGraphicsState(_durableBitmapReference, _productionCgText, compositing),
					cancellationToken)
				.ConfigureAwait(false);
			SetShowProjectState(
				"SAVED",
				$"Durable show project '{_showProject.Name}' is synchronized at storage version {_showProject.StorageVersion}.");
			return true;
		}
		catch (Exception exception)
		{
			MarkShowProjectPersistenceStale(exception);
			return false;
		}
	}

	private void MarkShowProjectPersistenceStale(Exception exception)
	{
		SetShowProjectState("STALE", $"Live authored state changed but durable show-project persistence failed: {exception.Message}");
		try
		{
			_controlAccessor()?.RecordObservation(
				"persistence",
				"show_project.persistence.stale",
				_showProjectDetail,
				new Failure("show_project.persistence.failed", exception.Message));
		}
		catch
		{
		}
	}

	private void SetShowProjectState(string state, string detail)
	{
		_showProjectState = state;
		_showProjectDetail = detail;
	}

	private static IReadOnlyList<RuntimeCompositingLayerSnapshot> ToRuntimeCompositingLayers(
		ProductionCompositingState? state) =>
		state is null
			? Array.Empty<RuntimeCompositingLayerSnapshot>()
			: Array.AsReadOnly(state.Layers
				.OrderBy(layer => layer.Order)
				.Select(layer => new RuntimeCompositingLayerSnapshot(
					layer.LayerId,
					(int)layer.Kind,
					layer.Order,
					layer.Visible,
					layer.Opacity,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.ContentIdentity,
					layer.RotationDegrees,
					layer.AnchorX,
					layer.AnchorY,
					layer.CropLeft,
					layer.CropTop,
					layer.CropRight,
					layer.CropBottom,
					layer.ProcessingNode is null
						? null
						: new PreparedCompositingProcessingNodeState(
							layer.ProcessingNode.NodeId,
							(PreparedCompositingProcessingNodeKind)(int)layer.ProcessingNode.Kind,
							layer.ProcessingNode.Enabled,
							new PreparedColorGradeSettings(
								layer.ProcessingNode.ColorGrade.Brightness,
								layer.ProcessingNode.ColorGrade.Contrast,
								layer.ProcessingNode.ColorGrade.Saturation))))
				.ToArray());

	private async ValueTask<AuthoritativeProductionState> SynchronizeCompositingAuthorityAsync(
		ControlHostService control,
		IReadOnlyList<RuntimeCompositingLayerSnapshot> layers,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(control);
		ArgumentNullException.ThrowIfNull(layers);

		var authority = control.ConfirmCompositingMutation(ToProductionCompositingState(layers));
		var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		if (runtime.AuthorityStateId == authority.ProductionId.Value &&
			runtime.AuthorityRevision == authority.Revision)
		{
			return authority;
		}

		var execution = control.PrepareCurrentExecution();
		var remote = await _runtimeTransport
			.ApplyExecutionAsync(
				execution.PreparedExecution,
				execution.ProgramSinkId,
				null,
				cancellationToken)
			.ConfigureAwait(false);
		if (!remote.Committed || remote.Commit is null)
		{
			throw new InvalidOperationException(
				remote.Commit?.Failure?.Message ??
				remote.Prepare.Failure?.Message ??
				"Runtime did not confirm the authoritative compositing revision.");
		}

		var confirmed = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		if (confirmed.AuthorityStateId != authority.ProductionId.Value ||
			confirmed.AuthorityRevision != authority.Revision)
		{
			throw new InvalidDataException(
				"Runtime committed compositing state without the current authoritative Control revision.");
		}

		return authority;
	}

	private async ValueTask<WireEnvelope> MutateGraphicsAsync(
		WireEnvelope request,
		Func<CancellationToken, ValueTask<RuntimeGraphicsOverlaySnapshot>> mutation,
		Func<RuntimeGraphicsOverlaySnapshot, RuntimeRemoteSnapshot, CancellationToken, ValueTask> confirmedMutation,
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
				var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				_compositingLayers = runtime.CompositingLayers ?? Array.Empty<RuntimeCompositingLayerSnapshot>();
				await SynchronizeCompositingAuthorityAsync(control, _compositingLayers, cancellationToken).ConfigureAwait(false);
				await confirmedMutation(snapshot, runtime, cancellationToken).ConfigureAwait(false);
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


	private async ValueTask<WireEnvelope> GetMediaAssetCatalogAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaAssetCatalog is null)
			return Error(request, "control.media_asset_catalog.unavailable", "Media asset catalogue service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaAssetCatalogRequest>(Wire.JsonOptions)
			?? new WireMediaAssetCatalogRequest(0, 0);
		var offset = Math.Max(0, wire.Offset);
		var limit = wire.Limit <= 0 ? 256 : Math.Min(wire.Limit, 256);
		try
		{
			var snapshot = await _mediaAssetCatalog.GetSnapshotAsync(refreshAvailability: offset == 0, cancellationToken).ConfigureAwait(false);
			var page = new MediaAssetCatalogSnapshot(
				snapshot.Version,
				snapshot.Revision,
				snapshot.Assets.Skip(offset).Take(limit).ToArray());
			return Success(
				request,
				"control.media_asset_catalog.snapshot.response",
				new WireMediaAssetCatalogPage(
					page.Version.ToString(),
					page.Revision,
					snapshot.Assets.Count,
					page.Assets.Select(ToWire).ToArray()));
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
		{
			return Error(request, "control.media_asset_catalog.read_failed", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> ImportMediaAssetsAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaAssetCatalog is null)
			return Error(request, "control.media_asset_catalog.unavailable", "Media asset catalogue service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaAssetImport>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media asset import payload is required.");
		try
		{
			var result = await _mediaAssetCatalog.ImportAsync(wire.SourceLocations, cancellationToken).ConfigureAwait(false);
			NotifyObservableStateChanged();
			return Success(request, "control.media_asset_catalog.mutation.response", ToWireMutation(result));
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
		{
			return Error(request, "control.media_asset_catalog.import_failed", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> RelinkMediaAssetAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaAssetCatalog is null)
			return Error(request, "control.media_asset_catalog.unavailable", "Media asset catalogue service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaAssetRelink>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media asset relink payload is required.");
		try
		{
			var result = await _mediaAssetCatalog.RelinkAsync(
				new MediaAssetId(Identity.Parse(wire.AssetId)),
				wire.SourceLocation,
				cancellationToken).ConfigureAwait(false);
			NotifyObservableStateChanged();
			return Success(request, "control.media_asset_catalog.mutation.response", ToWireMutation(result));
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or ArgumentException or FormatException or UnauthorizedAccessException)
		{
			return Error(request, "control.media_asset_catalog.relink_failed", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> RemoveMediaAssetAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaAssetCatalog is null)
			return Error(request, "control.media_asset_catalog.unavailable", "Media asset catalogue service is not configured.");
		var wire = request.Payload.Deserialize<WireMediaAssetRemove>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media asset removal payload is required.");
		try
		{
			var result = await _mediaAssetCatalog.RemoveAsync(
				new MediaAssetId(Identity.Parse(wire.AssetId)),
				cancellationToken).ConfigureAwait(false);
			NotifyObservableStateChanged();
			return Success(request, "control.media_asset_catalog.mutation.response", ToWireMutation(result));
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or ArgumentException or FormatException)
		{
			return Error(request, "control.media_asset_catalog.remove_failed", exception.Message);
		}
	}

	private async ValueTask<WireEnvelope> RefreshMediaAssetAvailabilityAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		if (_mediaAssetCatalog is null)
			return Error(request, "control.media_asset_catalog.unavailable", "Media asset catalogue service is not configured.");
		try
		{
			var snapshot = await _mediaAssetCatalog.RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);
			NotifyObservableStateChanged();
			return Success(
				request,
				"control.media_asset_catalog.refresh.response",
				new WireMediaAssetCatalogRefresh(snapshot.Revision));
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
		{
			return Error(request, "control.media_asset_catalog.refresh_failed", exception.Message);
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
				wire.Path,
				string.IsNullOrWhiteSpace(wire.AssetId)
					? null
					: new MediaAssetId(Identity.Parse(wire.AssetId))),
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

	private ValueTask<WireEnvelope> GetShowControlSnapshotAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		RunShowControlAsync(request, coordinator => coordinator.GetSnapshotAsync(cancellationToken), cancellationToken);

	private ValueTask<WireEnvelope> SaveShowControlCueListAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireShowControlCueList>(Wire.JsonOptions)
			?? throw new InvalidDataException("Show-control cue-list payload is required.");
		var cueList = ShowControlCanonicalSerializer.Deserialize(wire.CueListJson);
		return RunShowControlAsync(request, coordinator => coordinator.SaveCueListAsync(cueList, cancellationToken), cancellationToken);
	}

	private ValueTask<WireEnvelope> SelectShowControlCueListAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireShowControlSelection>(Wire.JsonOptions)
			?? throw new InvalidDataException("Show-control cue-list selection payload is required.");
		var cueListId = new ShowControlCueListId(Identity.Parse(wire.CueListId));
		return RunShowControlAsync(request, coordinator => coordinator.SelectCueListAsync(cueListId, cancellationToken), cancellationToken);
	}

	private ValueTask<WireEnvelope> ArmShowControlAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		RunShowControlAsync(request, coordinator => coordinator.ArmAsync(cancellationToken), cancellationToken);

	private ValueTask<WireEnvelope> GoShowControlAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		RunShowControlAsync(request, coordinator => coordinator.GoAsync(cancellationToken), cancellationToken);

	private ValueTask<WireEnvelope> CancelShowControlAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		RunShowControlAsync(request, coordinator => coordinator.CancelAsync(cancellationToken), cancellationToken);

	private ValueTask<WireEnvelope> AcknowledgeShowControlRecoveryAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireShowControlRecovery>(Wire.JsonOptions)
			?? throw new InvalidDataException("Show-control recovery payload is required.");
		return RunShowControlAsync(request, coordinator => coordinator.AcknowledgeRecoveryAsync(wire.Resume, cancellationToken), cancellationToken);
	}

	private async ValueTask<WireEnvelope> RunShowControlAsync(
		WireEnvelope request,
		Func<ShowControlCoordinator, ValueTask<ShowControlWorkspaceSnapshot>> operation,
		CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (_showControl is null)
			return Error(request, "control.show_control.unavailable", "Show-control persistence and coordination are not configured.");

		try
		{
			var snapshot = await operation(_showControl).ConfigureAwait(false);
			return Success(request, "control.show_control.snapshot.response", ToWire(snapshot));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException or InvalidDataException or IOException or KeyNotFoundException)
		{
			return Error(request, "control.show_control.rejected", exception.Message);
		}
	}

	private async ValueTask<Failure?> ExecuteShowControlActionAsync(
		ShowControlAction action,
		CancellationToken cancellationToken)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return new Failure("control.not_ready", "ControlHost has no committed authoritative state yet.");

		switch (action.Kind)
		{
			case ShowControlActionKind.ActivateScene:
				return await ExecuteShowControlMutationAsync(
					MutationKind.ActivateScene,
					null,
					action.SceneId,
					null,
					cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.SetPreview:
				return await ExecuteShowControlMutationAsync(
					MutationKind.SelectPreview,
					action.SourceId,
					null,
					null,
					cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.Cut:
				return await ExecuteShowControlMutationAsync(
					MutationKind.Cut,
					control.State.Routing.PreviewSourceId.ToString(),
					null,
					null,
					cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.Dissolve:
				return await ExecuteShowControlMutationAsync(
					MutationKind.Dissolve,
					control.State.Routing.PreviewSourceId.ToString(),
					null,
					action.DurationFrames,
					cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.JumpMediaCue:
				return await ExecuteShowControlMediaCueJumpAsync(action, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.MediaOpen:
				return await ExecuteShowControlMediaOpenAsync(action, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.MediaPlay:
				return await ExecuteShowControlMediaTransportAsync(action, MediaTransportCommandKind.Play, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.MediaPause:
				return await ExecuteShowControlMediaTransportAsync(action, MediaTransportCommandKind.Pause, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.MediaStop:
				return await ExecuteShowControlMediaTransportAsync(action, MediaTransportCommandKind.Stop, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.SetLayerVisibility:
				return await ExecuteShowControlLayerVisibilityAsync(action, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.SetAudioRouting:
				return await ExecuteShowControlAudioRoutingAsync(action, cancellationToken).ConfigureAwait(false);
			case ShowControlActionKind.StartRecording:
			{
				var response = await StartRecordingAsync(
					InternalRequest("control.recording.start", new WireRecordingStart(action.RecordingDestinationDirectory!, action.RecordingFileName!)),
					cancellationToken).ConfigureAwait(false);
				return ReadRecordingFailure(response);
			}
			case ShowControlActionKind.StopRecording:
			{
				var response = await StopRecordingAsync(
					InternalRequest("control.recording.stop", new { }),
					cancellationToken).ConfigureAwait(false);
				return ReadRecordingFailure(response);
			}
			case ShowControlActionKind.WaitFrames:
				return new Failure("control.show_control.wait.dispatch_invalid", "Frame waits are owned by the show-control coordinator and must not enter the production action executor.");
			default:
				return new Failure("control.show_control.action.unsupported", $"Show-control action '{action.Kind}' is not supported.");
		}
	}

	private async ValueTask<Failure?> ExecuteShowControlMutationAsync(
		MutationKind kind,
		string? sourceId,
		string? sceneId,
		uint? durationFrames,
		CancellationToken cancellationToken)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return new Failure("control.not_ready", "ControlHost has no committed authoritative state yet.");
		var state = control.State;
		var command = new WireControlCommand(
			ControlContractVersion.Current.ToString(),
			CommandId.New().ToString(),
			state.ProductionId.ToString(),
			state.Revision.Value,
			sourceId,
			durationFrames,
			sceneId);
		var response = await MutateAsync(
			InternalRequest("control.show_control.production_action", command),
			kind,
			cancellationToken).ConfigureAwait(false);
		return ReadMutationFailure(response);
	}

	private async ValueTask<Failure?> ExecuteShowControlMediaOpenAsync(
		ShowControlAction action,
		CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return new Failure("control.media_deck.unavailable", "Media-deck control service is not configured.");
		if (_mediaAssetCatalog is null)
			return new Failure("control.media_asset_catalog.unavailable", "Media asset catalogue service is not configured.");

		var assetId = new MediaAssetId(Identity.Parse(action.MediaAssetId!));
		var sourceId = new MediaSourceId(Identity.Parse(action.SourceId!));
		var catalog = await _mediaAssetCatalog.GetSnapshotAsync(refreshAvailability: true, cancellationToken).ConfigureAwait(false);
		var asset = catalog.Assets.FirstOrDefault(candidate => candidate.AssetId == assetId);
		if (asset is null)
			return new Failure("control.rundown.asset_missing", $"Rundown media asset '{assetId}' is not present in the persistent catalogue.");
		if (asset.Availability != MediaAssetAvailability.Online)
			return new Failure("control.rundown.asset_offline", $"Rundown media asset '{assetId}' is not online.");

		var snapshot = await _mediaDeck.OpenAsync(
			new MediaDeckOpenRequest(
				MediaContractVersion.Current,
				sourceId,
				asset.SourceLocation,
				asset.AssetId),
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return MediaDeckFailure(snapshot);
	}

	private async ValueTask<Failure?> ExecuteShowControlAudioRoutingAsync(
		ShowControlAction action,
		CancellationToken cancellationToken)
	{
		if (!_runtimeTransport.IsConnected)
			return new Failure("runtime.unavailable", "RuntimeHost is not connected.");

		var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var response = await SetAudioRoutingAsync(
			InternalRequest(
				"control.audio.routing.set",
				new WireAudioRoutingState(
					action.AudioRoutingMode!.Value,
					action.SourceId,
					runtime.AudioProgram.RoutingRevision)),
			cancellationToken).ConfigureAwait(false);
		return ReadErrorFailure(response);
	}

	private async ValueTask<Failure?> ExecuteShowControlMediaCueJumpAsync(
		ShowControlAction action,
		CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return new Failure("control.media_deck.unavailable", "Media-deck control service is not configured.");
		var assetId = new MediaAssetId(Identity.Parse(action.MediaAssetId!));
		var cueId = new MediaCuePointId(Identity.Parse(action.MediaCuePointId!));
		var current = await _mediaDeck.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		if (current.Probe is null || current.Transport is null || current.Markers is null)
			return new Failure("control.media_deck.unloaded", "Media deck has no loaded asset.");
		if (current.Probe.AssetId != assetId)
			return new Failure("control.media_deck.asset_mismatch", "Show-control media action does not match the loaded deck asset.");
		var cue = current.Markers.CuePoints.FirstOrDefault(candidate => candidate.Id == cueId);
		if (cue is null)
			return new Failure("control.media_deck.cue_not_found", $"Media cue '{cueId}' does not exist in the loaded asset.");
		var result = await _mediaDeck.ApplyTransportAsync(
			new MediaTransportCommand(MediaContractVersion.Current, assetId, MediaTransportCommandKind.Seek, cue.PositionFrame),
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return MediaDeckFailure(result);
	}

	private async ValueTask<Failure?> ExecuteShowControlMediaTransportAsync(
		ShowControlAction action,
		MediaTransportCommandKind kind,
		CancellationToken cancellationToken)
	{
		if (_mediaDeck is null)
			return new Failure("control.media_deck.unavailable", "Media-deck control service is not configured.");
		var assetId = new MediaAssetId(Identity.Parse(action.MediaAssetId!));
		var result = await _mediaDeck.ApplyTransportAsync(
			new MediaTransportCommand(MediaContractVersion.Current, assetId, kind),
			cancellationToken).ConfigureAwait(false);
		NotifyObservableStateChanged();
		return MediaDeckFailure(result);
	}

	private async ValueTask<Failure?> ExecuteShowControlLayerVisibilityAsync(
		ShowControlAction action,
		CancellationToken cancellationToken)
	{
		var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var layer = runtime.CompositingLayers?.FirstOrDefault(candidate =>
			string.Equals(candidate.LayerId, action.LayerId, StringComparison.Ordinal));
		if (layer is null)
			return new Failure("control.compositing.layer.unknown", $"Compositing layer '{action.LayerId}' is unavailable.");
		var response = await SetCompositingLayerStateAsync(
			InternalRequest(
				"control.compositing.layer.set",
				new WireCompositingLayerState(layer.LayerId, action.Visible!.Value, layer.Opacity)),
			cancellationToken).ConfigureAwait(false);
		return ReadErrorFailure(response);
	}

	private async ValueTask<ShowControlFrameObservation> ObserveShowControlFrameAsync(CancellationToken cancellationToken)
	{
		if (!_runtimeTransport.IsConnected)
			throw new InvalidOperationException("RuntimeHost is not connected.");
		var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var frameBudget = runtime.Performance?.FrameBudget ?? TimeSpan.Zero;
		return new ShowControlFrameObservation(runtime.HostInstanceId, runtime.NextSequenceNumber, frameBudget);
	}

	private WireEnvelope InternalRequest(string messageType, object payload)
	{
		var requestId = Identity.New().ToString();
		return Wire.Create(messageType, requestId, requestId, _hostInstanceId, StateVersion, NextSequence(), payload);
	}

	private static Failure? ReadMutationFailure(WireEnvelope response)
	{
		var transportFailure = ReadErrorFailure(response);
		if (transportFailure is not null)
			return transportFailure;
		var wire = response.Payload.Deserialize<WireMutationResponse>(Wire.JsonOptions)
			?? throw new InvalidDataException("Internal Control mutation response is invalid.");
		return wire.Accepted
			? null
			: wire.Failure is null
				? new Failure("control.command.rejected", "Control command was rejected.")
				: new Failure(wire.Failure.Code, wire.Failure.Message);
	}

	private static Failure? ReadRecordingFailure(WireEnvelope response)
	{
		var transportFailure = ReadErrorFailure(response);
		if (transportFailure is not null)
			return transportFailure;
		var wire = response.Payload.Deserialize<WireRecordingCommandResult>(Wire.JsonOptions)
			?? throw new InvalidDataException("Internal recording response is invalid.");
		if (wire.Succeeded)
			return null;
		return wire.Failure is null
			? new Failure("control.recording.rejected", "Recording command was rejected.")
			: new Failure(wire.Failure.Code, wire.Failure.Message);
	}

	private static Failure? ReadErrorFailure(WireEnvelope response)
	{
		if (!string.Equals(response.MessageType, "error", StringComparison.Ordinal))
			return null;
		var wire = response.Payload.Deserialize<WireFailure>(Wire.JsonOptions);
		return wire is null
			? new Failure("control.show_control.internal_error", "Internal Control command failed without failure detail.")
			: new Failure(wire.Code, wire.Message);
	}

	private static Failure? MediaDeckFailure(MediaDeckSnapshot snapshot) =>
		snapshot.Failure ??
		(snapshot.State == MediaDeckState.Error
			? new Failure("control.media_deck.failed", "Media-deck command failed.")
			: null);

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
		WireShowControlWorkspace? showControl = null;
		if (_showControl is not null)
		{
			try { showControl = ToWire(await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)); }
			catch { showControl = null; }
		}

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
				.ToArray(),
			showControl,
			new WireShowProject(
				_showProject?.ProjectId.ToString() ?? "unavailable",
				_showProject?.Name ?? "Unavailable",
				_showProjectState,
				_showProjectDetail));
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

			if (staged.Execution.PreparedExecution.CompositingState is { } preparedCompositing)
			{
				_compositingLayers = preparedCompositing.Layers
					.Select(layer => new RuntimeCompositingLayerSnapshot(
						layer.LayerId,
						(int)layer.Kind,
						layer.Order,
						layer.Visible,
						layer.Opacity,
						layer.PositionX,
						layer.PositionY,
						layer.Scale,
						layer.ContentIdentity,
						layer.RotationDegrees,
						layer.AnchorX,
						layer.AnchorY,
						layer.CropLeft,
						layer.CropTop,
						layer.CropRight,
						layer.CropBottom,
						layer.ProcessingNode))
					.ToArray();
			}

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
		snapshot.Health,
		snapshot.RoutingMode,
		snapshot.RoutingRevision,
		snapshot.ActiveAudioSourceId?.ToString());

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
		snapshot.ContentIdentity,
		snapshot.RotationDegrees,
		snapshot.AnchorX,
		snapshot.AnchorY,
		snapshot.CropLeft,
		snapshot.CropTop,
		snapshot.CropRight,
		snapshot.CropBottom,
		snapshot.ProcessingNode is null
			? null
			: new WireProcessingNode(
				snapshot.ProcessingNode.NodeId,
				(int)snapshot.ProcessingNode.Kind,
				snapshot.ProcessingNode.Enabled,
				new WireColorGrade(
					snapshot.ProcessingNode.ColorGrade.Brightness,
					snapshot.ProcessingNode.ColorGrade.Contrast,
					snapshot.ProcessingNode.ColorGrade.Saturation)));

	private static WireGraphicsOverlay ToWire(RuntimeGraphicsOverlaySnapshot snapshot) => new(
		snapshot.AssetLoaded,
		snapshot.AssetName,
		snapshot.AssetWidth,
		snapshot.AssetHeight,
		snapshot.Visible,
		snapshot.PositionX,
		snapshot.PositionY,
		snapshot.Scale);


	private static WireMediaAssetDescriptor ToWire(MediaAssetDescriptor asset) => new(
		asset.AssetId.ToString(),
		(int)asset.Origin,
		asset.SourceLocation,
		asset.DisplayName,
		(int)asset.Container,
		(int)asset.VideoCodec,
		(int)asset.AudioCodec,
		asset.VideoFormat.Width,
		asset.VideoFormat.Height,
		asset.VideoFormat.FrameRate.ToString(),
		(int)asset.VideoFormat.PixelFormat,
		(int)asset.VideoFormat.ScanMode,
		asset.AudioFormat.SampleRate,
		(int)asset.AudioFormat.ChannelLayout,
		(int)asset.AudioFormat.SampleFormat,
		asset.AudioFormat.ChannelCount,
		asset.Duration.Ticks,
		asset.LengthBytes,
		asset.FingerprintSha256,
		asset.ImportedAt.ToString(),
		asset.UpdatedAt.ToString(),
		(int)asset.Availability);

	private static WireMediaAssetCatalogMutationResult ToWireMutation(MediaAssetCatalogMutationResult result) => new(
		result.Snapshot.Revision,
		result.Items.Select(item => new WireMediaAssetMutationItem(
			item.SourceLocation,
			item.AssetId?.ToString(),
			(int)item.Disposition,
			item.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null)).ToArray());

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

	private static WireShowControlWorkspace ToWire(ShowControlWorkspaceSnapshot snapshot) => new(
		snapshot.CueLists.Select(ShowControlCanonicalSerializer.Serialize).ToArray(),
		snapshot.SelectedCueListId?.ToString(),
		new WireShowControlExecution(
			snapshot.Execution.Version.ToString(),
			snapshot.Execution.ExecutionId?.ToString(),
			snapshot.Execution.CueListId?.ToString(),
			(int)snapshot.Execution.State,
			snapshot.Execution.CueIndex,
			snapshot.Execution.ActionIndex,
			snapshot.Execution.CurrentCueId?.ToString(),
			snapshot.Execution.CurrentActionId?.ToString(),
			snapshot.Execution.ExecutionRevision,
			snapshot.Execution.WaitTargetFrameSequence,
			snapshot.Execution.RuntimeHostInstanceId,
			snapshot.Execution.RequiresAcknowledgement,
			snapshot.Execution.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null));

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
					layer.ContentIdentity,
					layer.RotationDegrees,
					layer.AnchorX,
					layer.AnchorY,
					layer.CropLeft,
					layer.CropTop,
					layer.CropRight,
					layer.CropBottom,
					layer.ProcessingNode is null
						? null
						: new WireProcessingNode(
							layer.ProcessingNode.NodeId,
							(int)layer.ProcessingNode.Kind,
							layer.ProcessingNode.Enabled,
							new WireColorGrade(
								layer.ProcessingNode.ColorGrade.Brightness,
								layer.ProcessingNode.ColorGrade.Contrast,
								layer.ProcessingNode.ColorGrade.Saturation)))).ToArray());

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
	private sealed record WireCompositingLayerTransform(string LayerId, double PositionX, double PositionY, double Scale, double RotationDegrees, double AnchorX, double AnchorY, double CropLeft, double CropTop, double CropRight, double CropBottom);
	private sealed record WireColorGrade(double Brightness, double Contrast, double Saturation);
	private sealed record WireProcessingNode(string NodeId, int Kind, bool Enabled, WireColorGrade ColorGrade);
	private sealed record WireCompositingLayerProcessing(string LayerId, WireProcessingNode? ProcessingNode);
	private sealed record WireCompositingLayerOrder(string[] LayerIds);
	private sealed record WireCompositingLayer(string LayerId, int Kind, int Order, bool Visible, byte Opacity, double PositionX, double PositionY, double Scale, string ContentIdentity, double RotationDegrees = 0, double AnchorX = 0, double AnchorY = 0, double CropLeft = 0, double CropTop = 0, double CropRight = 0, double CropBottom = 0, WireProcessingNode? ProcessingNode = null);
	private sealed record WireCompositingState(string Version, WireCompositingLayer[] Layers);
	private sealed record WireGraphicsOverlay(bool AssetLoaded, string? AssetName, uint AssetWidth, uint AssetHeight, bool Visible, double PositionX, double PositionY, double Scale)
	{
		public static WireGraphicsOverlay Empty { get; } = new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
	}
	private sealed record WireRundownSave(string RundownJson, ulong ExpectedStorageVersion);
	private sealed record WireRundownItemRequest(string ItemId);
	private sealed record WireRundownRecoveryAcknowledge(bool Resume);
	private sealed record WireRundownWorkspace(
		string? RundownJson,
		int State,
		string? RundownId,
		string? SelectedItemId,
		string? PreparedItemId,
		string? CurrentItemId,
		string? NextItemId,
		ulong Revision,
		string? CausalActionId,
		bool AutoAdvanceArmed,
		bool RequiresAcknowledgement,
		string? FailureCode,
		string? FailureMessage,
		ulong StorageVersion);
	private sealed record WireAudioInputState(string SourceId, double Gain, bool Muted);
	private sealed record WireAudioRoutingState(int Mode, string? BreakawaySourceId, ulong ExpectedRoutingRevision);
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
	private sealed record WireAudioProgram(string ActiveVideoSourceId, string ActiveStreamId, double Gain, bool Muted, double LeftPeak, double RightPeak, double MasterPeak, bool Clipping, string Health, int RoutingMode = 1, ulong RoutingRevision = 0, string? ActiveAudioSourceId = null)
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
	private sealed record WireOperatorSnapshot(WireProductionState Production, WireSource[] Sources, string RuntimeStatus, string TimingStatus, string InputStatus, string AIStatus, string RecordingStatus, bool VisualLayerEnabled, double AudioPeakLevel, WireGraphicsOverlay GraphicsOverlay, WireAudioInput[] AudioInputs, WireAudioProgram AudioProgram, WireRecordingSnapshot Recording, WireHealthSnapshot Health, WireAIShowcase AIShowcase, WireMediaDeckSnapshot MediaDeck, ulong StateVersion, WireProductionCgTextSnapshot? ProductionCgText = null, WireScene[]? Scenes = null, WireOutputRole[]? OutputRoles = null, WireCompositingLayer[]? CompositingLayers = null, WireShowControlWorkspace? ShowControl = null, WireShowProject? ShowProject = null);
	private sealed record WireShowProject(string ProjectId, string Name, string State, string Detail);
	private sealed record WireShowControlCueList(string CueListJson);
	private sealed record WireShowControlSelection(string CueListId);
	private sealed record WireShowControlRecovery(bool Resume);
	private sealed record WireShowControlExecution(
		string Version,
		string? ExecutionId,
		string? CueListId,
		int State,
		int? CueIndex,
		int? ActionIndex,
		string? CurrentCueId,
		string? CurrentActionId,
		ulong ExecutionRevision,
		ulong? WaitTargetFrameSequence,
		string? RuntimeHostInstanceId,
		bool RequiresAcknowledgement,
		WireFailure? Failure);
	private sealed record WireShowControlWorkspace(string[] CueLists, string? SelectedCueListId, WireShowControlExecution Execution);
	private sealed record WireMediaAssetCatalogRequest(int Offset, int Limit);
	private sealed record WireMediaAssetImport(string[] SourceLocations);
	private sealed record WireMediaAssetRelink(string AssetId, string SourceLocation);
	private sealed record WireMediaAssetRemove(string AssetId);
	private sealed record WireMediaAssetDescriptor(
		string AssetId,
		int Origin,
		string SourceLocation,
		string DisplayName,
		int Container,
		int VideoCodec,
		int AudioCodec,
		uint Width,
		uint Height,
		string FrameRate,
		int PixelFormat,
		int ScanMode,
		uint AudioSampleRate,
		int AudioChannelLayout,
		int AudioSampleFormat,
		uint AudioChannelCount,
		long DurationTicks,
		long LengthBytes,
		string FingerprintSha256,
		string ImportedAt,
		string UpdatedAt,
		int Availability);
	private sealed record WireMediaAssetCatalogPage(string Version, ulong Revision, int TotalCount, WireMediaAssetDescriptor[] Assets);
	private sealed record WireMediaAssetMutationItem(string SourceLocation, string? AssetId, int Disposition, WireFailure? Failure);
	private sealed record WireMediaAssetCatalogMutationResult(ulong Revision, WireMediaAssetMutationItem[] Items);
	private sealed record WireMediaAssetCatalogRefresh(ulong Revision);
	private sealed record WireMediaDeckOpen(string Version, string SourceId, string Path, string? AssetId);
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
