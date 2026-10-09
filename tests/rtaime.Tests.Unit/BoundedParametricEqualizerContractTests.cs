// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class BoundedParametricEqualizerContractTests
{
	private static readonly MediaSourceId SourceA = new(Identity.Parse("ad000000-0000-0000-0000-000000000001"));

	[Fact]
	public void Parameters_are_typed_bounded_and_finite()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLowShelfEqualizerBand(true, 19.9, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLowShelfEqualizerBand(true, 200, double.NaN));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBellEqualizerBand(true, 1_000, 0, 0.09));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBellEqualizerBand(true, 1_000, 18.1, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioHighShelfEqualizerBand(true, 20_001, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioHighShelfEqualizerBand(true, 8_000, double.PositiveInfinity));

		var minimum = new AudioLowShelfEqualizerBand(true, AudioEqualizerLimits.MinimumFrequencyHz, AudioEqualizerLimits.MinimumGainDb);
		var maximum = new AudioHighShelfEqualizerBand(true, AudioEqualizerLimits.MaximumFrequencyHz, AudioEqualizerLimits.MaximumGainDb);
		var bell = new AudioBellEqualizerBand(true, 1_000, 0, AudioEqualizerLimits.MaximumBellQ);
		Assert.Equal(AudioEqualizerLimits.MinimumFrequencyHz, minimum.FrequencyHz);
		Assert.Equal(AudioEqualizerLimits.MaximumFrequencyHz, maximum.FrequencyHz);
		Assert.Equal(AudioEqualizerLimits.MaximumBellQ, bell.Q);
	}

	[Fact]
	public void Missing_equalizer_and_enabled_zero_db_equalizer_are_byte_equivalent()
	{
		var baseline = CreateEngine(null);
		var zeroDb = CreateEngine(new AudioSourceEqualizerConfiguration(
			new AudioLowShelfEqualizerBand(true, 200, 0),
			new AudioBellEqualizerBand(true, 1_000, 0, 1),
			new AudioHighShelfEqualizerBand(true, 6_000, 0)));
		var input = Tone(256, 997, 0.2f);
		var buffers = new[] { new AudioProductionSourceBuffer(SourceA, input) };
		var expected = new float[input.Length];
		var actual = new float[input.Length];

		baseline.ProcessBus(AudioBusId.Program, 0, 256, SourceA, buffers, expected);
		zeroDb.ProcessBus(AudioBusId.Program, 0, 256, SourceA, buffers, actual);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void Extreme_allowed_coefficients_remain_finite()
	{
		var equalizer = new AudioSourceEqualizerConfiguration(
			new AudioLowShelfEqualizerBand(true, AudioEqualizerLimits.MinimumFrequencyHz, AudioEqualizerLimits.MaximumGainDb),
			new AudioBellEqualizerBand(true, AudioEqualizerLimits.MaximumFrequencyHz, AudioEqualizerLimits.MinimumGainDb, AudioEqualizerLimits.MinimumBellQ),
			new AudioHighShelfEqualizerBand(true, AudioEqualizerLimits.MaximumFrequencyHz, AudioEqualizerLimits.MaximumGainDb));
		var engine = CreateEngine(equalizer, 0.1);
		var output = new float[1_920];

		engine.ProcessBus(
			AudioBusId.Program,
			0,
			960,
			SourceA,
			new[] { new AudioProductionSourceBuffer(SourceA, Tone(960, 7_000, 0.1f)) },
			output);

		Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
	}

	private static AudioProductionEngine CreateEngine(AudioSourceEqualizerConfiguration? equalizer, double gain = 0.75) =>
		new(new AudioProductionConfiguration(
			0,
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
			}));

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
