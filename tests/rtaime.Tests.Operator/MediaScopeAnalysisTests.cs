// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaScopeAnalysisTests
{
	[Fact]
	public void Deterministic_pattern_produces_bounded_scope_samples()
	{
		var frame = CreateFrame(8, 4, (x, y) => ((byte)(x * 32), (byte)(y * 64), (byte)128, (byte)255), ColorDescription.SrgbFullRgba8);
		var scope = MediaScopeSnapshot.Analyze(frame, sampleStride: 2);

		Assert.Equal(8, scope.SampleCount);
		Assert.Equal(8, scope.LumaHistogram.Sum());
		Assert.Equal(8, scope.RedHistogram.Sum());
		Assert.Equal(8, scope.GreenHistogram.Sum());
		Assert.Equal(8, scope.BlueHistogram.Sum());
		Assert.Equal(8, scope.Waveform.Sum());
		Assert.Equal(8, scope.RedWaveform.Sum());
		Assert.Equal(8, scope.GreenWaveform.Sum());
		Assert.Equal(8, scope.BlueWaveform.Sum());
		Assert.Equal(8, scope.Vectorscope.Sum());
	}

	[Fact]
	public void Difference_rejects_mismatched_dimensions()
	{
		var a = CreateFrame(8, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255), ColorDescription.SrgbFullRgba8).Descriptor;
		var b = CreateFrame(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255), ColorDescription.SrgbFullRgba8).Descriptor;

		var compatibility = MediaComparisonCompatibility.Evaluate(a, b);

		Assert.False(compatibility.IsCompatible);
		Assert.Contains("dimensions", compatibility.Detail, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Difference_is_deterministic_for_compatible_inputs()
	{
		var a = CreateFrame(2, 1, (_, _) => ((byte)10, (byte)40, (byte)100, (byte)255), ColorDescription.SrgbFullRgba8);
		var b = CreateFrame(2, 1, (_, _) => ((byte)30, (byte)10, (byte)90, (byte)255), ColorDescription.SrgbFullRgba8);

		var difference = MediaDifference.CreateRgba(a, b);

		Assert.Equal(new byte[] { 20, 30, 10, 255, 20, 30, 10, 255 }, difference);
	}

	[Fact]
	public void Unknown_color_semantics_disable_vectorscope_instead_of_guessing()
	{
		var frame = CreateFrame(8, 4, (_, _) => ((byte)64, (byte)128, (byte)192, (byte)255), ColorDescription.UnknownRgba8);

		var scope = MediaScopeSnapshot.Analyze(frame, sampleStride: 2);

		Assert.False(scope.VectorscopeAvailable);
		Assert.Equal(0, scope.Vectorscope.Sum());
		Assert.Contains("unavailable", scope.Detail, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Difference_rejects_unknown_color_semantics()
	{
		var a = CreateFrame(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255), ColorDescription.UnknownRgba8).Descriptor;
		var b = CreateFrame(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255), ColorDescription.UnknownRgba8).Descriptor;

		var compatibility = MediaComparisonCompatibility.Evaluate(a, b);

		Assert.False(compatibility.IsCompatible);
		Assert.Contains("color", compatibility.Detail, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Gpu_scope_stride_is_bounded_for_full_resolution_sources()
	{
		var stride1080 = GpuMonitoringAnalysisPolicy.ResolveScopeSampleStride(1920, 1080);
		var stride4k = GpuMonitoringAnalysisPolicy.ResolveScopeSampleStride(3840, 2160);
		var stride8k = GpuMonitoringAnalysisPolicy.ResolveScopeSampleStride(7680, 4320);

		Assert.True(stride1080 >= GpuMonitoringAnalysisPolicy.MinimumScopeSampleStride);
		Assert.True((1920L + stride1080 - 1) / stride1080 * ((1080L + stride1080 - 1) / stride1080) <= GpuMonitoringAnalysisPolicy.MaxScopeSamples);
		Assert.True((3840L + stride4k - 1) / stride4k * ((2160L + stride4k - 1) / stride4k) <= GpuMonitoringAnalysisPolicy.MaxScopeSamples);
		Assert.True((7680L + stride8k - 1) / stride8k * ((4320L + stride8k - 1) / stride8k) <= GpuMonitoringAnalysisPolicy.MaxScopeSamples);
	}

	[Theory]
	[InlineData(5385u, 3105u)]
	[InlineData(6246u, 2052u)]
	[InlineData(3820u, 5558u)]
	public void Gpu_scope_stride_never_exceeds_sample_bound_for_nonstandard_dimensions(uint width, uint height)
	{
		var stride = GpuMonitoringAnalysisPolicy.ResolveScopeSampleStride(width, height);
		var samples = ((long)width + stride - 1) / stride * (((long)height + stride - 1) / stride);

		Assert.True(samples <= GpuMonitoringAnalysisPolicy.MaxScopeSamples);
	}

	[Fact]
	public void Gpu_result_shape_round_trips_cpu_reference_bins()
	{
		var frame = CreateFrame(8, 4, (x, y) => ((byte)(x * 32), (byte)(y * 64), (byte)128, (byte)255), ColorDescription.SrgbFullRgba8);
		var cpu = MediaScopeSnapshot.Analyze(frame, sampleStride: 2);
		var buffer = new uint[GpuMonitoringAnalysisPolicy.ScopeResultCount];

		Copy(cpu.LumaHistogram, GpuMonitoringAnalysisPolicy.LumaHistogramOffset);
		Copy(cpu.RedHistogram, GpuMonitoringAnalysisPolicy.RedHistogramOffset);
		Copy(cpu.GreenHistogram, GpuMonitoringAnalysisPolicy.GreenHistogramOffset);
		Copy(cpu.BlueHistogram, GpuMonitoringAnalysisPolicy.BlueHistogramOffset);
		Copy(cpu.Waveform, GpuMonitoringAnalysisPolicy.WaveformOffset);
		Copy(cpu.RedWaveform, GpuMonitoringAnalysisPolicy.RedWaveformOffset);
		Copy(cpu.GreenWaveform, GpuMonitoringAnalysisPolicy.GreenWaveformOffset);
		Copy(cpu.BlueWaveform, GpuMonitoringAnalysisPolicy.BlueWaveformOffset);
		Copy(cpu.Vectorscope, GpuMonitoringAnalysisPolicy.VectorscopeOffset);

		var gpu = MediaScopeSnapshot.FromGpuResultBuffer(
			cpu.SequenceNumber,
			cpu.SourceWidth,
			cpu.SourceHeight,
			cpu.Color,
			cpu.SampleStride,
			cpu.SampleCount,
			buffer,
			TimeSpan.FromMilliseconds(0.25));

		Assert.Equal(MediaScopeProcessingPath.GpuSharedResource, gpu.ProcessingPath);
		Assert.Equal(cpu.LumaHistogram, gpu.LumaHistogram);
		Assert.Equal(cpu.RedHistogram, gpu.RedHistogram);
		Assert.Equal(cpu.GreenHistogram, gpu.GreenHistogram);
		Assert.Equal(cpu.BlueHistogram, gpu.BlueHistogram);
		Assert.Equal(cpu.Waveform, gpu.Waveform);
		Assert.Equal(cpu.RedWaveform, gpu.RedWaveform);
		Assert.Equal(cpu.GreenWaveform, gpu.GreenWaveform);
		Assert.Equal(cpu.BlueWaveform, gpu.BlueWaveform);
		Assert.Equal(cpu.Vectorscope, gpu.Vectorscope);
		Assert.Equal(GpuMonitoringAnalysisPolicy.ScopeResultBytes, gpu.ResultTransferBytes);
		Assert.True(gpu.ManagedAllocationBytes >= GpuMonitoringAnalysisPolicy.ScopeResultBytes);
		Assert.True(cpu.ManagedAllocationBytes > 0);

		void Copy(IReadOnlyList<int> source, int offset)
		{
			for (var i = 0; i < source.Count; i++)
				buffer[offset + i] = checked((uint)source[i]);
		}
	}

	[Fact]
	public void Display_difference_matches_display_code_transform()
	{
		var color = new ColorDescription(
			ColorPrimaries.Rec709,
			ColorTransfer.Rec709,
			ColorMatrix.IdentityRgb,
			NominalRange.Limited,
			8,
			AlphaMode.Straight,
			ColorMetadataAuthority.Authoritative);
		var a = CreateFrame(1, 1, (_, _) => ((byte)16, (byte)64, (byte)235, (byte)255), color);
		var b = CreateFrame(1, 1, (_, _) => ((byte)235, (byte)16, (byte)64, (byte)255), color);

		var difference = MediaDifference.CreateDisplayRgba(a, b);

		Assert.Equal(
			(byte)Math.Abs(MonitoringDisplayTransform.TransformCodeValue(16, color) - MonitoringDisplayTransform.TransformCodeValue(235, color)),
			difference[0]);
		Assert.Equal(byte.MaxValue, difference[3]);
	}

	[Fact]
	public void Difference_accepts_matching_authoritative_semantics()
	{
		var a = CreateFrame(8, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255), ColorDescription.SrgbFullRgba8).Descriptor;
		var b = CreateFrame(8, 4, (_, _) => ((byte)255, (byte)255, (byte)255, (byte)255), ColorDescription.SrgbFullRgba8).Descriptor;

		Assert.True(MediaComparisonCompatibility.Evaluate(a, b).IsCompatible);
	}

	private static MonitoringFrame CreateFrame(
		uint width,
		uint height,
		Func<uint, uint, (byte R, byte G, byte B, byte A)> pixel,
		ColorDescription? color = null)
	{
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Source,
			new MediaSourceId(new Identity(Guid.Parse("11111111-1111-1111-1111-111111111111"))),
			width,
			height,
			PixelFormat.Rgba8,
			new FrameTiming(12, 12, new Timebase(1, 25)),
			color ?? ColorDescription.SrgbFullRgba8);
		var bytes = new byte[descriptor.RequiredPayloadBytes];
		for (uint y = 0; y < height; y++)
		for (uint x = 0; x < width; x++)
		{
			var p = pixel(x, y);
			var offset = checked((int)(((ulong)y * width + x) * 4UL));
			bytes[offset] = p.R;
			bytes[offset + 1] = p.G;
			bytes[offset + 2] = p.B;
			bytes[offset + 3] = p.A;
		}
		return new MonitoringFrame(descriptor, bytes);
	}
}
