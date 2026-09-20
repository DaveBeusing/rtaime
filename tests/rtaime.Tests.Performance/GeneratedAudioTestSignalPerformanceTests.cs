// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Performance;

public sealed class GeneratedAudioTestSignalPerformanceTests
{
	[Theory]
	[InlineData(GeneratedAudioTestSignalMode.Tone)]
	[InlineData(GeneratedAudioTestSignalMode.StereoIdentification)]
	[InlineData(GeneratedAudioTestSignalMode.ChannelIdentification)]
	[InlineData(GeneratedAudioTestSignalMode.Pulse)]
	public void Repeated_audio_generation_has_no_ongoing_managed_allocation_growth(
		GeneratedAudioTestSignalMode mode)
	{
		var format = AudioFormat.Stereo48kFloat32;
		var generator = new GeneratedAudioTestSignalGenerator(
			new GeneratedAudioTestSignalConfiguration(format, mode));
		var samples = new float[960 * 2];
		var timebase = new Timebase(1, 48_000);

		for (ulong sequence = 0; sequence < 16; sequence++)
		{
			var samplePosition = sequence * 960;
			generator.FillInterleavedFloat32(
				new AudioBufferTiming(samplePosition, 960, checked((long)samplePosition), timebase),
				samples);
		}

		var stopwatch = Stopwatch.StartNew();
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (ulong sequence = 16; sequence < 2_016; sequence++)
		{
			var samplePosition = sequence * 960;
			generator.FillInterleavedFloat32(
				new AudioBufferTiming(samplePosition, 960, checked((long)samplePosition), timebase),
				samples);
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		stopwatch.Stop();

		Assert.InRange(allocated, 0, 2_048);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Generated audio hot-path regression exceeded 10 seconds: {stopwatch.Elapsed}.");
		Assert.All(samples, sample => Assert.InRange(sample, -0.500001f, 0.500001f));
	}
}
