// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class AdvancedAudioProcessingQualificationTests
{
	private static readonly MediaSourceId SourceA = new(Identity.Parse("b1000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId SourceB = new(Identity.Parse("b1000000-0000-0000-0000-000000000002"));

	[Fact]
	public void Complete_processing_order_matches_golden_vector()
	{
		var equalizer = new AudioSourceEqualizerConfiguration(
			new AudioLowShelfEqualizerBand(true, 200, 3),
			new AudioBellEqualizerBand(true, 1_000, -2, 1.2),
			new AudioHighShelfEqualizerBand(true, 7_500, 1));
		var dynamics = new AudioBusDynamicsConfiguration(
			new AudioBusCompressorConfiguration(true, -18, 4, 0.1, 100, 6),
			new AudioBusSamplePeakLimiterConfiguration(true, -6, 100));
		var configuration = new AudioProductionConfiguration(
			1,
			new[]
			{
				new AudioProductionBusConfiguration(AudioBusId.Program, 1.5, muted: false, dynamics)
			},
			new[]
			{
				new AudioProductionSourceConfiguration(
					SourceA,
					0.8,
					muted: false,
					followRoutedSource: false,
					new[] { AudioBusId.Program }),
				new AudioProductionSourceConfiguration(
					SourceB,
					1.0,
					muted: false,
					followRoutedSource: false,
					new[] { AudioBusId.Program },
					equalizer)
			},
			new AudioCrossfadeConfiguration(
				AudioBusId.Program,
				SourceA,
				SourceB,
				startSamplePosition: 0,
				durationSamples: 4,
				AudioCrossfadeLaw.EqualPower),
			new AudioDuckingConfiguration(
				AudioBusId.Program,
				enabled: true,
				SourceA,
				new[] { SourceB },
				threshold: 0.5,
				attenuation: 0.5,
				attackSamples: 2,
				holdSamples: 0,
				releaseSamples: 2));

		var sourceA = Stereo(4, 0.9f, -0.9f);
		var sourceB = Stereo(4, 0.8f, 0.4f);
		var output = new float[8];
		var engine = new AudioProductionEngine(configuration);

		var result = engine.ProcessBus(
			AudioBusId.Program,
			0,
			4,
			SourceA,
			new[]
			{
				new AudioProductionSourceBuffer(SourceA, sourceA),
				new AudioProductionSourceBuffer(SourceB, sourceB)
			},
			output);

		var expected = new[]
		{
			0.5011872f, -0.5011872f,
			0.4846070f, -0.3495371f,
			0.3975844f, -0.1911560f,
			0.2799219f, -0.0455740f
		};
		for (var index = 0; index < expected.Length; index++)
			Assert.Equal(expected[index], output[index], 5);

		Assert.Equal(0.75, result.CrossfadeProgress);
		Assert.Equal(0.5, result.DuckingGain, 6);
		Assert.True(result.PreDynamicsPeak > result.PreClipPeak);
		Assert.True(result.CompressorGainReductionDb > 0);
		Assert.True(result.LimiterGainReductionDb > 0);
		Assert.Equal(4UL, result.LimiterHitCount);
		Assert.Equal(0UL, result.SafetyClippedSampleValues);
		Assert.All(output, sample =>
		{
			Assert.True(float.IsFinite(sample));
			Assert.InRange(sample, -1f, 1f);
		});
	}

	private static float[] Stereo(int frames, float left, float right)
	{
		var samples = new float[checked(frames * 2)];
		for (var frame = 0; frame < frames; frame++)
		{
			samples[frame * 2] = left;
			samples[(frame * 2) + 1] = right;
		}
		return samples;
	}
}
