// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Operator;

public sealed class ProductionMacroViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient _client;
	private ProductionMacroEditorItem? _selectedMacro;
	private ProductionMacroActionEditorItem? _selectedAction;
	private ShowControlActionKind _newActionKind = ShowControlActionKind.Cut;
	private string _primaryReference = string.Empty;
	private string _secondaryReference = string.Empty;
	private string _frameValue = "12";
	private bool _visibilityValue = true;
	private int _audioRoutingMode = 1;
	private double _audioGain = 1.0;
	private bool _audioMuted;
	private ulong _storageVersion;
	private bool _isBusy;
	private string _state = "IDLE";
	private string _status = "Production Macros not loaded.";
	private string _failure = "NONE";
	private string _currentAction = "—";
	private string _lastCompletedAction = "—";
	private bool _requiresAcknowledgement;

	public ProductionMacroViewModel(OperatorControlClient client)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		Macros = [];
		ActionKinds = Enum.GetValues<ShowControlActionKind>();
		AudioRoutingModes = [1, 2];

		RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
		NewMacroCommand = new AsyncRelayCommand(NewMacroAsync, () => !IsBusy);
		SaveMacroCommand = new AsyncRelayCommand(SaveMacroAsync, () => !IsBusy && SelectedMacro is not null);
		DeleteMacroCommand = new AsyncRelayCommand(DeleteMacroAsync, () => !IsBusy && SelectedMacro is not null);
		ValidateMacroCommand = new AsyncRelayCommand(ValidateMacroAsync, () => !IsBusy && SelectedMacro is not null);
		AddActionCommand = new AsyncRelayCommand(AddActionAsync, () => !IsBusy && SelectedMacro is not null);
		RemoveActionCommand = new AsyncRelayCommand(RemoveActionAsync, () => !IsBusy && SelectedMacro is not null && SelectedAction is not null && SelectedMacro.Actions.Count > 1);
		MoveActionUpCommand = new AsyncRelayCommand(() => MoveActionAsync(-1), () => CanMoveAction(-1));
		MoveActionDownCommand = new AsyncRelayCommand(() => MoveActionAsync(1), () => CanMoveAction(1));
		RunCommand = new AsyncRelayCommand(RunAsync, CanRun);
		CancelCommand = new AsyncRelayCommand(CancelAsync, () => !IsBusy && State is "EXECUTING" or "WAITING" or "ARMED");
		ResumeRecoveryCommand = new AsyncRelayCommand(() => AcknowledgeRecoveryAsync(true), () => !IsBusy && RequiresAcknowledgement);
		CancelRecoveryCommand = new AsyncRelayCommand(() => AcknowledgeRecoveryAsync(false), () => !IsBusy && RequiresAcknowledgement);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<ProductionMacroEditorItem> Macros { get; }
	public IReadOnlyList<ShowControlActionKind> ActionKinds { get; }
	public IReadOnlyList<int> AudioRoutingModes { get; }

	public ICommand RefreshCommand { get; }
	public ICommand NewMacroCommand { get; }
	public ICommand SaveMacroCommand { get; }
	public ICommand DeleteMacroCommand { get; }
	public ICommand ValidateMacroCommand { get; }
	public ICommand AddActionCommand { get; }
	public ICommand RemoveActionCommand { get; }
	public ICommand MoveActionUpCommand { get; }
	public ICommand MoveActionDownCommand { get; }
	public ICommand RunCommand { get; }
	public ICommand CancelCommand { get; }
	public ICommand ResumeRecoveryCommand { get; }
	public ICommand CancelRecoveryCommand { get; }

	public ProductionMacroEditorItem? SelectedMacro
	{
		get => _selectedMacro;
		set
		{
			if (!Set(ref _selectedMacro, value))
				return;
			SelectedAction = value?.Actions.FirstOrDefault();
			RaiseCommandState();
		}
	}

	public ProductionMacroActionEditorItem? SelectedAction
	{
		get => _selectedAction;
		set
		{
			if (!Set(ref _selectedAction, value))
				return;
			if (value is not null)
				LoadActionFields(value);
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

	public string PrimaryReference { get => _primaryReference; set => Set(ref _primaryReference, value ?? string.Empty); }
	public string SecondaryReference { get => _secondaryReference; set => Set(ref _secondaryReference, value ?? string.Empty); }
	public string FrameValue { get => _frameValue; set => Set(ref _frameValue, value ?? string.Empty); }
	public bool VisibilityValue { get => _visibilityValue; set => Set(ref _visibilityValue, value); }
	public int AudioRoutingMode { get => _audioRoutingMode; set => Set(ref _audioRoutingMode, value is 1 or 2 ? value : 1); }
	public double AudioGain { get => _audioGain; set => Set(ref _audioGain, double.IsFinite(value) ? Math.Clamp(value, 0.0, 4.0) : 1.0); }
	public bool AudioMuted { get => _audioMuted; set => Set(ref _audioMuted, value); }
	public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommandState(); } }
	public string State { get => _state; private set { if (Set(ref _state, value)) RaiseCommandState(); } }
	public string Status { get => _status; private set => Set(ref _status, value); }
	public string Failure { get => _failure; private set => Set(ref _failure, value); }
	public string CurrentAction { get => _currentAction; private set => Set(ref _currentAction, value); }
	public string LastCompletedAction { get => _lastCompletedAction; private set => Set(ref _lastCompletedAction, value); }
	public bool RequiresAcknowledgement { get => _requiresAcknowledgement; private set { if (Set(ref _requiresAcknowledgement, value)) RaiseCommandState(); } }
	public string StorageLabel => $"SAVED V{_storageVersion}";

	public string ActionInputHint => NewActionKind switch
	{
		ShowControlActionKind.ActivateScene => "Primary: Scene ID",
		ShowControlActionKind.SetPreview => "Primary: Source ID",
		ShowControlActionKind.Cut => "No parameters",
		ShowControlActionKind.Dissolve => "Frames: dissolve duration",
		ShowControlActionKind.JumpMediaCue => "Primary: Asset ID · Secondary: Cue ID",
		ShowControlActionKind.MediaOpen => "Primary: Asset ID · Secondary: Source ID",
		ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop => "Primary: Asset ID",
		ShowControlActionKind.SetLayerVisibility => "Primary: Layer ID · Visible toggle",
		ShowControlActionKind.StartRecording => "Primary: destination directory · Secondary: file name",
		ShowControlActionKind.StopRecording => "No parameters",
		ShowControlActionKind.WaitFrames => "Frames: bounded production-frame wait",
		ShowControlActionKind.SetAudioRouting => "Mode 1 FOLLOW_VIDEO · Mode 2 BREAKAWAY; Primary: source for mode 2",
		ShowControlActionKind.RouteOutputRole => "Primary: output role ID · Secondary: source ID",
		ShowControlActionKind.SetAudioInputState => "Primary: source ID · Gain 0..4 · Muted toggle",
		_ => "Unsupported action"
	};

	public async Task RefreshAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.GetProductionMacroSnapshotAsync()));

	private Task NewMacroAsync()
	{
		var macro = new ProductionMacroEditorItem(
			ProductionMacroId.New(),
			"New Production Macro",
			string.Empty,
			[new ProductionMacroActionEditorItem(ProductionMacroActionId.New(), ShowControlActionKind.Cut)]);
		Macros.Add(macro);
		SelectedMacro = macro;
		Status = "Local Macro draft. SAVE validates and persists through ControlHost.";
		Failure = "NONE";
		OnPropertyChanged(nameof(StorageLabel));
		return Task.CompletedTask;
	}

	private async Task SaveMacroAsync()
	{
		var macro = SelectedMacro ?? throw new InvalidOperationException("Select a Production Macro before saving.");
		await RunAsync(async () => ApplySnapshot(await _client.SaveProductionMacroAsync(macro.ToContract(), _storageVersion)));
	}

	private async Task DeleteMacroAsync()
	{
		var macro = SelectedMacro ?? throw new InvalidOperationException("Select a Production Macro before deleting.");
		await RunAsync(async () => ApplySnapshot(await _client.DeleteProductionMacroAsync(macro.MacroId, _storageVersion)));
	}

	private async Task ValidateMacroAsync()
	{
		var macro = SelectedMacro ?? throw new InvalidOperationException("Select a Production Macro before validating.");
		await RunAsync(async () =>
		{
			var result = await _client.ValidateProductionMacroAsync(macro.ToContract());
			Failure = result.IsValid ? "NONE" : string.Join(" | ", result.Issues.Select(issue => issue.Message));
			Status = result.IsValid
				? "VALID · ControlHost accepted the bounded Macro definition."
				: $"INVALID · {result.Issues.Count} issue(s).";
		});
	}

	private Task AddActionAsync()
	{
		var macro = SelectedMacro ?? throw new InvalidOperationException("Select a Production Macro before adding actions.");
		if (macro.Actions.Count >= ProductionMacroDefinition.MaximumActions)
			throw new InvalidOperationException($"Production Macros are limited to {ProductionMacroDefinition.MaximumActions} actions.");
		var action = CreateActionEditor();
		macro.Actions.Add(action);
		SelectedAction = action;
		Status = "Local action added. SAVE validates and persists the Macro.";
		return Task.CompletedTask;
	}

	private Task RemoveActionAsync()
	{
		var macro = SelectedMacro;
		var action = SelectedAction;
		if (macro is null || action is null || macro.Actions.Count <= 1)
			return Task.CompletedTask;
		var index = macro.Actions.IndexOf(action);
		macro.Actions.Remove(action);
		SelectedAction = macro.Actions[Math.Clamp(index, 0, macro.Actions.Count - 1)];
		Status = "Local action removed. SAVE persists the change.";
		return Task.CompletedTask;
	}

	private Task MoveActionAsync(int offset)
	{
		var macro = SelectedMacro;
		var action = SelectedAction;
		if (macro is null || action is null)
			return Task.CompletedTask;
		var from = macro.Actions.IndexOf(action);
		var to = from + offset;
		if (from < 0 || to < 0 || to >= macro.Actions.Count)
			return Task.CompletedTask;
		macro.Actions.Move(from, to);
		Status = "Local action order changed. SAVE persists the deterministic order.";
		RaiseCommandState();
		return Task.CompletedTask;
	}

	private bool CanMoveAction(int offset)
	{
		if (IsBusy || SelectedMacro is null || SelectedAction is null)
			return false;
		var index = SelectedMacro.Actions.IndexOf(SelectedAction);
		return index >= 0 && index + offset >= 0 && index + offset < SelectedMacro.Actions.Count;
	}

	private async Task RunAsync()
	{
		var macro = SelectedMacro ?? throw new InvalidOperationException("Select a Production Macro before running.");
		await RunAsync(async () => ApplySnapshot(await _client.ExecuteProductionMacroAsync(macro.MacroId)));
	}

	private async Task CancelAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.CancelProductionMacroAsync()));

	private async Task AcknowledgeRecoveryAsync(bool resume) =>
		await RunAsync(async () => ApplySnapshot(await _client.AcknowledgeProductionMacroRecoveryAsync(resume)));

	private bool CanRun() =>
		!IsBusy &&
		SelectedMacro is not null &&
		State is not "EXECUTING" and not "WAITING" and not "RECOVERYREQUIRED";

	private ProductionMacroActionEditorItem CreateActionEditor()
	{
		uint? frames = null;
		if (NewActionKind is ShowControlActionKind.Dissolve or ShowControlActionKind.WaitFrames)
		{
			if (!uint.TryParse(FrameValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
				throw new ArgumentException("Frames must be a non-negative integer.");
			frames = parsed;
		}
		return new ProductionMacroActionEditorItem(
			ProductionMacroActionId.New(),
			NewActionKind,
			PrimaryReference.Trim(),
			SecondaryReference.Trim(),
			frames,
			VisibilityValue,
			AudioRoutingMode,
			AudioGain,
			AudioMuted);
	}

	private void LoadActionFields(ProductionMacroActionEditorItem action)
	{
		NewActionKind = action.Kind;
		PrimaryReference = action.PrimaryReference;
		SecondaryReference = action.SecondaryReference;
		FrameValue = action.Frames?.ToString(CultureInfo.InvariantCulture) ?? "12";
		VisibilityValue = action.Visible;
		AudioRoutingMode = action.AudioRoutingMode;
		AudioGain = action.AudioGain;
		AudioMuted = action.AudioMuted;
	}

	private void ApplySnapshot(ProductionMacroWorkspaceSnapshot snapshot)
	{
		var selectedId = SelectedMacro?.MacroId;
		Macros.Clear();
		foreach (var macro in snapshot.Macros)
			Macros.Add(ProductionMacroEditorItem.FromContract(macro));
		_storageVersion = snapshot.StorageVersion;
		SelectedMacro = Macros.FirstOrDefault(macro => selectedId.HasValue && macro.MacroId == selectedId.Value)
			?? Macros.FirstOrDefault(macro => snapshot.Execution.MacroId.HasValue && macro.MacroId == snapshot.Execution.MacroId.Value)
			?? Macros.FirstOrDefault();

		var execution = snapshot.Execution;
		State = execution.State.ToString().ToUpperInvariant();
		RequiresAcknowledgement = execution.RequiresAcknowledgement;
		Failure = execution.Failure?.Message ?? "NONE";
		CurrentAction = ResolveAction(execution.CurrentActionId);
		LastCompletedAction = ResolveAction(execution.LastCompletedActionId);
		Status = execution.State switch
		{
			ProductionMacroExecutionState.Armed => "ARMED · Macro execution is prepared by ControlHost.",
			ProductionMacroExecutionState.Executing => "EXECUTING · sequential governed actions.",
			ProductionMacroExecutionState.Waiting => $"WAITING · Runtime frame target {execution.WaitTargetFrameSequence?.ToString() ?? "—"}.",
			ProductionMacroExecutionState.Completed => "COMPLETED · all bounded actions confirmed.",
			ProductionMacroExecutionState.Failed => "FAILED · execution stopped; no later actions were run.",
			ProductionMacroExecutionState.Cancelled => "CANCELLED · committed Production state was retained.",
			ProductionMacroExecutionState.RecoveryRequired => "RECOVERY REQUIRED · ambiguous action outcome requires explicit acknowledgement.",
			_ => snapshot.Macros.Count == 0 ? "No Production Macros authored." : "Production Macros ready."
		};
		OnPropertyChanged(nameof(StorageLabel));
		RaiseCommandState();
	}

	private string ResolveAction(ProductionMacroActionId? actionId)
	{
		if (!actionId.HasValue)
			return "—";
		foreach (var macro in Macros)
		{
			var action = macro.Actions.FirstOrDefault(candidate => candidate.ActionId == actionId.Value);
			if (action is not null)
				return action.Summary;
		}
		return actionId.Value.ToString();
	}

	private async Task RunAsync(Func<Task> operation)
	{
		if (IsBusy)
			return;
		IsBusy = true;
		try
		{
			await operation();
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or ArgumentException or InvalidDataException or
			IOException or FormatException or TimeoutException or NotSupportedException)
		{
			Failure = exception.Message;
			Status = "Production Macro command was rejected; no local Production authority was assumed.";
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void RaiseCommandState()
	{
		foreach (var command in new[]
		{
			RefreshCommand, NewMacroCommand, SaveMacroCommand, DeleteMacroCommand, ValidateMacroCommand,
			AddActionCommand, RemoveActionCommand, MoveActionUpCommand, MoveActionDownCommand,
			RunCommand, CancelCommand, ResumeRecoveryCommand, CancelRecoveryCommand
		}.OfType<AsyncRelayCommand>())
			command.RaiseCanExecuteChanged();
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

public sealed class ProductionMacroEditorItem : INotifyPropertyChanged
{
	private string _name;
	private string _description;

	public ProductionMacroEditorItem(
		ProductionMacroId macroId,
		string name,
		string description,
		IEnumerable<ProductionMacroActionEditorItem> actions)
	{
		MacroId = macroId;
		_name = name;
		_description = description;
		Actions = new ObservableCollection<ProductionMacroActionEditorItem>(actions);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public ProductionMacroId MacroId { get; }
	public ObservableCollection<ProductionMacroActionEditorItem> Actions { get; }
	public string Name
	{
		get => _name;
		set
		{
			var normalized = value ?? string.Empty;
			if (string.Equals(_name, normalized, StringComparison.Ordinal))
				return;
			_name = normalized;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
		}
	}
	public string Description
	{
		get => _description;
		set
		{
			var normalized = value ?? string.Empty;
			if (string.Equals(_description, normalized, StringComparison.Ordinal))
				return;
			_description = normalized;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
		}
	}
	public string Summary => $"{Actions.Count} bounded action{(Actions.Count == 1 ? string.Empty : "s")}";

	public ProductionMacroDefinition ToContract() =>
		new(
			ProductionMacroContractVersion.Current,
			MacroId,
			Name,
			Actions.Select(action => action.ToContract()).ToArray(),
			Description);

	public static ProductionMacroEditorItem FromContract(ProductionMacroDefinition macro) =>
		new(macro.MacroId, macro.Name, macro.Description ?? string.Empty, macro.Actions.Select(ProductionMacroActionEditorItem.FromContract));
}

public sealed class ProductionMacroActionEditorItem
{
	public ProductionMacroActionEditorItem(
		ProductionMacroActionId actionId,
		ShowControlActionKind kind,
		string primaryReference = "",
		string secondaryReference = "",
		uint? frames = null,
		bool visible = true,
		int audioRoutingMode = 1,
		double audioGain = 1.0,
		bool audioMuted = false)
	{
		ActionId = actionId;
		Kind = kind;
		PrimaryReference = primaryReference;
		SecondaryReference = secondaryReference;
		Frames = frames;
		Visible = visible;
		AudioRoutingMode = audioRoutingMode;
		AudioGain = audioGain;
		AudioMuted = audioMuted;
	}

	public ProductionMacroActionId ActionId { get; }
	public ShowControlActionKind Kind { get; }
	public string PrimaryReference { get; }
	public string SecondaryReference { get; }
	public uint? Frames { get; }
	public bool Visible { get; }
	public int AudioRoutingMode { get; }
	public double AudioGain { get; }
	public bool AudioMuted { get; }
	public string Summary => Summarize(ToContract().Command);

	public ProductionMacroAction ToContract() => Kind switch
	{
		ShowControlActionKind.ActivateScene => new ProductionMacroAction(ActionId, Kind, sceneId: PrimaryReference),
		ShowControlActionKind.SetPreview => new ProductionMacroAction(ActionId, Kind, sourceId: PrimaryReference),
		ShowControlActionKind.Cut => new ProductionMacroAction(ActionId, Kind),
		ShowControlActionKind.Dissolve => new ProductionMacroAction(ActionId, Kind, durationFrames: Frames),
		ShowControlActionKind.JumpMediaCue => new ProductionMacroAction(ActionId, Kind, mediaAssetId: PrimaryReference, mediaCuePointId: SecondaryReference),
		ShowControlActionKind.MediaOpen => new ProductionMacroAction(ActionId, Kind, sourceId: SecondaryReference, mediaAssetId: PrimaryReference),
		ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop =>
			new ProductionMacroAction(ActionId, Kind, mediaAssetId: PrimaryReference),
		ShowControlActionKind.SetLayerVisibility => new ProductionMacroAction(ActionId, Kind, layerId: PrimaryReference, visible: Visible),
		ShowControlActionKind.StartRecording => new ProductionMacroAction(ActionId, Kind, recordingDestinationDirectory: PrimaryReference, recordingFileName: SecondaryReference),
		ShowControlActionKind.StopRecording => new ProductionMacroAction(ActionId, Kind),
		ShowControlActionKind.WaitFrames => new ProductionMacroAction(ActionId, Kind, waitFrames: Frames),
		ShowControlActionKind.SetAudioRouting => new ProductionMacroAction(
			ActionId,
			Kind,
			sourceId: AudioRoutingMode == 2 ? PrimaryReference : null,
			audioRoutingMode: AudioRoutingMode),
		ShowControlActionKind.RouteOutputRole => new ProductionMacroAction(ActionId, Kind, sourceId: SecondaryReference, outputRoleId: PrimaryReference),
		ShowControlActionKind.SetAudioInputState => new ProductionMacroAction(ActionId, Kind, sourceId: PrimaryReference, audioGain: AudioGain, audioMuted: AudioMuted),
		_ => throw new NotSupportedException($"Unsupported Production Macro action '{Kind}'.")
	};

	public static ProductionMacroActionEditorItem FromContract(ProductionMacroAction macroAction)
	{
		var action = macroAction.Command;
		return action.Kind switch
		{
			ShowControlActionKind.ActivateScene => new(macroAction.ActionId, action.Kind, action.SceneId ?? string.Empty),
			ShowControlActionKind.SetPreview => new(macroAction.ActionId, action.Kind, action.SourceId ?? string.Empty),
			ShowControlActionKind.Dissolve => new(macroAction.ActionId, action.Kind, frames: action.DurationFrames),
			ShowControlActionKind.JumpMediaCue => new(macroAction.ActionId, action.Kind, action.MediaAssetId ?? string.Empty, action.MediaCuePointId ?? string.Empty),
			ShowControlActionKind.MediaOpen => new(macroAction.ActionId, action.Kind, action.MediaAssetId ?? string.Empty, action.SourceId ?? string.Empty),
			ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop =>
				new(macroAction.ActionId, action.Kind, action.MediaAssetId ?? string.Empty),
			ShowControlActionKind.SetLayerVisibility => new(macroAction.ActionId, action.Kind, action.LayerId ?? string.Empty, visible: action.Visible ?? true),
			ShowControlActionKind.StartRecording => new(macroAction.ActionId, action.Kind, action.RecordingDestinationDirectory ?? string.Empty, action.RecordingFileName ?? string.Empty),
			ShowControlActionKind.WaitFrames => new(macroAction.ActionId, action.Kind, frames: action.WaitFrames),
			ShowControlActionKind.SetAudioRouting => new(macroAction.ActionId, action.Kind, action.SourceId ?? string.Empty, audioRoutingMode: action.AudioRoutingMode ?? 1),
			ShowControlActionKind.RouteOutputRole => new(macroAction.ActionId, action.Kind, action.OutputRoleId ?? string.Empty, action.SourceId ?? string.Empty),
			ShowControlActionKind.SetAudioInputState => new(macroAction.ActionId, action.Kind, action.SourceId ?? string.Empty, audioGain: action.AudioGain ?? 1.0, audioMuted: action.AudioMuted ?? false),
			_ => new(macroAction.ActionId, action.Kind)
		};
	}

	private static string Summarize(ShowControlAction action) => action.Kind switch
	{
		ShowControlActionKind.ActivateScene => $"Scene · {action.SceneId}",
		ShowControlActionKind.SetPreview => $"Preview · {action.SourceId}",
		ShowControlActionKind.Cut => "CUT",
		ShowControlActionKind.Dissolve => $"DISSOLVE · {action.DurationFrames}f",
		ShowControlActionKind.JumpMediaCue => $"Media Cue · {action.MediaCuePointId}",
		ShowControlActionKind.MediaOpen => $"Media Open · {action.MediaAssetId}",
		ShowControlActionKind.MediaPlay => "Media Play",
		ShowControlActionKind.MediaPause => "Media Pause",
		ShowControlActionKind.MediaStop => "Media Stop",
		ShowControlActionKind.SetLayerVisibility => $"Layer {(action.Visible == true ? "SHOW" : "HIDE")} · {action.LayerId}",
		ShowControlActionKind.StartRecording => "Start Recording",
		ShowControlActionKind.StopRecording => "Stop Recording",
		ShowControlActionKind.WaitFrames => $"Wait · {action.WaitFrames}f",
		ShowControlActionKind.SetAudioRouting => action.AudioRoutingMode == 1 ? "Audio · FOLLOW VIDEO" : $"Audio · BREAKAWAY {action.SourceId}",
		ShowControlActionKind.RouteOutputRole => $"Output · {action.OutputRoleId} ← {action.SourceId}",
		ShowControlActionKind.SetAudioInputState => $"Audio Input · {action.SourceId} · {action.AudioGain:0.##}x · {(action.AudioMuted == true ? "MUTED" : "OPEN")}",
		_ => action.Kind.ToString()
	};
}
