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
	public void Full_resolution_gpu_coordinate_maps_to_bounded_cpu_monitor_sample()
	{
		var center = MediaPixelInspection.MapSourceToMonitoringSample(
			new MediaPixelCoordinate(960, 540), 1920, 1080, 320, 180);
		var last = MediaPixelInspection.MapSourceToMonitoringSample(
			new MediaPixelCoordinate(1919, 1079), 1920, 1080, 320, 180);

		Assert.Equal(new MediaPixelCoordinate(160, 90), center);
		Assert.Equal(new MediaPixelCoordinate(319, 179), last);
	}

	[Fact]
	public void Roi_from_corners_is_source_aligned_and_inclusive()
	{
		var roi = MediaInspectionRoi.FromCorners(
			new MediaPixelCoordinate(900, 700),
			new MediaPixelCoordinate(100, 200),
			1920,
			1080);

		Assert.Equal(new MediaInspectionRoi(100, 200, 801, 501), roi);
	}

	[Fact]
	public void Roi_clamps_to_source_bounds_without_changing_coordinate_space()
	{
		var roi = new MediaInspectionRoi(-10, 100, 2000, 1200).Clamp(1920, 1080);

		Assert.Equal(new MediaInspectionRoi(0, 100, 1920, 980), roi);
	}

	[Fact]
	public void Roi_move_preserves_extent_and_clamps_to_source()
	{
		var roi = new MediaInspectionRoi(100, 100, 400, 300);

		Assert.Equal(new MediaInspectionRoi(1520, 780, 400, 300), roi.MoveBy(2000, 2000, 1920, 1080));
		Assert.Equal(new MediaInspectionRoi(0, 0, 400, 300), roi.MoveBy(-500, -500, 1920, 1080));
	}

	[Fact]
	public void Roi_corner_resize_keeps_opposite_corner_fixed()
	{
		var roi = new MediaInspectionRoi(100, 100, 401, 301);
		var resized = roi.ResizeFromCorner(
			MediaInspectionRoiCorner.TopLeft,
			new MediaPixelCoordinate(50, 75),
			1920,
			1080);

		Assert.Equal(new MediaInspectionRoi(50, 75, 451, 326), resized);
		Assert.Equal(500, resized.RightExclusive - 1);
		Assert.Equal(400, resized.BottomExclusive - 1);
	}

	[Fact]
	public void Roi_pointer_mapping_clamps_drag_outside_visible_source()
	{
		var rect = MediaPresentationGeometry.Calculate(1920, 1080, 800, 800, MediaPresentationMode.Fit);
		var coordinate = MediaPixelInspection.MapViewportToSourceClamped(
			-100,
			900,
			1,
			1,
			rect,
			1920,
			1080);

		Assert.Equal(new MediaPixelCoordinate(0, 1079), coordinate);
	}

	[Fact]
	public void Inspection_modes_define_one_combined_and_five_gpu_channel_views()
	{
		Assert.Equal(
			new[]
			{
				MediaInspectionChannel.Combined,
				MediaInspectionChannel.Red,
				MediaInspectionChannel.Green,
				MediaInspectionChannel.Blue,
				MediaInspectionChannel.Alpha,
				MediaInspectionChannel.Luma
			},
			Enum.GetValues<MediaInspectionChannel>());
	}

	[Fact]
	public void Pixel_grid_is_reserved_for_high_zoom()
	{
		Assert.Equal(8.0, MediaPixelInspection.PixelGridMinimumScale);
	}
}
