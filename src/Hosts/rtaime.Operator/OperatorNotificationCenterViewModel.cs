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

	public OperatorNotificationItemViewModel(OperatorNotification notification)
	{
		_notification = notification ?? throw new ArgumentNullException(nameof(notification));
		ToggleTechnicalDetailsCommand = new NotificationCommand(
			() => IsTechnicalDetailsVisible = !IsTechnicalDetailsVisible,
			() => HasTechnicalDetail);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

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

public sealed class OperatorNotificationCenterViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly INotificationService _notifications;
	private readonly OperatorViewModel _operator;
	private readonly SynchronizationContext? _synchronizationContext;
	private readonly Timer _expiryTimer;
	private bool _wasRecovering;
	private bool _disposed;

	public OperatorNotificationCenterViewModel(
		INotificationService notifications,
		OperatorViewModel operatorViewModel,
		SynchronizationContext? synchronizationContext = null)
	{
		_notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
		_operator = operatorViewModel ?? throw new ArgumentNullException(nameof(operatorViewModel));
		_synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
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

	public ObservableCollection<OperatorNotificationItemViewModel> Toasts { get; }
	public ObservableCollection<OperatorNotificationItemViewModel> ActiveProblems { get; }
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
		_operator.PropertyChanged -= OnOperatorPropertyChanged;
		_notifications.Published -= OnNotificationChanged;
		_notifications.Removed -= OnNotificationChanged;
	}

	private void OnNotificationChanged(object? sender, OperatorNotificationEventArgs e) =>
		Post(Refresh);

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
			"The requested action was not committed. rtaime kept the last confirmed production state.",
			string.IsNullOrWhiteSpace(_operator.AffectedComponent) ? "Control" : _operator.AffectedComponent,
			"Review the technical detail and synchronize authoritative state before retrying.",
			blocked
				? "Production mutations remain blocked until authoritative state recovers."
				: "Existing Program state remains available; the failed action was not applied.",
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
		if (string.IsNullOrWhiteSpace(_operator.LastEvent))
			return;

		var success = ContainsAny(
			_operator.LastEvent,
			"confirmed",
			"started",
			"finalized",
			"restored",
			"loaded",
			"enabled",
			"disabled",
			"synchronized",
			"saved");

		if (success &&
			_operator.IsConnected &&
			!_operator.IsStale &&
			string.IsNullOrWhiteSpace(_operator.LastError))
		{
			_notifications.Dismiss("operator.last-error");
		}

		_notifications.Publish(new OperatorNotification(
			"operator.last-event",
			success ? OperatorNotificationCategory.Success : OperatorNotificationCategory.Information,
			OperatorNotificationCondition.Informational,
			success ? "Operation completed" : "Operator update",
			_operator.LastEvent,
			"Operator",
			"No action is required.",
			"Production state is unchanged unless the event explicitly confirms a committed production action.",
			Persistent: false));
	}

	private void Refresh()
	{
		if (_disposed)
			return;

		var current = _notifications.Current;
		Toasts.Clear();
		foreach (var notification in current
			.Where(candidate => !candidate.Persistent)
			.OrderByDescending(candidate => candidate.PublishedAtUtc)
			.Take(4))
		{
			Toasts.Add(new OperatorNotificationItemViewModel(notification));
		}

		ActiveProblems.Clear();
		foreach (var notification in current
			.Where(candidate =>
				candidate.Persistent &&
				(candidate.Condition is OperatorNotificationCondition.Active or OperatorNotificationCondition.Fatal))
			.OrderByDescending(candidate => candidate.Condition == OperatorNotificationCondition.Fatal)
			.ThenByDescending(candidate => candidate.PublishedAtUtc))
		{
			ActiveProblems.Add(new OperatorNotificationItemViewModel(notification));
		}

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

	public event EventHandler? CanExecuteChanged
	{
		add { }
		remove { }
	}

	public bool CanExecute(object? parameter) => _canExecute();

	public void Execute(object? parameter)
	{
		if (CanExecute(parameter))
			_execute();
	}
}
