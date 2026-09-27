// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

public enum MediaPresentationMode
{
	Fit,
	Fill,
	PixelPerfect,
	CustomZoom
}

public readonly record struct MediaPresentationRect(double X, double Y, double Width, double Height, double Scale);

public readonly record struct MediaPanBounds(double MinX, double MaxX, double MinY, double MaxY);

public static class MediaPresentationGeometry
{
	public static (double Width, double Height) ToPhysicalPixels(double logicalWidth, double logicalHeight, double dpiScaleX, double dpiScaleY)
	{
		ValidateExtent(logicalWidth, logicalHeight, nameof(logicalWidth));
		ValidateScale(dpiScaleX, nameof(dpiScaleX));
		ValidateScale(dpiScaleY, nameof(dpiScaleY));
		return (logicalWidth * dpiScaleX, logicalHeight * dpiScaleY);
	}

	public static MediaPresentationRect Calculate(
		double sourceWidth,
		double sourceHeight,
		double viewportWidth,
		double viewportHeight,
		MediaPresentationMode mode,
		double customZoom = 1.0,
		double panX = 0,
		double panY = 0)
	{
		ValidateExtent(sourceWidth, sourceHeight, nameof(sourceWidth));
		ValidateExtent(viewportWidth, viewportHeight, nameof(viewportWidth));
		ValidateScale(customZoom, nameof(customZoom));

		var scale = mode switch
		{
			MediaPresentationMode.Fit => Math.Min(viewportWidth / sourceWidth, viewportHeight / sourceHeight),
			MediaPresentationMode.Fill => Math.Max(viewportWidth / sourceWidth, viewportHeight / sourceHeight),
			MediaPresentationMode.PixelPerfect => 1.0,
			MediaPresentationMode.CustomZoom => customZoom,
			_ => throw new ArgumentOutOfRangeException(nameof(mode))
		};

		var width = sourceWidth * scale;
		var height = sourceHeight * scale;
		var bounds = CalculatePanBounds(width, height, viewportWidth, viewportHeight);
		var x = (viewportWidth - width) / 2.0 + Math.Clamp(panX, bounds.MinX, bounds.MaxX);
		var y = (viewportHeight - height) / 2.0 + Math.Clamp(panY, bounds.MinY, bounds.MaxY);
		return new MediaPresentationRect(x, y, width, height, scale);
	}

	public static MediaPanBounds CalculatePanBounds(double presentationWidth, double presentationHeight, double viewportWidth, double viewportHeight)
	{
		ValidateExtent(presentationWidth, presentationHeight, nameof(presentationWidth));
		ValidateExtent(viewportWidth, viewportHeight, nameof(viewportWidth));
		var horizontal = Math.Max(0, (presentationWidth - viewportWidth) / 2.0);
		var vertical = Math.Max(0, (presentationHeight - viewportHeight) / 2.0);
		return new MediaPanBounds(-horizontal, horizontal, -vertical, vertical);
	}

	private static void ValidateExtent(double width, double height, string parameterName)
	{
		if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(parameterName);
	}

	private static void ValidateScale(double scale, string parameterName)
	{
		if (!double.IsFinite(scale) || scale <= 0)
			throw new ArgumentOutOfRangeException(parameterName);
	}
}
