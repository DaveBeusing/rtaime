// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class BoundedParametricEqualizerProcessingTests
{
	private static readonly MediaSourceId SourceA = new(Identity.Parse("ae000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId SourceB = new(Identity.Parse("ae000000-0000-0000-0000-000000000002"));

	[Fact]
	public void Low_mid_and_high_bands_move_expected_frequency_regions()
	{
		var lowBaseline = MeasureToneRms(Equalizer(), 100);
		var lowBoost = MeasureToneRms(Equalizer(lowGainDb: 6), 100);
		Assert.True(lowBoost > lowBaseline * 1.5, $"Low-shelf boost was too small: baseline={lowBaseline}, boosted={lowBoost}.");

		var midBaseline = MeasureToneRms(Equalizer(), 1_000);
		var midCut = MeasureToneRms(Equalizer(midGainDb: -6), 1_000);
		Assert.True(midCut < midBaseline * 0.75, $"Bell cut was too small: baseline={midBaseline}, cut={midCut}.");

		var highBaseline = MeasureToneRms(Equalizer(), 10_000);
		var highBoost = MeasureToneRms(Equalizer(highGainDb: 6), 10_000);
		Assert.True(highBoost > highBaseline * 1.5, $"High-shelf boost was too small: baseline={highBaseline}, boosted={highBoost}.");
	}

	[Fact]
	public void Left_and_right_filter_state_are_independent()
	{
		var engine = CreateSingleSourceEngine(Equalizer(lowGainDb: 12), 0.25);
		var samples = new float[64];
		samples[0] = 1f;
		var output = new float[64];

		engine.ProcessBus(
			AudioBusId.Program,
			0,
			32,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, samples) },
			output);

		Assert.Contains(output.Where((_, index) => index % 2 == 0), sample => Math.Abs(sample) > 0.000001f);
		Assert.All(output.Where((_, index) => index % 2 == 1), sample => Assert.Equal(0f, sample));
	}

	[Fact]
	public void Same_source_state_is_replayed_for_multiple_buses_in_one_sample_window()
	{
		var aux = new AudioBusId("aux");
		var equalizer = Equalizer(lowGainDb: 9, midGainDb: -3, highGainDb: 4);
		var configuration = new AudioProductionConfiguration(
			1,
			new[]
			{
				new AudioProductionBusConfiguration(AudioBusId.Program, 1, false),
				new AudioProductionBusConfiguration(aux, 1, false)
			},
			new[]
			{
				new AudioProductionSourceConfiguration(SourceA, 0.25, false, false, new[] { AudioBusId.Program, aux }, equalizer)
			});
		var engine = new AudioProductionEngine(configuration);
		var input = Tone(256, 440, 0.1f);
		var buffers = new[] { new AudioProductionSourceBuffer(SourceA, input) };
		var program = new float[input.Length];
		var auxOutput = new float[input.Length];

		engine.ProcessBus(AudioBusId.Program, 0, 256, SourceA, buffers, program);
		engine.ProcessBus(aux, 0, 256, SourceA, buffers, auxOutput);

		Assert.Equal(program, auxOutput);
	}

	[Fact]
	public void Filter_state_is_isolated_by_source_identity()
	{
		var aux = new AudioBusId("aux");
		var equalizer = Equalizer(lowGainDb: 12);
		var engine = new AudioProductionEngine(new AudioProductionConfiguration(
			1,
			new[]
			{
				new AudioProductionBusConfiguration(AudioBusId.Program, 1, false),
				new AudioProductionBusConfiguration(aux, 1, false)
			},
			new[]
			{
				new AudioProductionSourceConfiguration(SourceA, 0.25, false, false, new[] { AudioBusId.Program }, equalizer),
				new AudioProductionSourceConfiguration(SourceB, 0.25, false, false, new[] { aux }, equalizer)
			}));
		var sourceA = new float[128];
		sourceA[0] = 1f;
		sourceA[1] = 1f;
		var buffers = new[]
		{
			new AudioProductionSourceBuffer(SourceA, sourceA),
			new AudioProductionSourceBuffer(SourceB, new float[128])
		};
		var program = new float[128];
		var auxOutput = new float[128];

		engine.ProcessBus(AudioBusId.Program, 0, 64, SourceA, buffers, program);
		engine.ProcessBus(aux, 0, 64, SourceA, buffers, auxOutput);

		Assert.Contains(program, sample => Math.Abs(sample) > 0.000001f);
		Assert.All(auxOutput, sample => Assert.Equal(0f, sample));
	}

	[Fact]
	public void Equivalent_configuration_retains_state_and_equalizer_change_resets_it()
	{
		var equalizer = Equalizer(lowGainDb: 12);
		var engine = CreateSingleSourceEngine(equalizer, 0.25);
		var impulse = new float[32];
		impulse[0] = 1f;
		impulse[1] = 1f;
		var output = new float[32];

		engine.ProcessBus(
			AudioBusId.Program,
			0,
			16,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, impulse) },
			output);

		engine.ApplyConfiguration(Configuration(1, equalizer, 0.25));
		Array.Clear(output);
		engine.ProcessBus(
			AudioBusId.Program,
			16,
			16,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, new float[32]) },
			output);
		Assert.Contains(output, sample => Math.Abs(sample) > 0.000001f);

		engine.ApplyConfiguration(Configuration(2, Equalizer(lowGainDb: -12), 0.25));
		Array.Clear(output);
		engine.ProcessBus(
			AudioBusId.Program,
			32,
			16,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, new float[32]) },
			output);
		Assert.All(output, sample => Assert.Equal(0f, sample));
	}

	[Fact]
	public void Processing_is_invariant_to_valid_block_splitting()
	{
		const int frames = 1_024;
		const int split = 377;
		var equalizer = Equalizer(lowGainDb: 4, midGainDb: -5, highGainDb: 3);
		var fullEngine = CreateSingleSourceEngine(equalizer, 0.3);
		var splitEngine = CreateSingleSourceEngine(equalizer, 0.3);
		var tone = Tone(frames, 1_337, 0.15f);
		var full = new float[frames * 2];
		var splitOutput = new float[frames * 2];

		fullEngine.ProcessBus(
			AudioBusId.Program,
			0,
			frames,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, tone) },
			full);

		splitEngine.ProcessBus(
			AudioBusId.Program,
			0,
			split,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, tone.AsMemory(0, split * 2)) },
			splitOutput.AsSpan(0, split * 2));
		splitEngine.ProcessBus(
			AudioBusId.Program,
			(uint)split,
			frames - split,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, tone.AsMemory(split * 2)) },
			splitOutput.AsSpan(split * 2));

		for (var index = 0; index < full.Length; index++)
			Assert.Equal(full[index], splitOutput[index], 5);
	}

	private static AudioProductionEngine CreateSingleSourceEngine(
		AudioSourceEqualizerConfiguration equalizer,
		double gain) =>
		new(Configuration(0, equalizer, gain));

	private static AudioProductionConfiguration Configuration(
		ulong revision,
		AudioSourceEqualizerConfiguration equalizer,
		double gain) =>
		new(
			revision,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1, false) },
			new[]
			{
				new AudioProductionSourceConfiguration(
					SourceA,
					gain,
					muted: false,
					followRoutedSource: false,
					new[] { AudioBusId.Program },
					equalizer)
			});

	private static AudioSourceEqualizerConfiguration Equalizer(
		double lowGainDb = 0,
		double midGainDb = 0,
		double highGainDb = 0) =>
		new(
			new AudioLowShelfEqualizerBand(true, 200, lowGainDb),
			new AudioBellEqualizerBand(true, 1_000, midGainDb, 1),
			new AudioHighShelfEqualizerBand(true, 6_000, highGainDb));

	private static double MeasureToneRms(AudioSourceEqualizerConfiguration equalizer, double frequencyHz)
	{
		const int frames = 4_096;
		const int settleFrames = 512;
		var engine = CreateSingleSourceEngine(equalizer, 0.25);
		var output = new float[frames * 2];
		engine.ProcessBus(
			AudioBusId.Program,
			0,
			frames,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, Tone(frames, frequencyHz, 0.2f)) },
			output);

		double sumSquares = 0;
		var count = 0;
		for (var frame = settleFrames; frame < frames; frame++)
		{
			var value = output[frame * 2];
			sumSquares += value * value;
			count++;
		}
		return Math.Sqrt(sumSquares / count);
	}

	private static float[] Tone(int frames, double frequencyHz, float amplitude)
	{
		var samples = new float[frames * 2];
		for (var frame = 0; frame < frames; frame++)
		{
			var value = amplitude * (float)Math.Sin(2d * Math.PI * frequencyHz * frame / 48_000d);
			samples[frame * 2] = value;
			samples[(frame * 2) + 1] = value;
		}
		return samples;
	}
}
