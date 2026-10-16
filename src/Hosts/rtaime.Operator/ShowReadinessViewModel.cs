// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class ShowPreflightCheckViewModel : INotifyPropertyChanged
{
	private ShowPreflightCheck _check;
	public ShowPreflightCheckViewModel(ShowPreflightCheck check, Action<string> navigate)
	{
		_check = check;
		NavigateCommand = new NotificationCommand(() => navigate(_check.Workspace));
	}
	public string Id => _check.Id;
	public string Label => _check.Label;
	public string State => _check.State switch { ShowPreflightState.NotApplicable => "NOT APPLICABLE", ShowPreflightState.Unknown => "UNVERIFIED", _ => _check.State.ToString().ToUpperInvariant() };
	public string Detail => _check.Detail;
	public ICommand NavigateCommand { get; }
	public event PropertyChangedEventHandler? PropertyChanged;
	public void Apply(ShowPreflightCheck check)
	{
		if (_check == check) return;
		_check = check;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
	}
}

public sealed record OperatorActionHistoryRow(OperatorActionObservation Observation)
{
	public string Operation => System.Text.RegularExpressions.Regex.Replace(
		Observation.Operation.Replace("Async", "", StringComparison.Ordinal), "(?<!^)([A-Z])", " $1");
	public string Outcome => Observation.Outcome.ToString().ToUpperInvariant();
	public string ResponseEvidence => Observation.ResponseAccepted is { } accepted
		? accepted ? "ACCEPTED / SUCCEEDED · Control response; Runtime completion is not exposed by this receipt."
			: "REJECTED / FAILED · explicit negative Control response."
		: Observation.Outcome == OperatorActionOutcome.Confirmed
			? "RESPONSE RECEIVED · acceptance and Runtime commitment are not exposed."
			: "NO RESPONSE EVIDENCE · do not infer rejection or repeat the action.";
	public string Provenance => $"Source: rtaime.Client / Control response · production {Observation.ObservedProductionId ?? "not observed"} · " +
		$"local observation epoch {Observation.ClientObservationEpoch?.ToString("N") ?? "not exposed"} · snapshot {Observation.SnapshotObservedAtUtc?.ToString("O") ?? "not observed"} · server epoch not exposed";
	public string Times => $"Requested {Observation.PendingAtUtc.ToLocalTime():HH:mm:ss.fff} · " +
		(Observation.CompletedAtUtc is { } completed ? $"response {completed.ToLocalTime():HH:mm:ss.fff}" : "awaiting response");
	public string Correlation => $"Request {Observation.Id:N} · wire command {Observation.CommandId ?? "not exposed"} · " +
		$"observed production revision {Observation.ObservedProductionRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} · " +
		$"expected production revision {Observation.ExpectedProductionRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} · " +
		$"response production revision {Observation.ResponseProductionRevision?.ToString(CultureInfo.InvariantCulture) ?? "not exposed"}";
}

