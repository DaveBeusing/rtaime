// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public class MonitorView : UserControl
{
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
		new PropertyMetadata(null));

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
	public static readonly DependencyProperty IsTransportSourceProperty = IsTransportSourcePropertyKey.DependencyProperty;
	public static readonly DependencyProperty DisplayTimecodeProperty = DisplayTimecodePropertyKey.DependencyProperty;

	public MonitorView()
	{
		FitCommand = new MonitorPresentationCommand(() => ZoomMode = "FIT");
		Zoom50Command = new MonitorPresentationCommand(() => ZoomMode = "50%");
		Zoom100Command = new MonitorPresentationCommand(() => ZoomMode = "100%");
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
	public bool IsTransportSource => (bool)GetValue(IsTransportSourceProperty);
	public string DisplayTimecode => (string)GetValue(DisplayTimecodeProperty);

	public ICommand FitCommand { get; }
	public ICommand Zoom50Command { get; }
	public ICommand Zoom100Command { get; }

	private static void OnZoomModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var view = (MonitorView)dependencyObject;
		var mode = NormalizeZoomMode(eventArgs.NewValue as string);
		if (!string.Equals(mode, eventArgs.NewValue as string, StringComparison.Ordinal))
		{
			view.SetCurrentValue(ZoomModeProperty, mode);
			return;
		}

		view.SetValue(ImageStretchPropertyKey, mode == "FIT" ? Stretch.Uniform : Stretch.None);
		view.SetValue(ImageScalePropertyKey, mode == "50%" ? 0.5 : 1.0);
	}

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
		"50%" => "50%",
		"100%" => "100%",
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
