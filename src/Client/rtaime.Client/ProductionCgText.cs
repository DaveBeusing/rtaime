// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum OperatorCgTextAlignment
{
	Left = 1,
	Center = 2,
	Right = 3
}

public enum OperatorCgAnchor
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

public enum OperatorCgLayer
{
	ProgramGraphics = 1
}

public readonly record struct OperatorCgColor(byte Red, byte Green, byte Blue, byte Alpha);

public sealed record OperatorCgPanelStyle(
	bool Enabled,
	OperatorCgColor Color,
	float CornerRadiusPixels,
	uint PaddingPixels);

public sealed record OperatorProductionCgText
{
	public OperatorProductionCgText(
		string text,
		string typeface,
		string? fallbackTypeface,
		float fontSizePixels,
		OperatorCgColor foreground,
		double positionX,
		double positionY,
		uint boxWidth,
		uint boxHeight,
		OperatorCgTextAlignment alignment,
		OperatorCgAnchor anchor,
		OperatorCgPanelStyle panel,
		bool visible = true,
		OperatorCgLayer layer = OperatorCgLayer.ProgramGraphics,
		int zOrder = 0)
	{
		if (string.IsNullOrWhiteSpace(text) || text.Length > 512 || text.Contains('\0'))
			throw new ArgumentException("Production CG text must contain 1..512 valid characters.", nameof(text));
		if (string.IsNullOrWhiteSpace(typeface) || typeface.Length > 128)
			throw new ArgumentException("Production CG typeface is required and must not exceed 128 characters.", nameof(typeface));
		if (fallbackTypeface is { Length: > 128 })
			throw new ArgumentException("Production CG fallback typeface must not exceed 128 characters.", nameof(fallbackTypeface));
		if (!float.IsFinite(fontSizePixels) || fontSizePixels is < 8 or > 256)
			throw new ArgumentOutOfRangeException(nameof(fontSizePixels));
		if (!double.IsFinite(positionX) || positionX is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(positionX));
		if (!double.IsFinite(positionY) || positionY is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(positionY));
		if (boxWidth is < 16 or > 1600 || boxHeight is < 16 or > 512)
			throw new ArgumentOutOfRangeException(nameof(boxWidth), "Production CG bounding box must be within 16..1600 by 16..512 pixels.");
		if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
		if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
		if (!Enum.IsDefined(layer) || layer != OperatorCgLayer.ProgramGraphics || zOrder != 0)
			throw new NotSupportedException("The qualified Production CG path supports only the Program Graphics layer at Z-order 0.");
		if (!float.IsFinite(panel.CornerRadiusPixels) || panel.CornerRadiusPixels is < 0 or > 64)
			throw new ArgumentOutOfRangeException(nameof(panel));
		if (panel.PaddingPixels * 2 >= boxWidth || panel.PaddingPixels * 2 >= boxHeight)
			throw new ArgumentOutOfRangeException(nameof(panel), "Production CG panel padding must leave a non-empty text area.");

		Text = text;
		Typeface = typeface.Trim();
		FallbackTypeface = string.IsNullOrWhiteSpace(fallbackTypeface) ? null : fallbackTypeface.Trim();
		FontSizePixels = fontSizePixels;
		Foreground = foreground;
		PositionX = positionX;
		PositionY = positionY;
		BoxWidth = boxWidth;
		BoxHeight = boxHeight;
		Alignment = alignment;
		Anchor = anchor;
		Panel = panel;
		Visible = visible;
		Layer = layer;
		ZOrder = zOrder;
	}

	public string Text { get; }
	public string Typeface { get; }
	public string? FallbackTypeface { get; }
	public float FontSizePixels { get; }
	public OperatorCgColor Foreground { get; }
	public double PositionX { get; }
	public double PositionY { get; }
	public uint BoxWidth { get; }
	public uint BoxHeight { get; }
	public OperatorCgTextAlignment Alignment { get; }
	public OperatorCgAnchor Anchor { get; }
	public OperatorCgPanelStyle Panel { get; }
	public bool Visible { get; }
	public OperatorCgLayer Layer { get; }
	public int ZOrder { get; }

	public static OperatorProductionCgText LowerThird(string text, bool visible = true) => new(
		text,
		"Segoe UI",
		"Arial",
		54,
		new OperatorCgColor(255, 255, 255, 255),
		0.05,
		0.91,
		900,
		144,
		OperatorCgTextAlignment.Left,
		OperatorCgAnchor.BottomLeft,
		new OperatorCgPanelStyle(true, new OperatorCgColor(18, 23, 32, 224), 14, 28),
		visible);
}

public sealed record OperatorProductionCgTextDescriptor(
	bool Active,
	string? Text,
	string? Typeface,
	string? ResolvedTypeface,
	float FontSizePixels,
	uint BoxWidth,
	uint BoxHeight,
	OperatorCgTextAlignment Alignment,
	OperatorCgAnchor Anchor,
	bool PanelEnabled,
	bool Visible,
	OperatorCgLayer Layer,
	int ZOrder,
	bool CacheHit,
	TimeSpan RenderDuration)
{
	public static OperatorProductionCgTextDescriptor Empty { get; } = new(
		false,
		null,
		null,
		null,
		0,
		0,
		0,
		OperatorCgTextAlignment.Left,
		OperatorCgAnchor.TopLeft,
		false,
		false,
		OperatorCgLayer.ProgramGraphics,
		0,
		false,
		TimeSpan.Zero);
}
