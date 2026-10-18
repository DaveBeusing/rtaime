// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public sealed class TimelineFrameToCanvasLeftConverter : IMultiValueConverter
{
	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		if (!TryLong(values.ElementAtOrDefault(0), out var frame) ||
			!TryLong(values.ElementAtOrDefault(1), out var visibleStart) ||
			!TryLong(values.ElementAtOrDefault(2), out var visibleEnd) ||
			!TryDouble(values.ElementAtOrDefault(3), out var width) ||
			width <= 0 ||
			visibleEnd < visibleStart)
		{
			return 0.0;
		}

		if (frame < visibleStart)
			return -10000.0;
		if (frame > visibleEnd)
			return width + 10000.0;
		if (visibleEnd == visibleStart)
			return 0.0;

		return (double)(frame - visibleStart) / (visibleEnd - visibleStart) * width;
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();

	internal static bool TryLong(object? value, out long result)
	{
		if (value is long number)
		{
			result = number;
			return true;
		}

		result = 0;
		return false;
	}

	internal static bool TryDouble(object? value, out double result)
	{
		if (value is double number && double.IsFinite(number))
		{
			result = number;
			return true;
		}

		result = 0;
		return false;
	}
}

public sealed class TimelineItemWidthConverter : IMultiValueConverter
{
	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
	{
		if (!TimelineFrameToCanvasLeftConverter.TryLong(values.ElementAtOrDefault(0), out var startFrame) ||
			!TimelineFrameToCanvasLeftConverter.TryLong(values.ElementAtOrDefault(1), out var durationFrames) ||
			!TimelineFrameToCanvasLeftConverter.TryLong(values.ElementAtOrDefault(2), out var visibleStart) ||
			!TimelineFrameToCanvasLeftConverter.TryLong(values.ElementAtOrDefault(3), out var visibleEnd) ||
			!TimelineFrameToCanvasLeftConverter.TryDouble(values.ElementAtOrDefault(4), out var width) ||
			durationFrames <= 0 ||
			width <= 0 ||
			visibleEnd < visibleStart)
		{
			return 0.0;
		}

		var itemEnd = startFrame > long.MaxValue - durationFrames + 1
			? long.MaxValue
			: startFrame + durationFrames - 1;
		var intersectionStart = Math.Max(startFrame, visibleStart);
		var intersectionEnd = Math.Min(itemEnd, visibleEnd);
		if (intersectionEnd < intersectionStart)
			return 0.0;

		var visibleCount = visibleEnd - visibleStart + 1;
		var intersectionCount = intersectionEnd - intersectionStart + 1;
		return Math.Max(2.0, (double)intersectionCount / visibleCount * width);
	}

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

public partial class MediaTimelineControl : UserControl
{
    public static readonly DependencyProperty PreviewPlaybackVisibilityProperty = DependencyProperty.Register(
        nameof(PreviewPlaybackVisibility), typeof(Visibility), typeof(MediaTimelineControl), new PropertyMetadata(Visibility.Visible));
    public Visibility PreviewPlaybackVisibility { get => (Visibility)GetValue(PreviewPlaybackVisibilityProperty); set => SetValue(PreviewPlaybackVisibilityProperty, value); }
	private bool _pointerSeeking;
	private string? _trimKind;
	private long _gestureContext;
	private double _dragOriginX;
	public bool OwnsEscape => _trimKind is not null || (_pointerSeeking && ViewModel?.EditCursorMode == true);
	public static readonly DependencyProperty PreviewSourceIdProperty = DependencyProperty.Register(nameof(PreviewSourceId), typeof(string), typeof(MediaTimelineControl), new PropertyMetadata(null, RoutingChanged));
	public static readonly DependencyProperty ProgramSourceIdProperty = DependencyProperty.Register(nameof(ProgramSourceId), typeof(string), typeof(MediaTimelineControl), new PropertyMetadata(null, RoutingChanged));
	public static readonly DependencyProperty RoutingConnectedProperty = DependencyProperty.Register(nameof(RoutingConnected), typeof(bool), typeof(MediaTimelineControl), new PropertyMetadata(false, RoutingChanged));
	public static readonly DependencyProperty RoutingStaleProperty = DependencyProperty.Register(nameof(RoutingStale), typeof(bool), typeof(MediaTimelineControl), new PropertyMetadata(true, RoutingChanged));
	public string? PreviewSourceId { get => (string?)GetValue(PreviewSourceIdProperty); set => SetValue(PreviewSourceIdProperty, value); }
	public string? ProgramSourceId { get => (string?)GetValue(ProgramSourceIdProperty); set => SetValue(ProgramSourceIdProperty, value); }
	public bool RoutingConnected { get => (bool)GetValue(RoutingConnectedProperty); set => SetValue(RoutingConnectedProperty, value); }
	public bool RoutingStale { get => (bool)GetValue(RoutingStaleProperty); set => SetValue(RoutingStaleProperty, value); }
	private static void RoutingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((MediaTimelineControl)sender).UpdateRouting();
	private void UpdateRouting() => ViewModel?.SetConfirmedRoutingContext(PreviewSourceId, ProgramSourceId, RoutingConnected && !RoutingStale);
	private double PreciseX(double x) => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? _dragOriginX + (x - _dragOriginX) / 10 : x;

