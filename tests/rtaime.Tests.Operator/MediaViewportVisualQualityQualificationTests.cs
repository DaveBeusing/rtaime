// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaViewportVisualQualityQualificationTests
{
	public static IEnumerable<object[]> PresentationMatrix()
	{
		yield return ["720p-fit-100", 1280d, 720d, 640d, 360d, 1.00, MediaPresentationMode.Fit, 1.0];
		yield return ["1080p-fit-125", 1920d, 1080d, 960d, 540d, 1.25, MediaPresentationMode.Fit, 1.0];
		yield return ["4k-fit-150", 3840d, 2160d, 1280d, 720d, 1.50, MediaPresentationMode.Fit, 1.0];
		yield return ["8k-fit-175", 7680d, 4320d, 960d, 540d, 1.75, MediaPresentationMode.Fit, 1.0];
		yield return ["4k-fit-200", 3840d, 2160d, 960d, 540d, 2.00, MediaPresentationMode.Fit, 1.0];
		yield return ["4x3-fill", 1440d, 1080d, 1280d, 720d, 1.00, MediaPresentationMode.Fill, 1.0];
		yield return ["portrait-fit", 1080d, 1920d, 900d, 600d, 1.25, MediaPresentationMode.Fit, 1.0];
		yield return ["square-fit", 2048d, 2048d, 1000d, 600d, 1.50, MediaPresentationMode.Fit, 1.0];
		yield return ["nonstandard-fit", 2048d, 858d, 1100d, 700d, 1.75, MediaPresentationMode.Fit, 1.0];
		yield return ["native-100", 1920d, 1080d, 1920d, 1080d, 1.00, MediaPresentationMode.PixelPerfect, 1.0];
		yield return ["zoom-200", 1920d, 1080d, 1280d, 720d, 1.00, MediaPresentationMode.CustomZoom, 2.0];
	}

	[Theory]
	[MemberData(nameof(PresentationMatrix))]
	public void Matrix_produces_finite_aspect_correct_physical_geometry(
		string caseName, double sourceWidth, double sourceHeight,
		double viewportWidthDip, double viewportHeightDip, double dpi,
		MediaPresentationMode mode, double zoom)
	{
		var physical = MediaPresentationGeometry.ToPhysicalPixels(viewportWidthDip, viewportHeightDip, dpi, dpi);
		var result = MediaPresentationGeometry.Calculate(
			sourceWidth, sourceHeight, physical.Width, physical.Height, mode, zoom);

		Assert.True(result.Width > 0 && result.Height > 0, $"{caseName}: empty destination.");
		Assert.True(double.IsFinite(result.X) && double.IsFinite(result.Y), $"{caseName}: invalid origin.");
		Assert.Equal(sourceWidth / sourceHeight, result.Width / result.Height, 8);

		if (mode == MediaPresentationMode.Fit)
		{
			Assert.True(result.Width <= physical.Width + 0.001, $"{caseName}: Fit width overflow.");
			Assert.True(result.Height <= physical.Height + 0.001, $"{caseName}: Fit height overflow.");
		}
		else if (mode == MediaPresentationMode.Fill)
		{
			Assert.True(result.Width + 0.001 >= physical.Width, $"{caseName}: Fill width underflow.");
			Assert.True(result.Height + 0.001 >= physical.Height, $"{caseName}: Fill height underflow.");
			Assert.Equal((physical.Width - result.Width) / 2.0, result.X, 6);
			Assert.Equal((physical.Height - result.Height) / 2.0, result.Y, 6);
		}
	}

	[Theory]
	[InlineData(1.00)]
	[InlineData(1.25)]
	[InlineData(1.50)]
	[InlineData(1.75)]
	[InlineData(2.00)]
	public void Pixel_perfect_maps_source_pixels_one_to_one_at_every_qualified_dpi(double dpi)
	{
		var physical = MediaPresentationGeometry.ToPhysicalPixels(1280, 720, dpi, dpi);
		var result = MediaPresentationGeometry.Calculate(3840, 2160, physical.Width, physical.Height, MediaPresentationMode.PixelPerfect);
		Assert.Equal(1.0, result.Scale);
		Assert.Equal(3840, result.Width);
		Assert.Equal(2160, result.Height);

		const int sourceX = 1733;
		const int sourceY = 911;
		Assert.Equal(result.X + sourceX, result.X + sourceX * result.Scale, 10);
		Assert.Equal(result.Y + sourceY, result.Y + sourceY * result.Scale, 10);
	}

	[Fact]
	public void One_physical_pixel_target_error_is_detectable()
	{
		var expected = MediaPresentationGeometry.ToPhysicalPixels(801, 451, 1.25, 1.25);
		var wrongWidth = expected.Width + 1;
		Assert.NotEqual(expected.Width, wrongWidth);
		Assert.Equal(1, Math.Abs(wrongWidth - expected.Width));
	}

	[Fact]
	public void A_second_scaling_stage_is_detectable_at_pixel_perfect()
	{
		var result = MediaPresentationGeometry.Calculate(3840, 2160, 1920, 1080, MediaPresentationMode.PixelPerfect);
		const double accidentalSecondScale = 0.5;
		Assert.Equal(1.0, result.Scale);
		Assert.NotEqual(result.Width, result.Width * accidentalSecondScale);
		Assert.NotEqual(result.Height, result.Height * accidentalSecondScale);
	}

	[Fact]
	public void Rapid_resize_and_dpi_transition_converges_without_target_churn()
	{
		var stabilizer = new MediaRenderTargetStabilizer(TimeSpan.FromMilliseconds(75));
		var now = DateTimeOffset.UnixEpoch;
		var initial = MediaRenderTarget.Create(1280, 720, 1.0, 1.0);
		stabilizer.Adopt(initial, now);

		for (var i = 1; i <= 40; i++)
		{
			var dpi = (i % 5) switch { 0 => 1.0, 1 => 1.25, 2 => 1.5, 3 => 1.75, _ => 2.0 };
			var requested = MediaRenderTarget.Create(1280 + i, 720 + i / 2.0, dpi, dpi);
			stabilizer.Adopt(requested, now.AddMilliseconds(i * 10));
		}

		Assert.Equal(1, stabilizer.Revision);
		Assert.True(stabilizer.ResizePending);

		var settled = MediaRenderTarget.Create(1600, 900, 1.75, 1.75);
		var settleStart = now.AddSeconds(1);
		stabilizer.Adopt(settled, settleStart);
		stabilizer.Adopt(settled, settleStart.AddMilliseconds(80));
		var active = stabilizer.Adopt(settled, settleStart.AddMilliseconds(96));

		Assert.Equal(settled, active);
		Assert.Equal(2, stabilizer.Revision);
		Assert.False(stabilizer.ResizePending);
	}

	[Fact]
	public void Stable_playback_target_has_constant_resource_revision()
	{
		var stabilizer = new MediaRenderTargetStabilizer();
		var target = MediaRenderTarget.Create(1920, 1080, 1.5, 1.5);
		var now = DateTimeOffset.UnixEpoch;
		stabilizer.Adopt(target, now);

		for (var frame = 1; frame <= 3600; frame++)
			Assert.Equal(target, stabilizer.Adopt(target, now.AddMilliseconds(frame * (1000.0 / 60.0))));

		Assert.Equal(1, stabilizer.Revision);
		Assert.False(stabilizer.ResizePending);
	}

	[Fact]
	public void Productive_viewport_uses_single_high_quality_scaling_surface()
	{
		var root = FindRepositoryRoot();
		var theme = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "Themes", "Controls", "RtaimeMonitorWorkspace.xaml"));
		Assert.Contains("BitmapScalingMode=\"HighQuality\"", theme, StringComparison.Ordinal);
		Assert.Contains("SnapsToDevicePixels=\"True\"", theme, StringComparison.Ordinal);
		Assert.DoesNotContain("<Viewbox", theme, StringComparison.Ordinal);
		var document = XDocument.Parse(theme);
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "Image");
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "GpuMonitorPresentationSurface");
	}

	[Fact]
	public void Clean_program_reuses_program_gpu_frame_without_operator_overlays()
	{
		var root = FindRepositoryRoot();
		var xaml = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProgramOutputWindow.xaml"));
		var document = XDocument.Parse(xaml);
		var gpuSurface = Assert.Single(document.Descendants(), element => element.Name.LocalName == "GpuMonitorPresentationSurface");
		var gpuFrameAttribute = Assert.Single(gpuSurface.Attributes(), attribute => attribute.Name.LocalName == "GpuFrame");

		Assert.Contains("ProgramGpuFrame", gpuFrameAttribute.Value, StringComparison.Ordinal);
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "Image");
		Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName is "RtaimePixelGridOverlay" or "RtaimeMonitorPresentation");
		Assert.DoesNotContain("Diagnostics", xaml, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Gpu_monitor_surface_does_not_materialize_cpu_bitmaps()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "Controls", "GpuMonitorPresentationSurface.cs"));

		Assert.DoesNotContain("BitmapSource", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopyPixels", source, StringComparison.Ordinal);
		Assert.Contains("OpenSharedResource", source, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
	}
}
