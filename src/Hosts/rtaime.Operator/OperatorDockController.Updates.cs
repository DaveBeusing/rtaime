// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;

namespace rtaime.Operator;

public sealed partial class OperatorDockController
{
    private readonly Dictionary<string, FrameworkElement> _groupRoots = new(StringComparer.Ordinal);
    private OperatorDockLayout? _renderedLayout;
    private bool _renderedCompact;
    private string? _renderedFocus;

    private void RememberRenderedLayout(OperatorDockLayout layout)
    {
        _renderedLayout = layout; _renderedCompact = _owner.Shell.IsCompactViewport; _renderedFocus = _focus;
    }
    private bool TryUpdateGroups(OperatorDockLayout desired)
    {
        var previous = _renderedLayout;
        if (previous is null) return false;
        // Disabled docking already uses the original homes and their live workspace bindings.
        if (!desired.Enabled && !previous.Enabled && _focus is null && _renderedFocus is null && _floating.Count == 0)
            return true;
        if (!desired.Enabled || !previous.Enabled || _focus is not null || _renderedFocus is not null ||
            _owner.Shell.IsCompactViewport || _renderedCompact || _groupRoots.Count != OperatorDockLayout.GroupIds.Count ||
            desired.LeftRatio != previous.LeftRatio || desired.RightRatio != previous.RightRatio || desired.BottomRatio != previous.BottomRatio)
            return false;

        var changed = desired.Groups.Where(group => {
            var old = previous.Groups.Single(g => g.Id == group.Id);
            return old.ActivePanel != group.ActivePanel || !old.Panels.SequenceEqual(group.Panels, StringComparer.Ordinal);
        }).ToArray();
        var wanted = desired.Floating.Select(f => f.Panel).ToHashSet(StringComparer.Ordinal);
        var existing = previous.Floating.Select(f => f.Panel).ToHashSet(StringComparer.Ordinal);
        var preserved = wanted.Intersect(existing).ToHashSet(StringComparer.Ordinal);
        var touched = changed.SelectMany(g => g.Panels.Concat(previous.Groups.Single(p => p.Id == g.Id).Panels))
            .Concat(wanted.Except(existing)).Concat(existing.Except(wanted)).Distinct(StringComparer.Ordinal).ToArray();
        _renderedLayout = null; // An interrupted update must retry the established full restore path.
        foreach (var panel in touched.Where(p => !preserved.Contains(p))) DetachPanel(panel);
        foreach (var pair in _floating.Where(p => !wanted.Contains(p.Key)).ToArray())
        {
            pair.Value.Content = null; pair.Value.Close(); _floating.Remove(pair.Key);
        }
        foreach (var group in changed)
        {
            if (_groupRoots.Remove(group.Id, out var old)) _host.Children.Remove(old);
            var position = group.Id switch { "LEFT" => (0,0,1), "CENTER" => (1,0,1), "RIGHT" => (2,0,1), _ => (0,1,3) };
            AddGroup(group,position.Item1,position.Item2,position.Item3);
        }
        EnsureFloatingWindows(desired);
        return true;
    }
    private void DetachPanel(string id)
    {
        if (_presenters.Remove(id,out var presenter)) presenter.Content = null;
        var view = _panels[id];
        if (view.Parent is Panel parent) parent.Children.Remove(view);
        else if (view.Parent is Decorator decorator) decorator.Child = null;
        else if (view.Parent is ContentControl control) control.Content = null;
    }

}
