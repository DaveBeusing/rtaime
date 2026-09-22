// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class ProductionCgTextContractTests
{
	[Fact]
	public void Lower_third_factory_exposes_bounded_program_graphics_contract()
	{
		var definition = OperatorProductionCgText.LowerThird("RTAIME");

		Assert.Equal("RTAIME", definition.Text);
		Assert.Equal("Segoe UI", definition.Typeface);
		Assert.Equal("Arial", definition.FallbackTypeface);
		Assert.Equal(54, definition.FontSizePixels);
		Assert.Equal(900u, definition.BoxWidth);
		Assert.Equal(144u, definition.BoxHeight);
		Assert.Equal(OperatorCgTextAlignment.Left, definition.Alignment);
		Assert.Equal(OperatorCgAnchor.BottomLeft, definition.Anchor);
		Assert.True(definition.Panel.Enabled);
		Assert.True(definition.Visible);
		Assert.Equal(OperatorCgLayer.ProgramGraphics, definition.Layer);
		Assert.Equal(0, definition.ZOrder);
	}

	[Fact]
	public void Contract_rejects_invalid_text_font_geometry_and_layer()
	{
		Assert.Throws<ArgumentException>(() => Create(text: string.Empty));
		Assert.Throws<ArgumentException>(() => Create(typeface: string.Empty));
		Assert.Throws<ArgumentOutOfRangeException>(() => Create(fontSize: 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => Create(positionX: 1.1));
		Assert.Throws<ArgumentOutOfRangeException>(() => Create(boxWidth: 8));
		Assert.Throws<NotSupportedException>(() => Create(zOrder: 1));
	}

	[Fact]
	public void Explicit_fallback_is_optional_but_never_implicit_in_the_contract()
	{
		var definition = Create(fallbackTypeface: null);

		Assert.Null(definition.FallbackTypeface);
		Assert.Equal("Segoe UI", definition.Typeface);
	}

	private static OperatorProductionCgText Create(
		string text = "CG",
		string typeface = "Segoe UI",
		string? fallbackTypeface = "Arial",
		float fontSize = 48,
		double positionX = 0.05,
		uint boxWidth = 640,
		int zOrder = 0) =>
		new(
			text,
			typeface,
			fallbackTypeface,
			fontSize,
			new OperatorCgColor(255, 255, 255, 255),
			positionX,
			0.9,
			boxWidth,
			120,
			OperatorCgTextAlignment.Left,
			OperatorCgAnchor.BottomLeft,
			new OperatorCgPanelStyle(true, new OperatorCgColor(18, 23, 32, 224), 12, 24),
			true,
			OperatorCgLayer.ProgramGraphics,
			zOrder);
}
