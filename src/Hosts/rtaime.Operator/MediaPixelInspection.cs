// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

public enum MediaInspectionValueSpace
{
	SourceCodeValues,
	DisplayCodeValues
}

public enum MediaInspectionChannel
{
	Combined,
	Red,
	Green,
	Blue,
	Alpha,
	Luma
}

public readonly record struct MediaPixelCoordinate(int X, int Y)
{
	public bool IsInside(int width, int height) => X >= 0 && Y >= 0 && X < width && Y < height;
}

public enum MediaInspectionRoiCorner
{
	TopLeft,
	TopRight,
	BottomLeft,
	BottomRight
}

public readonly record struct MediaInspectionRoi(int X, int Y, int Width, int Height)
{
	public int RightExclusive => checked(X + Width);
	public int BottomExclusive => checked(Y + Height);
	public bool IsEmpty => Width <= 0 || Height <= 0;

	public bool Contains(MediaPixelCoordinate coordinate) =>
		!IsEmpty &&
		coordinate.X >= X &&
		coordinate.Y >= Y &&
		coordinate.X < RightExclusive &&
		coordinate.Y < BottomExclusive;

	public MediaInspectionRoi MoveBy(int deltaX, int deltaY, int sourceWidth, int sourceHeight)
	{
		if (IsEmpty || sourceWidth <= 0 || sourceHeight <= 0)
			return default;

		var width = Math.Min(Width, sourceWidth);
		var height = Math.Min(Height, sourceHeight);
		var x = Math.Clamp(X + deltaX, 0, sourceWidth - width);
		var y = Math.Clamp(Y + deltaY, 0, sourceHeight - height);
		return new MediaInspectionRoi(x, y, width, height);
	}

	public MediaInspectionRoi ResizeFromCorner(
		MediaInspectionRoiCorner corner,
		MediaPixelCoordinate coordinate,
		int sourceWidth,
		int sourceHeight)
	{
		if (IsEmpty || sourceWidth <= 0 || sourceHeight <= 0)
			return default;

		var fixedCorner = corner switch
		{
			MediaInspectionRoiCorner.TopLeft => new MediaPixelCoordinate(RightExclusive - 1, BottomExclusive - 1),
			MediaInspectionRoiCorner.TopRight => new MediaPixelCoordinate(X, BottomExclusive - 1),
			MediaInspectionRoiCorner.BottomLeft => new MediaPixelCoordinate(RightExclusive - 1, Y),
			MediaInspectionRoiCorner.BottomRight => new MediaPixelCoordinate(X, Y),
			_ => new MediaPixelCoordinate(X, Y)
		};
		return FromCorners(fixedCorner, coordinate, sourceWidth, sourceHeight);
	}

	public MediaInspectionRoi Clamp(int sourceWidth, int sourceHeight)
	{
		if (sourceWidth <= 0 || sourceHeight <= 0)
			return default;

		var left = Math.Clamp(X, 0, sourceWidth);
		var top = Math.Clamp(Y, 0, sourceHeight);
		var right = Math.Clamp(RightExclusive, left, sourceWidth);
		var bottom = Math.Clamp(BottomExclusive, top, sourceHeight);
		return new MediaInspectionRoi(left, top, right - left, bottom - top);
	}

	public static MediaInspectionRoi FromCorners(MediaPixelCoordinate first, MediaPixelCoordinate second, int sourceWidth, int sourceHeight)
	{
		if (sourceWidth <= 0 || sourceHeight <= 0)
			return default;

		var left = Math.Clamp(Math.Min(first.X, second.X), 0, sourceWidth - 1);
		var top = Math.Clamp(Math.Min(first.Y, second.Y), 0, sourceHeight - 1);
		var right = Math.Clamp(Math.Max(first.X, second.X), 0, sourceWidth - 1);
		var bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, sourceHeight - 1);
		return new MediaInspectionRoi(left, top, checked(right - left + 1), checked(bottom - top + 1));
	}
}

