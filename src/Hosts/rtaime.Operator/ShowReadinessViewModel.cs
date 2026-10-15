// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Media.Contracts;

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
	public string State => _check.State == ShowPreflightState.NotApplicable ? "NOT APPLICABLE" : _check.State.ToString().ToUpperInvariant();
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
	private readonly Action<string> _navigate;
	private readonly SynchronizationContext? _context;
	private readonly object _gate = new();
	private readonly Timer _timer;
	private readonly CancellationTokenSource _stop = new();
	private bool _queued, _disposed, _reading;
	private MediaAssetCatalogSnapshot? _catalogue;
	private DateTimeOffset? _catalogueAt;
	private IReadOnlyList<OperatorActionObservation> _previousHistory = [];
	private string _readStatus = "Catalogue has not been read in this session.";
	public ShowReadinessViewModel(OperatorControlClient client, OperatorViewModel owner,
		IHealthSnapshotProvider health, OperatorNotificationCenterViewModel notifications, Action<string> navigate,
		SynchronizationContext? context = null)
	{
		_client = client; _operator = owner; _health = health; Notifications = notifications; _navigate = navigate;
		_context = context ?? SynchronizationContext.Current;
		ReadCatalogueCommand = new AsyncRelayCommand(ReadCatalogueAsync, () => !_reading && !_disposed && owner.IsConnected && !owner.IsStale);
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
	public ICommand ReadCatalogueCommand { get; }
	public string ReadStatus => _readStatus;
	public string Summary { get; private set; } = "Read-only observations; ControlHost owns launch policy.";
	public string Revision => _operator.IsConnected && !_operator.IsStale && _client.Snapshot is { } s
		? $"Production revision {s.Production.Revision.Value} · observed {_client.SnapshotObservedAtUtc:HH:mm:ss} UTC"
		: "UNKNOWN · current authoritative state unavailable";

	public async Task ReadCatalogueAsync()
	{
		if (_reading || _disposed || !_operator.IsConnected || _operator.IsStale) return;
		_reading = true; ((AsyncRelayCommand)ReadCatalogueCommand).RaiseCanExecuteChanged();
		try
		{
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
			deadline.CancelAfter(TimeSpan.FromSeconds(5));
			var result = await _client.GetMediaAssetCatalogAsync(deadline.Token);
			if (_disposed) return;
			_catalogue = result; _catalogueAt = DateTimeOffset.UtcNow;
			_readStatus = $"Catalogue revision {result.Revision} read at {_catalogueAt:HH:mm:ss} UTC. No availability rescan or production command was sent.";
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or TimeoutException or OperationCanceledException)
		{
			if (_disposed) return;
			_catalogue = null; _catalogueAt = null;
			_readStatus = "UNKNOWN · catalogue read unavailable. Review the existing Media Library connection.";
		}
		finally { _reading = false; QueueRefresh(); }
	}
	private void Changed(object? sender, EventArgs e) => QueueRefresh();
	private void HealthChanged(object? sender, HealthSnapshotChangedEventArgs e) => QueueRefresh();
	private void OperatorChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale))
		{
			if (!_operator.IsConnected || _operator.IsStale) { _catalogue = null; _catalogueAt = null; }
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
		var current = ShowPreflightSummary.Build(_client.Snapshot, fresh,
			_catalogue, _catalogueAt, _health.GetCurrent(), DateTimeOffset.UtcNow);
		foreach (var check in current)
		{
			var item = Checks.FirstOrDefault(candidate => candidate.Id == check.Id);
			if (item is null) Checks.Add(new(check, _navigate)); else item.Apply(check);
		}
		Summary = $"Read-only checks · {current.Count(item => item.State == ShowPreflightState.Fail)} failed · " +
			$"{current.Count(item => item.State == ShowPreflightState.Unknown)} unknown. ARM/GO remain validated by ControlHost.";
		var history = _client.ActionHistory.Current;
		if (!_previousHistory.SequenceEqual(history))
		{
			_previousHistory = history;
			History.Clear();
			foreach (var item in history.Reverse()) History.Add(new(item));
		}
		((AsyncRelayCommand)ReadCatalogueCommand).RaiseCanExecuteChanged();
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
	}
	public void Dispose()
	{
		lock (_gate) { if (_disposed) return; _disposed = true; }
		_timer.Dispose(); _stop.Cancel(); _stop.Dispose();
		_client.ActionHistory.Changed -= Changed; _client.SnapshotChanged -= Changed;
		_health.Changed -= HealthChanged; _operator.PropertyChanged -= OperatorChanged;
	}
}
