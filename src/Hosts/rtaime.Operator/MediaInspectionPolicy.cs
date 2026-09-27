// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

public static class MediaInspectionPolicy
{
	public const double MinimumZoom = 0.25;
	public const double MaximumZoom = 8.0;

	public static double ClampZoom(double zoom)
	{
		if (!double.IsFinite(zoom))
			throw new ArgumentOutOfRangeException(nameof(zoom));
		return Math.Clamp(zoom, MinimumZoom, MaximumZoom);
	}

	public static double StepZoom(double currentZoom, int wheelDelta)
	{
		var direction = Math.Sign(wheelDelta);
		if (direction == 0)
			return ClampZoom(currentZoom);

		var factor = direction > 0 ? 1.25 : 0.8;
		return ClampZoom(currentZoom * factor);
	}

	public static (double PanX, double PanY) AnchorZoom(
		double sourceWidth,
		double sourceHeight,
		double viewportWidth,
		double viewportHeight,
		double oldScale,
		double newScale,
		double panX,
		double panY,
		double anchorX,
		double anchorY)
	{
		if (oldScale <= 0 || newScale <= 0)
			throw new ArgumentOutOfRangeException(nameof(oldScale));

		var oldWidth = sourceWidth * oldScale;
		var oldHeight = sourceHeight * oldScale;
		var oldX = (viewportWidth - oldWidth) / 2.0 + panX;
		var oldY = (viewportHeight - oldHeight) / 2.0 + panY;
		var sourceX = (anchorX - oldX) / oldScale;
		var sourceY = (anchorY - oldY) / oldScale;

		var newWidth = sourceWidth * newScale;
		var newHeight = sourceHeight * newScale;
		var centeredX = (viewportWidth - newWidth) / 2.0;
		var centeredY = (viewportHeight - newHeight) / 2.0;
		var requestedPanX = anchorX - sourceX * newScale - centeredX;
		var requestedPanY = anchorY - sourceY * newScale - centeredY;
		var bounds = MediaPresentationGeometry.CalculatePanBounds(newWidth, newHeight, viewportWidth, viewportHeight);
		return (
			Math.Clamp(requestedPanX, bounds.MinX, bounds.MaxX),
			Math.Clamp(requestedPanY, bounds.MinY, bounds.MaxY));
	}
}
