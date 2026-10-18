// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Media.Imaging;

namespace rtaime.Operator;

public partial class MonitorView
{
	private DateTimeOffset? _lastBitmapReceipt, _lastGpuReceipt;
	private static readonly DependencyPropertyKey PresentationQualityPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(PresentationQuality), typeof(string), typeof(MonitorView), new PropertyMetadata("DETAIL UNAVAILABLE"));
	private static readonly DependencyPropertyKey ObservationStatusPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(ObservationStatus), typeof(string), typeof(MonitorView), new PropertyMetadata("NO FRAME OBSERVED"));
	private static readonly DependencyPropertyKey CropStatusPropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(CropStatus), typeof(string), typeof(MonitorView), new PropertyMetadata("FIT"));
	private static readonly DependencyPropertyKey HasPresentedFramePropertyKey = DependencyProperty.RegisterReadOnly(
		nameof(HasPresentedFrame), typeof(bool), typeof(MonitorView), new PropertyMetadata(false));
	public static readonly DependencyProperty PresentationQualityProperty = PresentationQualityPropertyKey.DependencyProperty;
	public static readonly DependencyProperty ObservationStatusProperty = ObservationStatusPropertyKey.DependencyProperty;
	public static readonly DependencyProperty CropStatusProperty = CropStatusPropertyKey.DependencyProperty;
	public static readonly DependencyProperty HasPresentedFrameProperty = HasPresentedFramePropertyKey.DependencyProperty;
	public string PresentationQuality => (string)GetValue(PresentationQualityProperty);
	public string ObservationStatus => (string)GetValue(ObservationStatusProperty);
	public string CropStatus => (string)GetValue(CropStatusProperty);
	public bool HasPresentedFrame => (bool)GetValue(HasPresentedFrameProperty);

	private static void OnObservationStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
		((MonitorView)sender).RefreshPresentationEvidence();

	private void RefreshPresentationEvidence()
	{
		var hasFrame = Frame is not null || (IsGpuPresentationActive && GpuFrame is not null);
		SetValue(HasPresentedFramePropertyKey, hasFrame);
		var bitmap = Frame as BitmapSource;
		var basis = IsGpuPresentationActive && GpuFrame is { } gpu
			? $"GPU SHARED {gpu.Resource.Format.Width}×{gpu.Resource.Format.Height} · 100% = shared-resource pixel"
			: bitmap is not null
				? $"REDUCED CPU {bitmap.PixelWidth}×{bitmap.PixelHeight} · 100% = fallback pixel"
				: "DETAIL UNAVAILABLE · no active presentation";
		SetValue(PresentationQualityPropertyKey, basis);
		var receipt = IsGpuPresentationActive ? _lastGpuReceipt : _lastBitmapReceipt;
		var retained = hasFrame && !string.Equals(State, "LIVE", StringComparison.OrdinalIgnoreCase);
		SetValue(ObservationStatusPropertyKey, !hasFrame ? $"{State} · FRAME UNAVAILABLE" :
			$"{State}{(retained ? " · RETAINED FRAME / NOT CURRENT" : "")} · local viewer receipt " +
			(receipt is { } time ? $"{time:HH:mm:ss.fff} UTC · age at refresh {Math.Max(0, (DateTimeOffset.UtcNow - time).TotalMilliseconds):0} ms" : "UNAVAILABLE"));
		SetValue(CropStatusPropertyKey, ZoomMode == "FILL" ? "FILL · CROPPED TO VIEWPORT" :
			_presentationRect.Width > ViewportPixelWidth || _presentationRect.Height > ViewportPixelHeight ? "ZOOM · CROPPED / PAN" : ZoomMode);
	}

	internal MediaPresentationRect CalculateGpuPresentation(int width, int height, int targetWidth, int targetHeight)
	{
		var (mode, zoom) = ResolvePresentationPolicy(ZoomMode);
		return MediaPresentationGeometry.Calculate(width, height, targetWidth, targetHeight, mode, zoom, _panXPhysical, _panYPhysical);
	}
}
