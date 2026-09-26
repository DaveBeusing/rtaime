// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Operator;

public sealed class RundownViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient _client;
	private readonly OperatorViewModel _operator;
	private readonly MediaPoolInspectorViewModel _mediaPool;
	private RundownOperatorItemViewModel? _selectedItem;
	private RundownId _rundownId = RundownId.New();
	private ulong _storageVersion;
	private string _name = "Production Rundown";
	private string _state = "IDLE";
	private string _detail = "No rundown loaded.";
	private string _failure = "NONE";
	private bool _isBusy;
	private bool _autoAdvanceMedia = true;
	private bool _useDissolve;
	private uint _transitionFrames = 12;
	private uint _holdFrames = 50;
	private bool _requiresAcknowledgement;

	public RundownViewModel(
		OperatorControlClient client,
		OperatorViewModel operatorViewModel,
		MediaPoolInspectorViewModel mediaPool)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_operator = operatorViewModel ?? throw new ArgumentNullException(nameof(operatorViewModel));
		_mediaPool = mediaPool ?? throw new ArgumentNullException(nameof(mediaPool));
		Items = [];
		RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
		SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
		AddMediaCommand = new AsyncRelayCommand(AddMediaAsync, CanAddMedia);
		AddSceneCommand = new AsyncRelayCommand(AddSceneAsync, CanAddScene);
		AddAudioBreakawayCommand = new AsyncRelayCommand(AddAudioBreakawayAsync, CanAddAudio);
		AddFollowVideoCommand = new AsyncRelayCommand(AddFollowVideoAsync, () => !IsBusy);
		AddGraphicsCommand = new AsyncRelayCommand(AddGraphicsAsync, CanAddGraphics);
		AddHoldCommand = new AsyncRelayCommand(AddHoldAsync, () => !IsBusy);
		RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => !IsBusy && SelectedItem is not null && Items.Count > 1);
		MoveUpCommand = new AsyncRelayCommand(() => MoveAsync(-1), () => CanMove(-1));
		MoveDownCommand = new AsyncRelayCommand(() => MoveAsync(1), () => CanMove(1));
		PrepareCommand = new AsyncRelayCommand(PrepareAsync, CanPrepare);
		GoCommand = new AsyncRelayCommand(GoAsync, () => !IsBusy && State == "PREPARED");
		NextCommand = new AsyncRelayCommand(NextAsync, CanNavigate);
		PreviousCommand = new AsyncRelayCommand(PreviousAsync, CanNavigate);
		HoldCommand = new AsyncRelayCommand(HoldAsync, () => !IsBusy && State is "EXECUTING" or "PREPARED" or "HELD");
		ResumeRecoveryCommand = new AsyncRelayCommand(() => AcknowledgeRecoveryAsync(true), () => !IsBusy && RequiresAcknowledgement);
		CancelRecoveryCommand = new AsyncRelayCommand(() => AcknowledgeRecoveryAsync(false), () => !IsBusy && RequiresAcknowledgement);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<RundownOperatorItemViewModel> Items { get; }
	public ICommand RefreshCommand { get; }
	public ICommand SaveCommand { get; }
	public ICommand AddMediaCommand { get; }
	public ICommand AddSceneCommand { get; }
	public ICommand AddAudioBreakawayCommand { get; }
	public ICommand AddFollowVideoCommand { get; }
	public ICommand AddGraphicsCommand { get; }
	public ICommand AddHoldCommand { get; }
	public ICommand RemoveCommand { get; }
	public ICommand MoveUpCommand { get; }
	public ICommand MoveDownCommand { get; }
	public ICommand PrepareCommand { get; }
	public ICommand GoCommand { get; }
	public ICommand NextCommand { get; }
	public ICommand PreviousCommand { get; }
	public ICommand HoldCommand { get; }
	public ICommand ResumeRecoveryCommand { get; }
	public ICommand CancelRecoveryCommand { get; }

	public string Name { get => _name; set { if (Set(ref _name, value ?? string.Empty)) RaiseCommandState(); } }
	public string State { get => _state; private set => Set(ref _state, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string Failure { get => _failure; private set => Set(ref _failure, value); }
	public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
	public bool AutoAdvanceMedia { get => _autoAdvanceMedia; set => Set(ref _autoAdvanceMedia, value); }
	public bool UseDissolve { get => _useDissolve; set => Set(ref _useDissolve, value); }
	public uint TransitionFrames { get => _transitionFrames; set => Set(ref _transitionFrames, Math.Clamp(value, 2, RundownTransition.MaximumDissolveFrames)); }
	public uint HoldFrames { get => _holdFrames; set => Set(ref _holdFrames, Math.Clamp(value, 1, RundownHoldItem.MaximumHoldFrames)); }
	public bool RequiresAcknowledgement { get => _requiresAcknowledgement; private set { if (Set(ref _requiresAcknowledgement, value)) RaiseCommandState(); } }
	public bool HasItems => Items.Count > 0;
	public string StorageLabel => $"SAVED V{_storageVersion}";

	public RundownOperatorItemViewModel? SelectedItem
	{
		get => _selectedItem;
		set
		{
			if (Set(ref _selectedItem, value))
				RaiseCommandState();
		}
	}

	public async Task RefreshAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.GetRundownSnapshotAsync()));

	private async Task SaveAsync()
	{
		var definition = BuildDefinition();
		await RunAsync(async () => ApplySnapshot(await _client.SaveRundownAsync(definition, _storageVersion)));
	}

	private Task AddMediaAsync()
	{
		var asset = _mediaPool.SelectedItem!;
		var source = _operator.SelectedSource!;
		var transition = UseDissolve
			? RundownTransition.Dissolve(TransitionFrames)
			: RundownTransition.Cut;
		var item = new RundownMediaItem(
			RundownItemId.New(),
			asset.Name,
			Identity.Parse(asset.ReferenceId!),
			new ProductionSourceId(Identity.Parse(source.Id)),
			transition,
			AutoAdvanceMedia ? RundownAdvanceMode.AutoOnMediaEnd : RundownAdvanceMode.Manual);
		AddDraft(item);
		return Task.CompletedTask;
	}

	private Task AddSceneAsync()
	{
		var scene = _operator.SelectedScene!;
		AddDraft(new RundownSceneItem(
			RundownItemId.New(),
			scene.Name,
			new SceneId(Identity.Parse(scene.Id))));
		return Task.CompletedTask;
	}

	private Task AddAudioBreakawayAsync()
	{
		var input = _operator.SelectedAudioInput!;
		AddDraft(new RundownAudioRoutingItem(
			RundownItemId.New(),
			$"Audio · {input.SourceName}",
			RundownAudioRoutingItem.BreakawayMode,
			new ProductionSourceId(Identity.Parse(input.SourceId))));
		return Task.CompletedTask;
	}

	private Task AddFollowVideoAsync()
	{
		AddDraft(new RundownAudioRoutingItem(
			RundownItemId.New(),
			"Audio · Follow Video",
			RundownAudioRoutingItem.FollowVideoMode));
		return Task.CompletedTask;
	}

	private Task AddGraphicsAsync()
	{
		AddDraft(new RundownGraphicsItem(
			RundownItemId.New(),
			"Graphics · Bitmap",
			"bitmap-graphics",
			visible: true));
		return Task.CompletedTask;
	}

	private Task AddHoldAsync()
	{
		AddDraft(new RundownHoldItem(RundownItemId.New(), $"Hold · {HoldFrames}f", HoldFrames));
		return Task.CompletedTask;
	}

	private Task RemoveAsync()
	{
		if (SelectedItem is null || Items.Count <= 1)
			return Task.CompletedTask;
		var index = Items.IndexOf(SelectedItem);
		Items.Remove(SelectedItem);
		SelectedItem = Items[Math.Clamp(index, 0, Items.Count - 1)];
		MarkDraft();
		return Task.CompletedTask;
	}

	private Task MoveAsync(int offset)
	{
		if (SelectedItem is null)
			return Task.CompletedTask;
		var from = Items.IndexOf(SelectedItem);
		var to = from + offset;
		if (from < 0 || to < 0 || to >= Items.Count)
			return Task.CompletedTask;
		Items.Move(from, to);
		MarkDraft();
		return Task.CompletedTask;
	}

	private async Task PrepareAsync()
	{
		var selected = SelectedItem;
		if (selected is null)
			return;
		await RunAsync(async () => ApplySnapshot(await _client.PrepareRundownItemAsync(selected.Item.ItemId)));
	}

	private async Task GoAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.GoRundownAsync()));

	private async Task NextAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.NextRundownAsync()));

	private async Task PreviousAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.PreviousRundownAsync()));

	private async Task HoldAsync() =>
		await RunAsync(async () => ApplySnapshot(await _client.HoldRundownAsync()));

	private async Task AcknowledgeRecoveryAsync(bool resume) =>
		await RunAsync(async () => ApplySnapshot(await _client.AcknowledgeRundownRecoveryAsync(resume)));

	private void AddDraft(RundownItem item)
	{
		var viewModel = new RundownOperatorItemViewModel(item);
		Items.Add(viewModel);
		SelectedItem = viewModel;
		MarkDraft();
	}

	private void MarkDraft()
	{
		State = "DRAFT";
		Detail = "Local authoring changes are not authoritative until saved.";
		Failure = "NONE";
		OnPropertyChanged(nameof(HasItems));
		RaiseCommandState();
	}

	private RundownDefinition BuildDefinition()
	{
		if (Items.Count == 0)
			throw new InvalidOperationException("Add at least one rundown item before saving.");
		return new RundownDefinition(
			RundownContractVersion.Current,
			_rundownId,
			string.IsNullOrWhiteSpace(Name) ? "Production Rundown" : Name.Trim(),
			Items.Select(item => item.Item).ToArray());
	}

	private void ApplySnapshot(RundownWorkspaceSnapshot snapshot)
	{
		var selectedId = SelectedItem?.Item.ItemId;
		Items.Clear();
		if (snapshot.Rundown is { } rundown)
		{
			_rundownId = rundown.RundownId;
			Name = rundown.Name;
			foreach (var item in rundown.Items)
				Items.Add(new RundownOperatorItemViewModel(item));
		}
		else
		{
			_rundownId = RundownId.New();
		}

		_storageVersion = snapshot.StorageVersion;
		var execution = snapshot.Execution;
		foreach (var item in Items)
			item.ApplyExecution(execution);
		SelectedItem = Items.FirstOrDefault(item => selectedId.HasValue && item.Item.ItemId == selectedId.Value)
			?? Items.FirstOrDefault(item => execution.SelectedItemId.HasValue && item.Item.ItemId == execution.SelectedItemId.Value)
			?? Items.FirstOrDefault();

		State = execution.State.ToString().ToUpperInvariant();
		RequiresAcknowledgement = execution.RequiresAcknowledgement;
		Failure = execution.Failure?.Message ?? "NONE";
		Detail = execution.State switch
		{
			RundownExecutionState.Prepared => "Selected item is prepared through authoritative Show Control.",
			RundownExecutionState.Executing => execution.AutoAdvanceArmed
				? "Executing · auto-advance armed from confirmed media completion."
				: "Executing through authoritative Show Control.",
			RundownExecutionState.Held => "Rundown is held. Program state remains authoritative.",
			RundownExecutionState.Completed => "Rundown reached its bounded end.",
			RundownExecutionState.Failed => "Execution stopped on a production failure.",
			RundownExecutionState.RecoveryRequired => "Execution state is ambiguous; operator acknowledgement is required.",
			_ => snapshot.Rundown is null ? "No rundown is authored." : "Rundown is ready."
		};
		OnPropertyChanged(nameof(HasItems));
		OnPropertyChanged(nameof(StorageLabel));
		RaiseCommandState();
	}

	private async Task RunAsync(Func<Task> operation)
	{
		if (IsBusy)
			return;
		IsBusy = true;
		RaiseCommandState();
		try
		{
			await operation();
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or
			ArgumentException or
			InvalidDataException or
			IOException or
			FormatException or
			TimeoutException or
			NotSupportedException)
		{
			Failure = exception.Message;
			State = "FAILED";
			Detail = "Rundown command was rejected; no local production authority was assumed.";
		}
		finally
		{
			IsBusy = false;
			RaiseCommandState();
		}
	}

	private bool CanSave() => !IsBusy && Items.Count > 0 && State is not "EXECUTING" and not "RECOVERYREQUIRED";
	private bool CanAddMedia() => !IsBusy &&
		_mediaPool.SelectedItem?.Kind == MediaPoolItemKind.Clip &&
		!string.IsNullOrWhiteSpace(_mediaPool.SelectedItem.ReferenceId) &&
		_operator.SelectedSource is not null;
	private bool CanAddScene() => !IsBusy && _operator.SelectedScene is not null;
	private bool CanAddAudio() => !IsBusy && _operator.SelectedAudioInput is not null;
	private bool CanAddGraphics() => !IsBusy &&
		!string.IsNullOrWhiteSpace(_operator.GraphicsAssetName) &&
		!string.Equals(_operator.GraphicsAssetName, "No graphics asset loaded", StringComparison.Ordinal);
	private bool CanPrepare() => !IsBusy && SelectedItem is not null && State is not "EXECUTING" and not "RECOVERYREQUIRED";
	private bool CanNavigate() => !IsBusy && Items.Count > 0 && State is not "EXECUTING" and not "RECOVERYREQUIRED";

	private bool CanMove(int offset)
	{
		if (IsBusy || SelectedItem is null || State is "EXECUTING" or "RECOVERYREQUIRED")
			return false;
		var index = Items.IndexOf(SelectedItem);
		return index >= 0 && index + offset >= 0 && index + offset < Items.Count;
	}

	private void RaiseCommandState()
	{
		foreach (var command in new[]
		{
			RefreshCommand, SaveCommand, AddMediaCommand, AddSceneCommand, AddAudioBreakawayCommand,
			AddFollowVideoCommand, AddGraphicsCommand, AddHoldCommand, RemoveCommand, MoveUpCommand,
			MoveDownCommand, PrepareCommand, GoCommand, NextCommand, PreviousCommand, HoldCommand,
			ResumeRecoveryCommand, CancelRecoveryCommand
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

public sealed class RundownOperatorItemViewModel : INotifyPropertyChanged
{
	private string _executionState = "READY";

	public RundownOperatorItemViewModel(RundownItem item)
	{
		Item = item ?? throw new ArgumentNullException(nameof(item));
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public RundownItem Item { get; }
	public string Id => Item.ItemId.ToString();
	public string Name => Item.Name;
	public string Kind => Item.Kind.ToString().ToUpperInvariant();
	public string ExecutionState
	{
		get => _executionState;
		private set
		{
			if (string.Equals(_executionState, value, StringComparison.Ordinal))
				return;
			_executionState = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExecutionState)));
		}
	}
	public string Detail => Item switch
	{
		RundownMediaItem media => $"{media.Transition!.Kind.ToString().ToUpperInvariant()} · {(media.AdvanceMode == RundownAdvanceMode.AutoOnMediaEnd ? "AUTO" : "MANUAL")} · {media.AssetId}",
		RundownSceneItem scene => $"SCENE · {scene.SceneId}",
		RundownGraphicsItem graphics => $"{graphics.LayerId} · {(graphics.Visible ? "SHOW" : "HIDE")}",
		RundownAudioRoutingItem audio => audio.RoutingMode == RundownAudioRoutingItem.BreakawayMode
			? $"BREAKAWAY · {audio.BreakawaySourceId}"
			: "FOLLOW_VIDEO",
		RundownHoldItem hold => $"HOLD · {hold.Frames}f",
		_ => Item.Kind.ToString()
	};

	public void ApplyExecution(RundownExecutionSnapshot execution)
	{
		ExecutionState =
			execution.State == RundownExecutionState.Failed && execution.CurrentItemId == Item.ItemId ? "FAILED" :
			execution.State == RundownExecutionState.RecoveryRequired && execution.CurrentItemId == Item.ItemId ? "RECOVERY" :
			execution.CurrentItemId == Item.ItemId ? "CURRENT" :
			execution.PreparedItemId == Item.ItemId ? "PREPARED" :
			execution.NextItemId == Item.ItemId ? "NEXT" :
			execution.SelectedItemId == Item.ItemId ? "SELECTED" :
			"READY";
	}
}
