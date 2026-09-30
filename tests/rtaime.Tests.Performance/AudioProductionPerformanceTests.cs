// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Performance;

public sealed class AudioProductionPerformanceTests
{
	[Fact]
	public void Maximum_input_mix_with_crossfade_and_ducking_is_bounded_and_allocation_free_after_warmup()
	{
		var sources = Enumerable.Range(1, AudioProductionLimits.MaximumSources)
			.Select(index => new MediaSourceId(Identity.Parse($"ab000000-0000-0000-0000-{index:000000000000}")))
			.ToArray();
		var configuration = new AudioProductionConfiguration(
			1,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 0.5, false) },
			sources.Select(source => new AudioProductionSourceConfiguration(
				source,
				0.5,
				muted: false,
				followRoutedSource: false,
				new[] { AudioBusId.Program })).ToArray(),
			new AudioCrossfadeConfiguration(
				AudioBusId.Program,
				sources[0],
				sources[1],
				0,
				48_000,
				AudioCrossfadeLaw.EqualPower),
			new AudioDuckingConfiguration(
				AudioBusId.Program,
				true,
				sources[0],
				sources.Skip(1).ToArray(),
				0.2,
				0.25,
				2_400,
				12_000,
				14_400));

		var engine = new AudioProductionEngine(configuration);
		var payloads = sources
			.Select((source, index) => new AudioProductionSourceBuffer(
				source,
				CreateStereoBlock(960, index == 0 ? 0.35f : 0.05f)))
			.ToArray();
		var output = new float[1_920];

		for (var index = 0; index < 64; index++)
			engine.ProcessBus(AudioBusId.Program, (ulong)index * 960, 960, sources[0], payloads, output);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		AudioProductionBlockResult result = default;
		for (var index = 0; index < 1_000; index++)
		{
			result = engine.ProcessBus(
				AudioBusId.Program,
				(ulong)(index + 64) * 960,
				960,
				sources[0],
				payloads,
				output);
		}
		stopwatch.Stop();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.InRange(result.MasterPeak, 0, 1);
		Assert.Equal(AudioProductionLimits.MaximumSources, result.ActiveSourceCount);
		Assert.Equal(0, allocated);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Advanced audio hosted qualification exceeded 10 seconds for 1,000 maximum-input blocks: {stopwatch.Elapsed}.");
	}

	private static float[] CreateStereoBlock(int frames, float amplitude)
	{
		var samples = new float[checked(frames * 2)];
		for (var frame = 0; frame < frames; frame++)
		{
			samples[frame * 2] = amplitude;
			samples[(frame * 2) + 1] = -amplitude;
		}
		return samples;
	}
}
