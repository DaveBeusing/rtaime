// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Integration;

public sealed class MotionTimingTestSignalGeneratorTests
{
	[Theory]
	[InlineData(25, 1, 25UL, 0, 0, 1, 0)]
	[InlineData(50, 1, 50UL, 0, 0, 1, 0)]
	[InlineData(60_000, 1_001, 60UL, 0, 0, 1, 0)]
	[InlineData(60_000, 1_001, 3_597UL, 0, 1, 0, 0)]
	public void Timecode_is_derived_from_rational_media_time_without_nominal_rate_drift(
		long numerator,
		long denominator,
		ulong sequence,
		int hours,
		int minutes,
		int seconds,
		int frames)
	{
		var timecode = MotionTimingTestSignalGenerator.CalculateTimecode(
			sequence,
			new FrameRate(numerator, denominator));

		Assert.Equal(new MotionTimingTimecode(hours, minutes, seconds, frames), timecode);
	}

	[Fact]
	public void Marker_uses_media_time_and_wraps_on_one_second_period()
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var generator = new MotionTimingTestSignalGenerator(format);
		var timebase = new Timebase(1, 50);

		var start = generator.GetMarkerX(new FrameTiming(0, 0, timebase));
		var half = generator.GetMarkerX(new FrameTiming(25, 25, timebase));
		var wrapped = generator.GetMarkerX(new FrameTiming(50, 50, timebase));

		Assert.Equal(start, wrapped);
		Assert.True(half > start + generator.RegionWidth / 3);
		Assert.True(half < start + generator.RegionWidth * 2 / 3);
	}

	[Fact]
	public void Fractional_rate_marker_uses_exact_frame_timebase()
	{
		var format = VideoFormat.Hd1080p59_94Rgba8;
		var generator = new MotionTimingTestSignalGenerator(format);
		var timebase = new Timebase(1_001, 60_000);

		var start = generator.GetMarkerX(new FrameTiming(0, 0, timebase));
		var nearHalf = generator.GetMarkerX(new FrameTiming(30, 30, timebase));
		var nextSecond = generator.GetMarkerX(new FrameTiming(60, 60, timebase));

		Assert.True(nearHalf > start + generator.RegionWidth / 3);
		Assert.True(nearHalf < start + generator.RegionWidth * 2 / 3);
		Assert.True(nextSecond >= start);
		Assert.True(nextSecond < start + generator.RegionWidth / 20);
	}

	[Fact]
	public void Same_frame_timing_is_bit_stable_and_successive_frames_are_visibly_distinct()
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var generator = new MotionTimingTestSignalGenerator(format);
		var timebase = new Timebase(1, 50);

		var frame17 = new FrameTiming(17, 17, timebase);
		var first = SHA256.HashData(generator.Render(frame17).Pixels.Span);
		var frame18 = SHA256.HashData(generator.Render(new FrameTiming(18, 18, timebase)).Pixels.Span);
		var repeated = SHA256.HashData(generator.Render(frame17).Pixels.Span);

		Assert.Equal(first, repeated);
		Assert.NotEqual(first, frame18);
	}

	[Fact]
	public void Render_reuses_the_same_region_storage()
	{
		var generator = new MotionTimingTestSignalGenerator(VideoFormat.Hd1080p50Rgba8);
		var timebase = new Timebase(1, 50);

		var first = generator.Render(new FrameTiming(1, 1, timebase));
		var second = generator.Render(new FrameTiming(2, 2, timebase));

		Assert.True(first.Pixels.Equals(second.Pixels));
		Assert.Equal(first.Width * first.Height * 4, first.Pixels.Length);
	}

	[Fact]
	public void Warm_render_loop_has_no_meaningful_managed_allocation_growth()
	{
		var generator = new MotionTimingTestSignalGenerator(VideoFormat.Hd1080p50Rgba8);
		var timebase = new Timebase(1, 50);
		for (ulong sequence = 0; sequence < 8; sequence++)
			generator.Render(new FrameTiming(sequence, checked((long)sequence), timebase));

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (ulong sequence = 8; sequence < 108; sequence++)
			generator.Render(new FrameTiming(sequence, checked((long)sequence), timebase));
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.InRange(allocated, 0, 8_192);
	}
}
