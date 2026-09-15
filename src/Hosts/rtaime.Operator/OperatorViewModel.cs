// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient? _client;
	private OperatorSourceDescriptor? _selectedSource;
	private string _preview = "—";
	private string _program = "—";
	private string _runtimeStatus = "DISCONNECTED";
	private string _timingStatus = "UNKNOWN";
	private string _inputStatus = "UNKNOWN";
	private string _aiStatus = "UNKNOWN";
	private string _recordingStatus = "UNKNOWN";
	private string _audioPeak = "0.000";
	private uint _transitionFrames = 12;
	private string? _lastError;

	public OperatorViewModel(OperatorControlClient? client = null)
	{
		_client = client;
		Sources = new ObservableCollection<OperatorSourceDescriptor>();
		SynchronizeCommand = new AsyncRelayCommand(SynchronizeAsync, () => _client is not null);
		SetPreviewCommand = new AsyncRelayCommand(SetPreviewAsync, () => _client is not null && SelectedSource is not null);
		CutCommand = new AsyncRelayCommand(CutAsync, () => _client is not null && SelectedSource is not null);
		DissolveCommand = new AsyncRelayCommand(DissolveAsync, () => _client is not null && SelectedSource is not null && TransitionFrames >= 2);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OperatorSourceDescriptor> Sources { get; }
	public ICommand SynchronizeCommand { get; }
	public ICommand SetPreviewCommand { get; }
	public ICommand CutCommand { get; }
	public ICommand DissolveCommand { get; }

	public OperatorSourceDescriptor? SelectedSource
	{
		get => _selectedSource;
		set
		{
			if (Set(ref _selectedSource, value))
				RaiseCommandState();
		}
	}

	public string Preview { get => _preview; private set => Set(ref _preview, value); }
	public string Program { get => _program; private set => Set(ref _program, value); }
	public string RuntimeStatus { get => _runtimeStatus; private set => Set(ref _runtimeStatus, value); }
	public string TimingStatus { get => _timingStatus; private set => Set(ref _timingStatus, value); }
	public string InputStatus { get => _inputStatus; private set => Set(ref _inputStatus, value); }
	public string AIStatus { get => _aiStatus; private set => Set(ref _aiStatus, value); }
	public string RecordingStatus { get => _recordingStatus; private set => Set(ref _recordingStatus, value); }
	public string AudioPeak { get => _audioPeak; private set => Set(ref _audioPeak, value); }
	public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

	public uint TransitionFrames
	{
		get => _transitionFrames;
		set
		{
			if (Set(ref _transitionFrames, value))
				RaiseCommandState();
		}
	}

	private async Task SynchronizeAsync()
	{
		if (_client is null)
		{
			LastError = "Remote control transport is not configured.";
			return;
		}

		await ExecuteAsync(async () => Apply(await _client.SynchronizeAsync()));
	}

	private async Task SetPreviewAsync()
	{
		if (_client is null || SelectedSource is null) return;
		await ExecuteAsync(async () =>
		{
			var response = await _client.SelectPreviewAsync(SelectedSource.Id);
			if (!response.Accepted) throw new InvalidOperationException(response.Failure?.Message ?? "Preview command rejected.");
			Apply(_client.Snapshot!);
		});
	}

	private async Task CutAsync()
	{
		if (_client is null || SelectedSource is null) return;
		await ExecuteAsync(async () =>
		{
			var response = await _client.CutAsync(SelectedSource.Id);
			if (!response.Accepted) throw new InvalidOperationException(response.Failure?.Message ?? "CUT command rejected.");
			Apply(_client.Snapshot!);
		});
	}

	private async Task DissolveAsync()
	{
		if (_client is null || SelectedSource is null) return;
		await ExecuteAsync(async () =>
		{
			var response = await _client.DissolveAsync(SelectedSource.Id, TransitionFrames);
			if (!response.Accepted) throw new InvalidOperationException(response.Failure?.Message ?? "DISSOLVE command rejected.");
			Apply(_client.Snapshot!);
		});
	}

	private async Task ExecuteAsync(Func<Task> action)
	{
		try
		{
			LastError = null;
			await action();
		}
		catch (RemoteHostSessionChangedException) when (_client is not null)
		{
			try
			{
				Apply(await _client.SynchronizeAsync());
				LastError = "ControlHost restarted. Authoritative state was resynchronized; repeat the requested operation.";
			}
			catch (Exception recoveryException)
			{
				LastError = $"ControlHost session changed and resynchronization failed: {recoveryException.Message}";
			}
		}
		catch (Exception exception)
		{
			LastError = exception.Message;
		}
	}

	private void Apply(OperatorStatusSnapshot snapshot)
	{
		Sources.Clear();
		foreach (var source in snapshot.Sources)
			Sources.Add(source);
		SelectedSource ??= Sources.FirstOrDefault();

		Preview = snapshot.Production.Routing.PreviewSourceId.ToString();
		Program = snapshot.Production.Routing.ProgramSourceId.ToString();
		RuntimeStatus = snapshot.RuntimeStatus;
		TimingStatus = snapshot.TimingStatus;
		InputStatus = snapshot.InputStatus;
		AIStatus = snapshot.AIStatus;
		RecordingStatus = snapshot.RecordingStatus;
		AudioPeak = snapshot.AudioPeakLevel.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
		RaiseCommandState();
	}

	private void RaiseCommandState()
	{
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
