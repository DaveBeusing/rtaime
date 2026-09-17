// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows.Controls;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class MediaTimelineControl : UserControl
{
	private bool _pointerSeeking;

	public MediaTimelineControl()
	{
		InitializeComponent();
	}

	private MediaTimelineViewModel? ViewModel => DataContext as MediaTimelineViewModel;

	private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (ViewModel is not { CanSeek: true } viewModel)
			return;

		_pointerSeeking = true;
		TimelineSlider.CaptureMouse();
		viewModel.BeginPointerSeek();
		var position = e.GetPosition(TimelineSlider);
		viewModel.PreviewPointerSeek(position.X, TimelineSlider.ActualWidth);
		e.Handled = true;
	}

	private void TimelineSlider_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_pointerSeeking || e.LeftButton != MouseButtonState.Pressed || ViewModel is not { CanSeek: true } viewModel)
			return;

		var position = e.GetPosition(TimelineSlider);
		viewModel.PreviewPointerSeek(position.X, TimelineSlider.ActualWidth);
		e.Handled = true;
	}

	private async void TimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!_pointerSeeking)
			return;

		_pointerSeeking = false;
		TimelineSlider.ReleaseMouseCapture();
		if (ViewModel is { CanSeek: true } viewModel)
		{
			var position = e.GetPosition(TimelineSlider);
			await viewModel.CompletePointerSeekAsync(position.X, TimelineSlider.ActualWidth);
		}
		e.Handled = true;
	}

	private void TimelineSlider_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (ViewModel is not { CanSeek: true } viewModel)
			return;

		ICommand? command = e.Key switch
		{
			Key.Left => viewModel.StepBackwardCommand,
			Key.Right => viewModel.StepForwardCommand,
			Key.Home => viewModel.JumpToStartCommand,
			Key.End => viewModel.JumpToEndCommand,
			_ => null
		};
		if (command is null || !command.CanExecute(null))
			return;

		command.Execute(null);
		e.Handled = true;
	}
}
