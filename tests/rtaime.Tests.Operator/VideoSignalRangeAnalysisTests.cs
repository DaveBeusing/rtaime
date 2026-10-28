// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class VideoSignalRangeAnalysisTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(15)]
	[InlineData(16)]
	[InlineData(235)]
	[InlineData(236)]
	[InlineData(246)]
	[InlineData(247)]
	[InlineData(254)]
	[InlineData(255)]
	public void Ebu_r103_eight_bit_threshold_edges_use_original_limited_codes(int code)
	{
		var frame = Frame(1, 1, [(byte)code, (byte)code, (byte)code, 255], Limited(8));
		var sampled = VideoSignalRangeAnalysis.AnalyzeRgba8(frame);
		var native = MediaScopeSnapshot.Analyze(frame, 1).SignalRange!;
		var nominal = code < 16 || code > 235 ? 1 : 0;
		var preferred = code < 5 || code > 246 ? 1 : 0;
		var total = code < 1 || code > 254 ? 1 : 0;

		Assert.Equal(nominal, sampled.Red.OutsideNominal);
		Assert.Equal(preferred, sampled.Red.OutsidePreferred);
		Assert.Equal(total, sampled.Red.OutsideTotal);
		Assert.Equal(sampled.Red, sampled.Green);
		Assert.Equal(sampled.Red, sampled.Blue);
		Assert.Equal(sampled.Red, sampled.Luma);
		Assert.Equal(sampled.Red, sampled.AnyComponent);
		Assert.Equal(sampled.AnyComponent, native.AnyComponent);
		Assert.Equal(total == 1 ? VideoRangeSampleStatus.SampledOutsideTotal :
			preferred == 1 ? VideoRangeSampleStatus.SampledOutOfGamut :
			nominal == 1 ? VideoRangeSampleStatus.SampledOutsideNominal :
			VideoRangeSampleStatus.SampledWithinNominal, sampled.Status);
		Assert.Equal(100.0, sampled.CoveragePercent);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(19)]
	[InlineData(20)]
	[InlineData(21)]
	[InlineData(63)]
	[InlineData(64)]
	[InlineData(940)]
	[InlineData(941)]
	[InlineData(984)]
	[InlineData(985)]
	[InlineData(1019)]
	[InlineData(1020)]
	[InlineData(1023)]
	public void Ebu_r103_ten_bit_threshold_edges_do_not_quantize_to_eight_bits(int code)
	{
		var pixel = new ushort[] { (ushort)code, (ushort)code, (ushort)code, 1023 };
		var result = VideoSignalRangeAnalysis.AnalyzeNativeRgba10(pixel, 1, 1, 17, Limited(10));

		Assert.Equal(code < 64 || code > 940 ? 1 : 0, result.Red.OutsideNominal);
		Assert.Equal(code < 20 || code > 984 ? 1 : 0, result.Red.OutsidePreferred);
		Assert.Equal(code < 4 || code > 1019 ? 1 : 0, result.Red.OutsideTotal);
		Assert.Equal(result.Red, result.Luma);
		Assert.Equal(10, result.Profile.ObservedCodeBitDepth);
		Assert.Contains("frame 17", result.TechnicalLabel, StringComparison.Ordinal);
	}

	[Fact]
	public void Out_of_gamut_threshold_exceeds_one_percent_of_sampled_active_image_not_one_bad_pixel()
	{
		var pixels = new byte[100 * 4];
		for (var i = 0; i < 100; i++)
		{
			pixels[i * 4] = 16;
			pixels[i * 4 + 1] = 16;
			pixels[i * 4 + 2] = 16;
			pixels[i * 4 + 3] = 255;
		}
		pixels[0] = pixels[1] = pixels[2] = 247;
		var one = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(100, 1, pixels, Limited(8)));
		Assert.Equal(1, one.AnyComponent.OutsidePreferred);
		Assert.Equal(1.0, one.AnyComponent.PreferredPercent(one.SampleCount));
		Assert.False(one.PreferredAreaThresholdExceeded);
		Assert.Equal(VideoRangeSampleStatus.SampledOutsideNominal, one.Status);

		pixels[4] = pixels[5] = pixels[6] = 247;
		var two = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(100, 1, pixels, Limited(8)));
		Assert.Equal(2, two.AnyComponent.OutsidePreferred);
		Assert.True(two.PreferredAreaThresholdExceeded);
		Assert.Equal(VideoRangeSampleStatus.SampledOutOfGamut, two.Status);
		Assert.Contains("sampled UNFILTERED diagnostic", two.DiagnosticLabel, StringComparison.Ordinal);
		Assert.Contains("no compliance PASS", two.DiagnosticLabel, StringComparison.Ordinal);
	}

	[Fact]
	public void Any_component_counts_unique_pixels_without_double_counting_rgb_and_luma()
	{
		var result = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(2, 1,
			[247, 16, 16, 255, 16, 16, 16, 255], Limited(8)));
		Assert.Equal(1, result.Red.OutsidePreferred);
		Assert.Equal(0, result.Green.OutsidePreferred);
		Assert.Equal(0, result.Blue.OutsidePreferred);
		Assert.Equal(1, result.AnyComponent.OutsidePreferred);
		Assert.Contains("R′ N/P/T", result.ChannelDiagnosticLabel, StringComparison.Ordinal);
		Assert.Contains("Y′ N/P/T", result.ChannelDiagnosticLabel, StringComparison.Ordinal);
		Assert.Contains("ANY N/P/T", result.ChannelDiagnosticLabel, StringComparison.Ordinal);
	}

	[Fact]
	public void Active_image_region_excludes_code_excursions_in_surrounding_border()
	{
		var pixels = new byte[4 * 2 * 4];
		for (var i = 0; i < 8; i++)
		{
			var value = i == 1 || i == 2 ? (byte)16 : (byte)255;
			pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = value;
			pixels[i * 4 + 3] = 255;
		}
		var frame = Frame(4, 2, pixels, Limited(8));
		var result = VideoSignalRangeAnalysis.AnalyzeRgba8(frame, new VideoActiveImageRegion(1, 0, 2, 1));
		Assert.Equal(2, result.SampleCount);
		Assert.Equal(0, result.AnyComponent.OutsideNominal);
		Assert.Equal(0, result.AnyComponent.OutsidePreferred);
		Assert.Equal(100.0, result.CoveragePercent);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			VideoSignalRangeAnalysis.AnalyzeRgba8(frame, new VideoActiveImageRegion(3, 0, 2, 1)));
	}

	[Fact]
	public void Unknown_inferred_full_range_and_hdr_like_color_are_not_a_legal_level_pass()
	{
		var unknown = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(1, 1, [16, 16, 16, 255], ColorDescription.UnknownRgba8));
		Assert.Equal(VideoRangeSampleStatus.Unverified, unknown.Status);
		Assert.Equal(0, unknown.SampleCount);

		var inferred = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(1, 1, [16, 16, 16, 255],
			Limited(8, authority: ColorMetadataAuthority.Inferred)));
		Assert.Equal(VideoRangeSampleStatus.Unverified, inferred.Status);

		var full = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(1, 1, [16, 16, 16, 255],
			Limited(8, range: NominalRange.Full)));
		Assert.Equal(VideoRangeSampleStatus.Unverified, full.Status);
		Assert.Contains("full-range", full.Detail);

		var unsupportedTransfer = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(1, 1, [16, 16, 16, 255],
			Limited(8, transfer: ColorTransfer.Unknown)));
		Assert.Equal(VideoRangeSampleStatus.Unverified, unsupportedTransfer.Status);
	}

	[Fact]
	public void Raw_ten_bit_vectors_reject_values_outside_native_code_precision()
	{
		var unavailable = VideoSignalRangeAnalysis.AnalyzeNativeRgba10(
			new ushort[] { 1024, 64, 64, 1023 }, 1, 1, 33, Limited(10));
		Assert.Equal(VideoRangeSampleStatus.Unavailable, unavailable.Status);
		Assert.Contains("wider than ten bits", unavailable.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public void Eight_bit_monitoring_of_ten_bit_source_reports_unavailable_not_fake_ten_bit_legal_check()
	{
		var sample = VideoSignalRangeAnalysis.AnalyzeRgba8(Frame(1, 1, [64, 64, 64, 255], Limited(10)));
		Assert.Equal(VideoRangeSampleStatus.Unavailable, sample.Status);
		Assert.Equal(0, sample.SampleCount);
		Assert.Contains("not preserved", sample.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public void Signal_counters_round_trip_through_fixed_additive_gpu_scope_buffer()
	{
		var data = new uint[GpuMonitoringAnalysisPolicy.CombinedResultCount];
		data[GpuMonitoringAnalysisPolicy.SignalResultOffset + 0] = 1;
		data[GpuMonitoringAnalysisPolicy.SignalResultOffset + 1] = 1;
		data[GpuMonitoringAnalysisPolicy.SignalResultOffset + 12] = 1;
		data[GpuMonitoringAnalysisPolicy.SignalResultOffset + 13] = 1;
		var scope = MediaScopeSnapshot.FromGpuResultBuffer(
			42, 4, 4, Limited(8), 2, 4, data, TimeSpan.FromMilliseconds(0.5));
		Assert.Equal(VideoRangeSampleStatus.SampledOutOfGamut, scope.SignalRange!.Status);
		Assert.Equal(1, scope.SignalRange.Red.OutsidePreferred);
		Assert.Equal(1, scope.SignalRange.AnyComponent.OutsidePreferred);
		Assert.Equal(4, scope.SignalRange.SampleCount);
		Assert.Equal(25.0, scope.SignalRange.CoveragePercent);
		Assert.Equal(GpuMonitoringAnalysisPolicy.ScopeResultBytes + 15 * sizeof(uint), scope.ResultTransferBytes);
		Assert.Equal(GpuMonitoringAnalysisPolicy.CombinedResultBytes, scope.ResultTransferBytes);
	}

	[Fact]
	public void Invalid_gpu_excursion_counters_cannot_produce_a_clean_range_verdict()
	{
		var counts = new uint[GpuMonitoringAnalysisPolicy.CombinedResultCount];
		counts[GpuMonitoringAnalysisPolicy.SignalResultOffset + 1] = 5;
		var invalid = MediaScopeSnapshot.FromGpuResultBuffer(
			42, 4, 4, Limited(8), 2, 4, counts, TimeSpan.Zero);
		Assert.Equal(VideoRangeSampleStatus.Unavailable, invalid.SignalRange!.Status);
		Assert.Contains("inconsistent", invalid.SignalRange.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public void Legacy_gpu_scope_result_remains_loadable_without_fake_range_evidence()
	{
		var scope = MediaScopeSnapshot.FromGpuResultBuffer(
			5, 8, 4, Limited(8), 2, 8,
			new uint[GpuMonitoringAnalysisPolicy.ScopeResultCount], TimeSpan.Zero);
		Assert.Equal(VideoRangeSampleStatus.Unavailable, scope.SignalRange!.Status);
		Assert.Equal(GpuMonitoringAnalysisPolicy.ScopeResultBytes, scope.ResultTransferBytes);
	}

	[Fact]
	public void Shader_reads_raw_source_codes_before_display_transfer_and_preserves_scope_bounds()
	{
		var root = FindRoot();
		var gpuSource = File.ReadAllText(Path.Combine(root,
			"src", "Hosts", "rtaime.Operator", "Controls", "GpuScopeAnalysisSurface.cs"));
		var managedSource = File.ReadAllText(Path.Combine(root,
			"src", "Hosts", "rtaime.Operator", "MediaScopeAnalysis.cs"));
		Assert.Contains("uint rawR = (uint)round(saturate(sampleColor.r) * 255.0);", gpuSource, StringComparison.Ordinal);
		Assert.Contains("uint outside = CountRawExcursion(rawR, 0)", gpuSource, StringComparison.Ordinal);
		Assert.Contains("static const uint SignalResultOffset = 21504", gpuSource, StringComparison.Ordinal);
		Assert.True(gpuSource.IndexOf("uint rawR = ", StringComparison.Ordinal) <
			gpuSource.IndexOf("sampleColor.r = TransformComponent", StringComparison.Ordinal));
		Assert.Contains("ScopeUpdateInterval", managedSource, StringComparison.Ordinal);
		Assert.Equal(262_144, GpuMonitoringAnalysisPolicy.MaxScopeSamples);
	}

	private static ColorDescription Limited(
		byte depth,
		ColorMetadataAuthority authority = ColorMetadataAuthority.Authoritative,
		NominalRange range = NominalRange.Limited,
		ColorTransfer transfer = ColorTransfer.Rec709) =>
		new(ColorPrimaries.Rec709, transfer, ColorMatrix.IdentityRgb, range,
			depth, AlphaMode.Opaque, authority);

	private static MonitoringFrame Frame(uint width, uint height, byte[] rgba, ColorDescription color) =>
		new(new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			new MediaSourceId(new Identity(Guid.Parse("11111111-1111-1111-1111-111111111111"))),
			width, height, PixelFormat.Rgba8,
			new FrameTiming(42, 42, new Timebase(1, 25)), color), rgba);

	private static string FindRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "rtaime.slnx"))) return dir.FullName;
		throw new DirectoryNotFoundException("Missing repository root.");
	}
}
