// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorNotificationItemViewModel : INotifyPropertyChanged
{
	private OperatorNotification _notification;
	private bool _technicalDetailsVisible;

	public OperatorNotificationItemViewModel(OperatorNotification notification,
		INotificationService? service = null, Action<string>? navigate = null)
	{
		_notification = notification ?? throw new ArgumentNullException(nameof(notification));
		AcknowledgeCommand = new NotificationCommand(() => service?.Acknowledge(Key), () => service is not null && !IsAcknowledged);
		SnoozeCommand = new NotificationCommand(() => service?.Snooze(Key, TimeSpan.FromMinutes(5)),
			() => service is not null && _notification.Condition != OperatorNotificationCondition.Fatal && !IsSnoozed);
		ResumeCommand = new NotificationCommand(() => service?.Resume(Key), () => service is not null && IsSnoozed);
		DismissCommand = new NotificationCommand(() => service?.Dismiss(Key), () => service is not null);
		NavigateCommand = new NotificationCommand(() => navigate?.Invoke(_notification.TargetWorkspace), () => navigate is not null);
		ToggleTechnicalDetailsCommand = new NotificationCommand(
			() => IsTechnicalDetailsVisible = !IsTechnicalDetailsVisible,
			() => HasTechnicalDetail);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ICommand AcknowledgeCommand { get; }
	public ICommand SnoozeCommand { get; }
	public ICommand ResumeCommand { get; }
	public bool IsSnoozed => _notification.SnoozedUntilUtc.HasValue;
	public string ReminderStatus => IsSnoozed ? $"REMINDERS PAUSED UNTIL {_notification.SnoozedUntilUtc:HH:mm:ss} UTC · condition retained" : "REMINDERS ACTIVE";
	public ICommand DismissCommand { get; }
	public ICommand NavigateCommand { get; }
	public bool IsAcknowledged => _notification.AcknowledgedAtUtc.HasValue;
	public string Acknowledgement => IsAcknowledged ? $"ACKNOWLEDGED · {_notification.AcknowledgedAtUtc:HH:mm:ss}" : "NOT ACKNOWLEDGED";
	public void Apply(OperatorNotification notification)
	{
		_notification = notification; OnPropertyChanged(string.Empty);
		(AcknowledgeCommand as NotificationCommand)?.RaiseCanExecuteChanged();
		(SnoozeCommand as NotificationCommand)?.RaiseCanExecuteChanged();
		(ResumeCommand as NotificationCommand)?.RaiseCanExecuteChanged();
	}
	public string Key => _notification.Key;
	public string Category => _notification.Category.ToString().ToUpperInvariant();
	public string Condition => _notification.Condition.ToString().ToUpperInvariant();
	public string Title => _notification.Title;
	public string Detail => _notification.Detail;
	public string AffectedComponent => _notification.AffectedComponent;
	public string OperatorAction => _notification.OperatorAction;
	public string ProductionImpact => _notification.ProductionImpact;
	public string? TechnicalDetail => _notification.TechnicalDetail;
	public bool HasTechnicalDetail => _notification.HasTechnicalDetail;
	public int OccurrenceCount => _notification.OccurrenceCount;
	public string OccurrenceLabel => OccurrenceCount > 1 ? $"×{OccurrenceCount}" : string.Empty;
	public DateTimeOffset PublishedAtUtc => _notification.PublishedAtUtc;
	public ICommand ToggleTechnicalDetailsCommand { get; }
	public string TechnicalDetailsActionLabel => IsTechnicalDetailsVisible ? "HIDE TECHNICAL" : "TECHNICAL DETAILS";

	public bool IsTechnicalDetailsVisible
	{
		get => _technicalDetailsVisible;
		private set
		{
			if (_technicalDetailsVisible == value)
				return;
			_technicalDetailsVisible = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(TechnicalDetailsActionLabel));
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class OperatorNotificationGroupViewModel(string label)
{
	public string Label { get; } = label;
	internal bool Includes(OperatorNotificationItemViewModel item) => Label == "FATAL" ? item.Condition == "FATAL" : item.Condition != "FATAL" && item.Category == Label;
	public ObservableCollection<OperatorNotificationItemViewModel> Items { get; } = [];
}

public sealed class OperatorNotificationCenterViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly INotificationService _notifications;
	private readonly OperatorViewModel _operator;
	private readonly SynchronizationContext? _synchronizationContext;
	private readonly Timer _expiryTimer;
	private readonly OperatorActionHistory? _history;
	private readonly Dictionary<Guid, OperatorActionOutcome> _actionStates = [];
	private readonly Action<string>? _navigate;
	private readonly object _refreshGate = new();
	private bool _refreshPending;
	private bool _wasRecovering;
	private bool _disposed;

	public OperatorNotificationCenterViewModel(
		INotificationService notifications,
		OperatorViewModel operatorViewModel,
		SynchronizationContext? synchronizationContext = null,
		Action<string>? navigate = null, OperatorActionHistory? history = null)
	{
		_history = history;
		if (_history is not null) _history.Changed += HistoryChanged;
		_navigate = navigate;
		_notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
		_operator = operatorViewModel ?? throw new ArgumentNullException(nameof(operatorViewModel));
		_synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
		Items = [];
		Toasts = new ObservableCollection<OperatorNotificationItemViewModel>();
		ActiveProblems = new ObservableCollection<OperatorNotificationItemViewModel>();

		_notifications.Published += OnNotificationChanged;
		_notifications.Removed += OnNotificationChanged;
		_operator.PropertyChanged += OnOperatorPropertyChanged;
		_expiryTimer = new Timer(
			_ => _notifications.RemoveExpired(DateTimeOffset.UtcNow),
			null,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1));
		Refresh();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OperatorNotificationItemViewModel> Items { get; }
	public ObservableCollection<OperatorNotificationItemViewModel> Toasts { get; }
	public ObservableCollection<OperatorNotificationItemViewModel> ActiveProblems { get; }
	public ObservableCollection<OperatorNotificationGroupViewModel> Groups { get; } =
	[
		new("FATAL"), new("ERROR"), new("WARNING"), new("RECOVERY"), new("INFORMATION"), new("SUCCESS")
	];
	public int ActiveProblemCount => ActiveProblems.Count;
	public bool HasActiveProblems => ActiveProblemCount > 0;

	public void NotifySessionRecovery(OperatorSessionRecoveryResult recovery)
	{
		ArgumentNullException.ThrowIfNull(recovery);

		if (recovery.PreviousSessionEndedUnexpectedly)
		{
			_notifications.Publish(new OperatorNotification(
				"session.unexpected-end",
				OperatorNotificationCategory.Recovery,
				OperatorNotificationCondition.Resolved,
				"Previous session ended unexpectedly",
				"Your safe workspace state was restored. Production outputs remain stopped until explicitly started.",
				"Operator session",
				"Review Runtime readiness before resuming production.",
				"No output or other production action was started automatically.",
				Persistent: false,
				Lifetime: TimeSpan.FromSeconds(12)));
		}

		if (recovery.StateWasCorrupted)
		{
			_notifications.Publish(new OperatorNotification(
				"session.state-corrupted",
				OperatorNotificationCategory.Warning,
				OperatorNotificationCondition.Resolved,
				"Saved session state could not be read",
				"rtaime ignored the damaged session state and continued with a safe workspace fallback.",
				"Operator workspace",
				"Review the workspace layout before production.",
				"Production actions remain stopped and authoritative Runtime state is unchanged.",
				Persistent: false,
				Lifetime: TimeSpan.FromSeconds(12)));
		}

		if (!recovery.PersistenceAvailable)
		{
			_notifications.Publish(new OperatorNotification(
				"session.persistence-unavailable",
				OperatorNotificationCategory.Warning,
				OperatorNotificationCondition.Active,
				"Session recovery storage is unavailable",
				"rtaime cannot persist the clean-shutdown marker for this session.",
				"Operator session",
				"Check access to the local rtaime application-data directory.",
				"Current production can continue, but crash recovery evidence may be unavailable after restart.",
				Persistent: true));
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_expiryTimer.Dispose();
		if (_history is not null) _history.Changed -= HistoryChanged;
		_operator.PropertyChanged -= OnOperatorPropertyChanged;
		_notifications.Published -= OnNotificationChanged;
		_notifications.Removed -= OnNotificationChanged;
	}

	private void HistoryChanged(object? sender, EventArgs e) => QueueRefresh();
	private void PublishActionOutcomes()
	{
		if (_disposed || _history is null) return;
		var current = _history.Current;
		foreach (var id in _actionStates.Keys.Where(id => !current.Any(item => item.Id == id)).ToArray()) _actionStates.Remove(id);
		foreach (var item in current)
		{
			if (_actionStates.TryGetValue(item.Id, out var outcome) && outcome == item.Outcome) continue;
			_actionStates[item.Id] = item.Outcome;
			if (item.Outcome == OperatorActionOutcome.Pending) continue;
			var confirmed = item.Outcome == OperatorActionOutcome.Confirmed;
			var rejected = item.Outcome == OperatorActionOutcome.Rejected;
			_notifications.Publish(new OperatorNotification("operator.command-outcome",
				confirmed ? OperatorNotificationCategory.Information : rejected ? OperatorNotificationCategory.Error : OperatorNotificationCategory.Warning,
				confirmed ? OperatorNotificationCondition.Informational : OperatorNotificationCondition.Active,
				confirmed ? "Control response confirmed" : rejected ? "Control request rejected" : "Command outcome unconfirmed",
				"Open action history for request identity, timestamps and available revision correlation.",
				"Control", confirmed ? "Inspect execution state for scheduled actions." : "Synchronize and inspect authoritative state before retrying.",
				"Program availability follows current authoritative state.", Persistent: !confirmed,
				TechnicalDetail: $"Request {item.Id:N} · {item.Operation} · wire command {item.CommandId ?? "not exposed"}"));
		}
	}

	private void OnNotificationChanged(object? sender, OperatorNotificationEventArgs e) =>
		QueueRefresh();

	private void OnOperatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(OperatorViewModel.LastError):
				PublishOperatorError();
				break;
			case nameof(OperatorViewModel.RecordingError):
				PublishRecordingError();
				break;
			case nameof(OperatorViewModel.EngineLifecycleState):
			case nameof(OperatorViewModel.EngineLifecycleDetail):
				PublishLifecycleState();
				break;
			case nameof(OperatorViewModel.LastEvent):
				PublishOperatorEvent();
				break;
		}
	}

	private void PublishOperatorError()
	{
		if (string.IsNullOrWhiteSpace(_operator.LastError))
			return;

		var blocked = string.Equals(_operator.ProgramSafety, OperatorProgramSafetyStates.Blocked, StringComparison.OrdinalIgnoreCase);
		_notifications.Publish(new OperatorNotification(
			"operator.last-error",
			OperatorNotificationCategory.Error,
			OperatorNotificationCondition.Active,
			"Operator action could not be completed",
			"The request or its state refresh did not complete. Check correlated action history before retrying.",
			string.IsNullOrWhiteSpace(_operator.AffectedComponent) ? "Control" : _operator.AffectedComponent,
			"Review the technical detail and synchronize authoritative state before retrying.",
			blocked
				? "Production mutations remain blocked until authoritative state recovers."
				: "Inspect authoritative Program state; this diagnostic alone does not establish command outcome.",
			Persistent: true,
			TechnicalDetail: _operator.LastError));
	}

	private void PublishRecordingError()
	{
		if (string.IsNullOrWhiteSpace(_operator.RecordingError))
		{
			_notifications.Dismiss("recording.failure");
			return;
		}

		_notifications.Publish(new OperatorNotification(
			"recording.failure",
			OperatorNotificationCategory.Error,
			OperatorNotificationCondition.Active,
			"Program recording reported a failure",
			"The recording path is impaired, but the confirmed Program path remains independent.",
			"Recording",
			"Review the recording destination and Runtime diagnostics before starting another recording.",
			"Program remains available; recording may be unavailable or incomplete.",
			Persistent: true,
			TargetWorkspace: "OUTPUTS",
			TechnicalDetail: _operator.RecordingError));
	}

	private void PublishLifecycleState()
	{
		var state = _operator.EngineLifecycleState?.Trim().ToUpperInvariant();
		switch (state)
		{
			case "RECOVERING":
				_wasRecovering = true;
				_notifications.Publish(new OperatorNotification(
					"runtime.recovery",
					OperatorNotificationCategory.Recovery,
					OperatorNotificationCondition.Active,
					"Runtime recovery is in progress",
					"rtaime is waiting for fresh authoritative state before production mutations are allowed.",
					string.IsNullOrWhiteSpace(_operator.AffectedComponent) ? "Runtime" : _operator.AffectedComponent,
					"No action is required unless recovery fails.",
					"Existing confirmed Program state may remain available, but new production mutations stay blocked.",
					Persistent: true,
					TechnicalDetail: _operator.EngineLifecycleDetail));
				break;

			case "FAILED":
				_wasRecovering = false;
				_notifications.Publish(new OperatorNotification(
					"runtime.recovery",
					OperatorNotificationCategory.Error,
					OperatorNotificationCondition.Fatal,
					"Runtime recovery failed",
					"rtaime could not restore qualified authoritative Runtime state.",
					string.IsNullOrWhiteSpace(_operator.AffectedComponent) ? "Runtime" : _operator.AffectedComponent,
					"Open Health, review diagnostics and restart the affected session only after the cause is understood.",
					"Production mutations remain blocked.",
					Persistent: true,
					TechnicalDetail: _operator.EngineLifecycleDetail));
				break;

			case "HEALTHY" when _wasRecovering:
				_wasRecovering = false;
				_notifications.Publish(new OperatorNotification(
					"runtime.recovery",
					OperatorNotificationCategory.Success,
					OperatorNotificationCondition.Resolved,
					"Runtime recovery completed",
					"Fresh authoritative state has been restored.",
					"Runtime",
					"No operator action is required.",
					"Production availability follows the current Runtime readiness state.",
					Persistent: false,
					TechnicalDetail: _operator.EngineLifecycleDetail,
					Lifetime: TimeSpan.FromSeconds(8)));
				break;

			case "HEALTHY":
				_notifications.Dismiss("runtime.recovery");
				break;
		}
	}

	private void PublishOperatorEvent()
	{
		if (string.IsNullOrWhiteSpace(_operator.LastEvent)) return;
		// Free-form event text is information, never evidence of command acceptance.
		_notifications.Publish(new OperatorNotification("operator.last-event",
			OperatorNotificationCategory.Information, OperatorNotificationCondition.Informational,
			"Operator update", _operator.LastEvent, "Operator", "Review correlated action history for command outcomes.",
			"Program availability follows authoritative state.", Persistent: false));
	}

	private void QueueRefresh()
	{
		lock (_refreshGate)
		{
			if (_disposed || _refreshPending) return;
			_refreshPending = true;
		}
		void Drain() { lock (_refreshGate) _refreshPending = false; Refresh(); }
		if (_synchronizationContext is null) Drain(); else _synchronizationContext.Post(_ => Drain(), null);
	}

	private void Refresh()
	{
		if (_disposed)
			return;

		PublishActionOutcomes();
		var current = _notifications.Current;
		var previous = Items.ToDictionary(item => item.Key, StringComparer.Ordinal);
		Items.Clear();
		foreach (var notification in current.OrderByDescending(item => item.Condition == OperatorNotificationCondition.Fatal)
			.ThenBy(item => item.Category switch { OperatorNotificationCategory.Error => 0, OperatorNotificationCategory.Warning => 1,
				OperatorNotificationCategory.Recovery => 2, OperatorNotificationCategory.Information => 3, _ => 4 })
			.ThenByDescending(item => item.Persistent).ThenByDescending(item => item.PublishedAtUtc))
		{
			if (!previous.TryGetValue(notification.Key, out var item))
				item = new OperatorNotificationItemViewModel(notification, _notifications, _navigate);
			else item.Apply(notification);
			Items.Add(item);
		}
		foreach (var group in Groups)
		{
			var desired = Items.Where(group.Includes).ToArray();
			for (var index = group.Items.Count - 1; index >= 0; index--)
				if (!desired.Contains(group.Items[index])) group.Items.RemoveAt(index);
			for (var index = 0; index < desired.Length; index++)
			{
				var oldIndex = group.Items.IndexOf(desired[index]);
				if (oldIndex < 0) group.Items.Insert(index, desired[index]);
				else if (oldIndex != index) group.Items.Move(oldIndex, index);
			}
		}
		Toasts.Clear();
		foreach (var item in Items.Where(item => current.Any(n => n.Key == item.Key && !n.Persistent && n.AcknowledgedAtUtc is null && !n.IsSnoozed(DateTimeOffset.UtcNow))).Take(4))
			Toasts.Add(item);
		ActiveProblems.Clear();
		foreach (var item in Items.Where(item => current.Any(n => n.Key == item.Key && n.Persistent &&
			n.Condition is OperatorNotificationCondition.Active or OperatorNotificationCondition.Fatal)))
			ActiveProblems.Add(item);

		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProblemCount)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActiveProblems)));
	}

	private void Post(Action action)
	{
		if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext)
		{
			action();
			return;
		}

		_synchronizationContext.Post(_ => action(), null);
	}

	private static bool ContainsAny(string value, params string[] needles) =>
		needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

internal sealed class NotificationCommand : ICommand
{
	private readonly Action _execute;
	private readonly Func<bool> _canExecute;

	public NotificationCommand(Action execute, Func<bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute ?? (() => true);
	}

	public event EventHandler? CanExecuteChanged;
	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

	public bool CanExecute(object? parameter) => _canExecute();

	public void Execute(object? parameter)
	{
		if (CanExecute(parameter))
			_execute();
	}
}
