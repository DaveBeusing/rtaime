// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;

namespace rtaime.Operator;

public sealed class HealthCenterSubsystemViewModel : INotifyPropertyChanged
{
	private SubsystemHealthSnapshot _snapshot;
	private OperationalObservation? _observation;
	private DateTimeOffset? _acknowledgedAtUtc;

	public HealthCenterSubsystemViewModel(SubsystemHealthSnapshot snapshot)
	{
		_snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	internal SubsystemHealthSnapshot CurrentSnapshot => _snapshot;
	public string Id => _snapshot.Id;
	public string DisplayName => _snapshot.DisplayName;
	public string Category => _snapshot.Category;
	public SubsystemHealthState HealthState => _snapshot.State;
	public string State => FormatState(_snapshot.State);
	public string Detail => _snapshot.Detail;
	// Availability and freshness describe evidence, independently of subsystem severity.
	public string EvidenceStatus => _snapshot.Metrics.FirstOrDefault(metric => metric.Label == "Evidence status")?.Value
		?? (_snapshot.State == SubsystemHealthState.Unknown ? "UNKNOWN" : "OBSERVED");
	public string StatusSince => _snapshot.StatusSince.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
	public string FirstObserved => StatusSince;
	public string LastObserved
	{
		get
		{
			var observedAt = _snapshot.LastObservedAt == default
				? _snapshot.StatusSince
				: _snapshot.LastObservedAt;
			return observedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
		}
	}
	public string LastSuccessfulCheck => _snapshot.LastSuccessfulCheck?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
	public IReadOnlyList<HealthMetricSnapshot> Metrics => _snapshot.Metrics;
	public string RecoveryStatus => _snapshot.RecoveryStatus;
	public string RecommendedAction => _snapshot.RecoveryStatus;
	public string TechnicalDetail => _snapshot.TechnicalDetail;
	public bool CanRecover => _snapshot.CanRecover;
	public string RecoveryActionLabel => _snapshot.RecoveryActionLabel ?? "RECOVER";
	public SubsystemProgramImpact ProgramImpactValue => _snapshot.ProgramImpact;
	public string ProgramImpact => FormatProgramImpact(_snapshot.ProgramImpact);
	public string EvidenceSource => string.IsNullOrWhiteSpace(_snapshot.EvidenceSource)
		? "UNSPECIFIED"
		: _snapshot.EvidenceSource;
	public string? ObservationKey => _observation?.Key;
	public string AlarmCondition => _observation?.Condition.ToString().ToUpperInvariant() ?? "UNVERIFIED";
	public string AlarmLifecycle => _observation switch
	{
		null => "UNVERIFIED",
		{ Condition: OperationalObservationCondition.Stale } => "STALE / UNVERIFIED",
		{ Condition: OperationalObservationCondition.Active } when _acknowledgedAtUtc.HasValue => "ACKNOWLEDGED · STILL ACTIVE",
		{ Condition: OperationalObservationCondition.Active } => "ACTIVE",
		{ ResolvedAtUtc: not null } => "RESOLVED",
		_ => "CLEAR"
	};
	public string AlarmDuration => _observation?.Duration.ToString(@"dd\.hh\:mm\:ss") ?? "—";
	public string LastGoodProjection => _observation?.LastGoodProjectedUtc?.ToLocalTime()
		.ToString("yyyy-MM-dd HH:mm:ss") ?? "UNVERIFIED";
	public string Acknowledgement => _acknowledgedAtUtc is { } acknowledged
		? $"ACKNOWLEDGED {acknowledged.ToLocalTime():yyyy-MM-dd HH:mm:ss} · NOT A REPAIR"
		: "NOT ACKNOWLEDGED";

	public void ApplyObservation(OperationalObservation? observation, DateTimeOffset? acknowledgedAtUtc)
	{
		if (Equals(_observation, observation) && _acknowledgedAtUtc == acknowledgedAtUtc)
			return;
		_observation = observation;
		_acknowledgedAtUtc = acknowledgedAtUtc;
		foreach (var property in new[]
		{
			nameof(ObservationKey), nameof(AlarmCondition), nameof(AlarmLifecycle),
			nameof(AlarmDuration), nameof(LastGoodProjection), nameof(Acknowledgement)
		})
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
	}

	public bool IsHealthy => _snapshot.State == SubsystemHealthState.Healthy;
	public bool RequiresAttention => !IsHealthy;
	public bool IsOverview => Id is "cpu" or "memory" or "gpu" or "media" or "audio" or "compositing" or "output" or "recording" or "frame-timing";

	public void Apply(SubsystemHealthSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!string.Equals(Id, snapshot.Id, StringComparison.Ordinal))
			throw new InvalidOperationException("Health subsystem identity cannot change.");

		_snapshot = snapshot;
		foreach (var propertyName in new[]
		{
			nameof(DisplayName),
			nameof(Category),
			nameof(HealthState),
			nameof(State),
			nameof(Detail),
			nameof(EvidenceStatus),
			nameof(StatusSince),
			nameof(FirstObserved),
			nameof(LastObserved),
			nameof(LastSuccessfulCheck),
			nameof(Metrics),
			nameof(RecoveryStatus),
			nameof(RecommendedAction),
			nameof(TechnicalDetail),
			nameof(CanRecover),
			nameof(RecoveryActionLabel),
			nameof(ProgramImpactValue),
			nameof(ProgramImpact),
			nameof(EvidenceSource),
			nameof(IsHealthy),
			nameof(RequiresAttention),
			nameof(IsOverview)
		})
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}

