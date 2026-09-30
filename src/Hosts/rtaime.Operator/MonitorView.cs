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
	private MediaPixelCoordinate? _roiAnchor;
	private MediaInspectionRoi? _roiInteractionStartRoi;
	private RoiInteractionMode _roiInteractionMode;

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
	private static readonly DependencyPropertyKey RoiInspectionReadoutPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(RoiInspectionReadout), typeof(string), typeof(MonitorView), new PropertyMetadata("ROI —"));
	private static readonly DependencyPropertyKey IsRoiActivePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(IsRoiActive), typeof(bool), typeof(MonitorView), new PropertyMetadata(false));
	private static readonly DependencyPropertyKey IsChannelInspectionUnavailablePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(IsChannelInspectionUnavailable), typeof(bool), typeof(MonitorView), new PropertyMetadata(false));
	private static readonly DependencyPropertyKey ChannelInspectionStatusPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(ChannelInspectionStatus), typeof(string), typeof(MonitorView), new PropertyMetadata("COMBINED"));
	private static readonly DependencyPropertyKey DiagnosticsHudTextPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(DiagnosticsHudText), typeof(string), typeof(MonitorView), new PropertyMetadata("FRAME DIAGNOSTICS — UNAVAILABLE"));
	private static readonly DependencyPropertyKey IsPixelGridVisiblePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(IsPixelGridVisible), typeof(bool), typeof(MonitorView), new PropertyMetadata(false));
	private static readonly DependencyPropertyKey IsGpuPresentationActivePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(IsGpuPresentationActive), typeof(bool), typeof(MonitorView), new PropertyMetadata(false));
	private static readonly DependencyPropertyKey GpuPresentationDetailPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(GpuPresentationDetail), typeof(string), typeof(MonitorView), new PropertyMetadata("CPU/WPF fallback"));
	private static readonly DependencyPropertyKey ViewportPixelWidthPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(ViewportPixelWidth), typeof(double), typeof(MonitorView), new PropertyMetadata(1.0));
	private static readonly DependencyPropertyKey ViewportPixelHeightPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(ViewportPixelHeight), typeof(double), typeof(MonitorView), new PropertyMetadata(1.0));
	private static readonly DependencyPropertyKey DpiScaleXPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(DpiScaleX), typeof(double), typeof(MonitorView), new PropertyMetadata(1.0));
	private static readonly DependencyPropertyKey DpiScaleYPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(DpiScaleY), typeof(double), typeof(MonitorView), new PropertyMetadata(1.0));
	private static readonly DependencyPropertyKey InverseDpiScaleXPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(InverseDpiScaleX), typeof(double), typeof(MonitorView), new PropertyMetadata(1.0));
	private static readonly DependencyPropertyKey InverseDpiScaleYPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(InverseDpiScaleY), typeof(double), typeof(MonitorView), new PropertyMetadata(1.0));

	public static readonly DependencyProperty InspectionReadoutProperty = InspectionReadoutPropertyKey.DependencyProperty;
	public static readonly DependencyProperty RoiInspectionReadoutProperty = RoiInspectionReadoutPropertyKey.DependencyProperty;
	public static readonly DependencyProperty IsRoiActiveProperty = IsRoiActivePropertyKey.DependencyProperty;
	public static readonly DependencyProperty IsChannelInspectionUnavailableProperty = IsChannelInspectionUnavailablePropertyKey.DependencyProperty;
	public static readonly DependencyProperty ChannelInspectionStatusProperty = ChannelInspectionStatusPropertyKey.DependencyProperty;
	public static readonly DependencyProperty IsPixelGridVisibleProperty = IsPixelGridVisiblePropertyKey.DependencyProperty;
	public static readonly DependencyProperty DiagnosticsHudTextProperty = DiagnosticsHudTextPropertyKey.DependencyProperty;
	public static readonly DependencyProperty IsGpuPresentationActiveProperty = IsGpuPresentationActivePropertyKey.DependencyProperty;
	public static readonly DependencyProperty GpuPresentationDetailProperty = GpuPresentationDetailPropertyKey.DependencyProperty;
	public static readonly DependencyProperty ViewportPixelWidthProperty = ViewportPixelWidthPropertyKey.DependencyProperty;
	public static readonly DependencyProperty ViewportPixelHeightProperty = ViewportPixelHeightPropertyKey.DependencyProperty;
	public static readonly DependencyProperty DpiScaleXProperty = DpiScaleXPropertyKey.DependencyProperty;
	public static readonly DependencyProperty DpiScaleYProperty = DpiScaleYPropertyKey.DependencyProperty;
	public static readonly DependencyProperty InverseDpiScaleXProperty = InverseDpiScaleXPropertyKey.DependencyProperty;
	public static readonly DependencyProperty InverseDpiScaleYProperty = InverseDpiScaleYPropertyKey.DependencyProperty;

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

	public static readonly DependencyProperty GpuFrameProperty = DependencyProperty.Register(
		nameof(GpuFrame),
		typeof(OperatorGpuMonitoringFrame),
		typeof(MonitorView),
		new PropertyMetadata(null, OnGpuFrameChanged));

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
		new PropertyMetadata(MediaInspectionChannel.Combined, OnInspectionStateChanged));

	public static readonly DependencyProperty InspectionRoiProperty = DependencyProperty.Register(
		nameof(InspectionRoi), typeof(MediaInspectionRoi?), typeof(MonitorView),
		new PropertyMetadata(null, OnInspectionStateChanged));

	public static readonly DependencyProperty IsRoiSelectionEnabledProperty = DependencyProperty.Register(
		nameof(IsRoiSelectionEnabled), typeof(bool), typeof(MonitorView),
		new PropertyMetadata(false));

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
		ShowRgbCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Combined);
		ShowRedCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Red);
		ShowGreenCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Green);
		ShowBlueCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Blue);
		ShowAlphaCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Alpha);
		ShowLumaCommand = new MonitorPresentationCommand(() => InspectionChannel = MediaInspectionChannel.Luma);
		ToggleRoiSelectionCommand = new MonitorPresentationCommand(() => IsRoiSelectionEnabled = !IsRoiSelectionEnabled);
		ClearRoiCommand = new MonitorPresentationCommand(ClearInspectionRoi);
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

	public OperatorGpuMonitoringFrame? GpuFrame
	{
		get => (OperatorGpuMonitoringFrame?)GetValue(GpuFrameProperty);
		set => SetValue(GpuFrameProperty, value);
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

	public MediaInspectionRoi? InspectionRoi
	{
		get => (MediaInspectionRoi?)GetValue(InspectionRoiProperty);
		set => SetValue(InspectionRoiProperty, value);
	}

	public bool IsRoiSelectionEnabled
	{
		get => (bool)GetValue(IsRoiSelectionEnabledProperty);
		set => SetValue(IsRoiSelectionEnabledProperty, value);
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
	public string RoiInspectionReadout => (string)GetValue(RoiInspectionReadoutProperty);
	public bool IsRoiActive => (bool)GetValue(IsRoiActiveProperty);
	public bool IsChannelInspectionUnavailable => (bool)GetValue(IsChannelInspectionUnavailableProperty);
	public string ChannelInspectionStatus => (string)GetValue(ChannelInspectionStatusProperty);
	public bool IsPixelGridVisible => (bool)GetValue(IsPixelGridVisibleProperty);
	public bool IsGpuPresentationActive => (bool)GetValue(IsGpuPresentationActiveProperty);
	public string GpuPresentationDetail => (string)GetValue(GpuPresentationDetailProperty);
	public double ViewportPixelWidth => (double)GetValue(ViewportPixelWidthProperty);
	public double ViewportPixelHeight => (double)GetValue(ViewportPixelHeightProperty);
	public double DpiScaleX => (double)GetValue(DpiScaleXProperty);
	public double DpiScaleY => (double)GetValue(DpiScaleYProperty);
	public double InverseDpiScaleX => (double)GetValue(InverseDpiScaleXProperty);
	public double InverseDpiScaleY => (double)GetValue(InverseDpiScaleYProperty);
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
	public ICommand ToggleRoiSelectionCommand { get; }
	public ICommand ClearRoiCommand { get; }

	private static void OnInspectionStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		var roi = view.InspectionRoi;
		view.SetValue(IsRoiActivePropertyKey, roi is { IsEmpty: false });
		if (roi is null || roi.Value.IsEmpty)
			view.SetValue(RoiInspectionReadoutPropertyKey, "ROI —");
		else
			view.SetValue(RoiInspectionReadoutPropertyKey, MediaPixelInspection.Format(MediaInspectionRoiStatistics.Unavailable(roi.Value, "GPU analysis pending")));
		view.RefreshChannelInspectionAvailability();
	}

	private static void OnDiagnosticsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		((MonitorView)dependencyObject).RefreshDiagnosticsHud();
	}

	private void RefreshDiagnosticsHud()
	{
		if (!ShowDiagnosticsHud)
			return;

		var now = DateTimeOffset.UtcNow;
		if (_lastDiagnosticsHudUpdateAt != default && now - _lastDiagnosticsHudUpdateAt < TimeSpan.FromMilliseconds(200))
			return;
		_lastDiagnosticsHudUpdateAt = now;
		SetValue(DiagnosticsHudTextPropertyKey, FormatDiagnosticsHud());
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
			$"COLOR {diagnostics.ColorPath}  TIMING {diagnostics.TimingAuthority.ToString().ToUpperInvariant()}\n" +
			$"DISPLAY {(IsGpuPresentationActive ? GpuPresentationDetail : "WPF HIGH QUALITY")}  FALLBACK CPU/WPF  DROPPED/REPEATED/LATE/DISCONTINUITY UNAVAILABLE";
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
		((MonitorView)dependencyObject).RefreshPresentation();
	}

	private static void OnGpuFrameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		if (eventArgs.NewValue is null)
			view.SetGpuPresentationState(false, "CPU/WPF fallback");
		view.RefreshPresentation();
	}

	internal void SetGpuPresentationState(bool active, string detail)
	{
		SetValue(IsGpuPresentationActivePropertyKey, active);
		SetValue(GpuPresentationDetailPropertyKey, string.IsNullOrWhiteSpace(detail) ? "CPU/WPF fallback" : detail);
		RefreshChannelInspectionAvailability();
		if (ShowDiagnosticsHud)
			RefreshDiagnosticsHud();
	}

	private void RefreshChannelInspectionAvailability()
	{
		var requiresGpuChannelView = InspectionChannel != MediaInspectionChannel.Combined;
		var unavailable = requiresGpuChannelView && !IsGpuPresentationActive;
		SetValue(IsChannelInspectionUnavailablePropertyKey, unavailable);
		SetValue(
			ChannelInspectionStatusPropertyKey,
			unavailable
				? $"{InspectionChannel.ToString().ToUpperInvariant()} · GPU CHANNEL VIEW UNAVAILABLE"
				: InspectionChannel.ToString().ToUpperInvariant());
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
		SetValue(DpiScaleXPropertyKey, dpiScaleX);
		SetValue(DpiScaleYPropertyKey, dpiScaleY);
		SetValue(InverseDpiScaleXPropertyKey, 1.0 / dpiScaleX);
		SetValue(InverseDpiScaleYPropertyKey, 1.0 / dpiScaleY);
		SetValue(ViewportPixelWidthPropertyKey, Math.Max(1.0, Math.Round(widthDip * dpiScaleX)));
		SetValue(ViewportPixelHeightPropertyKey, Math.Max(1.0, Math.Round(heightDip * dpiScaleY)));
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
		if ((Frame is null && GpuFrame is null) || direction == 0 || _viewportWidthDip <= 0 || _viewportHeightDip <= 0)
			return;

		var (sourceWidth, sourceHeight) = ResolveSourcePixels();
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

	internal bool BeginRoiSelectionAt(double pointerXDip, double pointerYDip)
	{
		var (sourceWidthValue, sourceHeightValue) = ResolveSourcePixels();
		var sourceWidth = Math.Max(1, checked((int)sourceWidthValue));
		var sourceHeight = Math.Max(1, checked((int)sourceHeightValue));
		if (!MediaPixelInspection.TryMapViewportToSource(
			pointerXDip, pointerYDip, _dpiScaleX, _dpiScaleY, _presentationRect,
			sourceWidth, sourceHeight, out var coordinate))
			return false;

		_roiAnchor = coordinate;
		_roiInteractionStartRoi = InspectionRoi;
		if (InspectionRoi is { IsEmpty: false } roi)
		{
			var thresholdX = Math.Max(1, (int)Math.Ceiling(7.0 * _dpiScaleX / Math.Max(_presentationRect.Scale, 0.0001)));
			var thresholdY = Math.Max(1, (int)Math.Ceiling(7.0 * _dpiScaleY / Math.Max(_presentationRect.Scale, 0.0001)));
			bool Near(int x, int y) =>
				Math.Abs(coordinate.X - x) <= thresholdX &&
				Math.Abs(coordinate.Y - y) <= thresholdY;

			if (Near(roi.X, roi.Y))
				_roiInteractionMode = RoiInteractionMode.ResizeTopLeft;
			else if (Near(roi.RightExclusive - 1, roi.Y))
				_roiInteractionMode = RoiInteractionMode.ResizeTopRight;
			else if (Near(roi.X, roi.BottomExclusive - 1))
				_roiInteractionMode = RoiInteractionMode.ResizeBottomLeft;
			else if (Near(roi.RightExclusive - 1, roi.BottomExclusive - 1))
				_roiInteractionMode = RoiInteractionMode.ResizeBottomRight;
			else if (roi.Contains(coordinate))
				_roiInteractionMode = RoiInteractionMode.Move;
			else
				_roiInteractionMode = RoiInteractionMode.Create;
		}
		else
		{
			_roiInteractionMode = RoiInteractionMode.Create;
		}

		if (_roiInteractionMode == RoiInteractionMode.Create)
		{
			_roiInteractionStartRoi = null;
			InspectionRoi = new MediaInspectionRoi(coordinate.X, coordinate.Y, 1, 1);
		}
		return true;
	}

	internal void UpdateRoiSelectionAt(double pointerXDip, double pointerYDip)
	{
		if (_roiAnchor is not { } anchor)
			return;

		var (sourceWidthValue, sourceHeightValue) = ResolveSourcePixels();
		var sourceWidth = Math.Max(1, checked((int)sourceWidthValue));
		var sourceHeight = Math.Max(1, checked((int)sourceHeightValue));
		var coordinate = MediaPixelInspection.MapViewportToSourceClamped(
			pointerXDip, pointerYDip, _dpiScaleX, _dpiScaleY, _presentationRect,
			sourceWidth, sourceHeight);

		InspectionRoi = _roiInteractionMode switch
		{
			RoiInteractionMode.Move when _roiInteractionStartRoi is { } start =>
				start.MoveBy(coordinate.X - anchor.X, coordinate.Y - anchor.Y, sourceWidth, sourceHeight),
			RoiInteractionMode.ResizeTopLeft when _roiInteractionStartRoi is { } start =>
				start.ResizeFromCorner(MediaInspectionRoiCorner.TopLeft, coordinate, sourceWidth, sourceHeight),
			RoiInteractionMode.ResizeTopRight when _roiInteractionStartRoi is { } start =>
				start.ResizeFromCorner(MediaInspectionRoiCorner.TopRight, coordinate, sourceWidth, sourceHeight),
			RoiInteractionMode.ResizeBottomLeft when _roiInteractionStartRoi is { } start =>
				start.ResizeFromCorner(MediaInspectionRoiCorner.BottomLeft, coordinate, sourceWidth, sourceHeight),
			RoiInteractionMode.ResizeBottomRight when _roiInteractionStartRoi is { } start =>
				start.ResizeFromCorner(MediaInspectionRoiCorner.BottomRight, coordinate, sourceWidth, sourceHeight),
			_ => MediaInspectionRoi.FromCorners(anchor, coordinate, sourceWidth, sourceHeight)
		};
	}

	internal void EndRoiSelection()
	{
		_roiAnchor = null;
		_roiInteractionStartRoi = null;
		_roiInteractionMode = RoiInteractionMode.None;
	}

	internal void ClearInspectionRoi()
	{
		EndRoiSelection();
		InspectionRoi = null;
		SetValue(RoiInspectionReadoutPropertyKey, "ROI —");
	}

	internal void SetRoiStatistics(MediaInspectionRoiStatistics statistics)
	{
		if (InspectionRoi is not { } current || current != statistics.Roi)
			return;
		SetValue(RoiInspectionReadoutPropertyKey, MediaPixelInspection.Format(statistics));
	}

	internal void SetRoiUnavailable(string detail)
	{
		if (InspectionRoi is not { } roi || roi.IsEmpty)
			return;
		SetValue(RoiInspectionReadoutPropertyKey, MediaPixelInspection.Format(MediaInspectionRoiStatistics.Unavailable(roi, detail)));
	}

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

		var (sourceWidthValue, sourceHeightValue) = ResolveSourcePixels();
		var sourceWidth = Math.Max(1, checked((int)sourceWidthValue));
		var sourceHeight = Math.Max(1, checked((int)sourceHeightValue));
		if (!MediaPixelInspection.TryMapViewportToSource(
			pointerXDip, pointerYDip, _dpiScaleX, _dpiScaleY, _presentationRect,
			sourceWidth, sourceHeight, out var coordinate))
		{
			SetValue(InspectionReadoutPropertyKey, "PIXEL — · OUTSIDE IMAGE");
			return;
		}

		SetValue(
			InspectionReadoutPropertyKey,
			MediaPixelInspection.Format(MediaPixelInspection.SampleDisplayPixel(bitmap, coordinate, sourceWidth, sourceHeight)));
	}

	private void RefreshPresentation()
	{
		if ((Frame is null && GpuFrame is null) || _viewportWidthDip <= 0 || _viewportHeightDip <= 0)
			return;

		var (sourceWidth, sourceHeight) = ResolveSourcePixels();
		SetValue(SourcePixelWidthPropertyKey, checked((int)sourceWidth));
		SetValue(SourcePixelHeightPropertyKey, checked((int)sourceHeight));
		if (sourceWidth <= 0 || sourceHeight <= 0)
			return;
		if (InspectionRoi is { } roi)
		{
			var clamped = roi.Clamp(checked((int)sourceWidth), checked((int)sourceHeight));
			if (clamped.IsEmpty)
				ClearInspectionRoi();
			else if (clamped != roi)
				InspectionRoi = clamped;
		}

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
		if (ShowDiagnosticsHud)
			RefreshDiagnosticsHud();

		SetValue(
			PresentationInfoPropertyKey,
			$"{sourceWidth:0}×{sourceHeight:0} | {ZoomMode} {result.Scale * 100:0.#}% | Target {target.PixelWidth}×{target.PixelHeight} px | {target.Quality} {target.Path} | Scale stages 1 | RT #{_renderTargetStabilizer.Revision}{(_renderTargetStabilizer.ResizePending ? " pending" : string.Empty)} | DPI {_dpiScaleX * 100:0}%");
	}

	private (double Width, double Height) ResolveSourcePixels()
	{
		if (GpuFrame is { Resource.Interop.IsPresentable: true } gpu)
			return (gpu.Resource.Format.Width, gpu.Resource.Format.Height);
		if (Frame is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
			return (bitmap.PixelWidth, bitmap.PixelHeight);
		if (Frame is { } source)
		{
			var width = double.IsFinite(source.Width) && source.Width > 0 ? source.Width : 1;
			var height = double.IsFinite(source.Height) && source.Height > 0 ? source.Height : 1;
			return (width, height);
		}
		return (1, 1);
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

	private enum RoiInteractionMode
	{
		None,
		Create,
		Move,
		ResizeTopLeft,
		ResizeTopRight,
		ResizeBottomLeft,
		ResizeBottomRight
	}

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
