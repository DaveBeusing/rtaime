// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class RuntimePerformanceDisplayFormatterTests
{
	[Fact]
	public void Frame_time_uses_invariant_millisecond_format()
	{
		Assert.Equal(
			"1.80 ms / 20.00 ms",
			RuntimePerformanceDisplayFormatter.FormatFrameTime(
				TimeSpan.FromMilliseconds(1.8),
				TimeSpan.FromMilliseconds(20)));
	}

	[Fact]
	public void Output_rate_requires_positive_finite_measurement()
	{
		Assert.Equal("60.00 FPS", RuntimePerformanceDisplayFormatter.FormatFramesPerSecond(60));
		Assert.Equal("59.94 FPS", RuntimePerformanceDisplayFormatter.FormatFramesPerSecond(59.94));
		Assert.Equal(RuntimePerformanceDisplayFormatter.Unverified, RuntimePerformanceDisplayFormatter.FormatFramesPerSecond(null));
		Assert.Equal(RuntimePerformanceDisplayFormatter.Unverified, RuntimePerformanceDisplayFormatter.FormatFramesPerSecond(0));
		Assert.Equal(RuntimePerformanceDisplayFormatter.Unverified, RuntimePerformanceDisplayFormatter.FormatFramesPerSecond(double.NaN));
	}

	[Fact]
	public void Dropped_frames_use_invariant_integer_format()
	{
		Assert.Equal("0", RuntimePerformanceDisplayFormatter.FormatDroppedFrames(0));
		Assert.Equal("1234", RuntimePerformanceDisplayFormatter.FormatDroppedFrames(1234));
	}
}
