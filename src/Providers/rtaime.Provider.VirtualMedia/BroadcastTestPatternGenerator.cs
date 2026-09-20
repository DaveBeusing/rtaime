// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Media.Contracts;

namespace rtaime.Provider.VirtualMedia;

public sealed record BroadcastTestPatternConfiguration
{
	public BroadcastTestPatternConfiguration(
		VideoFormat format,
		bool showActionSafe = true,
		bool showTitleSafe = true,
		string colorSpace = "UNVERIFIED")
	{
		if (format.PixelFormat != PixelFormat.Rgba8)
			throw new NotSupportedException("Broadcast test pattern generation currently supports RGBA8 only.");
		if (string.IsNullOrWhiteSpace(colorSpace))
			throw new ArgumentException("Color-space label is required.", nameof(colorSpace));

		Format = format;
		ShowActionSafe = showActionSafe;
		ShowTitleSafe = showTitleSafe;
		ColorSpace = colorSpace.Trim().ToUpperInvariant();
	}

	public VideoFormat Format { get; }
	public bool ShowActionSafe { get; }
	public bool ShowTitleSafe { get; }
	public string ColorSpace { get; }
}

public sealed record GeneratedVideoFrame(
	FrameDescriptor Descriptor,
	ReadOnlyMemory<byte> Pixels);

public sealed class VirtualGeneratedVideoSource
{
	private readonly VirtualSyntheticVideoSource _descriptorSource;
	private readonly ReadOnlyMemory<byte> _pixels;

	public VirtualGeneratedVideoSource(
		VirtualSyntheticVideoSource descriptorSource,
		ReadOnlyMemory<byte> pixels)
	{
		_descriptorSource = descriptorSource ?? throw new ArgumentNullException(nameof(descriptorSource));
		var expected = BroadcastTestPatternGenerator.RequiredByteLength(descriptorSource.Format);
		if (pixels.Length != expected)
			throw new ArgumentException("Generated source pixel payload does not match the source video format.", nameof(pixels));

		_pixels = pixels;
	}

	public MediaSourceId SourceId => _descriptorSource.SourceId;
	public string Name => _descriptorSource.Name;
	public VideoFormat Format => _descriptorSource.Format;
	public ReadOnlyMemory<byte> Pixels => _pixels;

	public GeneratedVideoFrame GenerateFrame(ulong sequenceNumber) =>
		new(_descriptorSource.GenerateFrame(sequenceNumber), _pixels);
}

public sealed class BroadcastTestPatternGenerator
{
	private static readonly Rgba Black = new(0, 0, 0);
	private static readonly Rgba NearBlack = new(16, 16, 16);
	private static readonly Rgba DarkPanel = new(12, 15, 20);
	private static readonly Rgba Grid = new(52, 58, 68);
	private static readonly Rgba White = new(235, 235, 235);
	private static readonly Rgba MidGray = new(128, 128, 128);
	private static readonly Rgba SignalAccent = new(0, 198, 255);
	private static readonly Rgba SafeAction = new(80, 190, 255);
	private static readonly Rgba SafeTitle = new(255, 196, 64);

	private static readonly Rgba[] ColorBars =
	[
		new(235, 235, 235),
		new(235, 235, 16),
		new(16, 235, 235),
		new(16, 235, 16),
		new(235, 16, 235),
		new(235, 16, 16),
		new(16, 16, 235)
	];

