// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using rtaime.Client;

namespace rtaime.Operator;

internal static class GraphicsOverlayAssetLoader
{
	public static OperatorGraphicsAsset LoadPng(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("Graphics asset path is required.", nameof(path));
		if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
			throw new NotSupportedException("V1 graphics workflow accepts PNG assets only.");

		using var stream = File.OpenRead(path);
		var decoder = new PngBitmapDecoder(
			stream,
			BitmapCreateOptions.PreservePixelFormat,
			BitmapCacheOption.OnLoad);
		if (decoder.Frames.Count == 0)
			throw new InvalidDataException("PNG graphics asset contains no image frame.");

		var source = decoder.Frames[0];
		var width = checked((uint)source.PixelWidth);
		var height = checked((uint)source.PixelHeight);
		if (width == 0 || height == 0 || width > 384 || height > 384)
			throw new InvalidDataException("V1 graphics assets must be between 1x1 and 384x384 pixels.");

		var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
		var stride = checked(source.PixelWidth * 4);
		var bgra = new byte[checked(stride * source.PixelHeight)];
		converted.CopyPixels(bgra, stride, 0);

		var rgba = new byte[bgra.Length];
		for (var offset = 0; offset < bgra.Length; offset += 4)
		{
			rgba[offset] = bgra[offset + 2];
			rgba[offset + 1] = bgra[offset + 1];
			rgba[offset + 2] = bgra[offset];
			rgba[offset + 3] = bgra[offset + 3];
		}

		return new OperatorGraphicsAsset(Path.GetFileName(path), width, height, rgba);
	}
}
