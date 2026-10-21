// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
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
	public void Cpu_fallback_uses_exact_source_pixel_sampling_only_at_integral_zoom()
	{
		var root = FindRepositoryRoot();
		var theme = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator",
			"Themes", "Controls", "RtaimeMonitorWorkspace.xaml"));
		var document = XDocument.Parse(theme);
		var viewport = Assert.Single(document.Descendants(), element =>
			element.Name.LocalName == "Grid" &&
			(string?)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name") == "PART_MediaViewport");
		Assert.Equal("True", (string?)viewport.Attribute("ClipToBounds"));
		Assert.Equal("True", (string?)viewport.Attribute("UseLayoutRounding"));
		Assert.Equal("HighQuality", (string?)viewport.Attribute("RenderOptions.BitmapScalingMode"));

		var image = Assert.Single(viewport.Elements(), element => element.Name.LocalName == "Image");
		Assert.Equal("Fill", (string?)image.Attribute("Stretch"));
		Assert.Null(image.Attribute("RenderOptions.BitmapScalingMode"));
		var style = Assert.Single(image.Descendants(), element => element.Name.LocalName == "Style");
		Assert.Contains(style.Descendants(), element =>
			element.Name.LocalName == "Setter" &&
			(string?)element.Attribute("Property") == "RenderOptions.BitmapScalingMode" &&
			(string?)element.Attribute("Value") == "HighQuality");

		var integralZoom = style.Descendants()
			.Where(element => element.Name.LocalName == "DataTrigger" &&
				((string?)element.Attribute("Binding"))?.Contains("Monitor.ZoomMode", StringComparison.Ordinal) == true &&
				element.Elements().Any(setter =>
					setter.Name.LocalName == "Setter" &&
					(string?)setter.Attribute("Property") == "RenderOptions.BitmapScalingMode" &&
					(string?)setter.Attribute("Value") == "NearestNeighbor"))
			.Select(element => (string?)element.Attribute("Value"))
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(new[] { "100%", "200%", "400%", "800%" }, integralZoom);
		// Reduced-resolution Fit/Fill and fractional zoom must not claim native detail.
		Assert.DoesNotContain(integralZoom, value => value is "FIT" or "FILL" or "25%" or "50%" or "FREE");
	}


	[Theory]
	[InlineData(1.00)]
	[InlineData(1.25)]
	[InlineData(1.50)]
	[InlineData(1.75)]
	[InlineData(2.00)]
	public void Subpixel_layout_changes_reuse_allocated_gpu_pixels_but_dpi_transitions_do_not(double dpi)
	{
		var target = MediaRenderTarget.Create(800.1, 450.1, dpi, dpi, MediaScalingPath.GpuProvider);
		Assert.True(GpuMonitorPresentationSurface.MatchesViewportPixels(
			target.PixelWidth, target.PixelHeight, 800.1, 450.1, dpi, dpi));
		Assert.True(GpuMonitorPresentationSurface.MatchesViewportPixels(
			target.PixelWidth, target.PixelHeight, 800.1001, 450.1001, dpi, dpi));
		Assert.False(GpuMonitorPresentationSurface.MatchesViewportPixels(
			target.PixelWidth, target.PixelHeight, 802, 452, dpi, dpi));
		Assert.False(GpuMonitorPresentationSurface.MatchesViewportPixels(
			target.PixelWidth, target.PixelHeight, 800.1, 450.1, dpi + 0.25, dpi + 0.25));
		Assert.False(GpuMonitorPresentationSurface.MatchesViewportPixels(
			target.PixelWidth, target.PixelHeight, 0, 450.1, dpi, dpi));
	}

	[Fact]
	public void Shared_gpu_resource_reuse_is_fenced_by_full_identity_and_lifetime()
	{
		var provider = Identity.New();
		var resource = MonitoringResourceId.New();
		var surface = SurfaceId.New();
		var lease = new SurfaceLifetimeDescriptor(new Generation(7), resource.Value);
		MonitoringSharedResourceDescriptor Create(
			MonitoringResourceId id, Identity providerId, SurfaceId surfaceId,
			SurfaceLifetimeDescriptor lifetime, long adapter = 17, ulong handle = 0x1234) =>
			new(id, providerId, surfaceId, VideoFormat.Hd1080p50Rgba8,
				SurfaceStorageDomain.Shared, MonitoringResourceAccessMode.ReadOnly, lifetime,
				new MonitoringSharedResourceInteropDescriptor(
					MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle, adapter, handle));

		var first = Create(resource, provider, surface, lease);
		var unchangedNextFrame = Create(resource, provider, surface, lease);
		Assert.True(GpuMonitorPresentationSurface.SameSharedResource(first, unchangedNextFrame));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first, null));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(null, first));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first,
			Create(MonitoringResourceId.New(), provider, surface, lease)));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first,
			Create(resource, Identity.New(), surface, lease)));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first,
			Create(resource, provider, SurfaceId.New(), lease)));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first,
			Create(resource, provider, surface, new SurfaceLifetimeDescriptor(new Generation(8), resource.Value))));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first,
			Create(resource, provider, surface, lease, adapter: 18)));
		Assert.False(GpuMonitorPresentationSurface.SameSharedResource(first,
			Create(resource, provider, surface, lease, handle: 0x2345)));
	}

	[Theory]
	[InlineData(801.0, 451.0, 1.25)]
	[InlineData(960.0, 540.0, 1.75)]
	[InlineData(1280.5, 720.25, 1.50)]
	[InlineData(1920.0, 1080.0, 1.00)]
	[InlineData(960.0, 540.0, 2.00)]
	public void Local_gpu_target_and_monitor_viewport_agree_on_ceil_physical_pixels(
		double widthDip, double heightDip, double dpi)
	{
		var requested = MediaRenderTarget.Create(widthDip, heightDip, dpi, dpi, MediaScalingPath.GpuProvider);
		Assert.True(OperatorSurfaceTargetPolicy.TryMeasure(
			widthDip, heightDip, dpi, dpi, 16384, 16384, out var actual, out var reason), reason);
		Assert.Equal(requested.PixelWidth, actual.Width);
		Assert.Equal(requested.PixelHeight, actual.Height);
		var fit = MediaPresentationGeometry.Calculate(3840, 2160, actual.Width, actual.Height, MediaPresentationMode.Fit);
		Assert.True(fit.Width <= actual.Width + 1e-6);
		Assert.True(fit.Height <= actual.Height + 1e-6);
	}

	[Fact]
	public void Subpixel_layout_changes_do_not_change_allocated_gpu_target()
	{
		var first = MediaRenderTarget.Create(800.1, 450.1, 1.25, 1.25, MediaScalingPath.GpuProvider);
		var second = MediaRenderTarget.Create(800.3, 450.3, 1.25, 1.25, MediaScalingPath.GpuProvider);
		Assert.Equal(first, second);
		var changed = MediaRenderTarget.Create(802, 452, 1.25, 1.25, MediaScalingPath.GpuProvider);
		Assert.NotEqual(first, changed);
	}

	[Fact]
	public void Timeline_uses_actual_scrollbar_adjusted_track_geometry()
	{
		var root = FindRepositoryRoot();
		var xaml = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator",
			"SequenceTimelineControl.xaml"));
		var source = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator",
			"SequenceTimelineControl.xaml.cs"));
		var document = XDocument.Parse(xaml);
		var ruler = Assert.Single(document.Descendants(),
			element => element.Attributes().Any(attribute =>
				attribute.Name.LocalName == "Name" && attribute.Value == "SequenceRuler"));
		var trackScroll = Assert.Single(document.Descendants(),
			element => element.Attributes().Any(attribute =>
				attribute.Name.LocalName == "Name" && attribute.Value == "SequenceTrackScroll"));

		Assert.Equal("OnViewportSizeChanged", (string?)ruler.Attribute("SizeChanged"));
		Assert.Equal("OnTrackScrollChanged", (string?)trackScroll.Attribute("ScrollChanged"));
		Assert.Contains("SequenceTrackScroll.ViewportWidth - 190", source, StringComparison.Ordinal);
		Assert.Contains("SequenceRuler.ActualWidth", source, StringComparison.Ordinal);
		Assert.DoesNotContain("ActualWidth - 190", source, StringComparison.Ordinal);
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

	[Fact]
	public void Gpu_surface_fails_open_and_releases_resources_for_graphics_recovery()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "Controls", "GpuMonitorPresentationSurface.cs"));

		Assert.Contains("UnloadContent += OnUnloadContent", source, StringComparison.Ordinal);
		Assert.Contains("CloseSharedResource();", source, StringComparison.Ordinal);
		Assert.Contains("GPU presentation failed · CPU/WPF fallback", source, StringComparison.Ordinal);
		Assert.Contains("VisualTreeHelper.GetDpi", source, StringComparison.Ordinal);
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
