// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed class ShowControlViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient _client;
	private ShowControlCueListEditorItem? _selectedCueList;
	private ShowControlCueEditorItem? _selectedCue;
	private ShowControlActionEditorItem? _selectedAction;
	private ShowControlActionKind _newActionKind = ShowControlActionKind.Cut;
	private string _primaryReference = string.Empty;
	private string _secondaryReference = string.Empty;
	private string _frameValue = "12";
	private bool _visibilityValue = true;
	private string _executionState = "IDLE";
	private string _executionDetail = "No cue list is armed.";
	private string _currentCue = "—";
	private string _currentAction = "—";
	private string _failure = "NONE";
	private string _status = "READY";
	private ShowControlCueListId? _serverSelectedCueListId;
	private ShowControlExecutionState _serverExecutionState = ShowControlExecutionState.Idle;
	private bool _requiresAcknowledgement;

	public ShowControlViewModel(OperatorControlClient client)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		CueLists = [];
		ActionKinds = Enum.GetValues<ShowControlActionKind>();
		RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
		NewCueListCommand = new AsyncRelayCommand(NewCueListAsync, () => !IsBusy);
		SelectCueListCommand = new AsyncRelayCommand(SelectCueListAsync, () => !IsBusy && SelectedCueList is not null);
		SaveCueListCommand = new AsyncRelayCommand(SaveCueListAsync, () => !IsBusy && SelectedCueList is not null);
		AddCueCommand = new AsyncRelayCommand(AddCueAsync, () => !IsBusy && SelectedCueList is not null);
		RemoveCueCommand = new AsyncRelayCommand(RemoveCueAsync, () => !IsBusy && SelectedCueList is not null && SelectedCue is not null && SelectedCueList.Cues.Count > 1);
		MoveCueUpCommand = new AsyncRelayCommand(() => MoveCueAsync(-1), () => CanMoveCue(-1));
		MoveCueDownCommand = new AsyncRelayCommand(() => MoveCueAsync(1), () => CanMoveCue(1));
		AddActionCommand = new AsyncRelayCommand(AddActionAsync, () => !IsBusy && SelectedCue is not null);
		RemoveActionCommand = new AsyncRelayCommand(RemoveActionAsync, () => !IsBusy && SelectedCue is not null && SelectedAction is not null && SelectedCue.Actions.Count > 1);
		MoveActionUpCommand = new AsyncRelayCommand(() => MoveActionAsync(-1), () => CanMoveAction(-1));
		MoveActionDownCommand = new AsyncRelayCommand(() => MoveActionAsync(1), () => CanMoveAction(1));
		ArmCommand = new AsyncRelayCommand(ArmAsync, CanArm);
		GoCommand = new AsyncRelayCommand(GoAsync, CanGo);
		CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
		ResumeRecoveryCommand = new AsyncRelayCommand(() => AcknowledgeRecoveryAsync(true), () => !IsBusy && _requiresAcknowledgement);
		CancelRecoveryCommand = new AsyncRelayCommand(() => AcknowledgeRecoveryAsync(false), () => !IsBusy && _requiresAcknowledgement);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<ShowControlCueListEditorItem> CueLists { get; }
	public IReadOnlyList<ShowControlActionKind> ActionKinds { get; }

	public ICommand RefreshCommand { get; }
	public ICommand NewCueListCommand { get; }
	public ICommand SelectCueListCommand { get; }
	public ICommand SaveCueListCommand { get; }
	public ICommand AddCueCommand { get; }
	public ICommand RemoveCueCommand { get; }
	public ICommand MoveCueUpCommand { get; }
	public ICommand MoveCueDownCommand { get; }
	public ICommand AddActionCommand { get; }
	public ICommand RemoveActionCommand { get; }
	public ICommand MoveActionUpCommand { get; }
	public ICommand MoveActionDownCommand { get; }
	public ICommand ArmCommand { get; }
	public ICommand GoCommand { get; }
	public ICommand CancelCommand { get; }
	public ICommand ResumeRecoveryCommand { get; }
	public ICommand CancelRecoveryCommand { get; }

	public ShowControlCueListEditorItem? SelectedCueList
	{
		get => _selectedCueList;
		set
		{
			if (!Set(ref _selectedCueList, value))
				return;
			SelectedCue = value?.Cues.FirstOrDefault();
			RaiseCommandState();
		}
	}

	public ShowControlCueEditorItem? SelectedCue
	{
		get => _selectedCue;
		set
		{
			if (!Set(ref _selectedCue, value))
				return;
			SelectedAction = value?.Actions.FirstOrDefault();
			RaiseCommandState();
		}
	}

	public ShowControlActionEditorItem? SelectedAction
	{
		get => _selectedAction;
		set
		{
			if (!Set(ref _selectedAction, value))
				return;
			if (value is not null)
			{
				NewActionKind = value.Kind;
				LoadActionFields(value);
			}
			RaiseCommandState();
		}
	}

	public ShowControlActionKind NewActionKind
	{
		get => _newActionKind;
		set
		{
			if (Set(ref _newActionKind, value))
				OnPropertyChanged(nameof(ActionInputHint));
		}
	}

	public string PrimaryReference { get => _primaryReference; set => Set(ref _primaryReference, value); }
	public string SecondaryReference { get => _secondaryReference; set => Set(ref _secondaryReference, value); }
	public string FrameValue { get => _frameValue; set => Set(ref _frameValue, value); }
	public bool VisibilityValue { get => _visibilityValue; set => Set(ref _visibilityValue, value); }
	public string ExecutionState { get => _executionState; private set => Set(ref _executionState, value); }
	public string ExecutionDetail { get => _executionDetail; private set => Set(ref _executionDetail, value); }
	public string CurrentCue { get => _currentCue; private set => Set(ref _currentCue, value); }
	public string CurrentAction { get => _currentAction; private set => Set(ref _currentAction, value); }
	public string Failure { get => _failure; private set => Set(ref _failure, value); }
	public string Status { get => _status; private set => Set(ref _status, value); }
	public bool IsBusy { get; private set; }
	public bool RequiresAcknowledgement => _requiresAcknowledgement;
	public bool HasCueLists => CueLists.Count > 0;

	public string ActionInputHint => NewActionKind switch
	{
		ShowControlActionKind.ActivateScene => "Primary: Scene ID",
		ShowControlActionKind.SetPreview => "Primary: Source ID",
		ShowControlActionKind.Dissolve => "Frames: dissolve duration",
		ShowControlActionKind.JumpMediaCue => "Primary: Asset ID · Secondary: Cue ID",
		ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop => "Primary: Asset ID",
		ShowControlActionKind.SetLayerVisibility => "Primary: Layer ID · Visible toggle",
		ShowControlActionKind.StartRecording => "Primary: destination directory · Secondary: file name",
		ShowControlActionKind.WaitFrames => "Frames: bounded production-frame wait",
		_ => "No action parameters required"
	};

	public void ApplyConfirmedSnapshot(ShowControlWorkspaceSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var localSelectionId = SelectedCueList?.CueListId;
		var localCueId = SelectedCue?.CueId;
		var localActionId = SelectedAction?.ActionId;

		CueLists.Clear();
		foreach (var list in snapshot.CueLists)
			CueLists.Add(ShowControlCueListEditorItem.FromContract(list));

		_serverSelectedCueListId = snapshot.SelectedCueListId;
		SelectedCueList =
			CueLists.FirstOrDefault(item => localSelectionId.HasValue && item.CueListId == localSelectionId.Value) ??
			CueLists.FirstOrDefault(item => snapshot.SelectedCueListId.HasValue && item.CueListId == snapshot.SelectedCueListId.Value) ??
			CueLists.FirstOrDefault();

		if (SelectedCueList is not null && localCueId.HasValue)
			SelectedCue = SelectedCueList.Cues.FirstOrDefault(cue => cue.CueId == localCueId.Value) ?? SelectedCueList.Cues.FirstOrDefault();
		if (SelectedCue is not null && localActionId.HasValue)
			SelectedAction = SelectedCue.Actions.FirstOrDefault(action => action.ActionId == localActionId.Value) ?? SelectedCue.Actions.FirstOrDefault();

		ApplyExecution(snapshot);
		OnPropertyChanged(nameof(HasCueLists));
		RaiseCommandState();
	}

	private async Task RefreshAsync() =>
		await RunAsync(async () => ApplyConfirmedSnapshot(await _client.GetShowControlSnapshotAsync().ConfigureAwait(false))).ConfigureAwait(false);

	private Task NewCueListAsync()
	{
		var action = new ShowControlActionEditorItem(ShowControlActionId.New(), ShowControlActionKind.Cut);
		var cue = new ShowControlCueEditorItem(ShowControlCueId.New(), "Cue 1", [action]);
		var list = new ShowControlCueListEditorItem(ShowControlCueListId.New(), "New Show", [cue]);
		CueLists.Add(list);
		SelectedCueList = list;
		SelectedCue = cue;
		SelectedAction = action;
		OnPropertyChanged(nameof(HasCueLists));
		Status = "DRAFT";
		return Task.CompletedTask;
	}

	private async Task SelectCueListAsync()
	{
		var selected = SelectedCueList;
		if (selected is null)
			return;
		await RunAsync(async () =>
		{
			var snapshot = await _client.SelectShowControlCueListAsync(selected.CueListId).ConfigureAwait(false);
			ApplyConfirmedSnapshot(snapshot);
		}).ConfigureAwait(false);
	}

	private async Task SaveCueListAsync()
	{
		var selected = SelectedCueList;
		if (selected is null)
			return;
		await RunAsync(async () =>
		{
			var snapshot = await _client.SaveShowControlCueListAsync(selected.ToContract()).ConfigureAwait(false);
			ApplyConfirmedSnapshot(snapshot);
		}).ConfigureAwait(false);
	}

	private Task AddCueAsync()
	{
		if (SelectedCueList is null)
			return Task.CompletedTask;
		if (SelectedCueList.Cues.Count >= ShowControlCueList.MaximumCues)
		{
			Failure = $"Cue list limit is {ShowControlCueList.MaximumCues}.";
			return Task.CompletedTask;
		}
		var action = new ShowControlActionEditorItem(ShowControlActionId.New(), ShowControlActionKind.Cut);
		var cue = new ShowControlCueEditorItem(
			ShowControlCueId.New(),
			$"Cue {SelectedCueList.Cues.Count + 1}",
			[action]);
		SelectedCueList.Cues.Add(cue);
		SelectedCue = cue;
		Status = "DRAFT";
		RaiseCommandState();
		return Task.CompletedTask;
	}

	private Task RemoveCueAsync()
	{
		if (SelectedCueList is null || SelectedCue is null || SelectedCueList.Cues.Count <= 1)
			return Task.CompletedTask;
		var index = SelectedCueList.Cues.IndexOf(SelectedCue);
		SelectedCueList.Cues.Remove(SelectedCue);
		SelectedCue = SelectedCueList.Cues[Math.Clamp(index, 0, SelectedCueList.Cues.Count - 1)];
		Status = "DRAFT";
		return Task.CompletedTask;
	}

	private Task MoveCueAsync(int offset)
	{
		if (SelectedCueList is null || SelectedCue is null)
			return Task.CompletedTask;
		var from = SelectedCueList.Cues.IndexOf(SelectedCue);
		var to = from + offset;
		if (from < 0 || to < 0 || to >= SelectedCueList.Cues.Count)
			return Task.CompletedTask;
		SelectedCueList.Cues.Move(from, to);
		Status = "DRAFT";
		RaiseCommandState();
		return Task.CompletedTask;
	}

	private Task AddActionAsync()
	{
		if (SelectedCue is null)
			return Task.CompletedTask;
		if (SelectedCue.Actions.Count >= ShowControlCue.MaximumActions)
		{
			Failure = $"Cue action limit is {ShowControlCue.MaximumActions}.";
			return Task.CompletedTask;
		}
		try
		{
			var action = CreateActionEditor();
			_ = action.ToContract();
			SelectedCue.Actions.Add(action);
			SelectedAction = action;
			Status = "DRAFT";
			Failure = "NONE";
		}
		catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
		{
			Failure = exception.Message;
		}
		RaiseCommandState();
		return Task.CompletedTask;
	}

	private Task RemoveActionAsync()
	{
		if (SelectedCue is null || SelectedAction is null || SelectedCue.Actions.Count <= 1)
			return Task.CompletedTask;
		var index = SelectedCue.Actions.IndexOf(SelectedAction);
		SelectedCue.Actions.Remove(SelectedAction);
		SelectedAction = SelectedCue.Actions[Math.Clamp(index, 0, SelectedCue.Actions.Count - 1)];
		Status = "DRAFT";
		return Task.CompletedTask;
	}

	private Task MoveActionAsync(int offset)
	{
		if (SelectedCue is null || SelectedAction is null)
			return Task.CompletedTask;
		var from = SelectedCue.Actions.IndexOf(SelectedAction);
		var to = from + offset;
		if (from < 0 || to < 0 || to >= SelectedCue.Actions.Count)
			return Task.CompletedTask;
		SelectedCue.Actions.Move(from, to);
		Status = "DRAFT";
		RaiseCommandState();
		return Task.CompletedTask;
	}

	private async Task ArmAsync() =>
		await RunAsync(async () => ApplyConfirmedSnapshot(await _client.ArmShowControlAsync().ConfigureAwait(false))).ConfigureAwait(false);

	private async Task GoAsync() =>
		await RunAsync(async () => ApplyConfirmedSnapshot(await _client.GoShowControlAsync().ConfigureAwait(false))).ConfigureAwait(false);

	private async Task CancelAsync() =>
		await RunAsync(async () => ApplyConfirmedSnapshot(await _client.CancelShowControlAsync().ConfigureAwait(false))).ConfigureAwait(false);

	private async Task AcknowledgeRecoveryAsync(bool resume) =>
		await RunAsync(async () => ApplyConfirmedSnapshot(await _client.AcknowledgeShowControlRecoveryAsync(resume).ConfigureAwait(false))).ConfigureAwait(false);

	private async Task RunAsync(Func<Task> operation)
	{
		if (IsBusy)
			return;
		IsBusy = true;
		Status = "WORKING";
		RaiseCommandState();
		try
		{
			await operation().ConfigureAwait(false);
			Status = "READY";
			if (_serverExecutionState != ShowControlExecutionState.Failed)
				Failure = "NONE";
		}
		catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or TimeoutException or NotSupportedException)
		{
			Status = "FAILED";
			Failure = exception.Message;
		}
		finally
		{
			IsBusy = false;
			OnPropertyChanged(nameof(IsBusy));
			RaiseCommandState();
		}
	}

	private void ApplyExecution(ShowControlWorkspaceSnapshot snapshot)
	{
		var execution = snapshot.Execution;
		_serverExecutionState = execution.State;
		_requiresAcknowledgement = execution.RequiresAcknowledgement;
		ExecutionState = execution.State.ToString().ToUpperInvariant();
		var list = execution.CueListId.HasValue
			? snapshot.CueLists.FirstOrDefault(candidate => candidate.CueListId == execution.CueListId.Value)
			: null;
		var cue = list is not null && execution.CueIndex is >= 0 && execution.CueIndex < list.Cues.Count
			? list.Cues[execution.CueIndex.Value]
			: null;
		var action = cue is not null && execution.ActionIndex is >= 0 && execution.ActionIndex < cue.Actions.Count
			? cue.Actions[execution.ActionIndex.Value]
			: null;
		CurrentCue = cue?.Name ?? "—";
		CurrentAction = action is null ? "—" : ShowControlActionEditorItem.Summarize(action);
		Failure = execution.Failure?.Message ?? "NONE";
		ExecutionDetail = execution.State switch
		{
			ShowControlExecutionState.Armed => $"Ready for GO · {CurrentCue}",
			ShowControlExecutionState.Executing => $"Executing · {CurrentCue}",
			ShowControlExecutionState.Waiting => execution.WaitTargetFrameSequence.HasValue
				? $"Waiting for frame {execution.WaitTargetFrameSequence.Value.ToString(CultureInfo.InvariantCulture)}"
				: "Waiting for production timing",
			ShowControlExecutionState.Completed => "Cue list completed.",
			ShowControlExecutionState.Failed => "Execution stopped on action failure.",
			ShowControlExecutionState.Cancelled => "Execution cancelled.",
			ShowControlExecutionState.RecoveryRequired => "Operator acknowledgement required before continuation.",
			_ => "No cue list is armed."
		};
		OnPropertyChanged(nameof(RequiresAcknowledgement));
	}

	private ShowControlActionEditorItem CreateActionEditor()
	{
		uint? frames = null;
		if (NewActionKind is ShowControlActionKind.Dissolve or ShowControlActionKind.WaitFrames)
		{
			if (!uint.TryParse(FrameValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
				throw new ArgumentException("Frames must be a non-negative integer.");
			frames = parsed;
		}
		return new ShowControlActionEditorItem(
			ShowControlActionId.New(),
			NewActionKind,
			PrimaryReference.Trim(),
			SecondaryReference.Trim(),
			frames,
			VisibilityValue);
	}

	private void LoadActionFields(ShowControlActionEditorItem action)
	{
		PrimaryReference = action.PrimaryReference;
		SecondaryReference = action.SecondaryReference;
		FrameValue = action.Frames?.ToString(CultureInfo.InvariantCulture) ?? "12";
		VisibilityValue = action.Visible;
	}

	private bool CanArm() =>
		!IsBusy &&
		SelectedCueList is not null &&
		_serverSelectedCueListId.HasValue &&
		SelectedCueList.CueListId == _serverSelectedCueListId.Value &&
		_serverExecutionState is not (ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired);

	private bool CanGo() => !IsBusy && _serverExecutionState == ShowControlExecutionState.Armed;

	private bool CanCancel() =>
		!IsBusy &&
		_serverExecutionState is ShowControlExecutionState.Armed or ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired or ShowControlExecutionState.Failed;

	private bool CanMoveCue(int offset)
	{
		if (IsBusy || SelectedCueList is null || SelectedCue is null)
			return false;
		var index = SelectedCueList.Cues.IndexOf(SelectedCue);
		return index >= 0 && index + offset >= 0 && index + offset < SelectedCueList.Cues.Count;
	}

	private bool CanMoveAction(int offset)
	{
		if (IsBusy || SelectedCue is null || SelectedAction is null)
			return false;
		var index = SelectedCue.Actions.IndexOf(SelectedAction);
		return index >= 0 && index + offset >= 0 && index + offset < SelectedCue.Actions.Count;
	}

	private void RaiseCommandState()
	{
		foreach (var command in new[]
		{
			RefreshCommand, NewCueListCommand, SelectCueListCommand, SaveCueListCommand, AddCueCommand,
			RemoveCueCommand, MoveCueUpCommand, MoveCueDownCommand, AddActionCommand, RemoveActionCommand,
			MoveActionUpCommand, MoveActionDownCommand, ArmCommand, GoCommand, CancelCommand,
			ResumeRecoveryCommand, CancelRecoveryCommand
		}.OfType<AsyncRelayCommand>())
		{
			command.RaiseCanExecuteChanged();
		}
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

public sealed class ShowControlCueListEditorItem : INotifyPropertyChanged
{
	private string _name;

	public ShowControlCueListEditorItem(ShowControlCueListId cueListId, string name, IEnumerable<ShowControlCueEditorItem> cues)
	{
		CueListId = cueListId;
		_name = name;
		Cues = new ObservableCollection<ShowControlCueEditorItem>(cues);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public ShowControlCueListId CueListId { get; }
	public ObservableCollection<ShowControlCueEditorItem> Cues { get; }
	public string Name
	{
		get => _name;
		set
		{
			if (string.Equals(_name, value, StringComparison.Ordinal))
				return;
			_name = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
		}
	}

	public ShowControlCueList ToContract() =>
		new(ShowControlContractVersion.Current, CueListId, Name, Cues.Select(cue => cue.ToContract()).ToArray());

	public static ShowControlCueListEditorItem FromContract(ShowControlCueList list) =>
		new(list.CueListId, list.Name, list.Cues.Select(ShowControlCueEditorItem.FromContract));
}

public sealed class ShowControlCueEditorItem : INotifyPropertyChanged
{
	private string _name;

	public ShowControlCueEditorItem(ShowControlCueId cueId, string name, IEnumerable<ShowControlActionEditorItem> actions)
	{
		CueId = cueId;
		_name = name;
		Actions = new ObservableCollection<ShowControlActionEditorItem>(actions);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public ShowControlCueId CueId { get; }
	public ObservableCollection<ShowControlActionEditorItem> Actions { get; }
	public string Name
	{
		get => _name;
		set
		{
			if (string.Equals(_name, value, StringComparison.Ordinal))
				return;
			_name = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
		}
	}
	public string Summary => $"{Actions.Count} action{(Actions.Count == 1 ? string.Empty : "s")}";

	public ShowControlCue ToContract() => new(CueId, Name, Actions.Select(action => action.ToContract()).ToArray());

	public static ShowControlCueEditorItem FromContract(ShowControlCue cue) =>
		new(cue.CueId, cue.Name, cue.Actions.Select(ShowControlActionEditorItem.FromContract));
}

public sealed class ShowControlActionEditorItem
{
	public ShowControlActionEditorItem(
		ShowControlActionId actionId,
		ShowControlActionKind kind,
		string primaryReference = "",
		string secondaryReference = "",
		uint? frames = null,
		bool visible = true)
	{
		ActionId = actionId;
		Kind = kind;
		PrimaryReference = primaryReference;
		SecondaryReference = secondaryReference;
		Frames = frames;
		Visible = visible;
	}

	public ShowControlActionId ActionId { get; }
	public ShowControlActionKind Kind { get; }
	public string PrimaryReference { get; }
	public string SecondaryReference { get; }
	public uint? Frames { get; }
	public bool Visible { get; }
	public string Summary => Summarize(ToContract());

	public ShowControlAction ToContract() => Kind switch
	{
		ShowControlActionKind.ActivateScene => new ShowControlAction(ActionId, Kind, sceneId: PrimaryReference),
		ShowControlActionKind.SetPreview => new ShowControlAction(ActionId, Kind, sourceId: PrimaryReference),
		ShowControlActionKind.Cut => new ShowControlAction(ActionId, Kind),
		ShowControlActionKind.Dissolve => new ShowControlAction(ActionId, Kind, durationFrames: Frames),
		ShowControlActionKind.JumpMediaCue => new ShowControlAction(ActionId, Kind, mediaAssetId: PrimaryReference, mediaCuePointId: SecondaryReference),
		ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop =>
			new ShowControlAction(ActionId, Kind, mediaAssetId: PrimaryReference),
		ShowControlActionKind.SetLayerVisibility => new ShowControlAction(ActionId, Kind, layerId: PrimaryReference, visible: Visible),
		ShowControlActionKind.StartRecording => new ShowControlAction(ActionId, Kind, recordingDestinationDirectory: PrimaryReference, recordingFileName: SecondaryReference),
		ShowControlActionKind.StopRecording => new ShowControlAction(ActionId, Kind),
		ShowControlActionKind.WaitFrames => new ShowControlAction(ActionId, Kind, waitFrames: Frames),
		_ => throw new NotSupportedException($"Unsupported show-control action '{Kind}'.")
	};

	public static ShowControlActionEditorItem FromContract(ShowControlAction action) =>
		action.Kind switch
		{
			ShowControlActionKind.ActivateScene => new(action.ActionId, action.Kind, action.SceneId ?? string.Empty),
			ShowControlActionKind.SetPreview => new(action.ActionId, action.Kind, action.SourceId ?? string.Empty),
			ShowControlActionKind.Dissolve => new(action.ActionId, action.Kind, frames: action.DurationFrames),
			ShowControlActionKind.JumpMediaCue => new(action.ActionId, action.Kind, action.MediaAssetId ?? string.Empty, action.MediaCuePointId ?? string.Empty),
			ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop =>
				new(action.ActionId, action.Kind, action.MediaAssetId ?? string.Empty),
			ShowControlActionKind.SetLayerVisibility => new(action.ActionId, action.Kind, action.LayerId ?? string.Empty, visible: action.Visible ?? true),
			ShowControlActionKind.StartRecording => new(action.ActionId, action.Kind, action.RecordingDestinationDirectory ?? string.Empty, action.RecordingFileName ?? string.Empty),
			ShowControlActionKind.WaitFrames => new(action.ActionId, action.Kind, frames: action.WaitFrames),
			_ => new(action.ActionId, action.Kind)
		};

	public static string Summarize(ShowControlAction action) => action.Kind switch
	{
		ShowControlActionKind.ActivateScene => $"Activate Scene · {action.SceneId}",
		ShowControlActionKind.SetPreview => $"Set Preview · {action.SourceId}",
		ShowControlActionKind.Dissolve => $"DISSOLVE · {action.DurationFrames}f",
		ShowControlActionKind.JumpMediaCue => $"Jump Cue · {action.MediaCuePointId}",
		ShowControlActionKind.MediaPlay => "Media Play",
		ShowControlActionKind.MediaPause => "Media Pause",
		ShowControlActionKind.MediaStop => "Media Stop",
		ShowControlActionKind.SetLayerVisibility => $"Layer {(action.Visible == true ? "Show" : "Hide")} · {action.LayerId}",
		ShowControlActionKind.StartRecording => "Start Recording",
		ShowControlActionKind.StopRecording => "Stop Recording",
		ShowControlActionKind.WaitFrames => $"Wait · {action.WaitFrames}f",
		_ => action.Kind.ToString()
	};
}
