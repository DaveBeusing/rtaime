// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class CompositingGraphControl : UserControl
{
	private bool _isPanning;
	private Point _lastPointer;

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
			viewModel.UpdateViewport(GraphViewport.ActualWidth, GraphViewport.ActualHeight);
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
}