public readonly record struct MediaInspectionRoiStatistics(
	bool IsAvailable,
	ulong SequenceNumber,
	MediaInspectionRoi Roi,
	int SampleCount,
	double MeanRed,
	double MeanGreen,
	double MeanBlue,
	double MeanAlpha,
	double MeanLuma,
	byte MinRed,
	byte MinGreen,
	byte MinBlue,
	byte MinAlpha,
	byte MinLuma,
	byte MaxRed,
	byte MaxGreen,
	byte MaxBlue,
	byte MaxAlpha,
	byte MaxLuma,
	string Detail)
{
	public static MediaInspectionRoiStatistics Unavailable(MediaInspectionRoi roi, string detail) =>
		new(false, 0, roi, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, detail);
}

public readonly record struct MediaPixelSample(
	MediaPixelCoordinate Coordinate,
	bool IsValid,
	byte Red,
	byte Green,
	byte Blue,
	byte Alpha,
	double Luma,
	MediaInspectionValueSpace ValueSpace,
	string Detail)
{
	public static MediaPixelSample Outside(MediaPixelCoordinate coordinate) =>
		new(coordinate, false, 0, 0, 0, 0, 0, MediaInspectionValueSpace.DisplayCodeValues, "Outside image");

	public static MediaPixelSample Unsupported(MediaPixelCoordinate coordinate, string detail) =>
		new(coordinate, false, 0, 0, 0, 0, 0, MediaInspectionValueSpace.DisplayCodeValues, detail);
}

public static class MediaPixelInspection
{
	[ThreadStatic]
	private static byte[]? _pixelBuffer;
	public const double PixelGridMinimumScale = 8.0;

