// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Operator.Controls;

namespace rtaime.Operator;

public partial class CompositingGraphControl : UserControl
{
	private bool _isPanning;
	private Point _lastPointer;
    private CompositingGraphNodeViewModel? _nodeDrag;
    private FrameworkElement? _nodeDragSource;
    private Point _nodeDragPointer;
    private Point _dragStart;
    private CompositingGraphNodeViewModel? _dragLayer;
    private sealed record LayerDrag(CompositingGraphViewModel Owner, string Id);

	public CompositingGraphControl()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private CompositingGraphViewModel? ViewModel => DataContext as CompositingGraphViewModel;

	private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        EndPan();
        EndNodeDrag();
        UpdateViewport();
    }

    private void OnGraphVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) { EndNodeDrag(); EndPan(); }
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is { } model) model.UpdateWorkspaceWidth(e.NewSize.Width);
        UpdateViewport();
    }

	private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) =>
		UpdateViewport();

	private void UpdateViewport()
	{
		if (ViewModel is { } viewModel && GraphViewport is not null)
			{
            viewModel.UpdateWorkspaceWidth(ActualWidth);
            viewModel.UpdateViewport(GraphViewport.ActualWidth, GraphViewport.ActualHeight);
        }
	}

	private void OnGraphMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (ViewModel is not { } viewModel)
			return;

		var position = e.GetPosition(GraphViewport);
		viewModel.ZoomAt(e.Delta > 0 ? 1.12 : 1.0 / 1.12, position.X, position.Y);
		e.Handled = true;
	}

	private void OnGraphMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (ViewModel is not { } viewModel)
			return;

		var beginPan = e.ChangedButton == MouseButton.Middle ||
			(e.ChangedButton == MouseButton.Left && viewModel.IsPanMode);
		if (!beginPan)
			return;

		_isPanning = true;
		_lastPointer = e.GetPosition(GraphViewport);
		GraphViewport.CaptureMouse();
		GraphViewport.Cursor = Cursors.SizeAll;
		e.Handled = true;
	}

	private void OnGraphMouseMove(object sender, MouseEventArgs e)
	{
		if (!_isPanning || ViewModel is not { } viewModel)
			return;

		var position = e.GetPosition(GraphViewport);
		viewModel.PanBy(position.X - _lastPointer.X, position.Y - _lastPointer.Y);
		_lastPointer = position;
		e.Handled = true;
	}

	private void OnGraphMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!_isPanning)
			return;
		EndPan();
		e.Handled = true;
	}

	private void OnGraphMouseLeave(object sender, MouseEventArgs e)
	{
		if (_isPanning && e.LeftButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed)
			EndPan();
	}

	private void EndPan()
	{
        _isPanning = false;
        // Visibility changes can arrive while the XAML name scope is initializing.
        if (GraphViewport is not { } viewport) return;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        viewport.Cursor = Cursors.Arrow;
	}
    private void OnNodeSelectionModifiers(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } model) return;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 ||
            !model.IsSelectMode || sender is not FrameworkElement source ||
            (sender as FrameworkElement)?.DataContext is not CompositingGraphNodeViewModel node)
        {
            model.ExtendSelection = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            return;
        }

        EndNodeDrag();
        model.SelectNodeCommand.Execute(node);
        _nodeDrag = node;
        _nodeDragSource = source;
        _nodeDragPointer = e.GetPosition(GraphViewport);
        source.CaptureMouse();
        source.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnNodeDragMove(object sender, MouseEventArgs e)
    {
        if (_nodeDrag is not { } node || ViewModel is not { } model ||
            !ReferenceEquals(sender, _nodeDragSource)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndNodeDrag(); return; }
        var current = e.GetPosition(GraphViewport);
        model.TryMoveNode(node, current.X - _nodeDragPointer.X, current.Y - _nodeDragPointer.Y);
        _nodeDragPointer = current;
        e.Handled = true;
    }

    private void OnNodeDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_nodeDrag is null || !ReferenceEquals(sender, _nodeDragSource)) return;
        EndNodeDrag();
        e.Handled = true; // Do not synthesize a second node click after a local move.
    }

    private void OnNodeDragLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _nodeDragSource)) EndNodeDrag();
    }

    private void EndNodeDrag()
    {
        var source = _nodeDragSource;
        _nodeDragSource = null;
        _nodeDrag = null;
        if (source is null) return;
        source.Cursor = Cursors.Arrow;
        if (source.IsMouseCaptured) source.ReleaseMouseCapture();
    }

    private void OnConnectionSelect(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } graph &&
            (sender as FrameworkElement)?.DataContext is CompositingGraphConnectionViewModel connection)
        {
            graph.SelectConnection(connection);
            (sender as UIElement)?.Focus();
            e.Handled = true;
        }
    }

    private void OnConnectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            ViewModel is not { } graph ||
            (sender as FrameworkElement)?.DataContext is not CompositingGraphConnectionViewModel connection) return;
        graph.SelectConnection(connection);
        e.Handled = true;
    }

    private void OnWorkspaceUnloaded(object sender, RoutedEventArgs e)
    {
        EndNodeDrag(); EndPan(); _dragLayer = null;
    }
    private void OnWorkspaceKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } model || e.OriginalSource is TextBox) return;
        if (e.Key == Key.F3) { model.ShowLayers = true; LayerSearch.Focus(); e.Handled = true; }
        else if (e.Key == Key.Home) { model.Fit(); e.Handled = true; }
        else if (e.Key is Key.Add or Key.OemPlus) { model.ZoomAt(1.12, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { model.ZoomAt(1 / 1.12, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2); e.Handled = true; }
        else if (GraphViewport.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Control &&
            model.SelectedNode is { } selected)
        {
            var step = e.Key switch
            {
                Key.Left => new Vector(-8, 0),
                Key.Right => new Vector(8, 0),
                Key.Up => new Vector(0, -8),
                Key.Down => new Vector(0, 8),
                _ => default
            };
            if (step != default && model.TryMoveNode(selected, step.X, step.Y)) e.Handled = true;
        }
    }
    private void OnLayerInputValidityChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } model) model.LayerDraftInputsValid = !LayerOpacityEditor.HasInputError;
    }
    private void OnLayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } model && LayerList.IsKeyboardFocusWithin)
            model.SelectLayers(LayerList.SelectedItems.OfType<CompositingGraphNodeViewModel>().ToArray());
    }
    private static T? Ancestor<T>(DependencyObject? child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T target) return target;
        return null;
    }
    private void OnLayerDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(LayerList);
        _dragLayer = Ancestor<RtaimeListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as CompositingGraphNodeViewModel;
    }
    private void OnLayerDragMove(object sender, MouseEventArgs e)
    {
        if (ViewModel is not { } model || _dragLayer is not { } row || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(LayerList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragLayer = null;
        DragDrop.DoDragDrop(LayerList, new LayerDrag(model, row.Id["layer:".Length..]), DragDropEffects.Move);
    }
    private void OnLayerDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } model || e.Data.GetData(typeof(LayerDrag)) is not LayerDrag drag ||
            !ReferenceEquals(drag.Owner, model) ||
            Ancestor<RtaimeListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not CompositingGraphNodeViewModel target) return;
        model.StageLayerOrder(drag.Id, target.Id["layer:".Length..]);
        e.Effects = DragDropEffects.Move; e.Handled = true;
    }

}
