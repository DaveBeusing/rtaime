// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Performance;

public sealed class AdvancedAudioProcessingQualificationPerformanceTests
{
	[Fact]
	public void Full_eight_source_four_bus_processing_remains_bounded_allocation_free_and_finite()
	{
		var buses = new[]
		{
			AudioBusId.Program,
			new AudioBusId("aux"),
			new AudioBusId("clean"),
			new AudioBusId("iso")
		};
		var sources = Enumerable.Range(1, AudioProductionLimits.MaximumSources)
			.Select(index => new MediaSourceId(Identity.Parse($"b2000000-0000-0000-0000-{index:000000000000}")))
			.ToArray();
		var equalizer = new AudioSourceEqualizerConfiguration(
			new AudioLowShelfEqualizerBand(true, 180, 3),
			new AudioBellEqualizerBand(true, 1_200, -2, 1.25),
			new AudioHighShelfEqualizerBand(true, 7_500, 1));
		var dynamics = new AudioBusDynamicsConfiguration(
			new AudioBusCompressorConfiguration(true, -18, 4, 5, 120, 2),
			new AudioBusSamplePeakLimiterConfiguration(true, -1, 100));
		var configuration = new AudioProductionConfiguration(
			1,
			buses.Select(bus => new AudioProductionBusConfiguration(bus, 0.35, muted: false, dynamics)).ToArray(),
			sources.Select((source, index) => new AudioProductionSourceConfiguration(
				source,
				0.2,
				muted: false,
				followRoutedSource: index < 4,
				buses,
				equalizer)).ToArray(),
			new AudioCrossfadeConfiguration(
				AudioBusId.Program,
				sources[0],
				sources[4],
				startSamplePosition: 0,
				durationSamples: 48_000,
				AudioCrossfadeLaw.EqualPower),
			new AudioDuckingConfiguration(
				AudioBusId.Program,
				enabled: true,
				sources[0],
				sources.Skip(4).ToArray(),
				threshold: 0.2,
				attenuation: 0.25,
				attackSamples: 2_400,
				holdSamples: 12_000,
				releaseSamples: 14_400));

		var engine = new AudioProductionEngine(configuration);
		var payloads = sources
			.Select((source, index) => new AudioProductionSourceBuffer(
				source,
				CreateStereoBlock(960, index == 0 ? 0.35f : 0.04f * (index + 1))))
			.ToArray();
		var outputs = buses.Select(_ => new float[1_920]).ToArray();
		var results = new AudioProductionBlockResult[buses.Length];

		for (var warmup = 0; warmup < 64; warmup++)
		{
			var samplePosition = (ulong)warmup * 960;
			for (var busIndex = 0; busIndex < buses.Length; busIndex++)
			{
				results[busIndex] = engine.ProcessBus(
					buses[busIndex],
					samplePosition,
					960,
					sources[0],
					payloads,
					outputs[busIndex]);
			}
		}

		var stopwatch = Stopwatch.StartNew();
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var block = 0; block < 500; block++)
		{
			var samplePosition = (ulong)(block + 64) * 960;
			for (var busIndex = 0; busIndex < buses.Length; busIndex++)
			{
				results[busIndex] = engine.ProcessBus(
					buses[busIndex],
					samplePosition,
					960,
					sources[0],
					payloads,
					outputs[busIndex]);
			}
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		stopwatch.Stop();

		Assert.Equal(0, allocated);
		Assert.Equal(AudioProductionLimits.MaximumBuses, results.Length);
		Assert.All(results, result =>
		{
			Assert.InRange(result.MasterPeak, 0, 1);
			Assert.True(double.IsFinite(result.PreDynamicsPeak));
			Assert.True(double.IsFinite(result.PreClipPeak));
			Assert.True(double.IsFinite(result.CompressorGainReductionDb));
			Assert.True(double.IsFinite(result.LimiterGainReductionDb));
		});
		Assert.All(outputs.SelectMany(output => output), sample =>
		{
			Assert.True(float.IsFinite(sample));
			Assert.InRange(sample, -1f, 1f);
		});
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(30),
			$"Full advanced-audio hosted qualification exceeded 30 seconds: {stopwatch.Elapsed}.");
	}

	private static float[] CreateStereoBlock(int frames, float amplitude)
	{
		var samples = new float[checked(frames * 2)];
		for (var frame = 0; frame < frames; frame++)
		{
			var phase = 2d * Math.PI * (440d + ((frame + 1) % 17)) * frame / 48_000d;
			var value = amplitude * (float)Math.Sin(phase);
			samples[frame * 2] = value;
			samples[(frame * 2) + 1] = -value;
		}
		return samples;
	}
}