	private static readonly IReadOnlyDictionary<char, byte[]> Glyphs =
		new ReadOnlyDictionary<char, byte[]>(new Dictionary<char, byte[]>
		{
			[' '] = [0, 0, 0, 0, 0, 0, 0],
			['-'] = [0, 0, 0, 31, 0, 0, 0],
			['/'] = [1, 2, 4, 8, 16, 0, 0],
			['.'] = [0, 0, 0, 0, 0, 12, 12],
			[':'] = [0, 12, 12, 0, 12, 12, 0],
			['0'] = [14, 17, 19, 21, 25, 17, 14],
			['1'] = [4, 12, 4, 4, 4, 4, 14],
			['2'] = [14, 17, 1, 2, 4, 8, 31],
			['3'] = [30, 1, 1, 14, 1, 1, 30],
			['4'] = [2, 6, 10, 18, 31, 2, 2],
			['5'] = [31, 16, 16, 30, 1, 1, 30],
			['6'] = [14, 16, 16, 30, 17, 17, 14],
			['7'] = [31, 1, 2, 4, 8, 8, 8],
			['8'] = [14, 17, 17, 14, 17, 17, 14],
			['9'] = [14, 17, 17, 15, 1, 1, 14],
			['A'] = [14, 17, 17, 31, 17, 17, 17],
			['B'] = [30, 17, 17, 30, 17, 17, 30],
			['C'] = [14, 17, 16, 16, 16, 17, 14],
			['D'] = [30, 17, 17, 17, 17, 17, 30],
			['E'] = [31, 16, 16, 30, 16, 16, 31],
			['F'] = [31, 16, 16, 30, 16, 16, 16],
			['G'] = [14, 17, 16, 23, 17, 17, 15],
			['H'] = [17, 17, 17, 31, 17, 17, 17],
			['I'] = [14, 4, 4, 4, 4, 4, 14],
			['J'] = [7, 2, 2, 2, 18, 18, 12],
			['K'] = [17, 18, 20, 24, 20, 18, 17],
			['L'] = [16, 16, 16, 16, 16, 16, 31],
			['M'] = [17, 27, 21, 21, 17, 17, 17],
			['N'] = [17, 25, 21, 19, 17, 17, 17],
			['O'] = [14, 17, 17, 17, 17, 17, 14],
			['P'] = [30, 17, 17, 30, 16, 16, 16],
			['Q'] = [14, 17, 17, 17, 21, 18, 13],
			['R'] = [30, 17, 17, 30, 20, 18, 17],
			['S'] = [15, 16, 16, 14, 1, 1, 30],
			['T'] = [31, 4, 4, 4, 4, 4, 4],
			['U'] = [17, 17, 17, 17, 17, 17, 14],
			['V'] = [17, 17, 17, 17, 17, 10, 4],
			['W'] = [17, 17, 17, 21, 21, 21, 10],
			['X'] = [17, 17, 10, 4, 10, 17, 17],
			['Y'] = [17, 17, 10, 4, 4, 4, 4],
			['Z'] = [31, 1, 2, 4, 8, 16, 31]
		});

	private readonly byte[] _pixels;

	public BroadcastTestPatternGenerator(BroadcastTestPatternConfiguration configuration)
	{
		Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		_pixels = new byte[RequiredByteLength(configuration.Format)];
		Render();
	}

	public BroadcastTestPatternConfiguration Configuration { get; }
	public ReadOnlyMemory<byte> Pixels => _pixels;

	public static int RequiredByteLength(VideoFormat format) =>
		checked((int)((ulong)format.Width * format.Height * 4UL));

	public (byte Red, byte Green, byte Blue, byte Alpha) GetPixel(uint x, uint y)
	{
		if (x >= Configuration.Format.Width)
			throw new ArgumentOutOfRangeException(nameof(x));
		if (y >= Configuration.Format.Height)
			throw new ArgumentOutOfRangeException(nameof(y));

		var offset = PixelOffset(x, y);
		return (_pixels[offset], _pixels[offset + 1], _pixels[offset + 2], _pixels[offset + 3]);
	}

	private void Render()
	{
		var width = checked((int)Configuration.Format.Width);
		var height = checked((int)Configuration.Format.Height);

		FillRect(0, 0, width, height, DarkPanel);

		var headerHeight = Math.Max(52, height * 7 / 100);
		var barsTop = headerHeight;
		var barsHeight = Math.Max(1, height * 37 / 100);
		var grayscaleTop = barsTop + barsHeight;
		var grayscaleHeight = Math.Max(1, height * 10 / 100);
		var rampTop = grayscaleTop + grayscaleHeight;
		var rampHeight = Math.Max(1, height * 10 / 100);
		var patchesTop = rampTop + rampHeight;
		var patchesHeight = Math.Max(1, height * 10 / 100);
		var infoTop = Math.Min(height - 1, patchesTop + patchesHeight);

		DrawHeader(width, headerHeight);
		DrawColorBars(width, barsTop, barsHeight);
		DrawGrayscaleSteps(width, grayscaleTop, grayscaleHeight);
		DrawLumaRamp(width, rampTop, rampHeight);
		DrawReferencePatches(width, patchesTop, patchesHeight);
		DrawInfoPanel(width, height, infoTop);
		DrawGeometry(width, height, barsTop, infoTop);
		DrawCenterMarker(width, height);
		DrawSafeMarkers(width, height);
		DrawBorder(width, height);
	}