public sealed class ShowReadinessViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly OperatorControlClient _client;
	private readonly OperatorViewModel _operator;
	private readonly IHealthSnapshotProvider _health;
	private readonly MediaPoolInspectorViewModel? _mediaPool;
	private readonly Action<string> _navigate;
	private readonly SynchronizationContext? _context;
	private readonly object _gate = new();
	private readonly Timer _timer;
	private bool _queued, _disposed;
	private DateTimeOffset _catalogueEpochStartUtc = DateTimeOffset.MinValue;
	private IReadOnlyList<OperatorActionObservation> _previousHistory = [];
	public ShowReadinessViewModel(OperatorControlClient client, OperatorViewModel owner,
		IHealthSnapshotProvider health, OperatorNotificationCenterViewModel notifications, Action<string> navigate,
		SynchronizationContext? context = null, MediaPoolInspectorViewModel? mediaPool = null)
	{
		_mediaPool = mediaPool;
		if (_mediaPool is not null) _mediaPool.PropertyChanged += CatalogueChanged;
		_client = client; _operator = owner; _health = health; Notifications = notifications; _navigate = navigate;
		_context = context ?? SynchronizationContext.Current;
		RefreshChecksCommand = new NotificationCommand(QueueRefresh, () => !_disposed);
		_client.ActionHistory.Changed += Changed;
		_client.SnapshotChanged += Changed;
		_health.Changed += HealthChanged;
		_operator.PropertyChanged += OperatorChanged;
		Refresh();
		_timer = new Timer(_ => QueueRefresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
	public event PropertyChangedEventHandler? PropertyChanged;
	public ObservableCollection<ShowPreflightCheckViewModel> Checks { get; } = [];
	public ObservableCollection<OperatorActionHistoryRow> History { get; } = [];
	public OperatorNotificationCenterViewModel Notifications { get; }
	public ICommand RefreshChecksCommand { get; }
	public string ReadStatus => _mediaPool?.CatalogObservedAtUtc is { } at
		? $"Media Library evidence received {at:HH:mm:ss} UTC. Refresh checks performs no server request. Use Media Library to refresh availability."
		: "UNKNOWN · Media Library catalogue evidence has not been received.";
	public string Summary { get; private set; } = "Read-only observations; ControlHost owns launch policy.";
	public string Revision => _operator.IsConnected && !_operator.IsStale && _client.Snapshot is { } s
		? $"Production revision {s.Production.Revision.Value} · observed {_client.SnapshotObservedAtUtc:HH:mm:ss} UTC"
		: "UNKNOWN · current authoritative state unavailable";

	private void CatalogueChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(MediaPoolInspectorViewModel.CatalogEvidence) or nameof(MediaPoolInspectorViewModel.IsLoading)) QueueRefresh();
	}
	private void Changed(object? sender, EventArgs e) => QueueRefresh();
	private void HealthChanged(object? sender, HealthSnapshotChangedEventArgs e) => QueueRefresh();
	private void OperatorChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale))
		{
			if (!_operator.IsConnected || _operator.IsStale) _catalogueEpochStartUtc = DateTimeOffset.UtcNow;
			QueueRefresh();
		}
	}
	private void QueueRefresh()
	{
		lock (_gate) { if (_disposed || _queued) return; _queued = true; }
		void Drain() { lock (_gate) { _queued = false; if (_disposed) return; } Refresh(); }
		if (_context is null) Drain();
		else _context.Post(_ => Drain(), null);
	}
	private void Refresh()
	{
		if (_disposed) return;
		var now = DateTimeOffset.UtcNow;
		var fresh = _operator.IsConnected && !_operator.IsStale && _client.SnapshotObservedAtUtc is { } observed &&
			now >= observed && now - observed <= TimeSpan.FromSeconds(10);
		var catalogueCurrent = _mediaPool?.CatalogObservedAtUtc is { } catalogueAt && catalogueAt >= _catalogueEpochStartUtc;
		var current = ShowPreflightSummary.Build(_client.Snapshot, fresh,
			catalogueCurrent && _mediaPool?.IsLoading == false ? _mediaPool?.CatalogEvidence : null,
			_mediaPool?.CatalogObservedAtUtc, _health.GetCurrent(), now);
		foreach (var check in current)
		{
			var item = Checks.FirstOrDefault(candidate => candidate.Id == check.Id);
			if (item is null) Checks.Add(new(check, _navigate)); else item.Apply(check);
		}
		Summary = $"Read-only checks · {current.Count(item => item.State == ShowPreflightState.Fail)} failed · " +
			$"{current.Count(item => item.State == ShowPreflightState.Unknown)} unverified. ARM/GO remain validated by ControlHost.";
		var history = _client.ActionHistory.Current;
		if (!_previousHistory.SequenceEqual(history))
		{
			_previousHistory = history;
			History.Clear();
			foreach (var item in history.Reverse()) History.Add(new(item));
		}
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
	}
	public void Dispose()
	{
		lock (_gate) { if (_disposed) return; _disposed = true; }
		_timer.Dispose();
		if (_mediaPool is not null) _mediaPool.PropertyChanged -= CatalogueChanged;
		_client.ActionHistory.Changed -= Changed; _client.SnapshotChanged -= Changed;
		_health.Changed -= HealthChanged; _operator.PropertyChanged -= OperatorChanged;
	}
}
