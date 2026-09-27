// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaPresentationGeometryTests
{
	[Theory]
	[InlineData(1920, 1080, 960, 540, 0, 0, 960, 540, 0.5)]
	[InlineData(1920, 1080, 800, 800, 0, 175, 800, 450, 0.4166666667)]
	[InlineData(1080, 1920, 800, 450, 273.4375, 0, 253.125, 450, 0.234375)]
	[InlineData(1000, 1000, 800, 450, 175, 0, 450, 450, 0.45)]
	public void Fit_preserves_aspect_ratio_and_stays_inside_viewport(
		double sw, double sh, double vw, double vh,
		double x, double y, double width, double height, double scale)
	{
		var result = MediaPresentationGeometry.Calculate(sw, sh, vw, vh, MediaPresentationMode.Fit);
		Assert.Equal(x, result.X, 4);
		Assert.Equal(y, result.Y, 4);
		Assert.Equal(width, result.Width, 4);
		Assert.Equal(height, result.Height, 4);
		Assert.Equal(scale, result.Scale, 6);
		Assert.True(result.Width <= vw + 0.001);
		Assert.True(result.Height <= vh + 0.001);
		Assert.Equal(sw / sh, result.Width / result.Height, 6);
	}

	[Fact]
	public void Fill_preserves_aspect_ratio_and_crops_only_excess()
	{
		var result = MediaPresentationGeometry.Calculate(1920, 1080, 800, 800, MediaPresentationMode.Fill);
		Assert.Equal(800, result.Height, 4);
		Assert.True(result.Width > 800);
		Assert.Equal(1920.0 / 1080.0, result.Width / result.Height, 6);
		Assert.True(result.X < 0);
		Assert.Equal(0, result.Y, 4);
	}

	[Fact]
	public void Pixel_perfect_maps_one_source_pixel_to_one_physical_pixel()
	{
		var result = MediaPresentationGeometry.Calculate(1920, 1080, 1200, 675, MediaPresentationMode.PixelPerfect);
		Assert.Equal(1, result.Scale);
		Assert.Equal(1920, result.Width);
		Assert.Equal(1080, result.Height);
		Assert.Equal(-360, result.X);
		Assert.Equal(-202.5, result.Y);
	}

	[Theory]
	[InlineData(0.25, 480, 270)]
	[InlineData(0.5, 960, 540)]
	[InlineData(1.0, 1920, 1080)]
	[InlineData(2.0, 3840, 2160)]
	[InlineData(4.0, 7680, 4320)]
	public void Custom_zoom_uses_source_resolution_as_its_basis(double zoom, double width, double height)
	{
		var result = MediaPresentationGeometry.Calculate(1920, 1080, 1200, 675, MediaPresentationMode.CustomZoom, zoom);
		Assert.Equal(width, result.Width);
		Assert.Equal(height, result.Height);
		Assert.Equal(zoom, result.Scale);
	}

	[Theory]
	[InlineData(1.25, 1000, 562.5)]
	[InlineData(1.5, 1200, 675)]
	[InlineData(2.0, 1600, 900)]
	public void Dpi_conversion_resolves_physical_viewport(double dpi, double expectedWidth, double expectedHeight)
	{
		var physical = MediaPresentationGeometry.ToPhysicalPixels(800, 450, dpi, dpi);
		Assert.Equal(expectedWidth, physical.Width);
		Assert.Equal(expectedHeight, physical.Height);
	}

	[Fact]
	public void Pan_is_constrained_to_visible_content_bounds()
	{
		var bounds = MediaPresentationGeometry.CalculatePanBounds(1920, 1080, 1200, 675);
		Assert.Equal(-360, bounds.MinX);
		Assert.Equal(360, bounds.MaxX);
		Assert.Equal(-202.5, bounds.MinY);
		Assert.Equal(202.5, bounds.MaxY);

		var result = MediaPresentationGeometry.Calculate(
			1920, 1080, 1200, 675, MediaPresentationMode.PixelPerfect, panX: 5000, panY: -5000);
		Assert.Equal(0, result.X);
		Assert.Equal(-405, result.Y);
	}

	[Fact]
	public void Invalid_or_zero_extents_fail_closed()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			MediaPresentationGeometry.Calculate(0, 1080, 800, 450, MediaPresentationMode.Fit));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			MediaPresentationGeometry.ToPhysicalPixels(800, 450, 0, 1));
	}
}
