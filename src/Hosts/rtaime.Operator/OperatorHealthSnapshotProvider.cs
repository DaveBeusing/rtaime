// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Globalization;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorHealthSnapshotProvider : IHealthSnapshotProvider, IDisposable
{
	private static readonly HashSet<string> RelevantOperatorProperties = new(StringComparer.Ordinal)
	{
		nameof(OperatorViewModel.HealthObserved),
		nameof(OperatorViewModel.GlobalReadinessState),
		nameof(OperatorViewModel.StartupComplete),
		nameof(OperatorViewModel.IsConnected),
		nameof(OperatorViewModel.IsStale),
		nameof(OperatorViewModel.OutputRoles),
		nameof(OperatorViewModel.AvSyncState),
		nameof(OperatorViewModel.AvSyncEvent),
		nameof(OperatorViewModel.AvSyncScheduledOffset),
		nameof(OperatorViewModel.AvSyncSubmitOffset),
		nameof(OperatorViewModel.AvSyncDrift),
		nameof(OperatorViewModel.AvSyncDetail),
		nameof(OperatorViewModel.RecordingStatus),
		nameof(OperatorViewModel.RecordingElapsed),
		nameof(OperatorViewModel.RecordingStatistics),
		nameof(OperatorViewModel.RecordingError),
		nameof(OperatorViewModel.RecordingActiveProfile),
		nameof(OperatorViewModel.RecordingActiveProvider),
		nameof(OperatorViewModel.AudioHealth),
		nameof(OperatorViewModel.AudioMeterStatus),
		nameof(OperatorViewModel.AudioClipping),
		nameof(OperatorViewModel.AudioMuted),
		nameof(OperatorViewModel.AudioProductionRevision)
	};

	private static readonly HashSet<string> RelevantDeckProperties = new(StringComparer.Ordinal)
	{
		nameof(MediaDeckViewModel.State),
		nameof(MediaDeckViewModel.StatusDetail),
		nameof(MediaDeckViewModel.FileName),
		nameof(MediaDeckViewModel.VideoCodec),
		nameof(MediaDeckViewModel.AudioCodec),
		nameof(MediaDeckViewModel.Resolution),
		nameof(MediaDeckViewModel.FrameRate),
		nameof(MediaDeckViewModel.LastError)
	};

	private static readonly HashSet<string> RelevantMonitoringProperties = new(StringComparer.Ordinal)
	{
		nameof(OperatorMonitoringViewModel.State),
		nameof(OperatorMonitoringViewModel.Detail),
		nameof(OperatorMonitoringViewModel.PreviewState),
		nameof(OperatorMonitoringViewModel.ProgramState)
	};

	private static readonly HashSet<string> RelevantOutputProperties = new(StringComparer.Ordinal)
	{
		nameof(ProgramOutputController.Health),
		nameof(ProgramOutputController.Detail),
		nameof(ProgramOutputController.RunState),
		nameof(ProgramOutputController.Mode),
		nameof(ProgramOutputController.SelectedDisplay)
	};

	private readonly object _gate = new();
	private readonly OperatorViewModel _operator;
	private readonly MediaDeckViewModel _mediaDeck;
	private readonly OperatorMonitoringViewModel _monitoring;
	private readonly ProgramOutputController _output;
	private readonly CompositingGraphViewModel _compositing;
	private readonly Func<DateTimeOffset> _clock;
	private IReadOnlyList<SubsystemHealthSnapshot> _current = Array.Empty<SubsystemHealthSnapshot>();
	private bool _disposed;

	public OperatorHealthSnapshotProvider(
		OperatorViewModel @operator,
		MediaDeckViewModel mediaDeck,
		OperatorMonitoringViewModel monitoring,
		ProgramOutputController output,
		CompositingGraphViewModel compositing,
		Func<DateTimeOffset>? clock = null)
	{
		_operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));
		_monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
		_output = output ?? throw new ArgumentNullException(nameof(output));
		_compositing = compositing ?? throw new ArgumentNullException(nameof(compositing));
		_clock = clock ?? (() => DateTimeOffset.UtcNow);

		_operator.PropertyChanged += OperatorPropertyChanged;
		_mediaDeck.PropertyChanged += MediaDeckPropertyChanged;
		_monitoring.PropertyChanged += MonitoringPropertyChanged;
		_output.PropertyChanged += OutputPropertyChanged;
		_compositing.PropertyChanged += CompositingPropertyChanged;
		Refresh();
	}

	public event EventHandler<HealthSnapshotChangedEventArgs>? Changed;

	public IReadOnlyList<SubsystemHealthSnapshot> GetCurrent()
	{
		lock (_gate)
			return _current.ToArray();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_operator.PropertyChanged -= OperatorPropertyChanged;
		_mediaDeck.PropertyChanged -= MediaDeckPropertyChanged;
		_monitoring.PropertyChanged -= MonitoringPropertyChanged;
		_output.PropertyChanged -= OutputPropertyChanged;
		_compositing.PropertyChanged -= CompositingPropertyChanged;
	}

	private void OperatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;
		if (!string.IsNullOrEmpty(e.PropertyName) && !RelevantOperatorProperties.Contains(e.PropertyName))
			return;
		Refresh();
	}

	private void MediaDeckPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;
		if (!string.IsNullOrEmpty(e.PropertyName) && !RelevantDeckProperties.Contains(e.PropertyName))
			return;
		Refresh();
	}

	private void MonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;
		if (!string.IsNullOrEmpty(e.PropertyName) && !RelevantMonitoringProperties.Contains(e.PropertyName))
			return;
		Refresh();
	}

	private void OutputPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;
		if (!string.IsNullOrEmpty(e.PropertyName) && !RelevantOutputProperties.Contains(e.PropertyName))
			return;
		Refresh();
	}

	private void CompositingPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;
		if (!string.Equals(e.PropertyName, nameof(CompositingGraphViewModel.HealthRevision), StringComparison.Ordinal))
			return;
		Refresh();
	}

	private void Refresh()
	{
		var now = _clock();
		IReadOnlyList<SubsystemHealthSnapshot> previous;
		lock (_gate)
			previous = _current;

		var next = BuildSnapshots(previous, now);

		lock (_gate)
		{
			if (_disposed)
				return;
			_current = next;
		}

		Changed?.Invoke(this, new HealthSnapshotChangedEventArgs(next));
	}

	private IReadOnlyList<SubsystemHealthSnapshot> BuildSnapshots(
		IReadOnlyList<SubsystemHealthSnapshot> previous,
		DateTimeOffset now)
	{
		var existing = previous.ToDictionary(item => item.Id, StringComparer.Ordinal);
		var snapshots = new List<SubsystemHealthSnapshot>
		{
			Create(
				existing,
				"cpu",
				"CPU",
				"Hardware",
				HardwareState(_operator.CpuUtilization, _operator.RuntimeHealth),
				$"{Availability(_operator.CpuDeviceName)} · {Availability(_operator.CpuUtilization)}",
				now,
				[
					new("Utilization", Availability(_operator.CpuUtilization)),
					new("Device", Availability(_operator.CpuDeviceName))
				],
				"Runtime telemetry updates on the existing Control synchronization cadence.",
				$"RuntimeHealth={_operator.RuntimeHealth}; Observed={_operator.HealthObserved}",
				ExecutionImpact(HardwareState(_operator.CpuUtilization, _operator.RuntimeHealth)),
				"Authoritative Control health snapshot"),
			Create(
				existing,
				"memory",
				"Memory",
				"Hardware",
				HardwareState(_operator.SystemMemory, _operator.RuntimeHealth),
				Availability(_operator.SystemMemory),
				now,
				[new("System memory", Availability(_operator.SystemMemory))],
				"Runtime telemetry updates on the existing Control synchronization cadence.",
				$"RuntimeHealth={_operator.RuntimeHealth}; Observed={_operator.HealthObserved}",
				ExecutionImpact(HardwareState(_operator.SystemMemory, _operator.RuntimeHealth)),
				"Authoritative Control health snapshot"),
			Create(
				existing,
				"gpu",
				"GPU",
				"Hardware",
				GpuState(),
				$"{Availability(_operator.GpuDeviceName)} · {Availability(_operator.GpuUtilization)}",
				now,
				[
					new("Utilization", Availability(_operator.GpuUtilization)),
					new("VRAM", Availability(_operator.Vram)),
					new("Device", Availability(_operator.GpuDeviceName))
				],
				"GPU health follows Runtime and provider evidence; no UI hardware query is performed.",
				$"GpuProviderHealth={_operator.GpuProviderHealth}; RuntimeHealth={_operator.RuntimeHealth}; Performance={_operator.PerformanceVerificationState}",
				ExecutionImpact(GpuState()),
				"Authoritative Control health snapshot"),
			Create(
				existing,
				"media",
				"Media Engine",
				"Media",
				HealthStateMapping.FromEvidence(_operator.MediaHealth),
				_operator.MediaHealth == "PASS" ? "Runtime media inputs and Program audio are healthy." : _operator.EngineHealthDetail,
				now,
				[
					new("Health", _operator.MediaHealth),
					new("Format", Availability(_operator.CurrentFormat))
				],
				ResolveRecoveryStatus(_operator.MediaHealth),
				$"EngineHealth={_operator.EngineHealth}; MediaHealth={_operator.MediaHealth}; Observed={_operator.HealthObserved}",
				ExecutionImpact(HealthStateMapping.FromEvidence(_operator.MediaHealth)),
				"Authoritative Control health snapshot"),
			CreateAudio(existing, now),
			CreateRecording(existing, now),
			CreateDecoder(existing, now),
			CreateCompositing(existing, now),
			CreateOutput(existing, now),
			Create(
				existing,
				"frame-timing",
				"Frame Timing",
				"Performance",
				PerformanceState(),
				_operator.AvSyncState == "UNAVAILABLE"
					? _operator.PerformanceVerificationDetail
					: $"{_operator.PerformanceVerificationDetail} A/V sync: {_operator.AvSyncDetail}",
				now,
				[
					new("Frame time / budget", Availability(_operator.FrameTime)),
					new("Output FPS", Availability(_operator.OutputFps)),
					new("Dropped frames", Availability(_operator.DroppedFrames)),
					new("Verification", _operator.PerformanceVerificationState),
					new("A/V sync", _operator.AvSyncState),
					new("A/V event", _operator.AvSyncEvent),
					new("Scheduled offset", _operator.AvSyncScheduledOffset),
					new("Internal submit offset", _operator.AvSyncSubmitOffset),
					new("Drift", _operator.AvSyncDrift)
				],
				_operator.PerformanceVerificationState == "VERIFIED"
					? "Current qualified performance evidence is valid."
					: "Awaiting or requalifying Runtime performance evidence.",
				$"Format={_operator.CurrentFormat}; Uptime={_operator.Uptime}; Observed={_operator.HealthObserved}; AvSyncState={_operator.AvSyncState}; AvSyncEvent={_operator.AvSyncEvent}; AvSyncScheduled={_operator.AvSyncScheduledOffset}; AvSyncSubmit={_operator.AvSyncSubmitOffset}; AvSyncDrift={_operator.AvSyncDrift}",
				ExecutionImpact(PerformanceState()),
				"Authoritative Runtime performance and A/V synchronization evidence"),
			Create(
				existing,
				"control",
				"Control Service",
				"Lifecycle",
				ControlState(),
				_operator.IsStale
					? "Authoritative Control state is stale; automatic recovery is active."
					: _operator.ControlHealth == "PASS"
						? "Authoritative Control state is available."
						: _operator.EngineHealthDetail,
				now,
				[
					new("Health", _operator.ControlHealth),
					new("Connection", _operator.IsConnected ? "CONNECTED" : "DISCONNECTED"),
					new("Readiness", _operator.GlobalReadinessState)
				],
				_operator.IsStale
					? "Automatic full-snapshot resynchronization is active."
					: !_operator.IsConnected
						? "Manual synchronization is available when the transport can connect."
						: "No recovery action is required.",
				$"Connected={_operator.IsConnected}; Stale={_operator.IsStale}; GlobalReadiness={_operator.GlobalReadinessState}",
				ControlImpact(ControlState()),
				"Authoritative Control connection and health snapshot",
				canRecover: !_operator.IsConnected && !_operator.IsStale && _operator.SynchronizeCommand.CanExecute(null),
				recoveryActionLabel: "SYNCHRONIZE"),
			Create(
				existing,
				"runtime",
				"Runtime Service",
				"Lifecycle",
				RuntimeState(),
				_operator.RuntimeHealth == "PASS"
					? "Runtime execution is healthy."
					: _operator.EngineHealthDetail,
				now,
				[
					new("Health", _operator.RuntimeHealth),
					new("Runtime status", _operator.RuntimeStatus),
					new("Uptime", Availability(_operator.Uptime))
				],
				ResolveRecoveryStatus(_operator.RuntimeHealth),
				$"RuntimeStatus={_operator.RuntimeStatus}; RuntimeHealth={_operator.RuntimeHealth}; Performance={_operator.PerformanceVerificationState}",
				ExecutionImpact(RuntimeState()),
				"Authoritative Control Runtime snapshot"),
			Create(
				existing,
				"providers",
				"Processing Providers",
				"Lifecycle",
				ProviderState(),
				_operator.ProviderHealth == "PASS" && _operator.GpuProviderHealth == "PASS"
					? "Runtime provider inventory and GPU provider are healthy."
					: _operator.EngineHealthDetail,
				now,
				[
					new("Providers", _operator.ProviderHealth),
					new("GPU provider", _operator.GpuProviderHealth)
				],
				ResolveRecoveryStatus(_operator.ProviderHealth == "FAIL" || _operator.GpuProviderHealth == "FAIL" ? "FAIL" : _operator.ProviderHealth),
				$"ProviderHealth={_operator.ProviderHealth}; GpuProviderHealth={_operator.GpuProviderHealth}",
				ExecutionImpact(ProviderState()),
				"Authoritative Control provider health snapshot"),
			CreateMonitoring(existing, now)
		};

		snapshots.AddRange(_operator.OutputRoles
			.Where(role => role.RoleId is "program" or "aux")
			.Select(role => CreateTransport(existing, role, now)));

		return snapshots;
	}

	private SubsystemHealthSnapshot CreateAudio(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var state = AudioState();
		var normalizedHealth = _operator.AudioHealth.Trim().ToUpperInvariant();
		var detail = state switch
		{
			SubsystemHealthState.Healthy => "Authoritative Program audio health and meters are normal.",
			SubsystemHealthState.Warning => $"Program audio reports {normalizedHealth}. Confirm that mute, routing and source state are intentional.",
			SubsystemHealthState.Degraded => $"Program audio is degraded: {normalizedHealth}. Review clipping, underrun and routing evidence.",
			SubsystemHealthState.Failed => $"Program audio reports an error while Runtime health is {_operator.RuntimeHealth}.",
			_ => "Authoritative Program audio evidence is currently unavailable."
		};

		return Create(
			existing,
			"audio",
			"Program Audio",
			"Audio",
			state,
			detail,
			now,
			[
				new("Health", Availability(_operator.AudioHealth)),
				new("Meter", Availability(_operator.AudioMeterStatus)),
				new("Left peak", _operator.AudioLeftDb),
				new("Right peak", _operator.AudioRightDb),
				new("Master peak", _operator.AudioMasterPeak.ToString("P0", CultureInfo.InvariantCulture)),
				new("Muted", _operator.AudioMuted ? "YES" : "NO"),
				new("Clipping", _operator.AudioClipping ? "YES" : "NO")
			],
			state == SubsystemHealthState.Healthy
				? "No recovery action is required."
				: "Review the existing Audio workspace and confirm source, routing, mute and gain before making an authorized change.",
			$"AudioHealth={_operator.AudioHealth}; Meter={_operator.AudioMeterStatus}; ProductionRevision={_operator.AudioProductionRevision}; Muted={_operator.AudioMuted}; Clipping={_operator.AudioClipping}; MasterPeak={_operator.AudioMasterPeak.ToString("0.000", CultureInfo.InvariantCulture)}",
			AudioImpact(state),
			"Authoritative Control audio production snapshot");
	}

	private SubsystemHealthSnapshot CreateRecording(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var state = RecordingState();
		var detail = state switch
		{
			SubsystemHealthState.Failed => string.IsNullOrWhiteSpace(_operator.RecordingError)
				? "Program recording failed without a published error detail."
				: $"Program recording failed: {_operator.RecordingError}",
			SubsystemHealthState.Healthy when string.Equals(_operator.RecordingStatus, "RECORDING", StringComparison.OrdinalIgnoreCase) =>
				$"Program recording is active using {Availability(_operator.RecordingActiveProfile)}.",
			SubsystemHealthState.Healthy when string.Equals(_operator.RecordingStatus, "FINALIZING", StringComparison.OrdinalIgnoreCase) =>
				"Program recording is finalizing through the existing recording workflow.",
			SubsystemHealthState.Healthy => $"Program recording state is {Availability(_operator.RecordingStatus)}.",
			_ => "Authoritative recording evidence is currently unavailable."
		};

		return Create(
			existing,
			"recording",
			"Recording",
			"Recording",
			state,
			detail,
			now,
			[
				new("State", Availability(_operator.RecordingStatus)),
				new("Elapsed", Availability(_operator.RecordingElapsed)),
				new("Samples", Availability(_operator.RecordingStatistics)),
				new("Profile", Availability(_operator.RecordingActiveProfile)),
				new("Provider", Availability(_operator.RecordingActiveProvider))
			],
			state == SubsystemHealthState.Failed
				? "Review the existing Recording controls and diagnostics. Do not restart Program or discard the failed session from this view."
				: "Recording lifecycle remains governed by the existing Start/Stop workflow.",
			$"Status={_operator.RecordingStatus}; Error={_operator.RecordingError ?? "none"}; ActiveProfile={_operator.RecordingActiveProfile}; ActiveProvider={_operator.RecordingActiveProvider}; FinalPath={_operator.RecordingFinalPath}",
			RecordingImpact(state),
			"Authoritative Control recording snapshot");
	}

	private SubsystemHealthSnapshot CreateTransport(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		OperatorOutputRoleDescriptor role,
		DateTimeOffset now)
	{
		var transport = role.NetworkOutput;

		var transmission = OutputRoutingHealthViewModel.TransmissionState(
			transport, _operator.IsConnected, _operator.IsStale, now);
		var state = !role.AuthoritativeActive || transport is null
			? SubsystemHealthState.Unknown
			: transmission switch
			{
				"LIVE" => SubsystemHealthState.Healthy,
				"FAILED" => SubsystemHealthState.Failed,
				"DEGRADED" => SubsystemHealthState.Degraded,
				"CONNECTING" or "RECONNECTING" => SubsystemHealthState.Recovering,
				_ => SubsystemHealthState.Warning
			};
		return Create(
			existing,
			$"transport-{role.RoleId}",
			$"{role.RoleId.ToUpperInvariant()} Transport",
			"Output",
			state,
			transport is null
				? "No network transport evidence is published for this output role."
				: $"Transmission={transmission}; Lifecycle={transport.Lifecycle}; Connected={transport.Connected}; {transport.Failure?.Message ?? "No current transport failure published."}",
			now,
			transport is null ? [] :
			[
				new("Provider", transport.Provider),
				new("Protocol", transport.Protocol),
				new("Sent samples", transport.SentSamples.ToString(CultureInfo.InvariantCulture)),
				new("Dropped samples (cumulative)", transport.DroppedSamples.ToString(CultureInfo.InvariantCulture)),
				new("Queue depth", transport.QueueDepth.ToString(CultureInfo.InvariantCulture)),
				new("Reconnects", transport.ReconnectCount.ToString(CultureInfo.InvariantCulture)),
				new("Last successful send UTC", transport.LastSuccessfulSendUtc?.ToString("O") ?? "UNAVAILABLE")
			],
			"Review the existing Outputs workspace. Connection is transport evidence; it does not qualify physical delivery or permit a Program restart.",
			$"Role={role.RoleId}; RoleHealth={role.HealthState}; Active={role.AuthoritativeActive}; Lifecycle={transport?.Lifecycle ?? "UNAVAILABLE"}",
			state == SubsystemHealthState.Healthy ? SubsystemProgramImpact.None :
				state == SubsystemHealthState.Unknown ? SubsystemProgramImpact.Unknown : SubsystemProgramImpact.OutputTransportOnly,
			"Control-confirmed output role / Runtime network transport snapshot");
	}

	private SubsystemHealthSnapshot CreateDecoder(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var state = _mediaDeck.State switch
		{
			"ERROR" => SubsystemHealthState.Failed,
			"OPENING" or "SEEKING" => SubsystemHealthState.Recovering,
			"READY" or "PLAYING" or "PAUSED" or "ENDED" => SubsystemHealthState.Healthy,
			"UNLOADED" => SubsystemHealthState.Unknown,
			_ => SubsystemHealthState.Warning
		};
		return Create(
			existing,
			"decoder",
			"Decoder",
			"Media",
			state,
			_mediaDeck.StatusDetail,
			now,
			[
				new("Video codec", Availability(_mediaDeck.VideoCodec)),
				new("Audio codec", Availability(_mediaDeck.AudioCodec)),
				new("Resolution", Availability(_mediaDeck.Resolution)),
				new("Frame rate", Availability(_mediaDeck.FrameRate))
			],
			state == SubsystemHealthState.Failed
				? "Close or reopen the media through the existing Media Deck workflow."
				: "Decoder state follows the active Media Deck session.",
			$"File={_mediaDeck.FileName}; State={_mediaDeck.State}; Error={_mediaDeck.LastError ?? "none"}",
			state == SubsystemHealthState.Healthy ? SubsystemProgramImpact.None : SubsystemProgramImpact.Unknown,
			"Media Deck session projection");
	}

	private SubsystemHealthSnapshot CreateCompositing(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var nodes = _compositing.Nodes.ToArray();
		var state = nodes.Length == 0
			? SubsystemHealthState.Unknown
			: nodes.Any(node => node.IsError)
				? SubsystemHealthState.Failed
				: nodes.Any(node => node.IsDegraded)
					? SubsystemHealthState.Degraded
					: SubsystemHealthState.Healthy;
		var affected = nodes.Where(node => node.IsError || node.IsDegraded).ToArray();
		var detail = affected.Length == 0
			? nodes.Length == 0
				? "No compositing graph nodes are currently projected."
				: $"{nodes.Length} compositing graph nodes are healthy."
			: string.Join("; ", affected.Select(node => $"{node.Title}: {node.Detail}"));
		return Create(
			existing,
			"compositing",
			"Compositing",
			"Compositing",
			state,
			detail,
			now,
			[
				new("Nodes", nodes.Length.ToString(CultureInfo.InvariantCulture)),
				new("Affected", affected.Length.ToString(CultureInfo.InvariantCulture))
			],
			affected.Length == 0 ? "No recovery action is required." : "Recovery follows authoritative Runtime/Control state.",
			string.Join(Environment.NewLine, nodes.Select(node => $"{node.Id}: {node.HealthLabel} · {node.Status}")),
			state switch
			{
				SubsystemHealthState.Healthy => SubsystemProgramImpact.None,
				SubsystemHealthState.Failed or SubsystemHealthState.Degraded => SubsystemProgramImpact.Unknown,
				_ => SubsystemProgramImpact.Unknown
			},
			"Compositing graph projection");
	}

	private SubsystemHealthSnapshot CreateOutput(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var state = _operator.IsStale
			? SubsystemHealthState.Recovering
			: _operator.RuntimeHealth == "FAIL"
				? SubsystemHealthState.Failed
				: _output.Health is "ERROR" or "FALLBACK"
					? SubsystemHealthState.Degraded
					: _operator.RuntimeHealth == "PASS"
						? SubsystemHealthState.Healthy
						: SubsystemHealthState.Unknown;
		var detail = _operator.RuntimeHealth == "PASS"
			? _output.Health is "ERROR" or "FALLBACK"
				? $"Runtime Program is healthy. Clean Program monitoring is {_output.Health}: {_output.Detail}"
				: "Authoritative Runtime Program execution is healthy."
			: _operator.EngineHealthDetail;
		var impact = _operator.RuntimeHealth == "FAIL"
			? SubsystemProgramImpact.Unknown
			: _output.Health is "ERROR" or "FALLBACK"
				? SubsystemProgramImpact.MonitoringOnly
				: state == SubsystemHealthState.Healthy
					? SubsystemProgramImpact.None
					: SubsystemProgramImpact.Unknown;
		return Create(
			existing,
			"output",
			"Output",
			"Output",
			state,
			detail,
			now,
			[
				new("Runtime Program", _operator.RuntimeHealth),
				new("Clean Program monitor", _output.Health),
				new("Monitor mode", _output.Mode),
				new("Display", _output.SelectedDisplay?.Label ?? "UNAVAILABLE")
			],
			state == SubsystemHealthState.Recovering
				? "Authoritative-state recovery is automatic."
				: "Runtime output remains authoritative; Clean Program changes stay under operator control.",
			$"RuntimeHealth={_operator.RuntimeHealth}; CleanProgram={_output.Health}; Monitoring={_monitoring.State}; SelectedDisplay={_output.SelectedDisplay?.Id ?? "none"}",
			impact,
			"Runtime Program and Clean Program monitoring projections");
	}

	private SubsystemHealthSnapshot CreateMonitoring(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var state = _monitoring.State switch
		{
			"LIVE" => SubsystemHealthState.Healthy,
			"RECOVERING" or "STALE" => SubsystemHealthState.Recovering,
			"DISCONNECTED" => SubsystemHealthState.Failed,
			"STARTING" => SubsystemHealthState.Warning,
			_ => SubsystemHealthState.Unknown
		};
		return Create(
			existing,
			"monitoring",
			"Monitoring Plane",
			"Diagnostics",
			state,
			_monitoring.Detail,
			now,
			[
				new("Plane", _monitoring.State),
				new("Preview", _monitoring.PreviewState),
				new("Program", _monitoring.ProgramState)
			],
			state == SubsystemHealthState.Recovering ? "Monitoring reconnect is automatic." : "Monitoring is observational and does not own Program continuity.",
			$"Preview={_monitoring.PreviewState}; Program={_monitoring.ProgramState}; State={_monitoring.State}",
			state switch
			{
				SubsystemHealthState.Healthy => SubsystemProgramImpact.None,
				SubsystemHealthState.Warning or SubsystemHealthState.Degraded or SubsystemHealthState.Recovering or SubsystemHealthState.Failed => SubsystemProgramImpact.MonitoringOnly,
				_ => SubsystemProgramImpact.Unknown
			},
			"Independent Runtime monitoring plane");
	}

	private SubsystemHealthState GpuState()
	{
		if (_operator.GpuProviderHealth == "FAIL")
			return SubsystemHealthState.Degraded;
		if (_operator.RuntimeHealth != "PASS")
			return SubsystemHealthState.Unknown;
		if (IsUnavailable(_operator.GpuUtilization) || IsUnavailable(_operator.GpuDeviceName))
			return SubsystemHealthState.Unknown;
		return _operator.PerformanceVerificationState == "VERIFIED"
			? SubsystemHealthState.Healthy
			: SubsystemHealthState.Warning;
	}

	private SubsystemHealthState AudioState()
	{
		if (_operator.AudioMeterStatus == "STALE")
			return SubsystemHealthState.Unknown;
		if (_operator.AudioClipping)
			return SubsystemHealthState.Degraded;
		return _operator.AudioHealth.Trim().ToUpperInvariant() switch
		{
			"HEALTHY" => SubsystemHealthState.Healthy,
			"MUTED" or "SILENCE" => SubsystemHealthState.Warning,
			"CLIPPING" or "UNDERRUN" => SubsystemHealthState.Degraded,
			"ERROR" or "FAILED" => SubsystemHealthState.Failed,
			_ => SubsystemHealthState.Unknown
		};
	}

	private SubsystemHealthState RecordingState() =>
		_operator.RecordingStatus.Trim().ToUpperInvariant() switch
		{
			"IDLE" or "RECORDING" or "FINALIZING" or "COMPLETED" => SubsystemHealthState.Healthy,
			"FAILED" => SubsystemHealthState.Failed,
			_ => SubsystemHealthState.Unknown
		};

	private SubsystemHealthState PerformanceState() =>
		_operator.PerformanceVerificationState switch
		{
			"VERIFIED" => SubsystemHealthState.Healthy,
			"INVALIDATED" => SubsystemHealthState.Degraded,
			_ => _operator.RuntimeHealth == "FAIL"
				? SubsystemHealthState.Failed
				: SubsystemHealthState.Warning
		};

	private SubsystemHealthState ControlState()
	{
		if (_operator.IsStale)
			return SubsystemHealthState.Recovering;
		if (!_operator.IsConnected)
			return _operator.StartupComplete ? SubsystemHealthState.Failed : SubsystemHealthState.Warning;
		return HealthStateMapping.FromEvidence(_operator.ControlHealth);
	}

	private SubsystemHealthState RuntimeState()
	{
		if (_operator.IsStale)
			return SubsystemHealthState.Recovering;
		return HealthStateMapping.FromEvidence(_operator.RuntimeHealth);
	}

	private SubsystemHealthState ProviderState()
	{
		if (_operator.ProviderHealth == "FAIL" || _operator.GpuProviderHealth == "FAIL")
			return SubsystemHealthState.Failed;
		if (_operator.ProviderHealth == "PASS" && _operator.GpuProviderHealth == "PASS")
			return SubsystemHealthState.Healthy;
		return SubsystemHealthState.Unknown;
	}

	private static SubsystemHealthState HardwareState(string value, string runtimeHealth)
	{
		if (runtimeHealth != "PASS")
			return SubsystemHealthState.Unknown;
		return IsUnavailable(value) ? SubsystemHealthState.Unknown : SubsystemHealthState.Healthy;
	}

	private static SubsystemProgramImpact ExecutionImpact(SubsystemHealthState state) => state switch
	{
		SubsystemHealthState.Healthy => SubsystemProgramImpact.None,
		// Aggregated health failure does not prove that Program execution stopped.
		SubsystemHealthState.Degraded or SubsystemHealthState.Recovering or SubsystemHealthState.Failed => SubsystemProgramImpact.Unknown,
		_ => SubsystemProgramImpact.Unknown
	};

	private static SubsystemProgramImpact AudioImpact(SubsystemHealthState state) => state switch
	{
		SubsystemHealthState.Healthy => SubsystemProgramImpact.None,
		SubsystemHealthState.Degraded or SubsystemHealthState.Failed => SubsystemProgramImpact.Degraded,
		_ => SubsystemProgramImpact.Unknown
	};

	private static SubsystemProgramImpact RecordingImpact(SubsystemHealthState state) => state switch
	{
		SubsystemHealthState.Healthy => SubsystemProgramImpact.None,
		SubsystemHealthState.Failed => SubsystemProgramImpact.RecordingOnly,
		_ => SubsystemProgramImpact.Unknown
	};

	private static SubsystemProgramImpact ControlImpact(SubsystemHealthState state) => state switch
	{
		SubsystemHealthState.Healthy => SubsystemProgramImpact.None,
		SubsystemHealthState.Warning or SubsystemHealthState.Degraded or SubsystemHealthState.Recovering or SubsystemHealthState.Failed => SubsystemProgramImpact.ControlLimited,
		_ => SubsystemProgramImpact.Unknown
	};

	private static string ResolveRecoveryStatus(string? evidence) =>
		string.Equals(evidence, "FAIL", StringComparison.OrdinalIgnoreCase)
			? "Recovery follows the owning Runtime/Control service; no unsafe local retry is exposed."
			: "No recovery action is required.";

	private static string Availability(string? value) =>
		IsUnavailable(value) ? "UNAVAILABLE" : value!.Trim();

	private static bool IsUnavailable(string? value) =>
		string.IsNullOrWhiteSpace(value) ||
		value.Trim() is "UNVERIFIED" or "UNKNOWN" or "UNAVAILABLE" or "—";

	private SubsystemHealthSnapshot Create(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		string id,
		string displayName,
		string category,
		SubsystemHealthState state,
		string detail,
		DateTimeOffset now,
		IReadOnlyList<HealthMetricSnapshot> metrics,
		string recoveryStatus,
		string technicalDetail,
		SubsystemProgramImpact programImpact,
		string evidenceSource,
		bool canRecover = false,
		string? recoveryActionLabel = null)
	{
		existing.TryGetValue(id, out var previous);
		var candidate = new SubsystemHealthSnapshot(
			id,
			displayName,
			category,
			state,
			string.IsNullOrWhiteSpace(detail) ? "No detail is currently published." : detail.Trim(),
			now,
			null,
			metrics,
			recoveryStatus,
			technicalDetail,
			canRecover,
			recoveryActionLabel,
			programImpact,
			evidenceSource,
			now);
		candidate = ApplyEvidenceFreshness(candidate, _operator.IsConnected, _operator.IsStale, _operator.RuntimeHealth);
		return HealthSnapshotHistory.Retain(candidate, previous, now);
	}

	internal static SubsystemHealthSnapshot ApplyEvidenceFreshness(
		SubsystemHealthSnapshot candidate, bool connected, bool stale, string runtimeHealth)
	{
		var reliesOnControl = candidate.Id is not ("control" or "decoder" or "monitoring");
		if (!reliesOnControl)
			return candidate;
		if (!connected || stale || runtimeHealth == "UNVERIFIED")
			return candidate with
			{
				State = SubsystemHealthState.Unknown,
				ProgramImpact = SubsystemProgramImpact.Unknown,
				Detail = "Evidence is unavailable or stale; retained measurements do not establish current Program health. " +
					$"Last projection: {candidate.Detail}",
				RecoveryStatus = "Wait for authoritative full-snapshot synchronization; review Control Service for the existing recovery action."
			};
		return candidate;
	}
}