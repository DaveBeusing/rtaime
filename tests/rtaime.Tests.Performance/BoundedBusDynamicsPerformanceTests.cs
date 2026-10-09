// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Performance;

public sealed class BoundedBusDynamicsPerformanceTests
{
	[Fact]
	public void Maximum_eight_source_four_bus_dynamics_processing_is_allocation_free_after_warmup()
	{
		var buses = new[]
		{
			AudioBusId.Program,
			new AudioBusId("aux"),
			new AudioBusId("clean"),
			new AudioBusId("iso")
		};
		var sources = Enumerable.Range(1, AudioProductionLimits.MaximumSources)
			.Select(index => new MediaSourceId(Identity.Parse($"ae000000-0000-0000-0000-{index:000000000000}")))
			.ToArray();
		var dynamics = new AudioBusDynamicsConfiguration(
			new AudioBusCompressorConfiguration(true, -18, 4, 5, 120, 2),
			new AudioBusSamplePeakLimiterConfiguration(true, -1, 100));
		var configuration = new AudioProductionConfiguration(
			1,
			buses.Select(bus => new AudioProductionBusConfiguration(bus, 0.5, false, dynamics)).ToArray(),
			sources.Select(source => new AudioProductionSourceConfiguration(
				source,
				0.25,
				muted: false,
				followRoutedSource: false,
				buses)).ToArray());
		var engine = new AudioProductionEngine(configuration);
		var payloads = sources
			.Select((source, index) => new AudioProductionSourceBuffer(
				source,
				CreateStereoBlock(960, 0.04f * (index + 1))))
			.ToArray();
		var outputs = buses.Select(_ => new float[1_920]).ToArray();

		for (var warmup = 0; warmup < 64; warmup++)
		{
			for (var busIndex = 0; busIndex < buses.Length; busIndex++)
				engine.ProcessBus(buses[busIndex], (ulong)warmup * 960, 960, sources[0], payloads, outputs[busIndex]);
		}

		var stopwatch = Stopwatch.StartNew();
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var block = 0; block < 500; block++)
		{
			for (var busIndex = 0; busIndex < buses.Length; busIndex++)
				engine.ProcessBus(buses[busIndex], (ulong)(block + 64) * 960, 960, sources[0], payloads, outputs[busIndex]);
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		stopwatch.Stop();

		Assert.Equal(0, allocated);
		Assert.All(outputs.SelectMany(output => output), sample =>
		{
			Assert.True(float.IsFinite(sample));
			Assert.InRange(sample, -1f, 1f);
		});
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(15),
			$"Bounded bus dynamics hosted qualification exceeded 15 seconds: {stopwatch.Elapsed}.");
	}

	private static float[] CreateStereoBlock(int frames, float amplitude)
	{
		var samples = new float[frames * 2];
		for (var frame = 0; frame < frames; frame++)
		{
			var phase = 2d * Math.PI * (440d + (frame % 13)) * frame / 48_000d;
			var value = amplitude * (float)Math.Sin(phase);
			samples[frame * 2] = value;
			samples[(frame * 2) + 1] = -value;
		}
		return samples;
	}
}