	public static bool TryMapViewportToSource(
		double pointerXDip,
		double pointerYDip,
		double dpiScaleX,
		double dpiScaleY,
		MediaPresentationRect presentation,
		int sourceWidth,
		int sourceHeight,
		out MediaPixelCoordinate coordinate)
	{
		coordinate = default;
		if (sourceWidth <= 0 || sourceHeight <= 0 || presentation.Scale <= 0 ||
			!double.IsFinite(pointerXDip) || !double.IsFinite(pointerYDip))
			return false;

		var physicalX = pointerXDip * dpiScaleX;
		var physicalY = pointerYDip * dpiScaleY;
		var sourceX = (physicalX - presentation.X) / presentation.Scale;
		var sourceY = (physicalY - presentation.Y) / presentation.Scale;
		if (sourceX < 0 || sourceY < 0 || sourceX >= sourceWidth || sourceY >= sourceHeight)
			return false;

		coordinate = new MediaPixelCoordinate(
			Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1),
			Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1));
		return true;
	}

	public static MediaPixelCoordinate MapViewportToSourceClamped(
		double pointerXDip,
		double pointerYDip,
		double dpiScaleX,
		double dpiScaleY,
		MediaPresentationRect presentation,
		int sourceWidth,
		int sourceHeight)
	{
		if (sourceWidth <= 0 || sourceHeight <= 0 || presentation.Scale <= 0 ||
			!double.IsFinite(pointerXDip) || !double.IsFinite(pointerYDip))
			return default;

		var physicalX = pointerXDip * dpiScaleX;
		var physicalY = pointerYDip * dpiScaleY;
		var sourceX = (physicalX - presentation.X) / presentation.Scale;
		var sourceY = (physicalY - presentation.Y) / presentation.Scale;
		return new MediaPixelCoordinate(
			Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1),
			Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1));
	}

	public static MediaPixelCoordinate MapSourceToMonitoringSample(
		MediaPixelCoordinate sourceCoordinate,
		int sourceWidth,
		int sourceHeight,
		int sampleWidth,
		int sampleHeight)
	{
		if (!sourceCoordinate.IsInside(sourceWidth, sourceHeight))
			throw new ArgumentOutOfRangeException(nameof(sourceCoordinate));
		if (sampleWidth <= 0 || sampleHeight <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleWidth), "Monitoring sample dimensions must be positive.");

		return new MediaPixelCoordinate(
			Math.Min(sampleWidth - 1, checked((int)((long)sourceCoordinate.X * sampleWidth / sourceWidth))),
			Math.Min(sampleHeight - 1, checked((int)((long)sourceCoordinate.Y * sampleHeight / sourceHeight))));
	}

	public static MediaPixelSample SampleDisplayPixel(
		System.Windows.Media.Imaging.BitmapSource bitmap,
		MediaPixelCoordinate sourceCoordinate,
		int sourceWidth,
		int sourceHeight)
	{
		ArgumentNullException.ThrowIfNull(bitmap);
		var sampleCoordinate = MapSourceToMonitoringSample(
			sourceCoordinate,
			sourceWidth,
			sourceHeight,
			bitmap.PixelWidth,
			bitmap.PixelHeight);
		var sample = SampleDisplayPixel(bitmap, sampleCoordinate);
		return sample with
		{
			Coordinate = sourceCoordinate,
			Detail = sourceWidth == bitmap.PixelWidth && sourceHeight == bitmap.PixelHeight
				? sample.Detail
				: "BGRA32 bounded monitoring sample"
		};
	}

	public static MediaPixelSample SampleDisplayPixel(System.Windows.Media.Imaging.BitmapSource bitmap, MediaPixelCoordinate coordinate)
	{
		ArgumentNullException.ThrowIfNull(bitmap);
		if (!coordinate.IsInside(bitmap.PixelWidth, bitmap.PixelHeight))
			return MediaPixelSample.Outside(coordinate);

		if (bitmap.Format != System.Windows.Media.PixelFormats.Bgra32 &&
			bitmap.Format != System.Windows.Media.PixelFormats.Pbgra32)
			return MediaPixelSample.Unsupported(coordinate, $"Sampling unsupported for {bitmap.Format}");

		var pixel = _pixelBuffer ??= new byte[4];
		bitmap.CopyPixels(
			new System.Windows.Int32Rect(coordinate.X, coordinate.Y, 1, 1),
			pixel,
			4,
			0);
		var red = pixel[2];
		var green = pixel[1];
		var blue = pixel[0];
		var alpha = pixel[3];
		var luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
		return new MediaPixelSample(coordinate, true, red, green, blue, alpha, luma, MediaInspectionValueSpace.DisplayCodeValues, "BGRA32 display sample");
	}

	public static string Format(MediaPixelSample sample) =>
		sample.IsValid
			? $"X {sample.Coordinate.X}  Y {sample.Coordinate.Y}  R {sample.Red}  G {sample.Green}  B {sample.Blue}  A {sample.Alpha}  Y' {sample.Luma:0.0}  DISPLAY CODE"
			: $"X {sample.Coordinate.X}  Y {sample.Coordinate.Y}  {sample.Detail.ToUpperInvariant()}";

	public static string Format(MediaInspectionRoiStatistics statistics)
	{
		if (!statistics.IsAvailable)
			return $"ROI {statistics.Roi.X},{statistics.Roi.Y} {statistics.Roi.Width}×{statistics.Roi.Height} · {statistics.Detail.ToUpperInvariant()}";

		return $"ROI {statistics.Roi.X},{statistics.Roi.Y} {statistics.Roi.Width}×{statistics.Roi.Height} · N {statistics.SampleCount} · " +
			$"MEAN R {statistics.MeanRed:0.0} G {statistics.MeanGreen:0.0} B {statistics.MeanBlue:0.0} A {statistics.MeanAlpha:0.0} Y' {statistics.MeanLuma:0.0} · " +
			$"MIN R {statistics.MinRed} G {statistics.MinGreen} B {statistics.MinBlue} A {statistics.MinAlpha} Y' {statistics.MinLuma} · " +
			$"MAX R {statistics.MaxRed} G {statistics.MaxGreen} B {statistics.MaxBlue} A {statistics.MaxAlpha} Y' {statistics.MaxLuma}";
	}
}
