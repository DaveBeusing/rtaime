// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace rtaime.Operator;

public class MonitorView : UserControl
{
	private double _viewportWidthDip;
	private double _viewportHeightDip;
	private double _dpiScaleX = 1.0;
	private double _dpiScaleY = 1.0;
	private double _panXPhysical;
	private double _panYPhysical;

	private static readonly DependencyPropertyKey ImageStretchPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(ImageStretch),
		typeof(Stretch),
		typeof(MonitorView),
		new PropertyMetadata(Stretch.Uniform));

	private static readonly DependencyPropertyKey ImageScalePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(ImageScale),
		typeof(double),
		typeof(MonitorView),
		new PropertyMetadata(1.0));

	private static readonly DependencyPropertyKey PresentationWidthPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(PresentationWidth),
		typeof(double),
		typeof(MonitorView),
		new PropertyMetadata(0.0));

	private static readonly DependencyPropertyKey PresentationHeightPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(PresentationHeight),
		typeof(double),
		typeof(MonitorView),
		new PropertyMetadata(0.0));

	private static readonly DependencyPropertyKey PresentationOffsetXPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(PresentationOffsetX),
		typeof(double),
		typeof(MonitorView),
		new PropertyMetadata(0.0));

	private static readonly DependencyPropertyKey PresentationOffsetYPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(PresentationOffsetY),
		typeof(double),
		typeof(MonitorView),
		new PropertyMetadata(0.0));

	private static readonly DependencyPropertyKey PresentationInfoPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(PresentationInfo),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("View unavailable"));

	private static readonly DependencyPropertyKey IsTransportSourcePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(IsTransportSource),
		typeof(bool),
		typeof(MonitorView),
		new PropertyMetadata(false));

	private static readonly DependencyPropertyKey DisplayTimecodePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(DisplayTimecode),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("—"));

	public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
		nameof(Frame),
		typeof(ImageSource),
		typeof(MonitorView),
		new PropertyMetadata(null, OnFrameChanged));

	public static readonly DependencyProperty SourceNameProperty = DependencyProperty.Register(
		nameof(SourceName),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("—"));

	public static readonly DependencyProperty SourceIdProperty = DependencyProperty.Register(
		nameof(SourceId),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("—", OnTransportContextChanged));

	public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
		nameof(Format),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("Monitoring format unavailable."));

	public static readonly DependencyProperty ProductionFormatProperty = DependencyProperty.Register(
		nameof(ProductionFormat),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("UNVERIFIED"));

	public static readonly DependencyProperty ColorSpaceProperty = DependencyProperty.Register(
		nameof(ColorSpace),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("N/A"));

	public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
		nameof(State),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("DISCONNECTED"));

	public static readonly DependencyProperty TimecodeProperty = DependencyProperty.Register(
		nameof(Timecode),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("—", OnTransportContextChanged));

	public static readonly DependencyProperty TransportSourceIdProperty = DependencyProperty.Register(
		nameof(TransportSourceId),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("—", OnTransportContextChanged));

	public static readonly DependencyProperty MaximizeCommandProperty = DependencyProperty.Register(
		nameof(MaximizeCommand),
		typeof(ICommand),
		typeof(MonitorView),
		new PropertyMetadata(null));

	public static readonly DependencyProperty FullscreenCommandProperty = DependencyProperty.Register(
		nameof(FullscreenCommand),
		typeof(ICommand),
		typeof(MonitorView),
		new PropertyMetadata(null));

	public static readonly DependencyProperty ShowSafeAreaProperty = DependencyProperty.Register(
		nameof(ShowSafeArea),
		typeof(bool),
		typeof(MonitorView),
		new PropertyMetadata(false));

	public static readonly DependencyProperty ShowCenterMarkProperty = DependencyProperty.Register(
		nameof(ShowCenterMark),
		typeof(bool),
		typeof(MonitorView),
		new PropertyMetadata(false));

	public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
		nameof(ShowGrid),
		typeof(bool),
		typeof(MonitorView),
		new PropertyMetadata(false));

	public static readonly DependencyProperty ZoomModeProperty = DependencyProperty.Register(
		nameof(ZoomMode),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("FIT", OnZoomModeChanged));

	public static readonly DependencyProperty ImageStretchProperty = ImageStretchPropertyKey.DependencyProperty;
	public static readonly DependencyProperty ImageScaleProperty = ImageScalePropertyKey.DependencyProperty;
	public static readonly DependencyProperty PresentationWidthProperty = PresentationWidthPropertyKey.DependencyProperty;
	public static readonly DependencyProperty PresentationHeightProperty = PresentationHeightPropertyKey.DependencyProperty;
	public static readonly DependencyProperty PresentationOffsetXProperty = PresentationOffsetXPropertyKey.DependencyProperty;
	public static readonly DependencyProperty PresentationOffsetYProperty = PresentationOffsetYPropertyKey.DependencyProperty;
	public static readonly DependencyProperty PresentationInfoProperty = PresentationInfoPropertyKey.DependencyProperty;
	public static readonly DependencyProperty IsTransportSourceProperty = IsTransportSourcePropertyKey.DependencyProperty;
	public static readonly DependencyProperty DisplayTimecodeProperty = DisplayTimecodePropertyKey.DependencyProperty;

	public MonitorView()
	{
		FitCommand = new MonitorPresentationCommand(() => ZoomMode = "FIT");
		FillCommand = new MonitorPresentationCommand(() => ZoomMode = "FILL");
		Zoom25Command = new MonitorPresentationCommand(() => ZoomMode = "25%");
		Zoom50Command = new MonitorPresentationCommand(() => ZoomMode = "50%");
		Zoom100Command = new MonitorPresentationCommand(() => ZoomMode = "100%");
		Zoom200Command = new MonitorPresentationCommand(() => ZoomMode = "200%");
		Zoom400Command = new MonitorPresentationCommand(() => ZoomMode = "400%");
	}

	public ImageSource? Frame
	{
		get => (ImageSource?)GetValue(FrameProperty);
		set => SetValue(FrameProperty, value);
	}

	public string SourceName
	{
		get => (string)GetValue(SourceNameProperty);
		set => SetValue(SourceNameProperty, value);
	}

	public string SourceId
	{
		get => (string)GetValue(SourceIdProperty);
		set => SetValue(SourceIdProperty, value);
	}

	public string Format
	{
		get => (string)GetValue(FormatProperty);
		set => SetValue(FormatProperty, value);
	}

	public string ProductionFormat
	{
		get => (string)GetValue(ProductionFormatProperty);
		set => SetValue(ProductionFormatProperty, value);
	}

	public string ColorSpace
	{
		get => (string)GetValue(ColorSpaceProperty);
		set => SetValue(ColorSpaceProperty, value);
	}

	public string State
	{
		get => (string)GetValue(StateProperty);
		set => SetValue(StateProperty, value);
	}

	public string Timecode
	{
		get => (string)GetValue(TimecodeProperty);
		set => SetValue(TimecodeProperty, value);
	}

	public string TransportSourceId
	{
		get => (string)GetValue(TransportSourceIdProperty);
		set => SetValue(TransportSourceIdProperty, value);
	}

	public ICommand? MaximizeCommand
	{
		get => (ICommand?)GetValue(MaximizeCommandProperty);
		set => SetValue(MaximizeCommandProperty, value);
	}

	public ICommand? FullscreenCommand
	{
		get => (ICommand?)GetValue(FullscreenCommandProperty);
		set => SetValue(FullscreenCommandProperty, value);
	}

	public bool ShowSafeArea
	{
		get => (bool)GetValue(ShowSafeAreaProperty);
		set => SetValue(ShowSafeAreaProperty, value);
	}

	public bool ShowCenterMark
	{
		get => (bool)GetValue(ShowCenterMarkProperty);
		set => SetValue(ShowCenterMarkProperty, value);
	}

	public bool ShowGrid
	{
		get => (bool)GetValue(ShowGridProperty);
		set => SetValue(ShowGridProperty, value);
	}

	public string ZoomMode
	{
		get => (string)GetValue(ZoomModeProperty);
		set => SetValue(ZoomModeProperty, NormalizeZoomMode(value));
	}

	public Stretch ImageStretch => (Stretch)GetValue(ImageStretchProperty);
	public double ImageScale => (double)GetValue(ImageScaleProperty);
	public double PresentationWidth => (double)GetValue(PresentationWidthProperty);
	public double PresentationHeight => (double)GetValue(PresentationHeightProperty);
	public double PresentationOffsetX => (double)GetValue(PresentationOffsetXProperty);
	public double PresentationOffsetY => (double)GetValue(PresentationOffsetYProperty);
	public string PresentationInfo => (string)GetValue(PresentationInfoProperty);
	public bool IsTransportSource => (bool)GetValue(IsTransportSourceProperty);
	public string DisplayTimecode => (string)GetValue(DisplayTimecodeProperty);

	public ICommand FitCommand { get; }
	public ICommand FillCommand { get; }
	public ICommand Zoom25Command { get; }
	public ICommand Zoom50Command { get; }
	public ICommand Zoom100Command { get; }
	public ICommand Zoom200Command { get; }
	public ICommand Zoom400Command { get; }

	private static void OnZoomModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		var mode = NormalizeZoomMode(eventArgs.NewValue as string);
		if (!string.Equals(mode, eventArgs.NewValue as string, StringComparison.Ordinal))
		{
			view.SetCurrentValue(ZoomModeProperty, mode);
			return;
		}

		view.SetValue(ImageStretchPropertyKey, mode switch
		{
			"FIT" => Stretch.Uniform,
			"FILL" => Stretch.UniformToFill,
			_ => Stretch.None
		});
		view.SetValue(ImageScalePropertyKey, mode switch
		{
			"25%" => 0.25,
			"50%" => 0.5,
			"200%" => 2.0,
			"400%" => 4.0,
			_ => 1.0
		});
		if (mode is "FIT" or "FILL")
		{
			view._panXPhysical = 0;
			view._panYPhysical = 0;
		}
		view.RefreshPresentation();
	}

	private static void OnFrameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		view._panXPhysical = 0;
		view._panYPhysical = 0;
		view.RefreshPresentation();
	}

	internal void UpdatePresentationViewport(double widthDip, double heightDip, double dpiScaleX, double dpiScaleY)
	{
		if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) || widthDip <= 0 || heightDip <= 0)
			return;
		if (!double.IsFinite(dpiScaleX) || !double.IsFinite(dpiScaleY) || dpiScaleX <= 0 || dpiScaleY <= 0)
			return;

		_viewportWidthDip = widthDip;
		_viewportHeightDip = heightDip;
		_dpiScaleX = dpiScaleX;
		_dpiScaleY = dpiScaleY;
		RefreshPresentation();
	}

	internal void PanPresentation(double deltaXDip, double deltaYDip)
	{
		if (ZoomMode is "FIT" or "FILL")
			return;

		_panXPhysical += deltaXDip * _dpiScaleX;
		_panYPhysical += deltaYDip * _dpiScaleY;
		RefreshPresentation();
	}

	internal void StepPresentationZoom(int direction)
	{
		var modes = new[] { "25%", "50%", "100%", "200%", "400%" };
		var current = Array.IndexOf(modes, ZoomMode);
		if (current < 0)
			current = direction > 0 ? 1 : 3;
		ZoomMode = modes[Math.Clamp(current + Math.Sign(direction), 0, modes.Length - 1)];
	}

	private void RefreshPresentation()
	{
		if (Frame is null || _viewportWidthDip <= 0 || _viewportHeightDip <= 0)
			return;

		var (sourceWidth, sourceHeight) = ResolveSourcePixels(Frame);
		if (sourceWidth <= 0 || sourceHeight <= 0)
			return;

		var physical = MediaPresentationGeometry.ToPhysicalPixels(
			_viewportWidthDip,
			_viewportHeightDip,
			_dpiScaleX,
			_dpiScaleY);
		var (mode, zoom) = ResolvePresentationPolicy(ZoomMode);
		var result = MediaPresentationGeometry.Calculate(
			sourceWidth,
			sourceHeight,
			physical.Width,
			physical.Height,
			mode,
			zoom,
			_panXPhysical,
			_panYPhysical);

		var centeredX = (physical.Width - result.Width) / 2.0;
		var centeredY = (physical.Height - result.Height) / 2.0;
		_panXPhysical = result.X - centeredX;
		_panYPhysical = result.Y - centeredY;

		SetValue(PresentationWidthPropertyKey, result.Width / _dpiScaleX);
		SetValue(PresentationHeightPropertyKey, result.Height / _dpiScaleY);
		SetValue(PresentationOffsetXPropertyKey, _panXPhysical / _dpiScaleX);
		SetValue(PresentationOffsetYPropertyKey, _panYPhysical / _dpiScaleY);
		SetValue(
			PresentationInfoPropertyKey,
			$"{sourceWidth:0}×{sourceHeight:0} | {ZoomMode} {result.Scale * 100:0.#}% | View {physical.Width:0}×{physical.Height:0} | DPI {_dpiScaleX * 100:0}%");
	}

	private static (double Width, double Height) ResolveSourcePixels(ImageSource source)
	{
		if (source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
			return (bitmap.PixelWidth, bitmap.PixelHeight);

		return (Math.Max(1, source.Width), Math.Max(1, source.Height));
	}

	private static (MediaPresentationMode Mode, double Zoom) ResolvePresentationPolicy(string zoomMode) => zoomMode switch
	{
		"FILL" => (MediaPresentationMode.Fill, 1.0),
		"25%" => (MediaPresentationMode.CustomZoom, 0.25),
		"50%" => (MediaPresentationMode.CustomZoom, 0.5),
		"100%" => (MediaPresentationMode.PixelPerfect, 1.0),
		"200%" => (MediaPresentationMode.CustomZoom, 2.0),
		"400%" => (MediaPresentationMode.CustomZoom, 4.0),
		_ => (MediaPresentationMode.Fit, 1.0)
	};

	private static void OnTransportContextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		var sourceId = view.SourceId?.Trim();
		var transportSourceId = view.TransportSourceId?.Trim();
		var matches = !string.IsNullOrWhiteSpace(sourceId) &&
			!string.Equals(sourceId, "—", StringComparison.Ordinal) &&
			string.Equals(sourceId, transportSourceId, StringComparison.Ordinal);

		view.SetValue(IsTransportSourcePropertyKey, matches);
		view.SetValue(
			DisplayTimecodePropertyKey,
			matches && !string.IsNullOrWhiteSpace(view.Timecode)
				? view.Timecode
				: "—");
	}

	private static string NormalizeZoomMode(string? value) => value?.Trim().ToUpperInvariant() switch
	{
		"FILL" => "FILL",
		"25%" => "25%",
		"50%" => "50%",
		"100%" => "100%",
		"200%" => "200%",
		"400%" => "400%",
		_ => "FIT"
	};

	private sealed class MonitorPresentationCommand : ICommand
	{
		private readonly Action _execute;

		public MonitorPresentationCommand(Action execute)
		{
			_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		}

#pragma warning disable CS0067
		public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

		public bool CanExecute(object? parameter) => true;
		public void Execute(object? parameter) => _execute();
	}
}
