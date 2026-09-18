// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
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
	private readonly string _hostInstanceId = Identity.New().ToString();
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
			"control.graphics.overlay.load" => await LoadGraphicsOverlayAsync(request, cancellationToken).ConfigureAwait(false),
			"control.graphics.overlay.set" => await SetGraphicsOverlayAsync(request, cancellationToken).ConfigureAwait(false),
			"control.graphics.overlay.clear" => await ClearGraphicsOverlayAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.snapshot.get" => await GetMediaDeckSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.open" => await OpenMediaDeckAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.transport" => await ApplyMediaDeckTransportAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.marker" => await ApplyMediaDeckMarkerAsync(request, cancellationToken).ConfigureAwait(false),
			"control.media_deck.close" => await CloseMediaDeckAsync(request, cancellationToken).ConfigureAwait(false),
			_ => Error(request, "ipc.message.unknown", $"Unknown ControlHost message type '{request.MessageType}'.")
		};
	}

	private async ValueTask<WireEnvelope> LoadGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireGraphicsAsset>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay asset payload is required.");
		return await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.LoadGraphicsOverlayAsync(wire.Name, wire.Width, wire.Height, wire.RgbaPixels, token),
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<WireEnvelope> SetGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireGraphicsOverlayState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay state payload is required.");
		return await MutateGraphicsAsync(
			request,
			token => _runtimeTransport.SetGraphicsOverlayAsync(wire.Visible, wire.PositionX, wire.PositionY, wire.Scale, token),
			cancellationToken).ConfigureAwait(false);
	}

	private ValueTask<WireEnvelope> ClearGraphicsOverlayAsync(WireEnvelope request, CancellationToken cancellationToken) =>
		MutateGraphicsAsync(
			request,
			token => _runtimeTransport.ClearGraphicsOverlayAsync(token),
			cancellationToken);

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
				wire.TargetFrame),
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

		RuntimeRemoteSnapshot? runtime = null;
		try { runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false); }
		catch { runtime = null; }

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
		var payload = new WireOperatorSnapshot(
			ToWire(state),
			sources,
			runtime is null ? "DEGRADED" : "READY",
			runtime is null ? "UNKNOWN" : runtime.TimingHealth.ToString(),
			runtime is null ? "UNKNOWN" : "VALID",
			"AVAILABLE",
			"IDLE",
			runtime?.GraphicsOverlay.Visible == true,
			0.0,
			runtime is null ? WireGraphicsOverlay.Empty : ToWire(runtime.GraphicsOverlay),
			StateVersion);
		return Success(request, "control.snapshot.response", payload);
	}


	private static WireSource ToWireSource(
		ProductionSourceSpecification source,
		RuntimeRemoteSnapshot? runtime,
		MediaDeckSnapshot? mediaDeck)
	{
		var mediaSourceId = new MediaSourceId(source.SourceId.Value);
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
				transport.Position.Remaining.Ticks,
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
			snapshot.Transport.Failure is { } transportFailure ? new WireFailure(transportFailure.Code, transportFailure.Message) : null),
		snapshot.Markers is null ? null : new WireMediaMarkerSnapshot(
			snapshot.Markers.Version.ToString(),
			snapshot.Markers.AssetId.ToString(),
			snapshot.Markers.TotalFrames,
			snapshot.Markers.InPointFrame,
			snapshot.Markers.OutPointFrame,
			snapshot.Markers.CuePoints.Select(cue => new WireCuePoint(cue.Id.ToString(), cue.Name, cue.PositionFrame)).ToArray()),
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

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
	private sealed record WireSource(string Id, string Name, string Type, string Format, string Health, string MediaState, long? RemainingTicks, string? MediaFileName);
	private sealed record WireProductionState(string Version, string ProductionId, ulong Revision, string PreviewSourceId, string ProgramSourceId);
	private sealed record WireGraphicsAsset(string Name, uint Width, uint Height, byte[] RgbaPixels);
	private sealed record WireGraphicsOverlayState(bool Visible, double PositionX, double PositionY, double Scale);
	private sealed record WireGraphicsOverlay(bool AssetLoaded, string? AssetName, uint AssetWidth, uint AssetHeight, bool Visible, double PositionX, double PositionY, double Scale)
	{
		public static WireGraphicsOverlay Empty { get; } = new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
	}
	private sealed record WireOperatorSnapshot(WireProductionState Production, WireSource[] Sources, string RuntimeStatus, string TimingStatus, string InputStatus, string AIStatus, string RecordingStatus, bool VisualLayerEnabled, double AudioPeakLevel, WireGraphicsOverlay GraphicsOverlay, ulong StateVersion);
	private sealed record WireMediaDeckOpen(string Version, string SourceId, string Path);
	private sealed record WireMediaTransportCommand(string Version, string AssetId, int Kind, long? TargetFrame);
	private sealed record WireMediaMarkerCommand(string Version, string AssetId, int Kind, long? PositionFrame, string? CuePointId, string? Name);
	private sealed record WireLocalMediaProbe(string Version, string AssetId, string SourceId, string FileName, int Container, int VideoCodec, int AudioCodec, uint Width, uint Height, string FrameRate, long DurationTicks);
	private sealed record WireMediaTransportSnapshot(string Version, string AssetId, string SourceId, int State, long CurrentFrame, long TotalFrames, long PositionTicks, long DurationTicks, long RemainingTicks, string FrameRate, WireFailure? Failure);
	private sealed record WireCuePoint(string Id, string Name, long PositionFrame);
	private sealed record WireMediaMarkerSnapshot(string Version, string AssetId, long TotalFrames, long? InPointFrame, long? OutPointFrame, WireCuePoint[] CuePoints);
	private sealed record WireMediaDeckSnapshot(int State, string? SourceId, WireLocalMediaProbe? Probe, WireMediaTransportSnapshot? Transport, WireMediaMarkerSnapshot? Markers, WireFailure? Failure);
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
