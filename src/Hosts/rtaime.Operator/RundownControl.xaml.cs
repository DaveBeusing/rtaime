// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class RundownControl : UserControl, INotifyPropertyChanged
{
    // Presentation only: the same rundown owner and governed commands serve both embeddings.
    public static readonly DependencyProperty IsConsoleModeProperty = DependencyProperty.Register(
        nameof(IsConsoleMode), typeof(bool), typeof(RundownControl),
        new PropertyMetadata(false, (owner, _) => ((RundownControl)owner).RefreshPresentation()));
    public bool IsConsoleMode { get => (bool)GetValue(IsConsoleModeProperty); set => SetValue(IsConsoleModeProperty, value); }
    public Visibility AuthoringVisibility => IsConsoleMode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ConsoleVisibility => IsConsoleMode ? Visibility.Visible : Visibility.Collapsed;
    public double MinimumListHeight => IsConsoleMode ? 0 : 140;
    public GridLength NavigationColumnWidth => IsConsoleMode ? new(0) : new(1, GridUnitType.Star);
    public GridLength NavigationGap => new(IsConsoleMode ? 0 : 3);
    public GridLength ConsoleGoWidth => IsConsoleMode ? new(104) : new(1, GridUnitType.Star);
    public double StatusMaximumHeight => IsConsoleMode ? 16 : double.PositiveInfinity;
    public double DetailMaximumHeight => IsConsoleMode ? 28 : double.PositiveInfinity;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void RefreshPresentation()
    {
        foreach (var name in new[] { nameof(AuthoringVisibility), nameof(ConsoleVisibility),
            nameof(MinimumListHeight), nameof(NavigationColumnWidth), nameof(NavigationGap),
            nameof(ConsoleGoWidth), nameof(StatusMaximumHeight), nameof(DetailMaximumHeight) })
            PropertyChanged?.Invoke(this, new(name));
    }
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
    private void Rows_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RundownViewModel model && RowAt(e.OriginalSource as DependencyObject) is { } row)
            model.SelectedItem = row; // Context selection is local and never prepares or executes.
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
