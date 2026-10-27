// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;

namespace rtaime.Operator;

// A pin holds only the existing Inspector selection identity. It does not own
// a snapshot, override Control state, or make any draft eligible for Program.
public sealed partial class MediaPoolInspectorViewModel
{
    private string? _inspectorPinnedIdentity;
    private int _inspectorIgnoredSelections;
    private ICommand? _toggleInspectorSelectionPin;

    public bool IsInspectorSelectionPinned => _inspectorPinnedIdentity is not null;
    public string InspectorSelectionPinLabel => IsInspectorSelectionPinned ? "UNPIN" : "PIN";
    public string InspectorSelectionPinStatus
    {
        get
        {
            if (_inspectorPinnedIdentity is null)
                return "FOLLOW SELECTION · context changes retain typed local intent for review";
            if (!string.Equals(_inspectorPinnedIdentity, EditingSelectionIdentity, StringComparison.Ordinal))
                return "PIN STALE · production or identity changed; edits blocked until UNPIN";
            if (!InspectorPinnedAudioTargetMatches)
                return "PIN HELD · audio selection changed outside Inspector; Apply blocked";
            return "PINNED INSPECTOR · " + _inspectorPinnedIdentity +
                (_inspectorIgnoredSelections == 0 ? " · following confirmed revisions" :
                    " · " + _inspectorIgnoredSelections + " other selection(s) ignored");
        }
    }
    public string InspectorContextBreadcrumb
    {
        get
        {
            var kind = _compositingNode is { } node ? "GRAPH / " + node.Kind.ToString().ToUpperInvariant() + " / " + node.Id :
                _timelineCue is { } cue ? "TIMELINE / CUE / " + cue.Id :
                _timelineItems.Count > 1 ? "TIMELINE / " + _timelineItems.Count + " ITEMS" :
                _timelineItem is { } item ? "TIMELINE / " + item.Category.ToString().ToUpperInvariant() + " / " + item.Id :
                HasMultipleSelection ? "MEDIA / MULTIPLE" :
                SelectedItem is { } media ? "MEDIA / " + media.Kind.ToString().ToUpperInvariant() + " / " + media.Key :
                "NO SELECTION";
            var revision = _operator.Client?.Snapshot?.Production.Revision.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "—";
            return kind + " · REV " + revision;
        }
    }

    // A stable selection can be followed through revised observations while pinned.
    // A new production or an externally changed audio target cannot be used as
    // authority to submit the old Inspector's draft.
    // Existing audio Apply relies on the mixer's selected input. It must still
    // refer to the exact Inspector source/stream, including when not pinned.
    private bool InspectorAudioSelectionMatches =>
        _activeInspectorTarget is { Kind: "gain" } target &&
        _operator.SelectedAudioInput is { } input &&
        input.SourceId == target.Id && input.StreamId == target.ChildId;

    private bool InspectorPinnedAudioTargetMatches =>
        _activeInspectorTarget is not { Kind: "gain" } || InspectorAudioSelectionMatches;

    private bool InspectorPinMatchesCurrent => _inspectorPinnedIdentity is null ||
        string.Equals(_inspectorPinnedIdentity, EditingSelectionIdentity, StringComparison.Ordinal) &&
        InspectorPinnedAudioTargetMatches;

    public ICommand ToggleInspectorSelectionPinCommand => _toggleInspectorSelectionPin ??=
        new OperatorShellCommand(() =>
        {
            if (_inspectorPinnedIdentity is null)
            {
                if (!HasSelection || HasMultipleSelection || _inspectorTransactionsStopped) return;
                _inspectorPinnedIdentity = EditingSelectionIdentity;
            }
            else _inspectorPinnedIdentity = null;
            _inspectorIgnoredSelections = 0;
            NotifyInspectorPinState();
            RaiseSelectionState();
        }, () => !_inspectorTransactionsStopped &&
            (IsInspectorSelectionPinned || HasSelection && !HasMultipleSelection));

    private bool ShouldSuppressInspectorSelection(string? selection)
    {
        if (_inspectorPinnedIdentity is null) return false;
        var production = _operator.Client?.Snapshot?.Production.ProductionId.ToString() ?? "offline";
        if (string.Equals(_inspectorPinnedIdentity, production + ":" + (selection ?? "none"), StringComparison.Ordinal))
            return false;
        _inspectorIgnoredSelections = Math.Min(99, _inspectorIgnoredSelections + 1);
        NotifyInspectorPinState();
        return true;
    }

    private void NotifyInspectorPinState()
    {
        OnPropertyChanged(nameof(IsInspectorSelectionPinned));
        OnPropertyChanged(nameof(InspectorSelectionPinLabel));
        OnPropertyChanged(nameof(InspectorSelectionPinStatus));
        OnPropertyChanged(nameof(InspectorContextBreadcrumb));
        (_toggleInspectorSelectionPin as OperatorShellCommand)?.RaiseCanExecuteChanged();
    }
}
