// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaPixelInspectionTests
{
	[Theory]
	[InlineData(400, 225, 1.0, 0, 0, 960, 540)]
	[InlineData(400, 225, 1.25, 0, 0, 960, 540)]
	public void Fit_center_maps_to_source_center(double xDip, double yDip, double dpi, double panX, double panY, int expectedX, int expectedY)
	{
		var viewport = MediaPresentationGeometry.ToPhysicalPixels(800, 450, dpi, dpi);
		var rect = MediaPresentationGeometry.Calculate(1920, 1080, viewport.Width, viewport.Height, MediaPresentationMode.Fit, panX: panX, panY: panY);
		Assert.True(MediaPixelInspection.TryMapViewportToSource(xDip, yDip, dpi, dpi, rect, 1920, 1080, out var coordinate));
		Assert.Equal(expectedX, coordinate.X);
		Assert.Equal(expectedY, coordinate.Y);
	}

	[Fact]
	public void Fill_crop_maps_visible_left_edge_into_source()
	{
		var rect = MediaPresentationGeometry.Calculate(1920, 1080, 800, 800, MediaPresentationMode.Fill);
		Assert.True(MediaPixelInspection.TryMapViewportToSource(0, 400, 1, 1, rect, 1920, 1080, out var coordinate));
		Assert.Equal(420, coordinate.X);
		Assert.Equal(540, coordinate.Y);
	}

	[Fact]
	public void Pixel_perfect_with_pan_maps_exact_source_pixel()
	{
		var rect = MediaPresentationGeometry.Calculate(1920, 1080, 1200, 675, MediaPresentationMode.PixelPerfect, panX: 100, panY: 50);
		Assert.True(MediaPixelInspection.TryMapViewportToSource(500, 300, 1, 1, rect, 1920, 1080, out var coordinate));
		Assert.Equal((int)Math.Floor((500 - rect.X) / rect.Scale), coordinate.X);
		Assert.Equal((int)Math.Floor((300 - rect.Y) / rect.Scale), coordinate.Y);
	}

	[Fact]
	public void Outside_image_is_invalid()
	{
		var rect = MediaPresentationGeometry.Calculate(1920, 1080, 800, 800, MediaPresentationMode.Fit);
		Assert.False(MediaPixelInspection.TryMapViewportToSource(400, 20, 1, 1, rect, 1920, 1080, out _));
	}

	[Fact]
	public void Pixel_grid_is_reserved_for_high_zoom()
	{
		Assert.Equal(8.0, MediaPixelInspection.PixelGridMinimumScale);
	}
}