	private static string FormatState(SubsystemHealthState state) => state switch
	{
		SubsystemHealthState.Healthy => "HEALTHY",
		SubsystemHealthState.Warning => "WARNING",
		SubsystemHealthState.Degraded => "DEGRADED",
		SubsystemHealthState.Recovering => "RECOVERING",
		SubsystemHealthState.Failed => "FAILED",
		_ => "UNKNOWN"
	};

	private static string FormatProgramImpact(SubsystemProgramImpact impact) => impact switch
	{
		SubsystemProgramImpact.RecordingOnly => "RECORDING ONLY",
		SubsystemProgramImpact.OutputTransportOnly => "OUTPUT TRANSPORT ONLY",
		SubsystemProgramImpact.None => "NO KNOWN IMPACT",
		SubsystemProgramImpact.MonitoringOnly => "MONITORING ONLY",
		SubsystemProgramImpact.ControlLimited => "CONTROL LIMITED",
		SubsystemProgramImpact.Degraded => "PROGRAM DEGRADED",
		SubsystemProgramImpact.Unavailable => "PROGRAM UNAVAILABLE",
		_ => "UNKNOWN"
	};
}

public sealed class HealthCenterViewModel : INotifyPropertyChanged, IDisposable
{
	private static readonly string[] OverviewIds =
	[
		"cpu",
		"memory",
		"gpu",
		"media",
		"audio",
		"compositing",
		"output",
		"recording",
		"frame-timing"
	];

	private readonly IHealthSnapshotProvider _provider;
	private readonly IRuntimeReadinessService _readiness;
	private readonly ICommand _synchronizeCommand;
	private readonly SynchronizationContext _uiContext;
	private readonly Action<string> _copyText;
	private readonly OperationalObservationProjector _alarmProjection = new();
	private readonly INotificationService? _notifications;
	private readonly Action<OperationalObservation>? _logAlarmTransition;
	private readonly Func<DateTimeOffset> _clock;
	private HealthCenterSubsystemViewModel? _selectedSubsystem;
	private bool _technicalDetailsVisible;
	private bool _telemetryOverviewVisible;
	private string _diagnosticActionStatus = string.Empty;
	private readonly object _updateGate = new();
	private IReadOnlyList<SubsystemHealthSnapshot>? _pendingSnapshots;
	private bool _updateQueued;
	private bool _disposed;

