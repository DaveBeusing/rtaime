// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public sealed class RuntimeHostIpcServer : IAsyncDisposable
{
	private const string ProtocolVersion = "1.0";
	private const int MaxFrameBytes = 1024 * 1024;
	private readonly string _endpoint;
	private readonly Func<V1RuntimeHostService?> _runtimeAccessor;
	private readonly Func<LocalMediaDeckRuntimeService?> _mediaDeckAccessor;
	private readonly Func<RuntimeAIShowcaseService?> _aiShowcaseAccessor;
	private readonly CancellationTokenSource _stop = new();
	private readonly BoundedRequestCache _requestCache = new(256);
	private readonly string _hostInstanceId = Identity.New().ToString();
	private Task? _acceptLoop;
	private ulong _stateVersion = 1;
	private ulong _sequence;
	private Identity? _committedAuthorityStateId;
	private Revision? _committedAuthorityRevision;

	public RuntimeHostIpcServer(
		string endpoint,
		Func<V1RuntimeHostService?> runtimeAccessor,
		Func<LocalMediaDeckRuntimeService?>? mediaDeckAccessor = null,
		Func<RuntimeAIShowcaseService?>? aiShowcaseAccessor = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("RuntimeHost IPC endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_runtimeAccessor = runtimeAccessor ?? throw new ArgumentNullException(nameof(runtimeAccessor));
		_mediaDeckAccessor = mediaDeckAccessor ?? (() => null);
		_aiShowcaseAccessor = aiShowcaseAccessor ?? (() => null);
	}

	public string Endpoint => _endpoint;
	public string HostInstanceId => _hostInstanceId;
	public bool Running => _acceptLoop is { IsCompleted: false };

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_acceptLoop is not null) throw new InvalidOperationException("RuntimeHost IPC server has already been started.");
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
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.protocol.unsupported", "RuntimeHost supports IPC protocol 1.0 only.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!string.Equals(hello.Role, "ControlHost", StringComparison.Ordinal))
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.role.invalid", "RuntimeHost accepts the ControlHost role on this endpoint.", cancellationToken).ConfigureAwait(false);
					return;
				}
				if (!hello.ContractVersions.TryGetValue("runtime", out var runtimeVersion) || runtimeVersion != RuntimeContractVersion.Current.ToString() ||
					!hello.ContractVersions.TryGetValue("provider", out var providerVersion) || providerVersion != ProviderContractVersion.Current.ToString())
				{
					await WriteErrorAsync(pipe, helloEnvelope, "ipc.contract.unsupported", "Runtime or Provider contract version is incompatible.", cancellationToken).ConfigureAwait(false);
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
							"RuntimeHost",
							_hostInstanceId,
							new Dictionary<string, string>(StringComparer.Ordinal)
							{
								["runtime"] = RuntimeContractVersion.Current.ToString(),
								["provider"] = ProviderContractVersion.Current.ToString()
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

	private ValueTask<WireEnvelope> DispatchAsync(WireEnvelope request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var runtime = _runtimeAccessor();
		if (runtime is null)
			return ValueTask.FromResult(Error(request, "runtime.unavailable", "RuntimeHost service is not available."));

		try
		{
			return request.MessageType switch
			{
				"runtime.ping" => ValueTask.FromResult(Success(request, "runtime.ping.response", new { status = "ready" })),
				"runtime.providers.get" => ValueTask.FromResult(Success(request, "runtime.providers.response", ProviderDescriptors(runtime).Select(ToWire).ToArray())),
				"runtime.snapshot.get" => ValueTask.FromResult(Success(request, "runtime.snapshot.response", ToWire(runtime.Snapshot, runtime.Format, _aiShowcaseAccessor()?.Snapshot ?? RuntimeAIShowcaseSnapshot.Disabled))),
				"runtime.ai_showcase.set" => ValueTask.FromResult(SetAIShowcase(request)),
				"runtime.execution.apply" => ValueTask.FromResult(ApplyExecution(request, runtime)),
				"runtime.graphics.overlay.load" => ValueTask.FromResult(LoadGraphicsOverlay(request, runtime)),
				"runtime.graphics.overlay.set" => ValueTask.FromResult(SetGraphicsOverlay(request, runtime)),
				"runtime.graphics.overlay.clear" => ValueTask.FromResult(ClearGraphicsOverlay(request, runtime)),
				"runtime.audio.input.set" => ValueTask.FromResult(SetAudioInputState(request, runtime)),
				"runtime.audio.test_signal.set" => ValueTask.FromResult(SetAudioTestSignal(request, runtime)),
				"runtime.test_pattern.set" => ValueTask.FromResult(SetBroadcastTestPattern(request, runtime)),
				"runtime.recording.start" => StartRecordingAsync(request, runtime, cancellationToken),
				"runtime.recording.stop" => StopRecordingAsync(request, runtime, cancellationToken),
				"runtime.media_deck.snapshot.get" => ValueTask.FromResult(MediaDeckSnapshot(request)),
				"runtime.media_deck.open" => ValueTask.FromResult(OpenMediaDeck(request)),
				"runtime.media_deck.transport" => ValueTask.FromResult(ApplyMediaDeckTransport(request)),
				"runtime.media_deck.close" => ValueTask.FromResult(CloseMediaDeck(request)),
				_ => ValueTask.FromResult(Error(request, "ipc.message.unknown", $"Unknown RuntimeHost message type '{request.MessageType}'."))
			};
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
		{
			return ValueTask.FromResult(Error(request, "runtime.request.rejected", exception.Message));
		}
	}

	private WireEnvelope SetAIShowcase(WireEnvelope request)
	{
		var showcase = _aiShowcaseAccessor();
		if (showcase is null)
			return Error(request, "runtime.ai_showcase.unavailable", "AI showcase service is not available.");
		var wire = request.Payload.Deserialize<WireAIShowcaseState>(Wire.JsonOptions)
			?? throw new InvalidDataException("AI showcase state payload is required.");
		var snapshot = showcase.SetEnabled(wire.Enabled);
		_stateVersion++;
		return Success(request, "runtime.ai_showcase.response", ToWire(snapshot));
	}

	private IReadOnlyList<ProviderDescriptor> ProviderDescriptors(V1RuntimeHostService runtime)
	{
		var deck = _mediaDeckAccessor();
		return deck is null
			? runtime.ProviderDescriptors
			: runtime.ProviderDescriptors.Concat(new[] { deck.ProviderDescriptor }).ToArray();
	}

	private WireEnvelope LoadGraphicsOverlay(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireGraphicsAsset>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay asset payload is required.");
		var snapshot = runtime.LoadGraphicsOverlay(wire.Name, wire.Width, wire.Height, wire.RgbaPixels);
		_stateVersion++;
		return Success(request, "runtime.graphics.overlay.response", ToWire(snapshot));
	}

	private WireEnvelope SetGraphicsOverlay(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireGraphicsOverlayState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Graphics overlay state payload is required.");
		var snapshot = runtime.SetGraphicsOverlay(wire.Visible, wire.PositionX, wire.PositionY, wire.Scale);
		_stateVersion++;
		return Success(request, "runtime.graphics.overlay.response", ToWire(snapshot));
	}

	private WireEnvelope ClearGraphicsOverlay(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var snapshot = runtime.ClearGraphicsOverlay();
		_stateVersion++;
		return Success(request, "runtime.graphics.overlay.response", ToWire(snapshot));
	}

	private WireEnvelope SetBroadcastTestPattern(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireTestPatternState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Broadcast test pattern state payload is required.");
		var sourceId = new MediaSourceId(Identity.Parse(wire.SourceId));
		var requestedMode = wire.MotionTiming
			? V1BroadcastTestPatternMode.MotionTiming
			: V1BroadcastTestPatternMode.Static;
		var changed = runtime.SetBroadcastTestPattern(sourceId, wire.Enabled, requestedMode);
		if (changed)
			_stateVersion++;
		var snapshot = runtime.Snapshot;
		return Success(
			request,
			"runtime.test_pattern.response",
			new WireTestPatternState(
				sourceId.ToString(),
				snapshot.BroadcastTestPatternSources.Contains(sourceId),
				snapshot.BroadcastTestPatternModes.TryGetValue(sourceId, out var activeMode) &&
					activeMode == V1BroadcastTestPatternMode.MotionTiming));
	}

	private WireEnvelope SetAudioInputState(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireAudioInputState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Audio input state payload is required.");
		var snapshot = runtime.SetAudioInputState(
			new MediaSourceId(Identity.Parse(wire.SourceId)),
			new AudioGain(wire.Gain),
			wire.Muted);
		_stateVersion++;
		return Success(request, "runtime.audio.input.response", ToWire(snapshot));
	}

	private WireEnvelope SetAudioTestSignal(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireAudioTestSignalState>(Wire.JsonOptions)
			?? throw new InvalidDataException("Generated audio test signal payload is required.");
		var mode = Enum.IsDefined(typeof(GeneratedAudioTestSignalMode), wire.Mode)
			? (GeneratedAudioTestSignalMode)wire.Mode
			: throw new InvalidDataException("Generated audio test signal mode is invalid.");
		var snapshot = runtime.SetGeneratedAudioTestSignal(
			new MediaSourceId(Identity.Parse(wire.SourceId)),
			wire.Enabled,
			mode,
			wire.FrequencyHz,
			wire.PeakLevel);
		_stateVersion++;
		return Success(request, "runtime.audio.test_signal.response", ToWire(snapshot));
	}

	private async ValueTask<WireEnvelope> StartRecordingAsync(
		WireEnvelope request,
		V1RuntimeHostService runtime,
		CancellationToken cancellationToken)
	{
		var wire = request.Payload.Deserialize<WireRecordingStart>(Wire.JsonOptions)
			?? throw new InvalidDataException("Recording start payload is required.");
		var result = await runtime.StartRecordingAsync(
			new RecordingSessionId(Identity.Parse(wire.SessionId)),
			new RecordingOutputId(Identity.Parse(wire.OutputId)),
			wire.DestinationDirectory,
			wire.FileName,
			cancellationToken).ConfigureAwait(false);
		_stateVersion++;
		return Success(
			request,
			"runtime.recording.command.response",
			new WireRecordingCommandResult(
				result.Succeeded,
				ToWire(runtime.Snapshot.RecordingOperator),
				result.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null));
	}

	private async ValueTask<WireEnvelope> StopRecordingAsync(
		WireEnvelope request,
		V1RuntimeHostService runtime,
		CancellationToken cancellationToken)
	{
		var result = await runtime.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
		_stateVersion++;
		return Success(
			request,
			"runtime.recording.command.response",
			new WireRecordingCommandResult(
				result.Status != RecordingStopStatus.Failed,
				ToWire(runtime.Snapshot.RecordingOperator),
				result.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null));
	}

	private WireEnvelope MediaDeckSnapshot(WireEnvelope request)
	{
		var deck = _mediaDeckAccessor();
		if (deck is null)
			return Error(request, "runtime.media_deck.unavailable", "Local media deck service is not available.");
		return Success(request, "runtime.media_deck.snapshot.response", ToWire(deck.Snapshot));
	}

	private WireEnvelope OpenMediaDeck(WireEnvelope request)
	{
		var deck = _mediaDeckAccessor();
		if (deck is null)
			return Error(request, "runtime.media_deck.unavailable", "Local media deck service is not available.");

		var wire = request.Payload.Deserialize<WireMediaDeckOpen>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media-deck open payload is required.");
		var open = new MediaDeckOpenRequest(
			CompatibilityVersion.Parse(wire.Version),
			new MediaSourceId(Identity.Parse(wire.SourceId)),
			wire.Path);
		var prepared = FromWire(wire.PreparedExecution);
		var snapshot = deck.Open(open, prepared);
		_stateVersion++;
		return Success(request, "runtime.media_deck.open.response", ToWire(snapshot));
	}

	private WireEnvelope ApplyMediaDeckTransport(WireEnvelope request)
	{
		var deck = _mediaDeckAccessor();
		if (deck is null)
			return Error(request, "runtime.media_deck.unavailable", "Local media deck service is not available.");

		var wire = request.Payload.Deserialize<WireMediaTransportCommand>(Wire.JsonOptions)
			?? throw new InvalidDataException("Media-deck transport payload is required.");
		var command = new MediaTransportCommand(
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
			wire.OutPointFrame);
		var result = deck.ApplyTransport(command);
		_stateVersion++;
		return Success(request, "runtime.media_deck.transport.response", ToWire(result));
	}

	private WireEnvelope CloseMediaDeck(WireEnvelope request)
	{
		var deck = _mediaDeckAccessor();
		if (deck is null)
			return Error(request, "runtime.media_deck.unavailable", "Local media deck service is not available.");
		var snapshot = deck.Close();
		_stateVersion++;
		return Success(request, "runtime.media_deck.close.response", ToWire(snapshot));
	}

	private WireEnvelope ApplyExecution(WireEnvelope request, V1RuntimeHostService runtime)
	{
		var wire = request.Payload.Deserialize<WireApplyRequest>(Wire.JsonOptions)
			?? throw new InvalidDataException("Runtime apply payload is required.");
		var prepared = FromWire(wire.PreparedExecution);
		var sink = new MediaSinkId(Identity.Parse(wire.ProgramSinkId));
		var transition = wire.Transition is null
			? null
			: new RuntimeProgramTransitionIntent(
				RuntimeContractVersion.Current,
				(Enum.IsDefined(typeof(RuntimeProgramTransitionKind), wire.Transition.Kind)
					? (RuntimeProgramTransitionKind)wire.Transition.Kind
					: throw new InvalidDataException("Transition kind is invalid.")),
				new MediaSourceId(Identity.Parse(wire.Transition.FromSourceId)),
				new MediaSourceId(Identity.Parse(wire.Transition.ToSourceId)),
				wire.Transition.DurationFrames);

		var result = runtime.ApplyExecution(prepared, sink, transition);
		if (result.Committed)
		{
			_committedAuthorityStateId = prepared.AuthoritySnapshot.StateId;
			_committedAuthorityRevision = prepared.AuthoritySnapshot.Revision;
			_stateVersion++;
		}
		return Success(request, "runtime.execution.apply.response", ToWire(result));
	}

	private WireEnvelope Success(WireEnvelope request, string messageType, object payload) =>
		Wire.Create(messageType, request.RequestId, request.CorrelationId, _hostInstanceId, _stateVersion, NextSequence(), payload);

	private WireEnvelope Error(WireEnvelope request, string code, string message) =>
		Wire.Create("error", request.RequestId, request.CorrelationId, _hostInstanceId, _stateVersion, NextSequence(), new WireFailure(code, message));

	private Task WriteErrorAsync(Stream stream, WireEnvelope request, string code, string message, CancellationToken cancellationToken) =>
		Wire.WriteAsync(stream, Error(request, code, message), cancellationToken);

	private ulong NextSequence() => _sequence == ulong.MaxValue
		? throw new InvalidOperationException("RuntimeHost IPC sequence exhausted.")
		: ++_sequence;

	private static WireProvider ToWire(ProviderDescriptor provider) => new(
		provider.Version.ToString(),
		provider.ProviderId.ToString(),
		provider.Name,
		(int)provider.Availability.State,
		provider.Availability.Failure is { } availabilityFailure ? new WireFailure(availabilityFailure.Code, availabilityFailure.Message) : null,
		provider.Capabilities.Select(capability => new WireCapability(
			capability.CapabilityId.ToString(),
			capability.Kind,
			capability.VideoFormats.Select(format => new WireVideoFormat(format.Width, format.Height, format.FrameRate.ToString(), (int)format.PixelFormat, (int)format.ScanMode)).ToArray())).ToArray(),
		provider.Resources.Select(resource => new WireResource(resource.ResourceId.ToString(), resource.ProviderId.ToString(), resource.Kind, resource.CapacityUnits, resource.Reservable)).ToArray());

	private WireRuntimeSnapshot ToWire(V1RuntimeHostSnapshot snapshot, VideoFormat format, RuntimeAIShowcaseSnapshot aiShowcase) => new(
		snapshot.Runtime.Version.ToString(),
		snapshot.Runtime.ActiveExecutionId?.ToString(),
		snapshot.Runtime.ExecutionRevision.Value,
		(int)snapshot.Runtime.Status,
		snapshot.Runtime.Failure is { } runtimeFailure ? new WireFailure(runtimeFailure.Code, runtimeFailure.Message) : null,
		_committedAuthorityStateId?.ToString(),
		_committedAuthorityRevision?.Value,
		snapshot.NextSequenceNumber,
		(int)snapshot.TimingHealth,
		snapshot.ActiveGpuSurfaces,
		new WireVideoFormat(format.Width, format.Height, format.FrameRate.ToString(), (int)format.PixelFormat, (int)format.ScanMode),
		snapshot.InputSignals.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
			.Select(pair => new WireInputSignal(pair.Key.ToString(), pair.Value.ToString().ToUpperInvariant()))
			.ToArray(),
		snapshot.BroadcastTestPatternSources
			.OrderBy(sourceId => sourceId.ToString(), StringComparer.Ordinal)
			.Select(sourceId => sourceId.ToString())
			.ToArray(),
		snapshot.BroadcastTestPatternModes
			.Where(pair => pair.Value == V1BroadcastTestPatternMode.MotionTiming)
			.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
			.Select(pair => pair.Key.ToString())
			.ToArray(),
		ToWire(snapshot.GraphicsOverlay),
		snapshot.AudioInputs.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
			.Select(pair => ToWire(pair.Value))
			.ToArray(),
		ToWire(snapshot.AudioProgram),
		ToWire(snapshot.RecordingOperator),
		ToWire(snapshot.Performance),
		ToWire(aiShowcase));

	private static WireGraphicsOverlay ToWire(V1GraphicsOverlaySnapshot snapshot) => new(
		snapshot.AssetLoaded,
		snapshot.AssetName,
		snapshot.AssetWidth,
		snapshot.AssetHeight,
		snapshot.Visible,
		snapshot.PositionX,
		snapshot.PositionY,
		snapshot.Scale);

	private static WireAudioInput ToWire(V1AudioInputSnapshot snapshot) => new(
		snapshot.SourceId.ToString(),
		snapshot.StreamId.ToString(),
		snapshot.Gain,
		snapshot.Muted,
		snapshot.LeftPeak,
		snapshot.RightPeak,
		snapshot.MasterPeak,
		snapshot.Clipping,
		(int)snapshot.Health,
		snapshot.TestSignalEnabled,
		snapshot.TestSignalMode is null ? null : (int)snapshot.TestSignalMode.Value,
		snapshot.TestSignalActiveChannel,
		snapshot.TestSignalFrequencyHz,
		snapshot.TestSignalPeakLevel);

	private static WireAudioProgram ToWire(V1AudioProgramSnapshot snapshot) => new(
		snapshot.ActiveVideoSourceId.ToString(),
		snapshot.ActiveStreamId.ToString(),
		snapshot.Gain,
		snapshot.Muted,
		snapshot.LeftPeak,
		snapshot.RightPeak,
		snapshot.MasterPeak,
		snapshot.Clipping,
		(int)snapshot.Health);


	private static WireRecordingSnapshot ToWire(V1RecordingOperatorSnapshot snapshot) => new(
		snapshot.State.ToString().ToUpperInvariant(),
		snapshot.Elapsed.Ticks,
		snapshot.Destination,
		snapshot.FileName,
		snapshot.FinalPath,
		snapshot.Statistics.Accepted,
		snapshot.Statistics.Written,
		snapshot.Statistics.Dropped,
		snapshot.Statistics.Rejected,
		snapshot.Statistics.WriterFailures,
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

	private static WireRuntimePerformance ToWire(V1RuntimePerformanceSnapshot snapshot) => new(
		snapshot.Uptime.Ticks,
		snapshot.FrameBudget.Ticks,
		snapshot.LastFrameProcessingTime.Ticks,
		snapshot.DroppedFrames,
		snapshot.GpuDeviceName,
		snapshot.GpuHardwareAccelerated,
		snapshot.GpuUtilizationPercent,
		snapshot.GpuVramUsedBytes,
		snapshot.GpuVramTotalBytes,
		snapshot.GpuTelemetryEvidence,
		snapshot.CpuDeviceName,
		snapshot.CpuLogicalProcessorCount,
		snapshot.CpuUtilizationPercent,
		snapshot.SystemMemoryUsedBytes,
		snapshot.SystemMemoryTotalBytes,
		snapshot.SystemTelemetryEvidence,
		snapshot.PhysicalGpuDeviceName,
		snapshot.OutputFramesPerSecond);

	private static WireAIShowcase ToWire(RuntimeAIShowcaseSnapshot snapshot) => new(
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

	private static WireApplyResponse ToWire(RuntimeHostApplyResult result) => new(
		new WirePrepareResult(
			result.Prepare.Version.ToString(),
			result.Prepare.PreparedExecutionId.ToString(),
			(int)result.Prepare.Status,
			result.Prepare.ReservationId?.ToString(),
			result.Prepare.Failure is { } prepareFailure ? new WireFailure(prepareFailure.Code, prepareFailure.Message) : null),
		result.Commit is null ? null : new WireCommitResult(
			result.Commit.Version.ToString(),
			(int)result.Commit.Status,
			result.Commit.ExecutionInstanceId?.ToString(),
			result.Commit.ExecutionRevision.Value,
			result.Commit.Failure is { } commitFailure ? new WireFailure(commitFailure.Code, commitFailure.Message) : null),
		result.ActivationSequence);

	private static WireMediaDeckRuntimeSnapshot ToWire(MediaDeckRuntimeSnapshot snapshot) => new(
		(int)snapshot.State,
		snapshot.SourceId?.ToString(),
		snapshot.Probe is null ? null : ToWire(snapshot.Probe),
		snapshot.Transport is null ? null : ToWire(snapshot.Transport),
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

	private static WireLocalMediaProbe ToWire(LocalMediaProbe probe) => new(
		probe.Version.ToString(),
		probe.AssetId.ToString(),
		probe.SourceId.ToString(),
		probe.FileName,
		(int)probe.Container,
		(int)probe.VideoCodec,
		(int)probe.AudioCodec,
		probe.VideoFormat.Width,
		probe.VideoFormat.Height,
		probe.VideoFormat.FrameRate.ToString(),
		probe.Duration.Ticks);

	private static WireMediaTransportSnapshot ToWire(MediaTransportSnapshot snapshot) => new(
		snapshot.Version.ToString(),
		snapshot.AssetId.ToString(),
		snapshot.SourceId.ToString(),
		(int)snapshot.State,
		snapshot.Position.CurrentFrame,
		snapshot.Position.TotalFrames,
		snapshot.Position.Position.Ticks,
		snapshot.Position.Duration.Ticks,
		snapshot.Position.Remaining.Ticks,
		snapshot.Position.FrameRate.ToString(),
		snapshot.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null,
		snapshot.AutoPlayOnProgram,
		(int)snapshot.EndBehavior,
		snapshot.IsOnProgram,
		snapshot.EffectiveStartFrame,
		snapshot.EffectiveEndFrame,
		snapshot.EffectiveRemainingFrames,
		snapshot.EffectiveRemaining.Ticks);

	private static WireMediaTransportResult ToWire(MediaTransportCommandResult result) => new(
		result.Succeeded,
		ToWire(result.Snapshot),
		result.Failure is { } failure ? new WireFailure(failure.Code, failure.Message) : null);

	private static PreparedExecutionContract FromWire(WirePreparedExecution prepared) => new(
		CompatibilityVersion.Parse(prepared.Version),
		new PreparedExecutionId(Identity.Parse(prepared.PreparedExecutionId)),
		new AuthoritySnapshotReference(Identity.Parse(prepared.AuthorityStateId), new Revision(prepared.AuthorityRevision)),
		new Generation(prepared.PlanGeneration),
		prepared.Bindings.Select(binding => new PreparedExecutionBinding(
			Identity.Parse(binding.LogicalNodeId),
			new CapabilityId(Identity.Parse(binding.CapabilityId)),
			new ProviderResourceDescriptor(
				new ProviderResourceId(Identity.Parse(binding.Resource.ResourceId)),
				new ProviderId(Identity.Parse(binding.Resource.ProviderId)),
				binding.Resource.Kind,
				binding.Resource.CapacityUnits,
				binding.Resource.Reservable),
			string.IsNullOrWhiteSpace(binding.MediaSourceId) ? null : new MediaSourceId(Identity.Parse(binding.MediaSourceId)),
			string.IsNullOrWhiteSpace(binding.MediaSinkId) ? null : new MediaSinkId(Identity.Parse(binding.MediaSinkId)))).ToArray());

	private sealed record ClientHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record ServerHello(string ProtocolVersion, string Role, string HostInstanceId, Dictionary<string, string> ContractVersions);
	private sealed record WireFailure(string Code, string Message);
	private sealed record WireVideoFormat(uint Width, uint Height, string FrameRate, int PixelFormat, int ScanMode);
	private sealed record WireInputSignal(string SourceId, string Health);
	private sealed record WireTestPatternState(string SourceId, bool Enabled, bool MotionTiming = false);
	private sealed record WireGraphicsAsset(string Name, uint Width, uint Height, byte[] RgbaPixels);
	private sealed record WireGraphicsOverlayState(bool Visible, double PositionX, double PositionY, double Scale);
	private sealed record WireGraphicsOverlay(bool AssetLoaded, string? AssetName, uint AssetWidth, uint AssetHeight, bool Visible, double PositionX, double PositionY, double Scale);
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
	private sealed record WirePreparedBinding(string LogicalNodeId, string CapabilityId, WireResource Resource, string? MediaSourceId, string? MediaSinkId);
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
	private sealed record WireRecordingCommandResult(bool Succeeded, WireRecordingSnapshot Snapshot, WireFailure? Failure);
	private sealed record WireMediaDeckOpen(string Version, string SourceId, string Path, WirePreparedExecution PreparedExecution);
	private sealed record WireMediaTransportCommand(string Version, string AssetId, int Kind, long? TargetFrame, bool? AutoPlayOnProgram, int? EndBehavior, long? InPointFrame, long? OutPointFrame);
	private sealed record WireLocalMediaProbe(string Version, string AssetId, string SourceId, string FileName, int Container, int VideoCodec, int AudioCodec, uint Width, uint Height, string FrameRate, long DurationTicks);
	private sealed record WireMediaTransportSnapshot(string Version, string AssetId, string SourceId, int State, long CurrentFrame, long TotalFrames, long PositionTicks, long DurationTicks, long RemainingTicks, string FrameRate, WireFailure? Failure, bool AutoPlayOnProgram, int EndBehavior, bool IsOnProgram, long EffectiveStartFrame, long EffectiveEndFrame, long EffectiveRemainingFrames, long EffectiveRemainingTicks);
	private sealed record WireMediaDeckRuntimeSnapshot(int State, string? SourceId, WireLocalMediaProbe? Probe, WireMediaTransportSnapshot? Transport, WireFailure? Failure);
	private sealed record WireMediaTransportResult(bool Accepted, WireMediaTransportSnapshot Snapshot, WireFailure? Failure);

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
		string[] BroadcastTestPatternSourceIds,
		string[] MotionTimingTestPatternSourceIds,
		WireGraphicsOverlay GraphicsOverlay,
		WireAudioInput[] AudioInputs,
		WireAudioProgram AudioProgram,
		WireRecordingSnapshot Recording,
		WireRuntimePerformance Performance,
		WireAIShowcase AIShowcase);

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

		public static WireEnvelope Create(string messageType, string requestId, string correlationId, string hostInstanceId, ulong stateVersion, ulong sequence, object payload)
		{
			var payloadElement = JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions);
			return new WireEnvelope(ProtocolVersion, messageType, requestId, correlationId, hostInstanceId, DateTimeOffset.UtcNow, stateVersion, sequence, payloadElement);
		}

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
			if (string.IsNullOrWhiteSpace(envelope.MessageType) || string.IsNullOrWhiteSpace(envelope.RequestId) || string.IsNullOrWhiteSpace(envelope.CorrelationId))
				throw new InvalidDataException("IPC envelope identifiers are required.");
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