	private void DrawHeader(int width, int height)
	{
		FillRect(0, 0, width, height, Black);
		var signalText = "INTERNAL TEST SIGNAL";
		var signalScale = Math.Max(2, height / 18);
		DrawText(
			signalText,
			Math.Max(12, width / 40),
			Math.Max(8, (height - 7 * signalScale) / 2),
			signalScale,
			White);

		var brand = "rtaime";
		var brandScale = Math.Max(2, height / 18);
		var brandWidth = MeasureText(brand, brandScale);
		DrawText(
			brand,
			Math.Max(12, width - brandWidth - width / 40),
			Math.Max(8, (height - 7 * brandScale) / 2),
			brandScale,
			SignalAccent);
	}

	private void DrawColorBars(int width, int top, int height)
	{
		for (var i = 0; i < ColorBars.Length; i++)
		{
			var left = i * width / ColorBars.Length;
			var right = (i + 1) * width / ColorBars.Length;
			FillRect(left, top, right - left, height, ColorBars[i]);
		}
	}

	private void DrawGrayscaleSteps(int width, int top, int height)
	{
		const int stepCount = 16;
		for (var i = 0; i < stepCount; i++)
		{
			var value = (byte)Math.Round(i * 255d / (stepCount - 1));
			var left = i * width / stepCount;
			var right = (i + 1) * width / stepCount;
			FillRect(left, top, right - left, height, new Rgba(value, value, value));
		}
	}

	private void DrawLumaRamp(int width, int top, int height)
	{
		for (var x = 0; x < width; x++)
		{
			var value = width <= 1
				? (byte)0
				: (byte)Math.Round(x * 255d / (width - 1));
			FillRect(x, top, 1, height, new Rgba(value, value, value));
		}

		var labelScale = Math.Max(1, height / 15);
		DrawText("LUMA 0-255", Math.Max(8, width / 80), top + Math.Max(5, height / 10), labelScale, SignalAccent);
	}

	private void DrawReferencePatches(int width, int top, int height)
	{
		var colors = new[]
		{
			Black,
			NearBlack,
			new Rgba(32, 32, 32),
			new Rgba(64, 64, 64),
			MidGray,
			new Rgba(192, 192, 192),
			White
		};

		for (var i = 0; i < colors.Length; i++)
		{
			var left = i * width / colors.Length;
			var right = (i + 1) * width / colors.Length;
			FillRect(left, top, right - left, height, colors[i]);
		}
	}

	private void DrawInfoPanel(int width, int height, int top)
	{
		if (top >= height)
			return;

		FillRect(0, top, width, height - top, DarkPanel);
		var format = Configuration.Format;
		var marginX = Math.Max(12, width / 40);
		var marginY = Math.Max(8, (height - top) / 10);
		var availableHeight = Math.Max(7, height - top - marginY * 2);
		var scale = Math.Max(1, Math.Min(width / 560, availableHeight / 22));

		var line1 = $"{format.Width}X{format.Height}  {format.FrameRate}  {format.ScanMode}";
		var line2 = $"PIXEL {format.PixelFormat}  8 BIT/CHANNEL  COLOR {Configuration.ColorSpace}";
		DrawText(line1, marginX, top + marginY, scale, White);
		DrawText(line2, marginX, top + marginY + 9 * scale, scale, new Rgba(180, 188, 202));

		var rightText = "REAL TIME AI MEDIA ENGINE";
		var rightWidth = MeasureText(rightText, scale);
		DrawText(
			rightText,
			Math.Max(marginX, width - rightWidth - marginX),
			top + marginY + 9 * scale,
			scale,
			SignalAccent);
	}

	private void DrawGeometry(int width, int height, int top, int bottom)
	{
		if (bottom <= top)
			return;

		var verticalSpacing = Math.Max(32, width / 12);
		var horizontalSpacing = Math.Max(24, (bottom - top) / 8);
		for (var x = verticalSpacing; x < width; x += verticalSpacing)
			DrawVerticalLine(x, top, bottom - 1, Grid);
		for (var y = top + horizontalSpacing; y < bottom; y += horizontalSpacing)
			DrawHorizontalLine(0, width - 1, y, Grid);

		var square = Math.Max(24, Math.Min(width, height) / 18);
		var inset = Math.Max(12, square / 3);
		DrawRectOutline(inset, top + inset, square, square, White);
		DrawRectOutline(width - inset - square, top + inset, square, square, White);
	}