	public HealthCenterViewModel(
		IHealthSnapshotProvider provider,
		IRuntimeReadinessService readiness,
		ICommand synchronizeCommand,
		SynchronizationContext? uiContext = null,
		Action<string>? copyText = null,
		INotificationService? notifications = null,
		Action<OperationalObservation>? logAlarmTransition = null,
		Func<DateTimeOffset>? clock = null)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		_readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
		_synchronizeCommand = synchronizeCommand ?? throw new ArgumentNullException(nameof(synchronizeCommand));
		_uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
		_copyText = copyText ?? Clipboard.SetText;
		_notifications = notifications;
		_logAlarmTransition = logAlarmTransition;
		_clock = clock ?? (() => DateTimeOffset.UtcNow);

		Subsystems = [];
		Overview = [];
		Attention = [];
		RecoverCommand = new AsyncRelayCommand(RecoverSelectedAsync, CanRecoverSelected);
		CopyIncidentSummaryCommand = new AsyncRelayCommand(CopySelectedIncidentAsync, () => !_disposed && SelectedSubsystem is not null);
		AcknowledgeAlarmCommand = new AsyncRelayCommand(AcknowledgeSelectedAlarmAsync, CanAcknowledgeSelectedAlarm);
		ToggleTechnicalDetailsCommand = new AsyncRelayCommand(
			() =>
			{
				TechnicalDetailsVisible = !TechnicalDetailsVisible;
				return Task.CompletedTask;
			},
			() => !_disposed && SelectedSubsystem is not null);
		ToggleTelemetryOverviewCommand = new AsyncRelayCommand(
			() =>
			{
				TelemetryOverviewVisible = !TelemetryOverviewVisible;
				return Task.CompletedTask;
			},
			() => !_disposed && Overview.Count > 0);

		_provider.Changed += ProviderChanged;
		_readiness.Changed += ReadinessChanged;
		_synchronizeCommand.CanExecuteChanged += SynchronizeCanExecuteChanged;
		if (_notifications is not null)
		{
			_notifications.Published += NotificationChanged;
			_notifications.Removed += NotificationChanged;
		}
		ApplySnapshots(_provider.GetCurrent());
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<HealthCenterSubsystemViewModel> Subsystems { get; }
	public ObservableCollection<HealthCenterSubsystemViewModel> Overview { get; }
	public ObservableCollection<HealthCenterSubsystemViewModel> Attention { get; }
	public ICommand RecoverCommand { get; }
	public ICommand CopyIncidentSummaryCommand { get; }
	public ICommand AcknowledgeAlarmCommand { get; }
	public ICommand ToggleTechnicalDetailsCommand { get; }
	public ICommand ToggleTelemetryOverviewCommand { get; }

	public bool TelemetryOverviewVisible
	{
		get => _telemetryOverviewVisible;
		private set
		{
			if (Set(ref _telemetryOverviewVisible, value))
				OnPropertyChanged(nameof(TelemetryOverviewAction));
		}
	}
	public string TelemetryOverviewAction => TelemetryOverviewVisible ? "HIDE TELEMETRY" : "SHOW TELEMETRY";

