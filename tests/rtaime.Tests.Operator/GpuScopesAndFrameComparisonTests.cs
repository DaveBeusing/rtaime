// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class GpuScopesAndFrameComparisonTests
{
	[Fact]
	public void Scope_compute_path_is_bounded_and_transfers_only_fixed_results()
	{
		var source = ReadOperatorSource("Controls", "GpuScopeAnalysisSurface.cs");

		Assert.Contains("MaxScopeSamples = 262_144", ReadOperatorSource("MediaScopeAnalysis.cs"), StringComparison.Ordinal);
		Assert.Contains("TimeSpan.FromMilliseconds(200)", ReadOperatorSource("MediaScopeAnalysis.cs"), StringComparison.Ordinal);
		Assert.Contains("ScopeResultBytes", source, StringComparison.Ordinal);
		Assert.Contains("RWStructuredBuffer<uint> Results", source, StringComparison.Ordinal);
		Assert.Contains("CopyResource(_readback!, _results!)", source, StringComparison.Ordinal);
		Assert.DoesNotContain("BitmapSource", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopyPixels", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopySubresourceRegion", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Scope_shader_preserves_cpu_reference_bin_semantics()
	{
		var source = ReadOperatorSource("Controls", "GpuScopeAnalysisSurface.cs");

		Assert.Contains("(77 * r + 150 * g + 29 * b + 128) >> 8", source, StringComparison.Ordinal);
		Assert.Contains("(-43 * (int)r - 85 * (int)g + 128 * (int)b + 128)", source, StringComparison.Ordinal);
		Assert.Contains("(128 * (int)r - 107 * (int)g - 21 * (int)b + 128)", source, StringComparison.Ordinal);
		Assert.Contains("if (VectorscopeEnabled != 0)", source, StringComparison.Ordinal);
		Assert.Contains("TransformComponent", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Gpu_compare_is_presentation_only_and_has_no_cpu_frame_materialization()
	{
		var source = ReadOperatorSource("Controls", "GpuMediaCompareSurface.cs");

		Assert.Contains("OpenSharedResource", source, StringComparison.Ordinal);
		Assert.Contains("abs(a.rgb - b.rgb)", source, StringComparison.Ordinal);
		Assert.Contains("CompareMode == 1", source, StringComparison.Ordinal);
		Assert.Contains("CompareMode == 4", source, StringComparison.Ordinal);
		Assert.DoesNotContain("BitmapSource", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopyPixels", source, StringComparison.Ordinal);
		Assert.DoesNotContain("CopyResource", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Operator_monitoring_ui_layers_gpu_compare_over_explicit_cpu_fallback()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml");
		var document = XDocument.Load(path);

		Assert.Single(document.Descendants(), element => element.Name.LocalName == "GpuScopeAnalysisSurface");
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "GpuMediaCompareSurface");
		Assert.Single(document.Descendants(), element => element.Name.LocalName == "RtaimeMediaCompareView");
		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "DataTrigger" &&
			element.Attributes().Any(attribute => attribute.Value.Contains("IsGpuComparisonActive", StringComparison.Ordinal)));
	}

	[Fact]
	public void View_model_skips_cpu_analysis_until_gpu_path_is_explicitly_unavailable()
	{
		var source = ReadOperatorSource("OperatorMonitoringViewModel.cs");

		Assert.Contains("cpuScopesRequired = !descriptor.HasSharedResource || Volatile.Read(ref _gpuScopeAnalysisUnavailable)", source, StringComparison.Ordinal);
		Assert.Contains("GpuMonitoringAnalysisPolicy.ScopeUpdateInterval", source, StringComparison.Ordinal);
		Assert.Contains("SetGpuScopesUnavailable", source, StringComparison.Ordinal);
		Assert.Contains("SetGpuComparisonState", source, StringComparison.Ordinal);
		Assert.Contains("CreateDisplayDifferenceBitmap", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Clean_program_remains_free_of_scope_and_compare_overlays()
	{
		var source = ReadOperatorSource("ProgramOutputWindow.xaml");

		Assert.DoesNotContain("GpuScopeAnalysisSurface", source, StringComparison.Ordinal);
		Assert.DoesNotContain("GpuMediaCompareSurface", source, StringComparison.Ordinal);
		Assert.DoesNotContain("RtaimeMediaScope", source, StringComparison.Ordinal);
		Assert.DoesNotContain("RtaimeMediaCompareView", source, StringComparison.Ordinal);
	}

	private static string ReadOperatorSource(params string[] relative)
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(new[] { root, "src", "Hosts", "rtaime.Operator" }.Concat(relative).ToArray());
		return File.ReadAllText(path);
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
