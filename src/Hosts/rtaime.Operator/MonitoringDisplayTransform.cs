// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

internal static class MonitoringDisplayTransform
{
	private static readonly ConcurrentDictionary<ColorDescription, byte[]> Luts = new();

	public static string Describe(ColorDescription color) =>
		color.IsComplete
			? $"DISPLAY sRGB <- {color.TechnicalLabel}"
			: $"DISPLAY PASSTHROUGH · COLOR {color.TechnicalLabel}";

	public static void ConvertRgbaToBgra(ReadOnlySpan<byte> source, Span<byte> destination, ColorDescription color)
	{
		if (source.Length != destination.Length || source.Length % 4 != 0)
			throw new ArgumentException("RGBA/BGRA buffers must have equal four-byte pixel lengths.");

		var lut = color.IsComplete ? Luts.GetOrAdd(color, BuildLut) : null;
		for (var offset = 0; offset < source.Length; offset += 4)
		{
			var red = source[offset];
			var green = source[offset + 1];
			var blue = source[offset + 2];
			destination[offset] = lut is null ? blue : lut[blue];
			destination[offset + 1] = lut is null ? green : lut[green];
			destination[offset + 2] = lut is null ? red : lut[red];
			destination[offset + 3] = source[offset + 3];
		}
	}

	private static byte[] BuildLut(ColorDescription color)
	{
		var lut = new byte[256];
		for (var value = 0; value < lut.Length; value++)
		{
			var normalized = color.Range == NominalRange.Limited
				? Math.Clamp((value - 16.0) / 219.0, 0.0, 1.0)
				: value / 255.0;

			var linear = color.Transfer switch
			{
				ColorTransfer.Linear => normalized,
				ColorTransfer.Srgb => SrgbToLinear(normalized),
				ColorTransfer.Rec709 => Rec709ToLinear(normalized),
				_ => normalized
			};
			var srgb = LinearToSrgb(linear);
			lut[value] = (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
		}
		return lut;
	}

	private static double Rec709ToLinear(double value) =>
		value < 0.081 ? value / 4.5 : Math.Pow((value + 0.099) / 1.099, 1.0 / 0.45);

	private static double SrgbToLinear(double value) =>
		value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

	private static double LinearToSrgb(double value) =>
		value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
}
