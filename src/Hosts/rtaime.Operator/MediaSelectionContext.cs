// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Operator;

// A stable borrowed selection port, never a second selected-item or snapshot store.
// All changes go through the existing Inspector's reconciliation/draft boundary.
public sealed class MediaSelectionContext
{
    private readonly MediaPoolInspectorViewModel _owner;
    internal MediaSelectionContext(MediaPoolInspectorViewModel owner) => _owner = owner;
    public MediaPoolItemViewModel? Primary => _owner.SelectedItem;
    public IReadOnlyList<MediaPoolItemViewModel> Items => _owner.SelectedItems;
    public string Identity => _owner.EditingSelectionIdentity;
    public void Select(IEnumerable<MediaPoolItemViewModel> items) => _owner.UpdateSelection(items);
}
