// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Specialized;
using System.ComponentModel;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorHealthSnapshotProvider : IHealthSnapshotProvider, IDisposable
{
	private static readonly HashSet<string> RelevantOperatorProperties = new(StringComparer.Ordinal)
	{
		nameof(OperatorViewModel.EngineHealth),
		nameof(OperatorViewModel.EngineHealthDetail),
		nameof(OperatorViewModel.ControlHealth),
		nameof(OperatorViewModel.RuntimeHealth),
		nameof(OperatorViewModel.MediaHealth),
		nameof(OperatorViewModel.ProviderHealth),
		nameof(OperatorViewModel.GpuProviderHealth),
		nameof(OperatorViewModel.CurrentFormat),
		nameof(OperatorViewModel.FrameTime),
		nameof(OperatorViewModel.DroppedFrames),
		nameof(OperatorViewModel.Uptime),
		nameof(OperatorViewModel.CpuDeviceName),
		nameof(OperatorViewModel.CpuUtilization),
		nameof(OperatorViewModel.SystemMemory),
		nameof(OperatorViewModel.GpuDeviceName),
		nameof(OperatorViewModel.GpuUtilization),
		nameof(OperatorViewModel.Vram),
		nameof(OperatorViewModel.HealthObserved),
		nameof(OperatorViewModel.IsConnected),
		nameof(OperatorViewModel.IsStale),
		nameof(OperatorViewModel.GlobalReadinessState),
		nameof(OperatorViewModel.PerformanceVerificationState),
		nameof(OperatorViewModel.PerformanceVerificationDetail)
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
		_compositing.Nodes.CollectionChanged += CompositingNodesChanged;
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
		_compositing.Nodes.CollectionChanged -= CompositingNodesChanged;
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
		Refresh();
	}

	private void CompositingNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (_disposed)
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
				$"RuntimeHealth={_operator.RuntimeHealth}; Observed={_operator.HealthObserved}"),
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
				$"RuntimeHealth={_operator.RuntimeHealth}; Observed={_operator.HealthObserved}"),
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
				$"GpuProviderHealth={_operator.GpuProviderHealth}; RuntimeHealth={_operator.RuntimeHealth}; Performance={_operator.PerformanceVerificationState}"),
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
				$"EngineHealth={_operator.EngineHealth}; MediaHealth={_operator.MediaHealth}; Observed={_operator.HealthObserved}"),
			CreateDecoder(existing, now),
			CreateCompositing(existing, now),
			CreateOutput(existing, now),
			Create(
				existing,
				"frame-timing",
				"Frame Timing",
				"Performance",
				PerformanceState(),
				_operator.PerformanceVerificationDetail,
				now,
				[
					new("Frame time / budget", Availability(_operator.FrameTime)),
					new("Dropped frames", Availability(_operator.DroppedFrames)),
					new("Verification", _operator.PerformanceVerificationState)
				],
				_operator.PerformanceVerificationState == "VERIFIED"
					? "Current qualified performance evidence is valid."
					: "Awaiting or requalifying Runtime performance evidence.",
				$"Format={_operator.CurrentFormat}; Uptime={_operator.Uptime}; Observed={_operator.HealthObserved}"),
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
				$"RuntimeStatus={_operator.RuntimeStatus}; RuntimeHealth={_operator.RuntimeHealth}; Performance={_operator.PerformanceVerificationState}"),
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
				$"ProviderHealth={_operator.ProviderHealth}; GpuProviderHealth={_operator.GpuProviderHealth}"),
			CreateMonitoring(existing, now)
		};

		return snapshots;
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
			$"File={_mediaDeck.FileName}; State={_mediaDeck.State}; Error={_mediaDeck.LastError ?? "none"}");
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
				new("Nodes", nodes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
				new("Affected", affected.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
			],
			affected.Length == 0 ? "No recovery action is required." : "Recovery follows authoritative Runtime/Control state.",
			string.Join(Environment.NewLine, nodes.Select(node => $"{node.Id}: {node.HealthLabel} · {node.Status}")));
	}

	private SubsystemHealthSnapshot CreateOutput(
		IReadOnlyDictionary<string, SubsystemHealthSnapshot> existing,
		DateTimeOffset now)
	{
		var state = _output.Health switch
		{
			"LIVE" => SubsystemHealthState.Healthy,
			"FALLBACK" => SubsystemHealthState.Degraded,
			"STALE" => SubsystemHealthState.Recovering,
			"ERROR" => SubsystemHealthState.Failed,
			"WAITING" or "STOPPED" => SubsystemHealthState.Warning,
			_ => SubsystemHealthState.Unknown
		};
		return Create(
			existing,
			"output",
			"Output",
			"Output",
			state,
			_output.Detail,
			now,
			[
				new("State", _output.RunState),
				new("Mode", _output.Mode),
				new("Display", _output.SelectedDisplay?.Label ?? "UNAVAILABLE")
			],
			state == SubsystemHealthState.Recovering ? "Monitoring recovery is automatic." : "Output changes remain under operator control.",
			$"Health={_output.Health}; Monitoring={_monitoring.State}; SelectedDisplay={_output.SelectedDisplay?.Id ?? "none"}");
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
			$"Preview={_monitoring.PreviewState}; Program={_monitoring.ProgramState}; State={_monitoring.State}");
	}

	private SubsystemHealthState GpuState()
	{
		if (_operator.GpuProviderHealth == "FAIL" || _operator.RuntimeHealth == "FAIL")
			return SubsystemHealthState.Failed;
		if (IsUnavailable(_operator.GpuUtilization) || IsUnavailable(_operator.GpuDeviceName))
			return SubsystemHealthState.Unknown;
		return _operator.PerformanceVerificationState == "VERIFIED"
			? SubsystemHealthState.Healthy
			: SubsystemHealthState.Warning;
	}

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
			return SubsystemHealthState.Unknown;
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
		if (runtimeHealth == "FAIL")
			return SubsystemHealthState.Failed;
		return IsUnavailable(value) ? SubsystemHealthState.Unknown : SubsystemHealthState.Healthy;
	}

	private static string ResolveRecoveryStatus(string? evidence) =>
		string.Equals(evidence, "FAIL", StringComparison.OrdinalIgnoreCase)
			? "Recovery follows the owning Runtime/Control service; no unsafe local retry is exposed."
			: "No recovery action is required.";

	private static string Availability(string? value) =>
		IsUnavailable(value) ? "UNAVAILABLE" : value!.Trim();

	private static bool IsUnavailable(string? value) =>
		string.IsNullOrWhiteSpace(value) ||
		value.Trim() is "UNVERIFIED" or "UNKNOWN" or "UNAVAILABLE" or "—";

	private static SubsystemHealthSnapshot Create(
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
		bool canRecover = false,
		string? recoveryActionLabel = null)
	{
		existing.TryGetValue(id, out var previous);
		var statusSince = previous is not null && previous.State == state
			? previous.StatusSince
			: now;
		var lastSuccessfulCheck = state == SubsystemHealthState.Healthy
			? now
			: previous?.LastSuccessfulCheck;
		return new SubsystemHealthSnapshot(
			id,
			displayName,
			category,
			state,
			string.IsNullOrWhiteSpace(detail) ? "No detail is currently published." : detail.Trim(),
			statusSince,
			lastSuccessfulCheck,
			metrics,
			recoveryStatus,
			technicalDetail,
			canRecover,
			recoveryActionLabel);
	}
}
