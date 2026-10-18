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
    private Point _dragStart;
    private CompositingGraphNodeViewModel? _dragLayer;
    private sealed record LayerDrag(CompositingGraphViewModel Owner, string Id);

	public CompositingGraphControl()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private CompositingGraphViewModel? ViewModel => DataContext as CompositingGraphViewModel;

	private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
		UpdateViewport();

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
		GraphViewport.ReleaseMouseCapture();
		GraphViewport.Cursor = Cursors.Arrow;
	}
    private void OnWorkspaceUnloaded(object sender, RoutedEventArgs e) { EndPan(); _dragLayer = null; }
    private void OnWorkspaceKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } model || e.OriginalSource is TextBox) return;
        if (e.Key == Key.F3) { model.ShowLayers = true; LayerSearch.Focus(); e.Handled = true; }
        else if (e.Key == Key.Home) { model.Fit(); e.Handled = true; }
        else if (e.Key is Key.Add or Key.OemPlus) { model.ZoomAt(1.12, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { model.ZoomAt(1 / 1.12, GraphViewport.ActualWidth / 2, GraphViewport.ActualHeight / 2); e.Handled = true; }
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
