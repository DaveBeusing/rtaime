// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed partial class RundownViewModel
{
	private sealed record Draft(string Name, RundownItem[] Items, RundownItemId? Selected);
	private readonly AuthoringDraftHistory<Draft> _draftHistory = new();
	private bool _applyingSnapshot, _restoringDraft;
	private string? _lastDraftEdit;
	public ICommand UndoDraftCommand { get; private set; } = null!;
	public ICommand RedoDraftCommand { get; private set; } = null!;
	public string DraftHistoryLabel => $"DRAFT UNDO {_draftHistory.UndoCount} / REDO {_draftHistory.RedoCount} · GO/SAVE excluded";
	private void InitializeDraftHistory()
	{
		UndoDraftCommand = new OperatorShellCommand(() => RestoreDraft(_draftHistory.Undo(CaptureDraft())), () => CanEditDraft() && _draftHistory.CanUndo);
		RedoDraftCommand = new OperatorShellCommand(() => RestoreDraft(_draftHistory.Redo(CaptureDraft())), () => CanEditDraft() && _draftHistory.CanRedo);
	}
    public void EndDraftNameEdit() => _lastDraftEdit = null;
	private Draft CaptureDraft() => new(Name, Items.Select(item => item.Item).ToArray(), SelectedItem?.Item.ItemId);
	private void RememberDraft(string kind = "items")
	{
		if (_applyingSnapshot || _restoringDraft) return;
		if (kind != "name" || _lastDraftEdit != "name") _draftHistory.Remember(CaptureDraft());
		_lastDraftEdit = kind;
	}
	private void RestoreDraft(Draft draft)
	{
		_restoringDraft = true;
		try
		{
			_name = draft.Name; OnPropertyChanged(nameof(Name));
			Items.Clear();
			foreach (var item in draft.Items) Items.Add(new RundownOperatorItemViewModel(item));
			SelectedItem = Items.FirstOrDefault(item => item.Item.ItemId == draft.Selected) ?? Items.FirstOrDefault();
			_lastDraftEdit = null;
			MarkDraft(); // Restores authoring data only; never restores an execution state.
		}
		finally { _restoringDraft = false; }
	}
	private void ClearDraftHistory()
	{
		_draftHistory.Clear(); _lastDraftEdit = null;
		OnPropertyChanged(nameof(DraftHistoryLabel));
		(UndoDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		(RedoDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
	}
}
