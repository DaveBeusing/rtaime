// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;

namespace rtaime.Tests.Operator;

public sealed class MediaScopeAnalysisTests
{
	[Fact]
	public void Deterministic_pattern_produces_bounded_scope_samples()
	{
		var frame = CreateFrame(8, 4, (x, y) => ((byte)(x * 32), (byte)(y * 64), (byte)128, (byte)255));
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
		var a = CreateFrame(8, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255)).Descriptor;
		var b = CreateFrame(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255)).Descriptor;

		var compatibility = MediaComparisonCompatibility.Evaluate(a, b);

		Assert.False(compatibility.IsCompatible);
		Assert.Contains("dimensions", compatibility.Detail, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Difference_accepts_matching_authoritative_semantics()
	{
		var a = CreateFrame(8, 4, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255)).Descriptor;
		var b = CreateFrame(8, 4, (_, _) => ((byte)255, (byte)255, (byte)255, (byte)255)).Descriptor;

		Assert.True(MediaComparisonCompatibility.Evaluate(a, b).IsCompatible);
	}

	private static MonitoringFrame CreateFrame(
		uint width,
		uint height,
		Func<uint, uint, (byte R, byte G, byte B, byte A)> pixel)
	{
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Source,
			new MediaSourceId(new Identity(Guid.Parse("11111111-1111-1111-1111-111111111111"))),
			width,
			height,
			PixelFormat.Rgba8,
			new FrameTiming(12, 12, new Timebase(1, 25)),
			ColorDescription.UnknownRgba8);
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
