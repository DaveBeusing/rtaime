// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace rtaime.Operator.Controls;

[TemplatePart(Name = MediaViewportPartName, Type = typeof(FrameworkElement))]
public sealed class RtaimeMonitorPresentation : ContentControl
{
	private const string MediaViewportPartName = "PART_MediaViewport";
	private FrameworkElement? _mediaViewport;
	private Point? _panOrigin;
	private readonly DispatcherTimer _resizeSettleTimer = new(DispatcherPriority.Render)
	{
		Interval = TimeSpan.FromMilliseconds(80)
	};
	public static readonly DependencyProperty MonitorProperty = DependencyProperty.Register(
		nameof(Monitor),
		typeof(MonitorView),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null, OnMonitorChanged));

	public static readonly DependencyProperty RoleTextProperty = DependencyProperty.Register(
		nameof(RoleText),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty RoleBrushProperty = DependencyProperty.Register(
		nameof(RoleBrush),
		typeof(Brush),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public static readonly DependencyProperty RoleStatusProperty = DependencyProperty.Register(
		nameof(RoleStatus),
		typeof(RtaimeStatusKind),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(RtaimeStatusKind.Neutral));

	public static readonly DependencyProperty PlaceholderStateProperty = DependencyProperty.Register(
		nameof(PlaceholderState),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty PlaceholderTitleProperty = DependencyProperty.Register(
		nameof(PlaceholderTitle),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty PlaceholderDetailProperty = DependencyProperty.Register(
		nameof(PlaceholderDetail),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty FrameUnavailableTextProperty = DependencyProperty.Register(
		nameof(FrameUnavailableText),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata("Frame unavailable"));

	public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
		nameof(HeaderContent),
		typeof(object),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
		nameof(StatusContent),
		typeof(object),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public static readonly DependencyProperty OverlayControlsEnabledProperty = DependencyProperty.Register(
		nameof(OverlayControlsEnabled),
		typeof(bool),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(true));

	public MonitorView? Monitor
	{
		get => (MonitorView?)GetValue(MonitorProperty);
		set => SetValue(MonitorProperty, value);
	}

	public string RoleText
	{
		get => (string)GetValue(RoleTextProperty);
		set => SetValue(RoleTextProperty, value);
	}

	public Brush? RoleBrush
	{
		get => (Brush?)GetValue(RoleBrushProperty);
		set => SetValue(RoleBrushProperty, value);
	}

	public RtaimeStatusKind RoleStatus
	{
		get => (RtaimeStatusKind)GetValue(RoleStatusProperty);
		set => SetValue(RoleStatusProperty, value);
	}

	public string PlaceholderState
	{
		get => (string)GetValue(PlaceholderStateProperty);
		set => SetValue(PlaceholderStateProperty, value);
	}

	public string PlaceholderTitle
	{
		get => (string)GetValue(PlaceholderTitleProperty);
		set => SetValue(PlaceholderTitleProperty, value);
	}

	public string PlaceholderDetail
	{
		get => (string)GetValue(PlaceholderDetailProperty);
		set => SetValue(PlaceholderDetailProperty, value);
	}

	public string FrameUnavailableText
	{
		get => (string)GetValue(FrameUnavailableTextProperty);
		set => SetValue(FrameUnavailableTextProperty, value);
	}

	public object? HeaderContent
	{
		get => GetValue(HeaderContentProperty);
		set => SetValue(HeaderContentProperty, value);
	}

	public object? StatusContent
	{
		get => GetValue(StatusContentProperty);
		set => SetValue(StatusContentProperty, value);
	}

	public bool OverlayControlsEnabled
	{
		get => (bool)GetValue(OverlayControlsEnabledProperty);
		set => SetValue(OverlayControlsEnabledProperty, value);
	}
	public override void OnApplyTemplate()
	{
		_resizeSettleTimer.Stop();
		_resizeSettleTimer.Tick -= OnResizeSettleTick;
		_resizeSettleTimer.Tick += OnResizeSettleTick;
		DetachViewport();
		base.OnApplyTemplate();

		_mediaViewport = GetTemplateChild(MediaViewportPartName) as FrameworkElement;
		if (_mediaViewport is null)
			return;

		_mediaViewport.SizeChanged += OnViewportSizeChanged;
		_mediaViewport.MouseWheel += OnViewportMouseWheel;
		_mediaViewport.PreviewKeyDown += OnViewportPreviewKeyDown;
		_mediaViewport.Focusable = true;
		_mediaViewport.MouseLeftButtonDown += OnViewportMouseLeftButtonDown;
		_mediaViewport.MouseLeftButtonUp += OnViewportMouseLeftButtonUp;
		_mediaViewport.MouseMove += OnViewportMouseMove;
		RefreshViewport();
	}

	private static void OnMonitorChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		((RtaimeMonitorPresentation)dependencyObject).RefreshViewport();
	}

	private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
	{
		RefreshViewport();
		_resizeSettleTimer.Stop();
		_resizeSettleTimer.Start();
	}

	private void OnResizeSettleTick(object? sender, EventArgs e)
	{
		_resizeSettleTimer.Stop();
		RefreshViewport();
	}

	private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (Monitor is null || _mediaViewport is null)
			return;

		var anchor = e.GetPosition(_mediaViewport);
		Monitor.ZoomPresentationAt(e.Delta, anchor.X, anchor.Y);
		e.Handled = true;
	}

	private void OnViewportPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (Monitor is null || Keyboard.Modifiers != ModifierKeys.None)
			return;

		if (e.Key == Key.D1 || e.Key == Key.NumPad1)
		{
			Monitor.ZoomMode = "100%";
			e.Handled = true;
		}
		else if (e.Key == Key.F)
		{
			Monitor.ZoomMode = "FIT";
			e.Handled = true;
		}
	}

	private void OnViewportMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (Monitor is null)
			return;

		_mediaViewport?.Focus();
		if (e.ClickCount == 2)
		{
			Monitor.ToggleFitPixelPerfect();
			e.Handled = true;
			return;
		}

		if (Monitor.ZoomMode is "FIT" or "FILL")
			return;

		_panOrigin = e.GetPosition(_mediaViewport);
		_mediaViewport?.CaptureMouse();
		e.Handled = true;
	}

	private void OnViewportMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (_panOrigin is null)
			return;

		_panOrigin = null;
		_mediaViewport?.ReleaseMouseCapture();
		e.Handled = true;
	}

	private void OnViewportMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (Monitor is null || _mediaViewport is null)
			return;

		var current = e.GetPosition(_mediaViewport);
		Monitor.InspectPresentationAt(current.X, current.Y);
		if (_panOrigin is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
			return;

		var delta = current - _panOrigin.Value;
		_panOrigin = current;
		Monitor.PanPresentation(delta.X, delta.Y);
		e.Handled = true;
	}

	private void RefreshViewport()
	{
		if (_mediaViewport is null || Monitor is null || _mediaViewport.ActualWidth <= 0 || _mediaViewport.ActualHeight <= 0)
			return;

		var dpi = VisualTreeHelper.GetDpi(_mediaViewport);
		Monitor.UpdatePresentationViewport(_mediaViewport.ActualWidth, _mediaViewport.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
	}

	private void DetachViewport()
	{
		if (_mediaViewport is null)
			return;

		_mediaViewport.SizeChanged -= OnViewportSizeChanged;
		_mediaViewport.MouseWheel -= OnViewportMouseWheel;
		_mediaViewport.PreviewKeyDown -= OnViewportPreviewKeyDown;
		_mediaViewport.MouseLeftButtonDown -= OnViewportMouseLeftButtonDown;
		_mediaViewport.MouseLeftButtonUp -= OnViewportMouseLeftButtonUp;
		_mediaViewport.MouseMove -= OnViewportMouseMove;
		_mediaViewport = null;
		_panOrigin = null;
		_resizeSettleTimer.Stop();
	}


}
