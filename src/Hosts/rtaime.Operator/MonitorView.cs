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
	private double _freeZoom = 1.0;
	private readonly MediaRenderTargetStabilizer _renderTargetStabilizer = new();
	private MediaPresentationRect _presentationRect;
	private DateTimeOffset _lastInspectionAt;
	private DateTimeOffset _lastDiagnosticsHudUpdateAt;

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

	private static readonly DependencyPropertyKey SourcePixelWidthPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SourcePixelWidth), typeof(int), typeof(MonitorView), new PropertyMetadata(0));
	private static readonly DependencyPropertyKey SourcePixelHeightPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SourcePixelHeight), typeof(int), typeof(MonitorView), new PropertyMetadata(0));
	public static readonly DependencyProperty SourcePixelWidthProperty = SourcePixelWidthPropertyKey.DependencyProperty;
	public static readonly DependencyProperty SourcePixelHeightProperty = SourcePixelHeightPropertyKey.DependencyProperty;

	private static readonly DependencyPropertyKey InspectionReadoutPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(InspectionReadout), typeof(string), typeof(MonitorView), new PropertyMetadata("PIXEL —"));
	private static readonly DependencyPropertyKey DiagnosticsHudTextPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(DiagnosticsHudText), typeof(string), typeof(MonitorView), new PropertyMetadata("FRAME DIAGNOSTICS — UNAVAILABLE"));
	private static readonly DependencyPropertyKey IsPixelGridVisiblePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(IsPixelGridVisible), typeof(bool), typeof(MonitorView), new PropertyMetadata(false));

	public static readonly DependencyProperty InspectionReadoutProperty = InspectionReadoutPropertyKey.DependencyProperty;
	public static readonly DependencyProperty IsPixelGridVisibleProperty = IsPixelGridVisiblePropertyKey.DependencyProperty;
	public static readonly DependencyProperty DiagnosticsHudTextProperty = DiagnosticsHudTextPropertyKey.DependencyProperty;

	public static readonly DependencyProperty DiagnosticsProperty = DependencyProperty.Register(
		nameof(Diagnostics), typeof(FrameDiagnosticsSnapshot), typeof(MonitorView),
		new PropertyMetadata(FrameDiagnosticsSnapshot.Unavailable, OnDiagnosticsChanged));

	public static readonly DependencyProperty ShowDiagnosticsHudProperty = DependencyProperty.Register(
		nameof(ShowDiagnosticsHud), typeof(bool), typeof(MonitorView), new PropertyMetadata(false, OnDiagnosticsChanged));

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

	public static readonly DependencyProperty ShowTechnicalOverlayProperty = DependencyProperty.Register(
		nameof(ShowTechnicalOverlay),
		typeof(bool),
		typeof(MonitorView),
		new PropertyMetadata(false));

	public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
		nameof(ShowGrid),
		typeof(bool),
		typeof(MonitorView),
		new PropertyMetadata(false, OnGridChanged));

	public static readonly DependencyProperty InspectionChannelProperty = DependencyProperty.Register(
		nameof(InspectionChannel), typeof(MediaInspectionChannel), typeof(MonitorView),
		new PropertyMetadata(MediaInspectionChannel.Rgb));

	public static readonly DependencyProperty ZoomModeProperty = DependencyProperty.Register(
		nameof(ZoomMode),
		typeof(string),
		typeof(MonitorView),
		new PropertyMetadata("FIT", OnZoomModeChanged));

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
		Zoom800Command = new MonitorPresentationCommand(() => ZoomMode = "800%");
		ToggleFitPixelPerfectCommand = new MonitorPresentationCommand(ToggleFitPixelPerfect);
		ToggleTechnicalOverlayCommand = new MonitorPresentationCommand(() => ShowTechnicalOverlay = !ShowTechnicalOverlay);
		ToggleDiagnosticsHudCommand = new MonitorPresentationCommand(() => ShowDiagnosticsHud = !ShowDiagnosticsHud);
		ShowRgbCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Rgb);
		ShowRedCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Red);
		ShowGreenCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Green);
		ShowBlueCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Blue);
		ShowAlphaCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Alpha);
		ShowLumaCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Luma);
	}

	public FrameDiagnosticsSnapshot Diagnostics
	{
		get => (FrameDiagnosticsSnapshot)GetValue(DiagnosticsProperty);
		set => SetValue(DiagnosticsProperty, value ?? FrameDiagnosticsSnapshot.Unavailable);
	}

	public bool ShowDiagnosticsHud
	{
		get => (bool)GetValue(ShowDiagnosticsHudProperty);
		set => SetValue(ShowDiagnosticsHudProperty, value);
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

	public bool ShowTechnicalOverlay
	{
		get => (bool)GetValue(ShowTechnicalOverlayProperty);
		set => SetValue(ShowTechnicalOverlayProperty, value);
	}

	public bool ShowGrid
	{
		get => (bool)GetValue(ShowGridProperty);
		set => SetValue(ShowGridProperty, value);
	}

	public MediaInspectionChannel InspectionChannel
	{
		get => (MediaInspectionChannel)GetValue(InspectionChannelProperty);
		set => SetValue(InspectionChannelProperty, value);
	}

	public string ZoomMode
	{
		get => (string)GetValue(ZoomModeProperty);
		set => SetValue(ZoomModeProperty, NormalizeZoomMode(value));
	}

	public double PresentationWidth => (double)GetValue(PresentationWidthProperty);
	public double PresentationHeight => (double)GetValue(PresentationHeightProperty);
	public double PresentationOffsetX => (double)GetValue(PresentationOffsetXProperty);
	public double PresentationOffsetY => (double)GetValue(PresentationOffsetYProperty);
	public string PresentationInfo => (string)GetValue(PresentationInfoProperty);
	public int SourcePixelWidth => (int)GetValue(SourcePixelWidthProperty);
	public int SourcePixelHeight => (int)GetValue(SourcePixelHeightProperty);
	public string InspectionReadout => (string)GetValue(InspectionReadoutProperty);
	public bool IsPixelGridVisible => (bool)GetValue(IsPixelGridVisibleProperty);
	public string DiagnosticsHudText => (string)GetValue(DiagnosticsHudTextProperty);
	public bool IsTransportSource => (bool)GetValue(IsTransportSourceProperty);
	public string DisplayTimecode => (string)GetValue(DisplayTimecodeProperty);

	public ICommand FitCommand { get; }
	public ICommand FillCommand { get; }
	public ICommand Zoom25Command { get; }
	public ICommand Zoom50Command { get; }
	public ICommand Zoom100Command { get; }
	public ICommand Zoom200Command { get; }
	public ICommand Zoom400Command { get; }
	public ICommand Zoom800Command { get; }
	public ICommand ToggleFitPixelPerfectCommand { get; }
	public ICommand ToggleTechnicalOverlayCommand { get; }
	public ICommand ToggleDiagnosticsHudCommand { get; }
	public ICommand ShowRgbCommand { get; }
	public ICommand ShowRedCommand { get; }
	public ICommand ShowGreenCommand { get; }
	public ICommand ShowBlueCommand { get; }
	public ICommand ShowAlphaCommand { get; }
	public ICommand ShowLumaCommand { get; }

	private static void OnDiagnosticsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		if (!view.ShowDiagnosticsHud)
			return;

		var now = DateTimeOffset.UtcNow;
		if (now - view._lastDiagnosticsHudUpdateAt < TimeSpan.FromMilliseconds(200))
			return;
		view._lastDiagnosticsHudUpdateAt = now;
		view.SetValue(DiagnosticsHudTextPropertyKey, view.FormatDiagnosticsHud());
	}

	private string FormatDiagnosticsHud()
	{
		var diagnostics = Diagnostics ?? FrameDiagnosticsSnapshot.Unavailable;
		var frame = diagnostics.FrameIndex is { } index ? index.ToString() : "UNAVAILABLE";
		var pts = diagnostics.PresentationTimestamp is { } timestamp ? timestamp.ToString() : "UNAVAILABLE";
		var source = diagnostics.SourceWidth > 0 && diagnostics.SourceHeight > 0
			? $"{diagnostics.SourceWidth}×{diagnostics.SourceHeight}"
			: "UNAVAILABLE";
		var target = _viewportWidthDip > 0 && _viewportHeightDip > 0
			? $"{Math.Max(1, (int)Math.Round(_viewportWidthDip * _dpiScaleX))}×{Math.Max(1, (int)Math.Round(_viewportHeightDip * _dpiScaleY))}"
			: "UNAVAILABLE";
		var scale = _presentationRect.Scale > 0 ? $"{_presentationRect.Scale * 100:0.#}%" : "UNAVAILABLE";
		return $"FRAME {frame}  PTS {pts}  MEDIA {diagnostics.PresentationTime}  RATE {diagnostics.Rate}\n" +
			$"SOURCE {source}  TARGET {target}  SCALE {scale}  MODE {ZoomMode}\n" +
			$"COLOR {diagnostics.ColorPath}  TIMING {diagnostics.TimingAuthority.ToString().ToUpperInvariant()}  DISPLAY WPF HIGH QUALITY";
	}

	private static void OnGridChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		((MonitorView)dependencyObject).RefreshPresentation();
	}

	private static void OnZoomModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		var mode = NormalizeZoomMode(eventArgs.NewValue as string);
		if (!string.Equals(mode, eventArgs.NewValue as string, StringComparison.Ordinal))
		{
			view.SetCurrentValue(ZoomModeProperty, mode);
			return;
		}

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

	internal void StepPresentationZoom(int direction) => ZoomPresentationAt(direction, _viewportWidthDip / 2.0, _viewportHeightDip / 2.0);

	internal void ZoomPresentationAt(int direction, double anchorXDip, double anchorYDip)
	{
		if (Frame is null || direction == 0 || _viewportWidthDip <= 0 || _viewportHeightDip <= 0)
			return;

		var (sourceWidth, sourceHeight) = ResolveSourcePixels(Frame);
		var physical = MediaPresentationGeometry.ToPhysicalPixels(_viewportWidthDip, _viewportHeightDip, _dpiScaleX, _dpiScaleY);
		var (currentMode, currentScale) = ResolvePresentationPolicy(ZoomMode);
		if (currentMode == MediaPresentationMode.Fit)
			currentScale = Math.Min(physical.Width / sourceWidth, physical.Height / sourceHeight);
		else if (currentMode == MediaPresentationMode.Fill)
			currentScale = Math.Max(physical.Width / sourceWidth, physical.Height / sourceHeight);

		var nextScale = MediaInspectionPolicy.StepZoom(currentScale, direction);
		var anchor = MediaInspectionPolicy.AnchorZoom(
			sourceWidth, sourceHeight, physical.Width, physical.Height,
			currentScale, nextScale, _panXPhysical, _panYPhysical,
			anchorXDip * _dpiScaleX, anchorYDip * _dpiScaleY);
		_panXPhysical = anchor.PanX;
		_panYPhysical = anchor.PanY;
		_freeZoom = nextScale;
		SetCurrentValue(ZoomModeProperty, "FREE");
		RefreshPresentation();
	}

	internal void ToggleFitPixelPerfect() => ZoomMode = ZoomMode == "100%" ? "FIT" : "100%";

	internal void InspectPresentationAt(double pointerXDip, double pointerYDip)
	{
		if (Frame is not BitmapSource bitmap)
		{
			SetValue(InspectionReadoutPropertyKey, "PIXEL — · SAMPLING UNSUPPORTED");
			return;
		}

		var now = DateTimeOffset.UtcNow;
		if (now - _lastInspectionAt < TimeSpan.FromMilliseconds(33))
			return;
		_lastInspectionAt = now;

		if (!MediaPixelInspection.TryMapViewportToSource(
			pointerXDip, pointerYDip, _dpiScaleX, _dpiScaleY, _presentationRect,
			bitmap.PixelWidth, bitmap.PixelHeight, out var coordinate))
		{
			SetValue(InspectionReadoutPropertyKey, "PIXEL — · OUTSIDE IMAGE");
			return;
		}

		SetValue(InspectionReadoutPropertyKey, MediaPixelInspection.Format(MediaPixelInspection.SampleDisplayPixel(bitmap, coordinate)));
	}

	private void RefreshPresentation()
	{
		if (Frame is null || _viewportWidthDip <= 0 || _viewportHeightDip <= 0)
			return;

		var (sourceWidth, sourceHeight) = ResolveSourcePixels(Frame);
		SetValue(SourcePixelWidthPropertyKey, checked((int)sourceWidth));
		SetValue(SourcePixelHeightPropertyKey, checked((int)sourceHeight));
		if (sourceWidth <= 0 || sourceHeight <= 0)
			return;

		var requestedTarget = MediaRenderTarget.Create(
			_viewportWidthDip,
			_viewportHeightDip,
			_dpiScaleX,
			_dpiScaleY);
		var target = _renderTargetStabilizer.Adopt(requestedTarget, DateTimeOffset.UtcNow);
		var physical = (Width: (double)target.PixelWidth, Height: (double)target.PixelHeight);
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

		_presentationRect = result;
		SetValue(IsPixelGridVisiblePropertyKey, ShowGrid && result.Scale >= MediaPixelInspection.PixelGridMinimumScale);
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
			$"{sourceWidth:0}×{sourceHeight:0} | {ZoomMode} {result.Scale * 100:0.#}% | Target {target.PixelWidth}×{target.PixelHeight} px | {target.Quality} {target.Path} | Scale stages 1 | RT #{_renderTargetStabilizer.Revision}{(_renderTargetStabilizer.ResizePending ? " pending" : string.Empty)} | DPI {_dpiScaleX * 100:0}%");
	}

	private static (double Width, double Height) ResolveSourcePixels(ImageSource source)
	{
		if (source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
			return (bitmap.PixelWidth, bitmap.PixelHeight);

		var width = double.IsFinite(source.Width) && source.Width > 0 ? source.Width : 1;
		var height = double.IsFinite(source.Height) && source.Height > 0 ? source.Height : 1;
		return (width, height);
	}

	private (MediaPresentationMode Mode, double Zoom) ResolvePresentationPolicy(string zoomMode) => zoomMode switch
	{
		"FILL" => (MediaPresentationMode.Fill, 1.0),
		"25%" => (MediaPresentationMode.CustomZoom, 0.25),
		"50%" => (MediaPresentationMode.CustomZoom, 0.5),
		"100%" => (MediaPresentationMode.PixelPerfect, 1.0),
		"200%" => (MediaPresentationMode.CustomZoom, 2.0),
		"400%" => (MediaPresentationMode.CustomZoom, 4.0),
		"800%" => (MediaPresentationMode.CustomZoom, 8.0),
		"FREE" => (MediaPresentationMode.CustomZoom, _freeZoom),
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
		"800%" => "800%",
		"FREE" => "FREE",
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
