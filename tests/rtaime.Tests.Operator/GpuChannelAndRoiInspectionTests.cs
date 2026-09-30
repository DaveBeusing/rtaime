// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class GpuChannelAndRoiInspectionTests
{
	[Fact]
	public void Gpu_shader_exposes_combined_rgba_and_luma_inspection_modes()
	{
		var source = ReadGpuSurface();

		Assert.Contains("InspectionMode == 1", source, StringComparison.Ordinal);
		Assert.Contains("InspectionMode == 2", source, StringComparison.Ordinal);
		Assert.Contains("InspectionMode == 3", source, StringComparison.Ordinal);
		Assert.Contains("InspectionMode == 4", source, StringComparison.Ordinal);
		Assert.Contains("InspectionMode == 5", source, StringComparison.Ordinal);
		Assert.Contains("return float4(sample.a, sample.a, sample.a, sample.a)", source, StringComparison.Ordinal);
		Assert.Contains("float3(0.2126, 0.7152, 0.0722)", source, StringComparison.Ordinal);
		Assert.Contains("sample.rgb *= sample.a", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Roi_compute_capability_is_isolated_from_the_existing_presentation_shader_model()
	{
		var source = ReadGpuSurface();
		var presentationStart = source.IndexOf("private const string ShaderSource", StringComparison.Ordinal);
		var analysisStart = source.IndexOf("private const string AnalysisShaderSource", StringComparison.Ordinal);
		Assert.True(presentationStart >= 0 && analysisStart > presentationStart);
		var presentationShaderSection = source[presentationStart..analysisStart];

		Assert.Contains("\"ps_4_0\"", source, StringComparison.Ordinal);
		Assert.Contains("\"cs_5_0\"", source, StringComparison.Ordinal);
		Assert.DoesNotContain("RWStructuredBuffer", presentationShaderSection, StringComparison.Ordinal);
		Assert.Contains("RWStructuredBuffer<uint> AnalysisResults", source[analysisStart..], StringComparison.Ordinal);
	}

	[Fact]
	public void Roi_analysis_is_gpu_reduced_rate_limited_and_bounded()
	{
		var source = ReadGpuSurface();

		Assert.Contains("CSMain", source, StringComparison.Ordinal);
		Assert.Contains("MaxAnalysisSamples = 262_144", source, StringComparison.Ordinal);
		Assert.Contains("TimeSpan.FromMilliseconds(200)", source, StringComparison.Ordinal);
		Assert.Contains("RWStructuredBuffer<uint> AnalysisResults", source, StringComparison.Ordinal);
		Assert.Contains("CopyResource(_analysisReadback, _analysisResults)", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopySubresourceRegion", source, StringComparison.Ordinal);
		Assert.DoesNotContain("BitmapSource", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopyPixels", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Roi_analysis_returns_only_derived_numeric_evidence_to_monitor_model()
	{
		var source = ReadGpuSurface();

		Assert.Contains("AnalysisResultCount = 16", source, StringComparison.Ordinal);
		Assert.Contains("new MediaInspectionRoiStatistics(", source, StringComparison.Ordinal);
		Assert.Contains("Monitor.SetRoiStatistics(statistics)", source, StringComparison.Ordinal);
		Assert.Contains("InterlockedAdd(AnalysisResults[0], 1)", source, StringComparison.Ordinal);
		Assert.Contains("InterlockedMax(AnalysisResults[15]", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Monitor_template_uses_custom_roi_overlay_and_custom_inspection_controls()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "Themes", "Controls", "RtaimeMonitorWorkspace.xaml");
		var document = XDocument.Load(path);

		Assert.Single(document.Descendants(), element => element.Name.LocalName == "RtaimeRoiOverlay");
		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "GpuMonitorPresentationSurface" &&
			element.Attributes().Any(attribute => attribute.Name.LocalName == "InspectionChannel"));
		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeToggleButton" &&
			string.Equals(element.Attribute("Content")?.Value, "ROI", StringComparison.Ordinal));
		Assert.DoesNotContain(document.Descendants(), element =>
			element.Name.LocalName is "Button" or "ToggleButton");
	}

	[Fact]
	public void Clean_program_has_no_roi_or_channel_inspection_state()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProgramOutputWindow.xaml"));

		Assert.DoesNotContain("RtaimeRoiOverlay", source, StringComparison.Ordinal);
		Assert.DoesNotContain("InspectionChannel", source, StringComparison.Ordinal);
		Assert.DoesNotContain("InspectionRoi", source, StringComparison.Ordinal);
	}

	private static string ReadGpuSurface()
	{
		var root = FindRepositoryRoot();
		return File.ReadAllText(Path.Combine(
			root,
			"src",
			"Hosts",
			"rtaime.Operator",
			"Controls",
			"GpuMonitorPresentationSurface.cs"));
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