	public MediaTimelineControl()
	{
		InitializeComponent();
		DataContextChanged += (_, _) => UpdateRouting();
		foreach (var (property, path) in new[] { (PreviewSourceIdProperty, "PreviewSourceId"), (ProgramSourceIdProperty, "ProgramSourceId"), (RoutingConnectedProperty, "IsConnected"), (RoutingStaleProperty, "IsStale") })
			SetBinding(property, new Binding("DataContext." + path) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
	}

	private MediaTimelineViewModel? ViewModel => DataContext as MediaTimelineViewModel;

	public TimelineTrackCategory? ResolveDropTarget(object? originalSource) =>
		FindDataContext<TimelineTrackViewModel>(originalSource as DependencyObject)?.Category;

	private void TimelineWorkspace_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (FindTaggedTrimHandle(e.OriginalSource as DependencyObject) is not null ||
			FindDataContext<TimelineTrackItemViewModel>(e.OriginalSource as DependencyObject) is not null ||
			FindDataContext<TimelineCueViewModel>(e.OriginalSource as DependencyObject) is not null)
		{
			return;
		}

		if (ViewModel is not { IsLoaded: true } viewModel || (!viewModel.EditCursorMode && !viewModel.CanSeek))
			return;

		var position = e.GetPosition(TimelineContentArea);
		if (position.X < 0 || position.X > TimelineContentArea.ActualWidth)
			return;

		Focus();
		_dragOriginX = position.X; _gestureContext = viewModel.EditingContextVersion;
		_pointerSeeking = true;
		TimelineWorkspace.CaptureMouse();
		viewModel.BeginPointerSeek();
		viewModel.PreviewPointerSeek(PreciseX(position.X), TimelineContentArea.ActualWidth);
		e.Handled = true;
	}

	private void TimelineWorkspace_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if ((_pointerSeeking || _trimKind is not null) && _gestureContext != ViewModel?.EditingContextVersion)
		{
			_pointerSeeking = false; _trimKind = null;
			ViewModel?.CancelTrimPreview(); ViewModel?.CancelPointerEdit();
			TimelineWorkspace.ReleaseMouseCapture(); return;
		}
		if (_trimKind is { } trimKind)
		{
			if (ViewModel is { CanSeek: true } trimViewModel &&
				e.LeftButton == MouseButtonState.Pressed)
			{
				var trimPosition = e.GetPosition(TimelineContentArea);
				trimViewModel.PreviewTrim(trimKind, PreciseX(trimPosition.X), TimelineContentArea.ActualWidth);
			}
			e.Handled = true;
			return;
		}

		if (!_pointerSeeking ||
			e.LeftButton != MouseButtonState.Pressed ||
			ViewModel is not { IsLoaded: true } viewModel || (!viewModel.EditCursorMode && !viewModel.CanSeek))
		{
			return;
		}

		var position = e.GetPosition(TimelineContentArea);
		viewModel.PreviewPointerSeek(PreciseX(position.X), TimelineContentArea.ActualWidth);
		e.Handled = true;
	}

	private async void TimelineWorkspace_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if ((_pointerSeeking || _trimKind is not null) && _gestureContext != ViewModel?.EditingContextVersion)
		{
			_pointerSeeking = false; _trimKind = null;
			ViewModel?.CancelTrimPreview(); ViewModel?.CancelPointerEdit();
			TimelineWorkspace.ReleaseMouseCapture(); e.Handled = true; return;
		}
		if (_trimKind is { } trimKind)
		{
			_trimKind = null;
			TimelineWorkspace.ReleaseMouseCapture();
			if (ViewModel is { CanSeek: true } trimViewModel)
			{
				var position = e.GetPosition(TimelineContentArea);
				try
				{
					if (string.Equals(trimKind, "IN", StringComparison.Ordinal))
						await trimViewModel.TrimInAsync(PreciseX(position.X), TimelineContentArea.ActualWidth);
					else
						await trimViewModel.TrimOutAsync(PreciseX(position.X), TimelineContentArea.ActualWidth);
				}
				catch (OperationCanceledException)
				{
					// Lifecycle shutdown may cancel a bounded trim command.
				}
				finally
				{
					trimViewModel.CancelTrimPreview();
				}
			}
			e.Handled = true;
			return;
		}

		if (!_pointerSeeking)
			return;

