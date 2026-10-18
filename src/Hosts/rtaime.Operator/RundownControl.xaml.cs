// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class RundownControl : UserControl
{
	public RundownControl()
	{
		InitializeComponent();
		LiveConsoleInput.Attach(this);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) =>
        { if (DataContext is RundownViewModel model) model.EndDraftNameEdit(); }));
	}
    private Point _dragStart;
    private RundownOperatorItemViewModel? _dragItem;
    private void Rows_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = RowAt(e.OriginalSource as DependencyObject);
    }
    private void Rows_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null ||
            DataContext is not RundownViewModel { CanEditAuthoring: true }) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem; _dragItem = null;
        DragDrop.DoDragDrop(this, new DataObject(typeof(RundownOperatorItemViewModel), item), DragDropEffects.Move);
    }
    private void Rows_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataContext is RundownViewModel { CanEditAuthoring: true } model &&
            e.Data.GetData(typeof(RundownOperatorItemViewModel)) is RundownOperatorItemViewModel item &&
            model.Items.Contains(item) && RowAt(e.OriginalSource as DependencyObject) is not null
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void Rows_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is RundownViewModel model &&
            e.Data.GetData(typeof(RundownOperatorItemViewModel)) is RundownOperatorItemViewModel item &&
            RowAt(e.OriginalSource as DependencyObject) is { } target)
            model.MoveDraftBefore(item, target);
        e.Handled = true;
    }
    private static RundownOperatorItemViewModel? RowAt(DependencyObject? element)
    {
        for (var current = element; current is not null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ListBoxItem { DataContext: RundownOperatorItemViewModel row }) return row;
        return null;
    }
	private void Rundown_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		// Text editors retain their own native text undo.
		if (Keyboard.FocusedElement is TextBoxBase || DataContext is not RundownViewModel model) return;
		if (Keyboard.Modifiers != ModifierKeys.Control) return;
		var command = e.Key == Key.Z ? model.UndoDraftCommand : e.Key == Key.Y ? model.RedoDraftCommand : null;
		if (command is null) return;
		if (!e.IsRepeat && command.CanExecute(null)) command.Execute(null);
		e.Handled = true;
	}
}
