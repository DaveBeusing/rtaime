// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed partial class MediaTimelineViewModel
{
	private sealed record LocalDraft(long? Cursor, TimelineResourceProjection[] Resources);
	private readonly AuthoringDraftHistory<LocalDraft> _draftHistory = new();
	private LocalDraft? _cursorGestureStart;
	private long? _editCursorFrame;
	private bool _editCursorMode, _mediaOnProgram, _routingCurrent;
	private string? _previewSource, _programSource, _loadedSource;
	private MediaMarkerSnapshot? _authoringMarkers;
	private string _snapFeedback = "FRAME GRID";
	public long EditingContextVersion { get; private set; }
	public bool EditCursorMode
	{
		get => _editCursorMode;
		set
		{
			if (_editCursorMode == value) return;
			CancelPointerEdit(); _editCursorMode = value;
			OnPropertyChanged(); RaiseEditingState();
		}
	}
	public long EditCursorFrame => _editCursorFrame ?? ConfirmedFrame;
	public bool HasEditCursor => _editCursorFrame.HasValue && IsLoaded;
	public string EditCursorLabel => HasEditCursor ? $"EDIT DRAFT · {FormatFrame(EditCursorFrame)}" : "EDIT DRAFT · unset";
	public string PreviewPositionLabel => !_routingCurrent ? "PREVIEW · evidence unavailable" :
		_loadedSource == _previewSource && IsLoaded ? $"PREVIEW CONFIRMED · {FormatFrame(ConfirmedFrame)}" : "PREVIEW · other source / position unavailable";
	public string ProgramPositionLabel => !_routingCurrent ? "PROGRAM · evidence unavailable" :
		_loadedSource == _programSource && _mediaOnProgram && IsLoaded ? $"PROGRAM CONFIRMED · {FormatFrame(ConfirmedFrame)}" : "PROGRAM · independent position unavailable";
	public string SnapFeedback => _snapFeedback;
	public string DraftHistoryLabel => $"LOCAL UNDO {_draftHistory.UndoCount} / REDO {_draftHistory.RedoCount} · confirmed operations excluded";
	public ICommand UndoDraftCommand { get; private set; } = null!;
	public ICommand RedoDraftCommand { get; private set; } = null!;
	public ICommand SelectAllItemsCommand { get; private set; } = null!;
	public ICommand ClearSelectionCommand { get; private set; } = null!;
	public ICommand CursorToSelectionCommand { get; private set; } = null!;
	public ICommand SeekEditCursorCommand { get; private set; } = null!;
	private bool CanChangeLocalDraft => IsLoaded && !HasPendingSeek && !HasTrimPreview && _cursorGestureStart is null;
	private void InitializeAuthoringCommands()
	{
		UndoDraftCommand = new OperatorShellCommand(() => RestoreDraft(_draftHistory.Undo(CaptureDraft())), () => CanChangeLocalDraft && _draftHistory.CanUndo);
		RedoDraftCommand = new OperatorShellCommand(() => RestoreDraft(_draftHistory.Redo(CaptureDraft())), () => CanChangeLocalDraft && _draftHistory.CanRedo);
		SelectAllItemsCommand = new OperatorShellCommand(() =>
		{
			ClearSelectedItems();
			foreach (var item in Tracks.SelectMany(t => t.Items)) { item.IsSelected = true; SelectedItems.Add(item); }
			_selectedItem = SelectedItems.FirstOrDefault(); ClearSelectedCue(); _selectedCue = null;
			RaiseSelectionProperties(); RaiseEditingState();
			SelectionChanged?.Invoke(new TimelineSelection(_selectedItem, null, SelectedItems.ToArray()));
		}, () => IsLoaded);
		ClearSelectionCommand = new OperatorShellCommand(() => SelectItem(null), () => IsLoaded);
		CursorToSelectionCommand = new OperatorShellCommand(() => SetEditCursor(SelectedCue?.Frame ?? SelectedItem!.StartFrame),
			() => IsLoaded && (SelectedCue is not null || SelectedItem is not null));
		SeekEditCursorCommand = new AsyncRelayCommand(async () =>
		{
			if (!CanSeekEditCursor()) return;
			InvalidateDraftHistory();
			await _controller.SeekToFrameAsync(EditCursorFrame);
		}, CanSeekEditCursor);
	}
	private bool CanSeekEditCursor() => HasEditCursor && CanSeek && _routingCurrent &&
		_loadedSource == _previewSource && _loadedSource != _programSource && !_mediaOnProgram;

	public void SetConfirmedRoutingContext(string? previewSource, string? programSource, bool current)
	{
		if (_previewSource == previewSource && _programSource == programSource && _routingCurrent == current) return;
		_previewSource = previewSource; _programSource = programSource; _routingCurrent = current; RaiseEditingState();
	}
	private void UpdateEditingEvidence(MediaDeckSnapshot snapshot)
	{
		var markers = snapshot.Markers;
		var contextChanged = _loadedSource != snapshot.SourceId?.ToString() ||
			_authoringMarkers?.AssetId != markers?.AssetId || _authoringMarkers?.TotalFrames != markers?.TotalFrames;
		var markersChanged = contextChanged || _authoringMarkers?.InPointFrame != markers?.InPointFrame ||
			_authoringMarkers?.OutPointFrame != markers?.OutPointFrame ||
			!(_authoringMarkers?.CuePoints ?? []).SequenceEqual(markers?.CuePoints ?? []);
		if (markersChanged)
		{
			EditingContextVersion++;
			_cursorGestureStart = null;
			_controller.CancelPointerSeek();
			InvalidateDraftHistory();
			if (contextChanged) _editCursorFrame = null;
		}
		_authoringMarkers = markers; _loadedSource = snapshot.SourceId?.ToString();
		_mediaOnProgram = snapshot.Transport?.IsOnProgram == true;
		RaiseEditingState();
	}
	public void SetEditCursor(long frame, bool remember = true)
	{
		if (!IsLoaded) return;
		frame = Math.Clamp(frame, 0, TotalFrames - 1);
		if (_editCursorFrame == frame) return;
		if (remember) _draftHistory.Remember(CaptureDraft());
		_editCursorFrame = frame; RaiseEditingState();
	}
	public void StepEditCursor(long delta)
	{
		if (!IsLoaded) return;
		var current = EditCursorFrame;
		var target = delta > 0 ? current + Math.Min(delta, TotalFrames - 1 - current) : current - Math.Min(-delta, current);
		SetEditCursor(target);
	}
	public void CancelPointerEdit()
	{
		if (_cursorGestureStart is { } draft) RestoreDraft(draft);
		_cursorGestureStart = null;
		_controller.CancelPointerSeek();
		RaiseEditingState();
	}
	private LocalDraft CaptureDraft() => new(_editCursorFrame, _resourceProjections.ToArray());
	private void RestoreDraft(LocalDraft state)
	{
		_editCursorFrame = state.Cursor;
		_resourceProjections.Clear(); _resourceProjections.AddRange(state.Resources);
		RebuildResourceProjections(); SelectItem(null); RaiseEditingState();
	}
	private void InvalidateDraftHistory() { _draftHistory.Clear(); RaiseEditingState(); }
	private void RaiseEditingState()
	{
		foreach (var property in new[] { nameof(EditCursorFrame), nameof(HasEditCursor), nameof(EditCursorLabel),
			nameof(PreviewPositionLabel), nameof(ProgramPositionLabel), nameof(DraftHistoryLabel), nameof(SnapFeedback), nameof(EditingContextVersion) })
			OnPropertyChanged(property);
		(UndoDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		(RedoDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		(SelectAllItemsCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		(ClearSelectionCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		(CursorToSelectionCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		(SeekEditCursorCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}
}
