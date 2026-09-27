// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

public enum MediaInspectionValueSpace
{
	SourceCodeValues,
	DisplayCodeValues
}

public enum MediaInspectionChannel
{
	Rgb,
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
}
