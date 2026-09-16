// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient? _client;
	private OperatorSourceDescriptor? _selectedSource;
	private string _previewSourceName = "—";
	private string _previewSourceId = "—";
	private string _programSourceName = "—";
	private string _programSourceId = "—";
	private string _runtimeStatus = "DISCONNECTED";
	private string _timingStatus = "UNKNOWN";
	private string _inputStatus = "UNKNOWN";
	private string _aiStatus = "UNKNOWN";
	private string _recordingStatus = "UNKNOWN";
	private string _visualLayerStatus = "UNKNOWN";
	private string _audioPeak = "0.000";
	private string _audioPeakPercent = "0%";
	private string _connectionState = "DISCONNECTED";
	private string _connectionDetail = "Synchronize to load authoritative state.";
	private string _commandStatus = "IDLE";
	private string _lastEvent = "Operator started. Synchronization pending.";
	private string _revisionLabel = "REV —";
	private uint _transitionFrames = 12;
	private string? _lastError;
	private bool _isBusy;
	private bool _isConnected;
	private bool _isStale;

	public OperatorViewModel(OperatorControlClient? client = null)
	{
		_client = client;
		Sources = new ObservableCollection<OperatorSourceDescriptor>();
		SynchronizeCommand = new AsyncRelayCommand(SynchronizeAsync, () => _client is not null && !IsBusy);
		SetPreviewCommand = new AsyncRelayCommand(SetPreviewAsync, CanMutate);
		CutCommand = new AsyncRelayCommand(CutAsync, CanMutate);
		DissolveCommand = new AsyncRelayCommand(DissolveAsync, () => CanMutate() && TransitionFrames >= 2);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OperatorSourceDescriptor> Sources { get; }
	public ICommand SynchronizeCommand { get; }
	public ICommand SetPreviewCommand { get; }
	public ICommand CutCommand { get; }
	public ICommand DissolveCommand { get; }

	public string MonitoringStatus => "Monitoring unavailable until AP-29";
	public string FormatStatus => "Format metadata is not exposed by the management snapshot.";

	public OperatorSourceDescriptor? SelectedSource
	{
		get => _selectedSource;
		set
		{
			if (Set(ref _selectedSource, value))
				RaiseCommandState();
		}
	}

	public string PreviewSourceName { get => _previewSourceName; private set => Set(ref _previewSourceName, value); }
	public string PreviewSourceId { get => _previewSourceId; private set => Set(ref _previewSourceId, value); }
	public string ProgramSourceName { get => _programSourceName; private set => Set(ref _programSourceName, value); }
	public string ProgramSourceId { get => _programSourceId; private set => Set(ref _programSourceId, value); }
	public string RuntimeStatus { get => _runtimeStatus; private set => Set(ref _runtimeStatus, value); }
	public string TimingStatus { get => _timingStatus; private set => Set(ref _timingStatus, value); }
	public string InputStatus { get => _inputStatus; private set => Set(ref _inputStatus, value); }
	public string AIStatus { get => _aiStatus; private set => Set(ref _aiStatus, value); }
	public string RecordingStatus { get => _recordingStatus; private set => Set(ref _recordingStatus, value); }
	public string VisualLayerStatus { get => _visualLayerStatus; private set => Set(ref _visualLayerStatus, value); }
	public string AudioPeak { get => _audioPeak; private set => Set(ref _audioPeak, value); }
	public string AudioPeakPercent { get => _audioPeakPercent; private set => Set(ref _audioPeakPercent, value); }
	public string ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }
	public string ConnectionDetail { get => _connectionDetail; private set => Set(ref _connectionDetail, value); }
	public string CommandStatus { get => _commandStatus; private set => Set(ref _commandStatus, value); }
	public string LastEvent { get => _lastEvent; private set => Set(ref _lastEvent, value); }
	public string RevisionLabel { get => _revisionLabel; private set => Set(ref _revisionLabel, value); }
	public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }
	public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
	public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
	public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }

	public uint TransitionFrames
	{
		get => _transitionFrames;
		set
		{
			if (Set(ref _transitionFrames, value))
				RaiseCommandState();
		}
	}

	private bool CanMutate() =>
		_client is not null &&
		SelectedSource is not null &&
		IsConnected &&
		!IsStale &&
		!IsBusy &&
		string.Equals(RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase);

	private async Task SynchronizeAsync()
	{
		if (_client is null)
		{
			LastError = "Remote control transport is not configured.";
			ConnectionState = "DISCONNECTED";
			CommandStatus = "UNAVAILABLE";
			return;
		}

		await ExecuteAsync("SYNCHRONIZE", async () =>
		{
			Apply(await _client.SynchronizeAsync());
			CommandStatus = "SYNCHRONIZED";
			LastEvent = "Authoritative state synchronized from ControlHost.";
		});
	}

	private async Task SetPreviewAsync()
	{
		if (_client is null || SelectedSource is null) return;
		var source = SelectedSource;
		await ExecuteAsync("SET PREVIEW", async () =>
		{
			var response = await _client.SelectPreviewAsync(source.Id);
			if (!Accept(response, "Set Preview")) return;
			Apply(_client.Snapshot!);
			CommandStatus = "APPLIED";
			LastEvent = $"Preview source changed to {source.Name}.";
		});
	}

	private async Task CutAsync()
	{
		if (_client is null || SelectedSource is null) return;
		var source = SelectedSource;
		await ExecuteAsync("CUT", async () =>
		{
			var response = await _client.CutAsync(source.Id);
			if (!Accept(response, "CUT")) return;
			Apply(_client.Snapshot!);
			CommandStatus = "APPLIED";
			LastEvent = $"CUT committed to {source.Name}.";
		});
	}

	private async Task DissolveAsync()
	{
		if (_client is null || SelectedSource is null) return;
		var source = SelectedSource;
		var durationFrames = TransitionFrames;
		await ExecuteAsync("DISSOLVE", async () =>
		{
			var response = await _client.DissolveAsync(source.Id, durationFrames);
			if (!Accept(response, "DISSOLVE")) return;
			Apply(_client.Snapshot!);
			CommandStatus = "APPLIED";
			LastEvent = $"DISSOLVE committed to {source.Name} over {durationFrames} frames.";
		});
	}

	private bool Accept(OperatorMutationResponse response, string operation)
	{
		if (response.Accepted) return true;

		var message = response.Failure?.Message ?? $"{operation} command rejected.";
		CommandStatus = "REJECTED";
		LastError = message;
		LastEvent = $"{operation} rejected by authoritative control.";
		return false;
	}

	private async Task ExecuteAsync(string operation, Func<Task> action)
	{
		if (IsBusy) return;

		IsBusy = true;
		CommandStatus = $"{operation} IN FLIGHT";
		LastError = null;
		RaiseCommandState();
		try
		{
			await action();
		}
		catch (RemoteHostSessionChangedException) when (_client is not null)
		{
			IsStale = true;
			ConnectionState = "RESYNCING";
			ConnectionDetail = "ControlHost session changed. Full authoritative resynchronization is required.";
			CommandStatus = "RESYNC REQUIRED";
			RaiseCommandState();
			try
			{
				Apply(await _client.SynchronizeAsync());
				CommandStatus = "RESYNCHRONIZED";
				LastError = "ControlHost restarted. Authoritative state was resynchronized; repeat the requested operation.";
				LastEvent = "ControlHost session changed and a full snapshot was restored.";
			}
			catch (Exception recoveryException)
			{
				MarkStale($"ControlHost session changed and resynchronization failed: {recoveryException.Message}");
				CommandStatus = "RESYNC FAILED";
			}
		}
		catch (Exception exception)
		{
			if (exception is IOException or TimeoutException or OperationCanceledException)
				MarkStale(exception.Message);
			else
				LastError = exception.Message;

			CommandStatus = "FAILED";
			LastEvent = $"{operation} failed.";
		}
		finally
		{
			IsBusy = false;
			RaiseCommandState();
		}
	}

	private void Apply(OperatorStatusSnapshot snapshot)
	{
		var previousSelectionId = SelectedSource?.Id;
		Sources.Clear();
		foreach (var source in snapshot.Sources)
			Sources.Add(source);

		var previewId = snapshot.Production.Routing.PreviewSourceId.ToString();
		var programId = snapshot.Production.Routing.ProgramSourceId.ToString();
		SelectedSource = Sources.FirstOrDefault(source => string.Equals(source.Id, previousSelectionId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault(source => string.Equals(source.Id, previewId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault();

		PreviewSourceId = previewId;
		ProgramSourceId = programId;
		PreviewSourceName = ResolveSourceName(snapshot, previewId);
		ProgramSourceName = ResolveSourceName(snapshot, programId);
		RuntimeStatus = snapshot.RuntimeStatus;
		TimingStatus = snapshot.TimingStatus;
		InputStatus = snapshot.InputStatus;
		AIStatus = snapshot.AIStatus;
		RecordingStatus = snapshot.RecordingStatus;
		VisualLayerStatus = snapshot.VisualLayerEnabled ? "ENABLED" : "DISABLED";
		AudioPeak = snapshot.AudioPeakLevel.ToString("0.000", CultureInfo.InvariantCulture);
		AudioPeakPercent = snapshot.AudioPeakLevel.ToString("P0", CultureInfo.InvariantCulture);
		RevisionLabel = $"REV {snapshot.Production.Revision.Value}";
		IsConnected = true;
		IsStale = false;
		ConnectionState = string.Equals(snapshot.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase)
			? "CONNECTED"
			: "DEGRADED";
		ConnectionDetail = $"Authoritative snapshot loaded. Runtime status: {snapshot.RuntimeStatus}.";
		RaiseCommandState();
	}

	private void MarkStale(string detail)
	{
		IsConnected = false;
		IsStale = true;
		ConnectionState = "STALE";
		ConnectionDetail = detail;
		LastError = detail;
		RaiseCommandState();
	}

	private static string ResolveSourceName(OperatorStatusSnapshot snapshot, string sourceId) =>
		snapshot.Sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal))?.Name ?? sourceId;

	private void RaiseCommandState()
	{
		(SynchronizeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(SetPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(CutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(DissolveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		return true;
	}
}

internal sealed class AsyncRelayCommand : ICommand
{
	private readonly Func<Task> _execute;
	private readonly Func<bool> _canExecute;
	private bool _executing;

	public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute ?? (() => true);
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => !_executing && _canExecute();

	public async void Execute(object? parameter)
	{
		if (!CanExecute(parameter)) return;
		_executing = true;
		RaiseCanExecuteChanged();
		try
		{
			await _execute();
		}
		finally
		{
			_executing = false;
			RaiseCanExecuteChanged();
		}
	}

	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
