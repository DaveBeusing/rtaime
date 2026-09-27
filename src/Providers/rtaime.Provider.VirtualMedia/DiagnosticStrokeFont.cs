// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Provider.VirtualMedia;

/// <summary>
/// Deterministic dependency-free stroke-font rasterizer for static diagnostic labels.
/// Technical reference pixels remain owned by the pattern generator; this component
/// only composites annotation coverage into explicitly selected label regions.
/// </summary>
internal static class DiagnosticStrokeFont
{
	private const double GlyphWidth = 5.0;
	private const double GlyphHeight = 7.0;
	private const double Advance = 6.0;
	private const double StrokeWidth = 0.72;

	private static readonly IReadOnlyDictionary<char, string[]> Strokes = new Dictionary<char, string[]>
	{
		[' '] = [],
		['-'] = ["0333"], ['/'] = ["0640"], ['.'] = ["2626"], [':'] = ["2222","2525"],
		['0'] = ["0040460600"], ['1'] = ["1120","2026","1636"], ['2'] = ["004043030646"], ['3'] = ["0040432343460603"],
		['4'] = ["000343","4046"], ['5'] = ["400003434606"], ['6'] = ["400006464303"], ['7'] = ["004046"], ['8'] = ["0040460600","0343"], ['9'] = ["460040000343"],
		['A'] = ["06024046","2343"], ['B'] = ["0006404643030040"], ['C'] = ["40000646"], ['D'] = ["0006464000"], ['E'] = ["40000646","0343"],
		['F'] = ["400006","0343"], ['G'] = ["400006464323"], ['H'] = ["0006","4046","0343"], ['I'] = ["0040","2026","0646"], ['J'] = ["40044626"],
		['K'] = ["0006","0340","0346"], ['L'] = ["000646"], ['M'] = ["0600204046"], ['N'] = ["06004046"], ['O'] = ["0040460600"], ['P'] = ["0600404303"],
		['Q'] = ["0040460600","2346"], ['R'] = ["0600404303","2346"], ['S'] = ["400003434606"], ['T'] = ["0040","2026"], ['U'] = ["00064640"],
		['V'] = ["00022640"], ['W'] = ["0006204640"], ['X'] = ["0046","4006"], ['Y'] = ["0023","4023","2326"], ['Z'] = ["00400646"],
	};

	public static int Measure(string text, int pixelHeight)
	{
		if (string.IsNullOrEmpty(text) || pixelHeight <= 0)
			return 0;
		var unit = pixelHeight / GlyphHeight;
		return Math.Max(0, (int)Math.Ceiling(((text.Length - 1) * Advance + GlyphWidth) * unit));
	}

	public static void Draw(
		Span<byte> pixels,
		int surfaceWidth,
		int surfaceHeight,
		string text,
		int originX,
		int originY,
		int pixelHeight,
		byte red,
		byte green,
		byte blue,
		byte alpha = 255)
	{
		if (string.IsNullOrEmpty(text) || pixelHeight <= 0)
			return;

		var unit = pixelHeight / GlyphHeight;
		var cursor = 0.0;
		foreach (var raw in text)
		{
			var glyph = char.ToUpperInvariant(raw);
			if (!Strokes.TryGetValue(glyph, out var strokes))
				strokes = Strokes[' '];

			foreach (var encoded in strokes)
				DrawPolyline(pixels, surfaceWidth, surfaceHeight, encoded, originX + cursor * unit, originY, unit, red, green, blue, alpha);

			cursor += Advance;
		}
	}

	private static void DrawPolyline(
		Span<byte> pixels,
		int width,
		int height,
		string encoded,
		double originX,
		double originY,
		double unit,
		byte red,
		byte green,
		byte blue,
		byte alpha)
	{
		if (encoded.Length < 4 || (encoded.Length & 1) != 0)
			return;

		for (var index = 0; index + 3 < encoded.Length; index += 2)
		{
			var x1 = originX + Decode(encoded[index]) * unit;
			var y1 = originY + Decode(encoded[index + 1]) * unit;
			var x2 = originX + Decode(encoded[index + 2]) * unit;
			var y2 = originY + Decode(encoded[index + 3]) * unit;
			DrawSegment(pixels, width, height, x1, y1, x2, y2, unit * StrokeWidth, red, green, blue, alpha);
		}
	}

	private static int Decode(char value) => value - '0';

	private static void DrawSegment(
		Span<byte> pixels,
		int width,
		int height,
		double x1,
		double y1,
		double x2,
		double y2,
		double strokeWidth,
		byte red,
		byte green,
		byte blue,
		byte alpha)
	{
		var radius = strokeWidth * 0.5;
		var left = Math.Max(0, (int)Math.Floor(Math.Min(x1, x2) - radius - 1));
		var right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(x1, x2) + radius + 1));
		var top = Math.Max(0, (int)Math.Floor(Math.Min(y1, y2) - radius - 1));
		var bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(y1, y2) + radius + 1));

		for (var y = top; y <= bottom; y++)
		{
			for (var x = left; x <= right; x++)
			{
				var distance = DistanceToSegment(x + 0.5, y + 0.5, x1, y1, x2, y2);
				var coverage = Math.Clamp(radius + 0.75 - distance, 0.0, 1.0);
				if (coverage <= 0)
					continue;

				var offset = ((y * width) + x) * 4;
				Blend(pixels, offset, red, green, blue, alpha, coverage);
			}
		}
	}

	private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
	{
		var dx = x2 - x1;
		var dy = y2 - y1;
		var lengthSquared = dx * dx + dy * dy;
		if (lengthSquared <= double.Epsilon)
			return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));

		var t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0.0, 1.0);
		var nearestX = x1 + t * dx;
		var nearestY = y1 + t * dy;
		var deltaX = px - nearestX;
		var deltaY = py - nearestY;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static void Blend(Span<byte> pixels, int offset, byte red, byte green, byte blue, byte alpha, double coverage)
	{
		var sourceAlpha = coverage * alpha / 255.0;
		var inverse = 1.0 - sourceAlpha;
		pixels[offset] = (byte)Math.Clamp((int)Math.Round(red * sourceAlpha + pixels[offset] * inverse), 0, 255);
		pixels[offset + 1] = (byte)Math.Clamp((int)Math.Round(green * sourceAlpha + pixels[offset + 1] * inverse), 0, 255);
		pixels[offset + 2] = (byte)Math.Clamp((int)Math.Round(blue * sourceAlpha + pixels[offset + 2] * inverse), 0, 255);
		pixels[offset + 3] = 255;
	}
}