	private void DrawCenterMarker(int width, int height)
	{
		var centerX = width / 2;
		var centerY = height / 2;
		var radius = Math.Max(12, Math.Min(width, height) / 36);
		DrawHorizontalLine(centerX - radius, centerX + radius, centerY, White);
		DrawVerticalLine(centerX, centerY - radius, centerY + radius, White);
		DrawRectOutline(centerX - radius, centerY - radius, radius * 2 + 1, radius * 2 + 1, White);
	}

	private void DrawSafeMarkers(int width, int height)
	{
		if (Configuration.ShowActionSafe)
		{
			var insetX = width / 20;
			var insetY = height / 20;
			DrawRectOutline(insetX, insetY, width - insetX * 2, height - insetY * 2, SafeAction);
		}

		if (Configuration.ShowTitleSafe)
		{
			var insetX = width / 10;
			var insetY = height / 10;
			DrawRectOutline(insetX, insetY, width - insetX * 2, height - insetY * 2, SafeTitle);
		}
	}

	private void DrawBorder(int width, int height)
	{
		DrawRectOutline(0, 0, width, height, White);
	}

	private void DrawText(string text, int x, int y, int scale, Rgba color)
	{
		if (string.IsNullOrEmpty(text) || scale <= 0)
			return;

		var cursor = x;
		foreach (var raw in text)
		{
			var character = char.ToUpperInvariant(raw);
			if (!Glyphs.TryGetValue(character, out var rows))
				rows = Glyphs[' '];

			for (var row = 0; row < 7; row++)
			{
				for (var column = 0; column < 5; column++)
				{
					if ((rows[row] & (1 << (4 - column))) == 0)
						continue;

					FillRect(cursor + column * scale, y + row * scale, scale, scale, color);
				}
			}

			cursor += 6 * scale;
		}
	}

	private static int MeasureText(string text, int scale) =>
		string.IsNullOrEmpty(text) ? 0 : Math.Max(0, text.Length * 6 * scale - scale);

	private void DrawRectOutline(int x, int y, int width, int height, Rgba color)
	{
		if (width <= 0 || height <= 0)
			return;

		DrawHorizontalLine(x, x + width - 1, y, color);
		DrawHorizontalLine(x, x + width - 1, y + height - 1, color);
		DrawVerticalLine(x, y, y + height - 1, color);
		DrawVerticalLine(x + width - 1, y, y + height - 1, color);
	}

	private void DrawHorizontalLine(int x1, int x2, int y, Rgba color)
	{
		if (y < 0 || y >= (int)Configuration.Format.Height)
			return;

		var start = Math.Max(0, Math.Min(x1, x2));
		var end = Math.Min((int)Configuration.Format.Width - 1, Math.Max(x1, x2));
		for (var x = start; x <= end; x++)
			SetPixel(x, y, color);
	}

	private void DrawVerticalLine(int x, int y1, int y2, Rgba color)
	{
		if (x < 0 || x >= (int)Configuration.Format.Width)
			return;

		var start = Math.Max(0, Math.Min(y1, y2));
		var end = Math.Min((int)Configuration.Format.Height - 1, Math.Max(y1, y2));
		for (var y = start; y <= end; y++)
			SetPixel(x, y, color);
	}

	private void FillRect(int x, int y, int width, int height, Rgba color)
	{
		if (width <= 0 || height <= 0)
			return;

		var left = Math.Max(0, x);
		var top = Math.Max(0, y);
		var right = Math.Min((int)Configuration.Format.Width, x + width);
		var bottom = Math.Min((int)Configuration.Format.Height, y + height);
		if (left >= right || top >= bottom)
			return;

		for (var row = top; row < bottom; row++)
		{
			for (var column = left; column < right; column++)
				SetPixel(column, row, color);
		}
	}

	private void SetPixel(int x, int y, Rgba color)
	{
		var offset = checked(((y * (int)Configuration.Format.Width) + x) * 4);
		_pixels[offset] = color.Red;
		_pixels[offset + 1] = color.Green;
		_pixels[offset + 2] = color.Blue;
		_pixels[offset + 3] = color.Alpha;
	}

	private int PixelOffset(uint x, uint y) =>
		checked((int)(((ulong)y * Configuration.Format.Width + x) * 4UL));

	private readonly record struct Rgba(byte Red, byte Green, byte Blue, byte Alpha = 255);
}
