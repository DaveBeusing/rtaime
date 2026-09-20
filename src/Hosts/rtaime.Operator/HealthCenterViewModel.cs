// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class HealthCenterSubsystemViewModel : INotifyPropertyChanged
{
	private SubsystemHealthSnapshot _snapshot;

	public HealthCenterSubsystemViewModel(SubsystemHealthSnapshot snapshot)
	{
		_snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Id => _snapshot.Id;
	public string DisplayName => _snapshot.DisplayName;
	public string Category => _snapshot.Category;
	public SubsystemHealthState HealthState => _snapshot.State;
	public string State => FormatState(_snapshot.State);
	public string Detail => _snapshot.Detail;
	public string StatusSince => _snapshot.StatusSince.ToLocalTime().ToString("HH:mm:ss");
	public string LastSuccessfulCheck => _snapshot.LastSuccessfulCheck?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
	public IReadOnlyList<HealthMetricSnapshot> Metrics => _snapshot.Metrics;
	public string RecoveryStatus => _snapshot.RecoveryStatus;
	public string TechnicalDetail => _snapshot.TechnicalDetail;
	public bool CanRecover => _snapshot.CanRecover;
	public string RecoveryActionLabel => _snapshot.RecoveryActionLabel ?? "RECOVER";
	public bool IsHealthy => _snapshot.State == SubsystemHealthState.Healthy;
	public bool RequiresAttention => HealthSnapshotAnalysis.RequiresAttention(_snapshot.State);
	public bool IsOverview => Id is "cpu" or "memory" or "gpu" or "media" or "compositing" or "output" or "frame-timing";

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
			nameof(StatusSince),
			nameof(LastSuccessfulCheck),
			nameof(Metrics),
			nameof(RecoveryStatus),
			nameof(TechnicalDetail),
			nameof(CanRecover),
			nameof(RecoveryActionLabel),
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
}

public sealed class HealthCenterViewModel : INotifyPropertyChanged, IDisposable
{
	private static readonly string[] OverviewIds =
	[
		"cpu",
		"memory",
		"gpu",
		"media",
		"compositing",
		"output",
		"frame-timing"
	];

	private readonly IHealthSnapshotProvider _provider;
	private readonly IRuntimeReadinessService _readiness;
	private readonly ICommand _synchronizeCommand;
	private readonly SynchronizationContext _uiContext;
	private HealthCenterSubsystemViewModel? _selectedSubsystem;
	private bool _technicalDetailsVisible;
	private bool _disposed;

	public HealthCenterViewModel(
		IHealthSnapshotProvider provider,
		IRuntimeReadinessService readiness,
		ICommand synchronizeCommand,
		SynchronizationContext? uiContext = null)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		_readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
		_synchronizeCommand = synchronizeCommand ?? throw new ArgumentNullException(nameof(synchronizeCommand));
		_uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();

		Subsystems = [];
		Overview = [];
		Attention = [];
		RecoverCommand = new AsyncRelayCommand(RecoverSelectedAsync, CanRecoverSelected);
		ToggleTechnicalDetailsCommand = new AsyncRelayCommand(
			() =>
			{
				TechnicalDetailsVisible = !TechnicalDetailsVisible;
				return Task.CompletedTask;
			},
			() => SelectedSubsystem is not null);

		_provider.Changed += ProviderChanged;
		_readiness.Changed += ReadinessChanged;
		ApplySnapshots(_provider.GetCurrent());
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<HealthCenterSubsystemViewModel> Subsystems { get; }
	public ObservableCollection<HealthCenterSubsystemViewModel> Overview { get; }
	public ObservableCollection<HealthCenterSubsystemViewModel> Attention { get; }
	public ICommand RecoverCommand { get; }
	public ICommand ToggleTechnicalDetailsCommand { get; }

	public HealthCenterSubsystemViewModel? SelectedSubsystem
	{
		get => _selectedSubsystem;
		set
		{
			if (!Set(ref _selectedSubsystem, value))
				return;
			TechnicalDetailsVisible = false;
			OnPropertyChanged(nameof(HasSelection));
			(RecoverCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
	public string OverallState => FormatReadiness(_readiness.Current.State);
	public string OverallSummary => _readiness.Current.Reasons.FirstOrDefault()?.Detail ??
		"All required production readiness evidence is qualified.";
	public string ProductionReadiness => _readiness.Current.IsProductionReady ? "PRODUCTION READY" : "PRODUCTION BLOCKED";
	public int HealthyCount => Subsystems.Count(item => item.IsHealthy);
	public int AttentionCount => Attention.Count;
	public int FailedCount => Subsystems.Count(item => item.State == "FAILED");

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_provider.Changed -= ProviderChanged;
		_readiness.Changed -= ReadinessChanged;
	}

	private void ProviderChanged(object? sender, HealthSnapshotChangedEventArgs e)
	{
		if (_disposed)
			return;
		var snapshots = e.Current.ToArray();
		_uiContext.Post(
			_ =>
			{
				if (!_disposed)
					ApplySnapshots(snapshots);
			},
			null);
	}

	private void ReadinessChanged(object? sender, RuntimeReadinessChangedEventArgs e)
	{
		if (_disposed)
			return;
		_uiContext.Post(
			_ =>
			{
				if (_disposed)
					return;
				OnPropertyChanged(nameof(OverallState));
				OnPropertyChanged(nameof(OverallSummary));
				OnPropertyChanged(nameof(ProductionReadiness));
			},
			null);
	}

	private void ApplySnapshots(IReadOnlyList<SubsystemHealthSnapshot> snapshots)
	{
		var byId = Subsystems.ToDictionary(item => item.Id, StringComparer.Ordinal);
		var ordered = new List<HealthCenterSubsystemViewModel>(snapshots.Count);
		foreach (var snapshot in snapshots)
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
				.ThenBy(item => item.DisplayName, StringComparer.Ordinal)
				.ToArray());

		var selectedId = SelectedSubsystem?.Id;
		SelectedSubsystem = selectedId is null
			? ordered.FirstOrDefault(item => item.RequiresAttention) ?? ordered.FirstOrDefault()
			: ordered.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal))
				?? ordered.FirstOrDefault();

		OnPropertyChanged(nameof(HealthyCount));
		OnPropertyChanged(nameof(AttentionCount));
		OnPropertyChanged(nameof(FailedCount));
	}

	private Task RecoverSelectedAsync()
	{
		if (SelectedSubsystem?.Id == "control" && _synchronizeCommand.CanExecute(null))
			_synchronizeCommand.Execute(null);
		return Task.CompletedTask;
	}

	private bool CanRecoverSelected() =>
		SelectedSubsystem is { CanRecover: true, Id: "control" } &&
		_synchronizeCommand.CanExecute(null);

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
