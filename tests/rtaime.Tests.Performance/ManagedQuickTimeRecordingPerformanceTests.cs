// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Performance;

public sealed class ManagedQuickTimeRecordingPerformanceTests
{
	[Fact]
	public void Mov_1080p_pixel_conversion_reuses_buffers_with_bounded_allocations()
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var input = new byte[checked((int)((long)format.Width * format.Height * 4))];
		var output = new byte[checked((int)((long)format.Width * format.Height * 2))];

		ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(
			input,
			output,
			checked((int)format.Width),
			checked((int)format.Height));

		const int iterations = 4;
		var stopwatch = new Stopwatch();
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		stopwatch.Start();
		for (var index = 0; index < iterations; index++)
		{
			ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(
				input,
				output,
				checked((int)format.Width),
				checked((int)format.Height));
		}
		stopwatch.Stop();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		var pixelsPerSecond = checked((double)format.Width * format.Height * iterations / stopwatch.Elapsed.TotalSeconds);

		Assert.True(
			allocated <= 1024,
			$"MOV RGBA-to-2vuy conversion allocated {allocated} bytes after warm-up.");
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Four 1080p MOV conversions took {stopwatch.Elapsed}; measured throughput was {pixelsPerSecond:N0} pixels/s.");
	}
}
