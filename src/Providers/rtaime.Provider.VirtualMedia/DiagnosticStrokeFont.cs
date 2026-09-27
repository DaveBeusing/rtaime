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
		['-'] = ["0363"], ['/'] = ["0670"], ['.'] = ["6656"], [':'] = ["2525","5555"],
		['0'] = ["00104050601600"], ['1'] = ["102030405060","0010","5060"], ['2'] = ["00104030205060"], ['3'] = ["00104030405060"],
		['4'] = ["004030","40102030405060"], ['5'] = ["40100030405060"], ['6'] = ["40100060504030"], ['7'] = ["0010405060"],
		['8'] = ["001040506000","003040"], ['9'] = ["40300010405060"],
		['A'] = ["6000104060","2030"], ['B'] = ["006040301000","003040","30405060"], ['C'] = ["4010006050"],
		['D'] = ["006050401000"], ['E'] = ["40100060","0030"], ['F'] = ["401000","0030"], ['G'] = ["4010006050403035"],
		['H'] = ["0060","4060","2030"], ['I'] = ["0040","2060","5060"], ['J'] = ["40105060"], ['K'] = ["0060","203040","304060"],
		['L'] = ["0060"], ['M'] = ["6000204060"], ['N'] = ["60004060"], ['O'] = ["001040506000"], ['P'] = ["600010403020"],
		['Q'] = ["001040506000","304060"], ['R'] = ["600010403020","304060"], ['S'] = ["40100030405060"], ['T'] = ["0040","2060"],
		['U'] = ["00506040"], ['V'] = ["00206040"], ['W'] = ["0060204060"], ['X'] = ["0060","4060"], ['Y'] = ["00203040","3060"],
		['Z'] = ["004060"],
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
