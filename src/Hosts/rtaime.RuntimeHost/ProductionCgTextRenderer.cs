// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace rtaime.RuntimeHost;

public enum V1CgTextAlignment
{
	Left = 1,
	Center = 2,
	Right = 3
}

public enum V1CgAnchor
{
	TopLeft = 1,
	TopCenter = 2,
	TopRight = 3,
	CenterLeft = 4,
	Center = 5,
	CenterRight = 6,
	BottomLeft = 7,
	BottomCenter = 8,
	BottomRight = 9
}

public enum V1CgLayer
{
	ProgramGraphics = 1
}

public readonly record struct V1CgColor(byte Red, byte Green, byte Blue, byte Alpha);

public sealed record V1CgPanelStyle(
	bool Enabled,
	V1CgColor Color,
	float CornerRadiusPixels,
	uint PaddingPixels);

public sealed record V1ProductionCgTextDefinition(
	string Text,
	string Typeface,
	string? FallbackTypeface,
	float FontSizePixels,
	V1CgColor Foreground,
	double PositionX,
	double PositionY,
	uint BoxWidth,
	uint BoxHeight,
	V1CgTextAlignment Alignment,
	V1CgAnchor Anchor,
	V1CgPanelStyle Panel,
	bool Visible,
	V1CgLayer Layer,
	int ZOrder);

public sealed record V1ProductionCgTextSnapshot(
	bool Active,
	string? Text,
	string? Typeface,
	string? ResolvedTypeface,
	float FontSizePixels,
	uint BoxWidth,
	uint BoxHeight,
	V1CgTextAlignment Alignment,
	V1CgAnchor Anchor,
	bool PanelEnabled,
	bool Visible,
	V1CgLayer Layer,
	int ZOrder,
	bool CacheHit,
	TimeSpan RenderDuration)
{
	public static V1ProductionCgTextSnapshot Empty { get; } = new(
		false,
		null,
		null,
		null,
		0,
		0,
		0,
		V1CgTextAlignment.Left,
		V1CgAnchor.TopLeft,
		false,
		false,
		V1CgLayer.ProgramGraphics,
		0,
		false,
		TimeSpan.Zero);
}

internal sealed record ProductionCgRenderResult(
	byte[] RgbaPixels,
	uint Width,
	uint Height,
	string ResolvedTypeface,
	bool CacheHit,
	TimeSpan RenderDuration);

internal sealed class ProductionCgTextRenderer
{
	private const int MaximumCachedSurfaces = 16;
	private readonly object _gate = new();
	private readonly Dictionary<CgRenderKey, LinkedListNode<CachedSurface>> _cache = [];
	private readonly LinkedList<CachedSurface> _lru = [];

	public int CachedSurfaceCount
	{
		get
		{
			lock (_gate)
				return _cache.Count;
		}
	}

	public ProductionCgRenderResult Render(V1ProductionCgTextDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		Validate(definition);
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Production CG text rendering requires the qualified Windows runtime.");

		var key = CgRenderKey.From(definition);
		var started = Stopwatch.GetTimestamp();
		lock (_gate)
		{
			if (_cache.TryGetValue(key, out var cached))
			{
				_lru.Remove(cached);
				_lru.AddFirst(cached);
				return new ProductionCgRenderResult(
					cached.Value.RgbaPixels,
					definition.BoxWidth,
					definition.BoxHeight,
					cached.Value.ResolvedTypeface,
					true,
					Stopwatch.GetElapsedTime(started));
			}
		}

		var rendered = RenderWindows(definition);
		var duration = Stopwatch.GetElapsedTime(started);
		lock (_gate)
		{
			if (_cache.TryGetValue(key, out var raced))
			{
				_lru.Remove(raced);
				_lru.AddFirst(raced);
				return new ProductionCgRenderResult(
					raced.Value.RgbaPixels,
					definition.BoxWidth,
					definition.BoxHeight,
					raced.Value.ResolvedTypeface,
					true,
					duration);
			}

			var entry = new CachedSurface(key, rendered.Pixels, rendered.ResolvedTypeface);
			var node = _lru.AddFirst(entry);
			_cache.Add(key, node);
			while (_cache.Count > MaximumCachedSurfaces)
			{
				var oldest = _lru.Last!;
				_lru.RemoveLast();
				_cache.Remove(oldest.Value.Key);
			}
		}

		return new ProductionCgRenderResult(
			rendered.Pixels,
			definition.BoxWidth,
			definition.BoxHeight,
			rendered.ResolvedTypeface,
			false,
			duration);
	}

