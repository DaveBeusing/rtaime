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
	[Theory]
	[InlineData(1280, 720, 1.0, 1280, 720)]
	[InlineData(1536, 864, 1.25, 1920, 1080)]
	[InlineData(1280, 720, 1.5, 1920, 1080)]
	[InlineData(960, 540, 2.0, 1920, 1080)]
	public void Render_target_uses_physical_viewport_pixels(double widthDip, double heightDip, double dpi, int expectedWidth, int expectedHeight)
	{
		var target = MediaRenderTarget.Create(widthDip, heightDip, dpi, dpi);
		Assert.Equal(expectedWidth, target.PixelWidth);
		Assert.Equal(expectedHeight, target.PixelHeight);
		Assert.Equal(MediaScalingQuality.Bicubic, target.Quality);
		Assert.Equal(MediaScalingPath.WpfFallback, target.Path);
		Assert.True(target.RequiresPresentationRescale);
	}

	[Fact]
	public void Render_target_rounds_fractional_physical_extents_deterministically()
	{
		var target = MediaRenderTarget.Create(801, 451, 1.25, 1.25);
		Assert.Equal(1001, target.PixelWidth);
		Assert.Equal(564, target.PixelHeight);
	}

	[Fact]
	public void Resize_stabilizer_keeps_active_target_until_requested_size_settles()
	{
		var stabilizer = new MediaRenderTargetStabilizer(TimeSpan.FromMilliseconds(75));
		var now = DateTimeOffset.UnixEpoch;
		var initial = MediaRenderTarget.Create(800, 450, 1, 1);
		var resizing = MediaRenderTarget.Create(1000, 562.5, 1, 1);
		Assert.Equal(initial, stabilizer.Adopt(initial, now));
		Assert.Equal(initial, stabilizer.Adopt(resizing, now.AddMilliseconds(20)));
		Assert.True(stabilizer.ResizePending);
		Assert.Equal(initial, stabilizer.Adopt(resizing, now.AddMilliseconds(80)));
		Assert.Equal(resizing, stabilizer.Adopt(resizing, now.AddMilliseconds(96)));
		Assert.False(stabilizer.ResizePending);
		Assert.Equal(2, stabilizer.Revision);
	}

	[Fact]
	public void Stable_target_does_not_churn_revision()
	{
		var stabilizer = new MediaRenderTargetStabilizer();
		var target = MediaRenderTarget.Create(1920, 1080, 1.5, 1.5);
		var now = DateTimeOffset.UnixEpoch;
		stabilizer.Adopt(target, now);
		for (var i = 0; i < 120; i++)
			Assert.Equal(target, stabilizer.Adopt(target, now.AddMilliseconds(i * 16)));
		Assert.Equal(1, stabilizer.Revision);
		Assert.False(stabilizer.ResizePending);
	}
}