		_pointerSeeking = false;
		TimelineWorkspace.ReleaseMouseCapture();
		if (ViewModel is { IsLoaded: true } viewModel && (viewModel.EditCursorMode || viewModel.CanSeek))
		{
			var position = e.GetPosition(TimelineContentArea);
			try
			{
				await viewModel.CompletePointerSeekAsync(PreciseX(position.X), TimelineContentArea.ActualWidth);
			}
			catch (OperationCanceledException)
			{
				// Endpoint loss or lifecycle shutdown can cancel an in-flight pointer seek.
			}
		}
		e.Handled = true;
	}

	private void TimelineWorkspace_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (ViewModel is not { IsLoaded: true } viewModel)
			return;

		if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
		{
			var command = e.Delta > 0 ? viewModel.ZoomInCommand : viewModel.ZoomOutCommand;
			if (command.CanExecute(null))
				command.Execute(null);
			e.Handled = true;
			return;
		}

		if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && viewModel.ScrollMaximum > 0)
		{
			var delta = Math.Max(1, viewModel.VisibleFrameCount / 10);
			viewModel.ScrollValue += e.Delta > 0 ? -delta : delta;
			e.Handled = true;
		}
	}

	private void TrimHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string tag } ||
			tag is not ("IN" or "OUT") ||
			ViewModel is not { CanSeek: true })
		{
			return;
		}

		_pointerSeeking = false;
		Focus();
		_trimKind = tag;
		_dragOriginX = e.GetPosition(TimelineContentArea).X; _gestureContext = ViewModel.EditingContextVersion;
		ViewModel.BeginTrimPreview(tag);
		TimelineWorkspace.CaptureMouse();
		e.Handled = true;
	}

	private void TimelineItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: TimelineTrackItemViewModel item } &&
			ViewModel is { } viewModel)
		{
			var modifiers = Keyboard.Modifiers;
			var toggle = (modifiers & ModifierKeys.Control) != 0;
			var extend = toggle || (modifiers & ModifierKeys.Shift) != 0;
			viewModel.SelectItem(item, extendSelection: extend, toggleSelection: toggle, rangeSelection: !toggle && modifiers.HasFlag(ModifierKeys.Shift));
			e.Handled = true;
		}
	}

	private void Cue_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: TimelineCueViewModel cue } ||
			ViewModel is not { } viewModel)
		{
			return;
		}

		viewModel.SelectCue(cue);
		if (e.ClickCount >= 2 && viewModel.JumpSelectedCueCommand.CanExecute(null))
			viewModel.JumpSelectedCueCommand.Execute(null);
		e.Handled = true;
	}

	private void Timeline_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null ||
			FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) is not null)
		{
			return;
		}

		if (ViewModel is not { } viewModel)
			return;

		if (e.Key == Key.Escape && OwnsEscape)
		{
			_trimKind = null;
			_pointerSeeking = false;
			viewModel.CancelTrimPreview();
			viewModel.CancelPointerEdit();
			TimelineWorkspace.ReleaseMouseCapture();
			e.Handled = true;
			return;
		}

		if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y or Key.A)
		{
			var local = e.Key == Key.Z ? viewModel.UndoDraftCommand : e.Key == Key.Y ? viewModel.RedoDraftCommand : viewModel.SelectAllItemsCommand;
			if (!e.IsRepeat && local.CanExecute(null)) local.Execute(null);
			e.Handled = true; return;
		}
		if (viewModel.EditCursorMode && viewModel.IsLoaded && e.Key is Key.Left or Key.Right or Key.Home or Key.End)
		{
			if (e.Key is Key.Home or Key.End) viewModel.SetEditCursor(e.Key == Key.Home ? 0 : viewModel.TotalFrames - 1);
			else viewModel.StepEditCursor((e.Key == Key.Left ? -1 : 1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1));
			e.Handled = true; return;
		}
		ICommand? command = e.Key switch
		{
			Key.Left => viewModel.StepBackwardCommand,
			Key.Right => viewModel.StepForwardCommand,
			Key.Home => viewModel.JumpToStartCommand,
			Key.End => viewModel.JumpToEndCommand,
			Key.PageUp => viewModel.PreviousCueCommand,
			Key.PageDown => viewModel.NextCueCommand,
			Key.Add or Key.OemPlus when (Keyboard.Modifiers & ModifierKeys.Control) != 0 => viewModel.ZoomInCommand,
			Key.Subtract or Key.OemMinus when (Keyboard.Modifiers & ModifierKeys.Control) != 0 => viewModel.ZoomOutCommand,
			Key.F when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) => viewModel.FitCommand,
			_ => null
		};
		if (command is null || !command.CanExecute(null))
			return;

		command.Execute(null);
		e.Handled = true;
	}

	private void TimelineWorkspace_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (_trimKind is null && !_pointerSeeking)
			return;

		ViewModel?.CancelPointerEdit();
		_trimKind = null;
		_pointerSeeking = false;
		ViewModel?.CancelTrimPreview();
	}

	private static FrameworkElement? FindTaggedTrimHandle(DependencyObject? current)
	{
		while (current is not null)
		{
			if (current is FrameworkElement { Tag: string tag } element && tag is "IN" or "OUT")
				return element;
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}

	private static T? FindAncestor<T>(DependencyObject? current)
		where T : DependencyObject
	{
		while (current is not null)
		{
			if (current is T match)
				return match;
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}

	private static T? FindDataContext<T>(DependencyObject? current)
		where T : class
	{
		while (current is not null)
		{
			if (current is FrameworkElement { DataContext: T context })
				return context;
			current = VisualTreeHelper.GetParent(current);
		}
		return null;
	}
}