	private static void Validate(V1ProductionCgTextDefinition definition)
	{
		if (string.IsNullOrWhiteSpace(definition.Text))
			throw new ArgumentException("Production CG text content is required.", nameof(definition));
		if (definition.Text.Length > 512 || definition.Text.Contains('\0'))
			throw new ArgumentException("Production CG text must contain at most 512 valid characters.", nameof(definition));
		if (string.IsNullOrWhiteSpace(definition.Typeface) || definition.Typeface.Length > 128)
			throw new ArgumentException("Production CG typeface is required and must not exceed 128 characters.", nameof(definition));
		if (definition.FallbackTypeface is { Length: > 128 })
			throw new ArgumentException("Production CG fallback typeface must not exceed 128 characters.", nameof(definition));
		if (!float.IsFinite(definition.FontSizePixels) || definition.FontSizePixels is < 8 or > 256)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG font size must be between 8 and 256 pixels.");
		if (!double.IsFinite(definition.PositionX) || definition.PositionX is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG X position must be normalized to 0..1.");
		if (!double.IsFinite(definition.PositionY) || definition.PositionY is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG Y position must be normalized to 0..1.");
		if (definition.BoxWidth is < 16 or > 1600 || definition.BoxHeight is < 16 or > 512)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG bounding box must be within 16..1600 by 16..512 pixels.");
		if (!Enum.IsDefined(definition.Alignment))
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG text alignment is invalid.");
		if (!Enum.IsDefined(definition.Anchor))
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG anchor is invalid.");
		if (definition.Layer != V1CgLayer.ProgramGraphics || definition.ZOrder != 0)
			throw new NotSupportedException("The qualified Production CG path supports only the Program Graphics layer at Z-order 0.");
		if (!float.IsFinite(definition.Panel.CornerRadiusPixels) || definition.Panel.CornerRadiusPixels is < 0 or > 64)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG panel corner radius must be between 0 and 64 pixels.");
		if (definition.Panel.PaddingPixels * 2 >= definition.BoxWidth || definition.Panel.PaddingPixels * 2 >= definition.BoxHeight)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG panel padding must leave a non-empty text area.");
	}

	[SupportedOSPlatform("windows")]
	private static RenderedSurface RenderWindows(V1ProductionCgTextDefinition definition)
	{
		using var installedFonts = new InstalledFontCollection();
		var family = ResolveTypeface(installedFonts, definition.Typeface, definition.FallbackTypeface);
		if (!family.IsStyleAvailable(FontStyle.Regular))
			throw new InvalidOperationException($"Production CG typeface '{family.Name}' does not provide a regular style.");

		using var bitmap = new Bitmap(
			checked((int)definition.BoxWidth),
			checked((int)definition.BoxHeight),
			DrawingPixelFormat.Format32bppArgb);
		using var graphics = Graphics.FromImage(bitmap);
		graphics.Clear(Color.Transparent);
		graphics.CompositingMode = CompositingMode.SourceOver;
		graphics.CompositingQuality = CompositingQuality.HighQuality;
		graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
		graphics.PixelOffsetMode = PixelOffsetMode.Half;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

		if (definition.Panel.Enabled)
		{
			using var panelBrush = new SolidBrush(ToDrawingColor(definition.Panel.Color));
			var panelRect = new RectangleF(0, 0, definition.BoxWidth, definition.BoxHeight);
			if (definition.Panel.CornerRadiusPixels <= 0)
			{
				graphics.FillRectangle(panelBrush, panelRect);
			}
			else
			{
				using var panelPath = RoundedRectangle(panelRect, definition.Panel.CornerRadiusPixels);
				graphics.FillPath(panelBrush, panelPath);
			}
		}

		using var font = new Font(family, definition.FontSizePixels, FontStyle.Regular, GraphicsUnit.Pixel);
		using var textBrush = new SolidBrush(ToDrawingColor(definition.Foreground));
		using var format = new StringFormat(StringFormat.GenericTypographic)
		{
			Alignment = definition.Alignment switch
			{
				V1CgTextAlignment.Left => StringAlignment.Near,
				V1CgTextAlignment.Center => StringAlignment.Center,
				V1CgTextAlignment.Right => StringAlignment.Far,
				_ => throw new InvalidOperationException("Unsupported Production CG alignment.")
			},
			LineAlignment = StringAlignment.Center,
			Trimming = StringTrimming.EllipsisCharacter,
			FormatFlags = StringFormatFlags.LineLimit
		};

		var padding = definition.Panel.PaddingPixels;
		var textRect = new RectangleF(
			padding,
			padding,
			definition.BoxWidth - (padding * 2),
			definition.BoxHeight - (padding * 2));
		graphics.DrawString(definition.Text, font, textBrush, textRect, format);
		graphics.Flush(FlushIntention.Sync);

		return new RenderedSurface(ReadRgba(bitmap), family.Name);
	}

	[SupportedOSPlatform("windows")]
	private static FontFamily ResolveTypeface(
		InstalledFontCollection installedFonts,
		string primary,
		string? fallback)
	{
		var family = installedFonts.Families.FirstOrDefault(
			candidate => string.Equals(candidate.Name, primary.Trim(), StringComparison.OrdinalIgnoreCase));
		if (family is not null)
			return family;

		if (!string.IsNullOrWhiteSpace(fallback))
		{
			family = installedFonts.Families.FirstOrDefault(
				candidate => string.Equals(candidate.Name, fallback.Trim(), StringComparison.OrdinalIgnoreCase));
			if (family is not null)
				return family;
		}

		var fallbackDetail = string.IsNullOrWhiteSpace(fallback)
			? "no explicit fallback was configured"
			: $"explicit fallback '{fallback.Trim()}' is also unavailable";
		throw new InvalidOperationException($"Production CG typeface '{primary.Trim()}' is unavailable and {fallbackDetail}.");
	}

	[SupportedOSPlatform("windows")]
	private static byte[] ReadRgba(Bitmap bitmap)
	{
		var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
		var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
		try
		{
			var rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
			var sourceRow = new byte[checked(Math.Abs(data.Stride))];
			for (var y = 0; y < bitmap.Height; y++)
			{
				var sourceY = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
				Marshal.Copy(IntPtr.Add(data.Scan0, checked(sourceY * Math.Abs(data.Stride))), sourceRow, 0, sourceRow.Length);
				var destinationOffset = checked(y * bitmap.Width * 4);
				for (var x = 0; x < bitmap.Width; x++)
				{
					var sourceOffset = x * 4;
					var targetOffset = destinationOffset + sourceOffset;
					rgba[targetOffset] = sourceRow[sourceOffset + 2];
					rgba[targetOffset + 1] = sourceRow[sourceOffset + 1];
					rgba[targetOffset + 2] = sourceRow[sourceOffset];
					rgba[targetOffset + 3] = sourceRow[sourceOffset + 3];
				}
			}
			return rgba;
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
	}

	private static Color ToDrawingColor(V1CgColor color) =>
		Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

	private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
	{
		var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
		var arc = new RectangleF(rectangle.X, rectangle.Y, diameter, diameter);
		var path = new GraphicsPath();
		path.AddArc(arc, 180, 90);
		arc.X = rectangle.Right - diameter;
		path.AddArc(arc, 270, 90);
		arc.Y = rectangle.Bottom - diameter;
		path.AddArc(arc, 0, 90);
		arc.X = rectangle.Left;
		path.AddArc(arc, 90, 90);
		path.CloseFigure();
		return path;
	}

	private sealed record CgRenderKey(
		string Text,
		string Typeface,
		string? FallbackTypeface,
		float FontSizePixels,
		V1CgColor Foreground,
		uint BoxWidth,
		uint BoxHeight,
		V1CgTextAlignment Alignment,
		V1CgPanelStyle Panel)
	{
		public static CgRenderKey From(V1ProductionCgTextDefinition definition) => new(
			definition.Text,
			definition.Typeface.Trim(),
			string.IsNullOrWhiteSpace(definition.FallbackTypeface) ? null : definition.FallbackTypeface.Trim(),
			definition.FontSizePixels,
			definition.Foreground,
			definition.BoxWidth,
			definition.BoxHeight,
			definition.Alignment,
			definition.Panel);
	}

	private sealed record CachedSurface(CgRenderKey Key, byte[] RgbaPixels, string ResolvedTypeface);
	private sealed record RenderedSurface(byte[] Pixels, string ResolvedTypeface);
}
