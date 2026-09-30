// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MonitoringVisualPerformanceQualificationTests
{
	public static IEnumerable<object[]> QualifiedDpiScales()
	{
		yield return [1.00];
		yield return [1.25];
		yield return [1.50];
		yield return [2.00];
	}

	[Theory]
	[MemberData(nameof(QualifiedDpiScales))]
	public void Gpu_and_wpf_paths_share_the_same_physical_geometry(double dpi)
	{
		var wpfTarget = MediaRenderTarget.Create(1280, 720, dpi, dpi, MediaScalingPath.WpfFallback);
		var gpuTarget = MediaRenderTarget.Create(1280, 720, dpi, dpi, MediaScalingPath.GpuProvider);

		Assert.Equal(wpfTarget.PixelWidth, gpuTarget.PixelWidth);
		Assert.Equal(wpfTarget.PixelHeight, gpuTarget.PixelHeight);
		Assert.Equal(MediaScalingQuality.HighQuality, wpfTarget.Quality);
		Assert.Equal(MediaScalingQuality.HighQuality, gpuTarget.Quality);
		Assert.True(wpfTarget.RequiresPresentationRescale);
		Assert.False(gpuTarget.RequiresPresentationRescale);

		var wpfGeometry = MediaPresentationGeometry.Calculate(
			1920, 1080, wpfTarget.PixelWidth, wpfTarget.PixelHeight, MediaPresentationMode.Fit);
		var gpuGeometry = MediaPresentationGeometry.Calculate(
			1920, 1080, gpuTarget.PixelWidth, gpuTarget.PixelHeight, MediaPresentationMode.Fit);

		Assert.Equal(wpfGeometry, gpuGeometry);
	}

	[Fact]
	public void High_zoom_pixel_mapping_remains_source_aligned_with_pan()
	{
		const int sourceWidth = 1920;
		const int sourceHeight = 1080;
		const double zoom = 8.0;
		var presentation = MediaPresentationGeometry.Calculate(
			sourceWidth,
			sourceHeight,
			1920,
			1080,
			MediaPresentationMode.CustomZoom,
			zoom,
			panX: 320,
			panY: -180);
		var expected = new MediaPixelCoordinate(960, 540);
		var physicalX = presentation.X + (expected.X + 0.25) * presentation.Scale;
		var physicalY = presentation.Y + (expected.Y + 0.25) * presentation.Scale;

		Assert.True(MediaPixelInspection.TryMapViewportToSource(
			physicalX,
			physicalY,
			1.0,
			1.0,
			presentation,
			sourceWidth,
			sourceHeight,
			out var mapped));
		Assert.Equal(expected, mapped);
		Assert.True(presentation.Scale >= MediaPixelInspection.PixelGridMinimumScale);
	}

	[Fact]
	public void Steady_state_geometry_calculation_has_a_bounded_managed_allocation_budget()
	{
		for (var warmup = 0; warmup < 256; warmup++)
			_ = MediaPresentationGeometry.Calculate(3840, 2160, 2560, 1440, MediaPresentationMode.Fit);

		var before = GC.GetAllocatedBytesForCurrentThread();
		var started = Stopwatch.GetTimestamp();
		double checksum = 0;
		for (var iteration = 0; iteration < 50_000; iteration++)
		{
			var mode = (iteration & 1) == 0 ? MediaPresentationMode.Fit : MediaPresentationMode.Fill;
			var rect = MediaPresentationGeometry.Calculate(
				3840,
				2160,
				2560 + (iteration % 3),
				1440 + (iteration % 5),
				mode);
			checksum += rect.Width + rect.Height + rect.X + rect.Y;
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		var elapsed = Stopwatch.GetElapsedTime(started);

		Assert.True(double.IsFinite(checksum));
		Assert.InRange(allocated, 0, 64 * 1024);
		Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Geometry qualification exceeded the software guardrail: {elapsed.TotalMilliseconds:0.###} ms.");
	}

	[Fact]
	public void Sustained_resize_and_dpi_churn_does_not_create_unbounded_target_revisions()
	{
		var stabilizer = new MediaRenderTargetStabilizer(TimeSpan.FromMilliseconds(75));
		var now = DateTimeOffset.UnixEpoch;
		var initial = MediaRenderTarget.Create(1280, 720, 1.0, 1.0, MediaScalingPath.GpuProvider);
		stabilizer.Adopt(initial, now);

		for (var iteration = 1; iteration <= 10_000; iteration++)
		{
			var dpi = (iteration % 4) switch
			{
				0 => 1.0,
				1 => 1.25,
				2 => 1.5,
				_ => 2.0
			};
			var requested = MediaRenderTarget.Create(
				1280 + (iteration % 31),
				720 + (iteration % 17),
				dpi,
				dpi,
				MediaScalingPath.GpuProvider);
			stabilizer.Adopt(requested, now.AddMilliseconds(iteration));
		}

		Assert.Equal(1, stabilizer.Revision);
		Assert.True(stabilizer.ResizePending);

		var settled = MediaRenderTarget.Create(1600, 900, 1.5, 1.5, MediaScalingPath.GpuProvider);
		var settleStart = now.AddSeconds(20);
		stabilizer.Adopt(settled, settleStart);
		var active = stabilizer.Adopt(settled, settleStart.AddMilliseconds(80));

		Assert.Equal(settled, active);
		Assert.Equal(2, stabilizer.Revision);
		Assert.False(stabilizer.ResizePending);
	}

	[Fact]
	public void Media_checkerboard_pixel_grid_and_roi_share_one_presentation_coordinate_system()
	{
		var root = FindRepositoryRoot();
		var themePath = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "Themes", "Controls", "RtaimeMonitorWorkspace.xaml");
		var theme = File.ReadAllText(themePath);
		var document = XDocument.Parse(theme);

		Assert.Single(document.Descendants(), element => element.Name.LocalName == "Image");
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "GpuMonitorPresentationSurface");
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "RtaimePixelGridOverlay");
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "RtaimeRoiOverlay");
		Assert.Contains("Viewport=\"0,0,12,12\"", theme, StringComparison.Ordinal);
		Assert.Contains("PresentationWidth", theme, StringComparison.Ordinal);
		Assert.Contains("PresentationHeight", theme, StringComparison.Ordinal);
		Assert.Contains("PresentationOffsetX", theme, StringComparison.Ordinal);
		Assert.Contains("PresentationOffsetY", theme, StringComparison.Ordinal);
		Assert.Contains("SourcePixelWidth", theme, StringComparison.Ordinal);
		Assert.Contains("SourcePixelHeight", theme, StringComparison.Ordinal);
		Assert.DoesNotContain("<Viewbox", theme, StringComparison.Ordinal);
	}

	[Fact]
	public void Presentation_channel_scope_and_compare_shaders_share_the_qualified_color_basis()
	{
		var root = FindRepositoryRoot();
		var controls = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "Controls");
		var presentation = File.ReadAllText(Path.Combine(controls, "GpuMonitorPresentationSurface.cs"));
		var scopes = File.ReadAllText(Path.Combine(controls, "GpuScopeAnalysisSurface.cs"));
		var compare = File.ReadAllText(Path.Combine(controls, "GpuMediaCompareSurface.cs"));

		foreach (var source in new[] { presentation, scopes, compare })
		{
			Assert.Contains("(value - (16.0 / 255.0)) / (219.0 / 255.0)", source, StringComparison.Ordinal);
			Assert.Contains("value <= 0.04045 ? value / 12.92", source, StringComparison.Ordinal);
			Assert.Contains("value < 0.081 ? value / 4.5", source, StringComparison.Ordinal);
			Assert.Contains("1.055 * pow(value, 1.0 / 2.4) - 0.055", source, StringComparison.Ordinal);
		}

		Assert.Contains("if (CompleteColor != 0)", presentation, StringComparison.Ordinal);
		Assert.Contains("VectorscopeEnabled", scopes, StringComparison.Ordinal);
		Assert.Contains("CompleteColor", compare, StringComparison.Ordinal);
	}

	[Fact]
	public void Analysis_and_diagnostics_invalidation_rates_are_explicitly_bounded()
	{
		var root = FindRepositoryRoot();
		var operatorRoot = Path.Combine(root, "src", "Hosts", "rtaime.Operator");
		var monitor = File.ReadAllText(Path.Combine(operatorRoot, "MonitorView.cs"));
		var presentation = File.ReadAllText(Path.Combine(operatorRoot, "Controls", "GpuMonitorPresentationSurface.cs"));
		var scopes = File.ReadAllText(Path.Combine(operatorRoot, "MediaScopeAnalysis.cs"));

		Assert.Contains("TimeSpan.FromMilliseconds(200)", monitor, StringComparison.Ordinal);
		Assert.Contains("TimeSpan.FromMilliseconds(200)", presentation, StringComparison.Ordinal);
		Assert.Contains("ScopeUpdateInterval = TimeSpan.FromMilliseconds(200)", scopes, StringComparison.Ordinal);
		Assert.Contains("MaxAnalysisSamples = 262_144", presentation, StringComparison.Ordinal);
		Assert.Contains("AnalysisResultCount = 16", presentation, StringComparison.Ordinal);
		Assert.Contains("MaxScopeSamples = 262_144", scopes, StringComparison.Ordinal);
		Assert.Equal(86_016, GpuMonitoringAnalysisPolicy.ScopeResultBytes);
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