	public HealthCenterSubsystemViewModel? SelectedSubsystem
	{
		get => _selectedSubsystem;
		set
		{
			if (!Set(ref _selectedSubsystem, value))
				return;
			TechnicalDetailsVisible = false;
			DiagnosticActionStatus = string.Empty;
			OnPropertyChanged(nameof(HasSelection));
			(RecoverCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
			(CopyIncidentSummaryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
			(AcknowledgeAlarmCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
			(ToggleTechnicalDetailsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		}
	}

	public bool HasSelection => SelectedSubsystem is not null;
	public bool TechnicalDetailsVisible
	{
		get => _technicalDetailsVisible;
		set
		{
			if (!Set(ref _technicalDetailsVisible, value))
				return;
			OnPropertyChanged(nameof(TechnicalDetailsAction));
		}
	}
	public string TechnicalDetailsAction => TechnicalDetailsVisible ? "HIDE TECHNICAL DETAILS" : "TECHNICAL DETAILS";
	public string DiagnosticActionStatus
	{
		get => _diagnosticActionStatus;
		private set => Set(ref _diagnosticActionStatus, value);
	}
	public string OverallState => FormatReadiness(_readiness.Current.State);
	public string OverallSummary => _readiness.Current.Reasons.FirstOrDefault()?.Detail ??
		(_readiness.Current.IsProductionReady ? "All required production readiness evidence is qualified." :
			"Current production readiness is not qualified. Review subsystem evidence and its safe next action.");
	public string ProductionReadiness => _readiness.Current.IsProductionReady ? "PRODUCTION READY" : "PRODUCTION BLOCKED";
	public int HealthyCount => Subsystems.Count(item => item.IsHealthy);
	public int AttentionCount => Attention.Count;
	public int FailedCount => Subsystems.Count(item => item.State == "FAILED");

	public void Dispose()
	{
		if (_disposed)
			return;
		lock (_updateGate)
		{
			_disposed = true;
			_pendingSnapshots = null;
		}
		_provider.Changed -= ProviderChanged;
		_readiness.Changed -= ReadinessChanged;
		_synchronizeCommand.CanExecuteChanged -= SynchronizeCanExecuteChanged;
		if (_notifications is not null)
		{
			_notifications.Published -= NotificationChanged;
			_notifications.Removed -= NotificationChanged;
		}
		(ToggleTelemetryOverviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleTechnicalDetailsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}


	private void ProviderChanged(object? sender, HealthSnapshotChangedEventArgs e) =>
		QueueUpdate(e.Current.ToArray());

	private void ReadinessChanged(object? sender, RuntimeReadinessChangedEventArgs e) =>
		QueueUpdate(null);

	private void SynchronizeCanExecuteChanged(object? sender, EventArgs e) =>
		QueueUpdate(null);

	private void NotificationChanged(object? sender, OperatorNotificationEventArgs e) =>
		QueueUpdate(null);

	private void QueueUpdate(IReadOnlyList<SubsystemHealthSnapshot>? snapshots)
	{
		lock (_updateGate)
		{
			if (_disposed)
				return;
			if (snapshots is not null)
				_pendingSnapshots = snapshots;
			if (_updateQueued)
				return;
			_updateQueued = true;
		}
		_uiContext.Post(_ => DrainUpdate(), null);
	}

	private void DrainUpdate()
	{
		IReadOnlyList<SubsystemHealthSnapshot>? snapshots;
		lock (_updateGate)
		{
			_updateQueued = false;
			if (_disposed)
				return;
			snapshots = _pendingSnapshots;
			_pendingSnapshots = null;
		}
		if (snapshots is not null)
			ApplySnapshots(snapshots);
		else
			RefreshAlarmPresentation();
		OnPropertyChanged(nameof(OverallState));
		OnPropertyChanged(nameof(OverallSummary));
		OnPropertyChanged(nameof(ProductionReadiness));
		(RecoverCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private void ApplySnapshots(IReadOnlyList<SubsystemHealthSnapshot> snapshots)
	{
		var now = _clock();
		var transitions = _alarmProjection.Update(snapshots, now);
		var byId = Subsystems.ToDictionary(item => item.Id, StringComparer.Ordinal);
		var incoming = snapshots.Take(OperationalObservationProjector.Capacity).ToList();
		var incomingIds = incoming.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
		// An absent provider entry retains its last values as unavailable.
		// Do not make an incident disappear merely because its source stopped reporting.
		foreach (var item in Subsystems)
		{
			if (incoming.Count == OperationalObservationProjector.Capacity)
				break;
			if (!incomingIds.Add(item.Id))
				continue;
			var retained = item.CurrentSnapshot;
			var alreadyUnavailable = retained.State == SubsystemHealthState.Unknown &&
				retained.Metrics.Any(metric => metric.Label == "Evidence status" && metric.Value == "UNAVAILABLE");
			incoming.Add(retained with
			{
				State = SubsystemHealthState.Unknown,
				ProgramImpact = SubsystemProgramImpact.Unknown,
				StatusSince = alreadyUnavailable ? retained.StatusSince : now,
				Metrics = retained.Metrics.Where(metric => metric.Label != "Evidence status")
					.Append(new HealthMetricSnapshot("Evidence status", "UNAVAILABLE")).ToArray(),
				Detail = alreadyUnavailable ? retained.Detail :
					"Source stopped publishing this subsystem; retained values are not current evidence.",
				RecoveryStatus = "Await source recovery and new authoritative evidence; no automatic Program action is taken."
			});
		}
		var ordered = new List<HealthCenterSubsystemViewModel>(incoming.Count);
		foreach (var snapshot in incoming)
		{
			if (!byId.TryGetValue(snapshot.Id, out var item))
				item = new HealthCenterSubsystemViewModel(snapshot);
			else
				item.Apply(snapshot);
			ordered.Add(item);
		}

		ReplaceCollection(Subsystems, ordered);
		ReplaceCollection(
			Overview,
			OverviewIds
				.Select(id => ordered.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
				.Where(item => item is not null)
				.Cast<HealthCenterSubsystemViewModel>()
				.ToArray());
		ReplaceCollection(
			Attention,
			ordered
				.Where(item => item.RequiresAttention)
				.OrderByDescending(item => HealthSnapshotAnalysis.Severity(item.HealthState))
				.ThenByDescending(item => HealthSnapshotAnalysis.ImpactSeverity(item.ProgramImpactValue))
				.ThenBy(item => item.DisplayName, StringComparer.Ordinal)
				.ToArray());

		var selectedId = SelectedSubsystem?.Id;
		SelectedSubsystem = selectedId is null
			? Attention.FirstOrDefault() ?? ordered.FirstOrDefault()
			: ordered.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal))
				?? ordered.FirstOrDefault();

		OnPropertyChanged(nameof(HealthyCount));
		OnPropertyChanged(nameof(AttentionCount));
		OnPropertyChanged(nameof(FailedCount));
		RefreshAlarmPresentation();
		PublishAlarmTransitions(transitions);
		(RecoverCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleTelemetryOverviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private void RefreshAlarmPresentation()
	{
		var observations = _alarmProjection.Current.ToDictionary(item => item.ComponentId, StringComparer.Ordinal);
		var notices = _notifications?.Current.ToDictionary(item => item.Key, StringComparer.Ordinal);
		foreach (var item in Subsystems)
		{
			observations.TryGetValue(item.Id, out var observation);
			OperatorNotification? notice = null;
			if (observation is not null)
				notices?.TryGetValue(observation.Key, out notice);
			item.ApplyObservation(observation, notice?.AcknowledgedAtUtc);
		}
		(AcknowledgeAlarmCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private void PublishAlarmTransitions(IReadOnlyList<OperationalObservation> transitions)
	{
		foreach (var observation in transitions)
		{
			// The projector omits initial unknown observations. A transition
			// from an active failure to lost evidence must still be published.
			// A broken log sink must not suppress Operator acknowledgement.
			try
			{
				_logAlarmTransition?.Invoke(observation);
			}
			catch (Exception)
			{
				// Host logging is observational and best-effort.
			}
			if (_notifications is null)
				continue;
			try
			{
				var resolved = observation.Condition == OperationalObservationCondition.Clear;
				var stale = observation.Condition == OperationalObservationCondition.Stale;
				_notifications.Publish(new OperatorNotification(
					observation.Key,
					resolved ? OperatorNotificationCategory.Recovery :
						observation.Severity == OperationalObservationSeverity.Error
							? OperatorNotificationCategory.Error : OperatorNotificationCategory.Warning,
					resolved ? OperatorNotificationCondition.Resolved : OperatorNotificationCondition.Active,
					DiagnosticRedactor.RedactText($"{observation.DisplayName}: {(resolved ? "resolved" : stale ? "evidence unavailable" : observation.Severity.ToString())}"),
					DiagnosticRedactor.RedactText(observation.Detail),
					DiagnosticRedactor.RedactText(observation.DisplayName),
					resolved ? "Review the recovered evidence before resuming operations." : DiagnosticRedactor.RedactText(observation.RecommendedAction),
					observation.ProgramImpact.ToString(),
					Persistent: !resolved,
					TechnicalDetail: DiagnosticRedactor.RedactText($"Source={observation.EvidenceSource}; Evidence={observation.EvidenceAvailability}; ProjectedAtUtc={observation.ProjectedAtUtc:O}"),
					Lifetime: resolved ? TimeSpan.FromSeconds(12) : null,
					TargetWorkspace: "HEALTH"));
			}
			catch (Exception)
			{
				// A notification subscriber failure must not become a production failure.
			}
		}
	}

	private Task AcknowledgeSelectedAlarmAsync()
	{
		if (CanAcknowledgeSelectedAlarm())
			_notifications?.Acknowledge(SelectedSubsystem!.ObservationKey!);
		return Task.CompletedTask;
	}

	private bool CanAcknowledgeSelectedAlarm() =>
		!_disposed &&
		SelectedSubsystem is { ObservationKey: { } key, AlarmCondition: not "CLEAR" } &&
		_notifications?.Current.Any(item => item.Key == key &&
			item.Condition == OperatorNotificationCondition.Active &&
			item.AcknowledgedAtUtc is null) == true;

	private Task RecoverSelectedAsync()
	{
		if (CanRecoverSelected())
			_synchronizeCommand.Execute(null);
		return Task.CompletedTask;
	}

	private bool CanRecoverSelected() =>
		!_disposed && SelectedSubsystem is { CanRecover: true, Id: "control" } &&
		_synchronizeCommand.CanExecute(null);

	private Task CopySelectedIncidentAsync()
	{
		var selected = SelectedSubsystem;
		if (selected is null)
			return Task.CompletedTask;

		try
		{
			_copyText(BuildIncidentSummary(selected));
			DiagnosticActionStatus = "Redacted incident summary copied to the clipboard.";
		}
		catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
		{
			DiagnosticActionStatus = "The clipboard is currently unavailable.";
		}

		return Task.CompletedTask;
	}

	internal static string BuildIncidentSummary(HealthCenterSubsystemViewModel incident)
	{
		ArgumentNullException.ThrowIfNull(incident);

		var fields = new (string Label, string Value)[]
		{
			("Component", incident.DisplayName),
			("Severity", incident.State),
			("Evidence status", incident.EvidenceStatus),
			("Program impact", incident.ProgramImpact),
			("Alarm lifecycle", incident.AlarmLifecycle),
			("Alarm duration", incident.AlarmDuration),
			("Last good health projection", incident.LastGoodProjection),
			("Acknowledgement", incident.Acknowledgement),
			("State first projected", incident.FirstObserved),
			("Last projected", incident.LastObserved),
			("Evidence source", incident.EvidenceSource),
			("Current evidence", incident.Detail),
			("Recommended safe next action", incident.RecommendedAction)
		};
		var summary = string.Join(Environment.NewLine,
			fields.Select(field => $"{field.Label}: {DiagnosticRedactor.RedactText(field.Value)}"));
		return summary;
	}

	private static string FormatReadiness(RuntimeReadinessState state) => state switch
	{
		RuntimeReadinessState.Initializing => "INITIALIZING",
		RuntimeReadinessState.Ready => "READY",
		RuntimeReadinessState.Degraded => "DEGRADED",
		RuntimeReadinessState.NotReady => "NOT READY",
		RuntimeReadinessState.Recovering => "RECOVERING",
		RuntimeReadinessState.Failed => "FAILED",
		_ => "UNKNOWN"
	};

	private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
	{
		for (var index = 0; index < items.Count; index++)
		{
			var item = items[index];
			if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], item))
				continue;

			var existingIndex = target.IndexOf(item);
			if (existingIndex >= 0)
				target.Move(existingIndex, index);
			else
				target.Insert(index, item);
		}

		while (target.Count > items.Count)
			target.RemoveAt(target.Count - 1);
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}