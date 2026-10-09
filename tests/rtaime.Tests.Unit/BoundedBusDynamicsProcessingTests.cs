// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class BoundedBusDynamicsProcessingTests
{
	private static readonly MediaSourceId Source = new(Identity.Parse("ad000000-0000-0000-0000-000000000001"));
	private static readonly AudioBusId Aux = new("aux");

	[Fact]
	public void Disabled_dynamics_is_exactly_bypass_equivalent()
	{
		var plain = new AudioProductionEngine(Configuration(null));
		var disabled = new AudioProductionEngine(Configuration(Dynamics(
			compressorEnabled: false,
			limiterEnabled: false)));
		var input = Stereo(256, 0.37f, -0.21f);
		var source = new[] { new AudioProductionSourceBuffer(Source, input) };
		var expected = new float[input.Length];
		var actual = new float[input.Length];

		var plainResult = plain.ProcessBus(AudioBusId.Program, 0, 256, Source, source, expected);
		var disabledResult = disabled.ProcessBus(AudioBusId.Program, 0, 256, Source, source, actual);

		Assert.Equal(expected, actual);
		Assert.Equal(plainResult.PreClipPeak, disabledResult.PreClipPeak);
		Assert.Equal(0, disabledResult.CompressorGainReductionDb);
		Assert.Equal(0, disabledResult.LimiterGainReductionDb);
		Assert.Equal(0UL, disabledResult.LimiterHitCount);
	}

	[Fact]
	public void Threshold_and_ratio_produce_expected_steady_state_level()
	{
		const double thresholdDbFs = -12d;
		const double ratio = 4d;
		var threshold = (float)Math.Pow(10d, thresholdDbFs / 20d);
		var thresholdEngine = new AudioProductionEngine(Configuration(Dynamics(
			thresholdDbFs: thresholdDbFs,
			ratio: ratio,
			attackMilliseconds: 0.1,
			limiterEnabled: false)));
		var thresholdOutput = new float[2_000];
		thresholdEngine.ProcessBus(
			AudioBusId.Program,
			0,
			1_000,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, Stereo(1_000, threshold, threshold)) },
			thresholdOutput);

		Assert.Equal(threshold, thresholdOutput[^2], 5);

		const float input = 0.8f;
		var engine = new AudioProductionEngine(Configuration(Dynamics(
			thresholdDbFs: thresholdDbFs,
			ratio: ratio,
			attackMilliseconds: 0.1,
			limiterEnabled: false)));
		var output = new float[2_000];
		engine.ProcessBus(
			AudioBusId.Program,
			0,
			1_000,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, Stereo(1_000, input, input)) },
			output);

		var inputDb = 20d * Math.Log10(input);
		var expectedDb = thresholdDbFs + ((inputDb - thresholdDbFs) / ratio);
		var expected = Math.Pow(10d, expectedDb / 20d);
		Assert.InRange(output[^2], (float)(expected - 0.001), (float)(expected + 0.001));
	}

	[Fact]
	public void Compressor_attack_and_release_progress_per_sample()
	{
		var engine = new AudioProductionEngine(Configuration(Dynamics(
			thresholdDbFs: -20,
			ratio: 10,
			attackMilliseconds: 10,
			releaseMilliseconds: 5,
			limiterEnabled: false)));
		var attack = new float[2_000];
		engine.ProcessBus(
			AudioBusId.Program,
			0,
			1_000,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, Stereo(1_000, 0.8f, 0.8f)) },
			attack);
		Assert.True(Math.Abs(attack[^2]) < Math.Abs(attack[0]));

		var release = new float[2_000];
		engine.ProcessBus(
			AudioBusId.Program,
			1_000,
			1_000,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, Stereo(1_000, 0.01f, 0.01f)) },
			release);
		Assert.True(Math.Abs(release[^2]) > Math.Abs(release[0]));
	}

	[Fact]
	public void Compressor_makeup_gain_is_applied_after_gain_reduction_stage()
	{
		var engine = new AudioProductionEngine(Configuration(Dynamics(
			thresholdDbFs: -20,
			ratio: 1,
			makeupGainDb: 6,
			limiterEnabled: false)));
		var output = new float[2];
		engine.ProcessBus(
			AudioBusId.Program,
			0,
			1,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, new float[] { 0.1f, -0.1f }) },
			output);

		var expected = 0.1d * Math.Pow(10d, 6d / 20d);
		Assert.Equal(expected, output[0], 6);
		Assert.Equal(-expected, output[1], 6);
	}

	[Fact]
	public void Sample_peak_limiter_never_exceeds_ceiling_and_is_stereo_linked()
	{
		const double ceilingDbFs = -6d;
		var ceiling = Math.Pow(10d, ceilingDbFs / 20d);
		var engine = new AudioProductionEngine(Configuration(Dynamics(
			compressorEnabled: false,
			limiterEnabled: true,
			ceilingDbFs: ceilingDbFs)));
		var output = new float[200];
		var result = engine.ProcessBus(
			AudioBusId.Program,
			0,
			100,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, Stereo(100, 0.9f, 0.1f)) },
			output);

		Assert.All(output, sample => Assert.InRange(Math.Abs((double)sample), 0, ceiling + 1e-6));
		Assert.Equal(0, result.SafetyClippedSampleValues);
		Assert.Equal(100UL, result.LimiterHitCount);
		Assert.True(result.LimiterGainReductionDb > 0);
		Assert.Equal(0.1 / 0.9, output[1] / output[0], 5);
	}

	[Fact]
	public void Non_finite_source_values_remain_finite_and_bounded()
	{
		var engine = new AudioProductionEngine(Configuration(Dynamics()));
		var output = new float[4];
		engine.ProcessBus(
			AudioBusId.Program,
			0,
			2,
			Source,
			new[]
			{
				new AudioProductionSourceBuffer(Source, new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.NaN })
			},
			output);

		Assert.All(output, sample =>
		{
			Assert.True(float.IsFinite(sample));
			Assert.InRange(sample, -1f, 1f);
		});
	}

	[Fact]
	public void Dynamics_state_is_invariant_to_contiguous_block_splitting()
	{
		var configuration = Configuration(Dynamics(
			thresholdDbFs: -18,
			ratio: 6,
			attackMilliseconds: 5,
			releaseMilliseconds: 80,
			ceilingDbFs: -3));
		var fullEngine = new AudioProductionEngine(configuration);
		var splitEngine = new AudioProductionEngine(configuration);
		var input = Stereo(1_000, 0.75f, -0.35f);
		var full = new float[input.Length];
		var split = new float[input.Length];

		fullEngine.ProcessBus(
			AudioBusId.Program,
			0,
			1_000,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, input) },
			full);

		splitEngine.ProcessBus(
			AudioBusId.Program,
			0,
			400,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, input.AsMemory(0, 800)) },
			split.AsSpan(0, 800));
		splitEngine.ProcessBus(
			AudioBusId.Program,
			400,
			600,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, input.AsMemory(800, 1_200)) },
			split.AsSpan(800, 1_200));

		Assert.Equal(full, split);
	}

	[Fact]
	public void Equivalent_configuration_retains_state_and_dynamics_change_resets_it()
	{
		var dynamics = Dynamics(
			thresholdDbFs: -30,
			ratio: 10,
			attackMilliseconds: 0.1,
			releaseMilliseconds: 1_000,
			limiterEnabled: false);
		var engine = new AudioProductionEngine(Configuration(dynamics));
		var loud = new float[1_920];
		engine.ProcessBus(
			AudioBusId.Program,
			0,
			960,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, Stereo(960, 0.8f, 0.8f)) },
			loud);

		engine.ApplyConfiguration(Configuration(dynamics, revision: 1));
		var retained = new float[2];
		engine.ProcessBus(
			AudioBusId.Program,
			960,
			1,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, new float[] { 0.005f, 0.005f }) },
			retained);
		Assert.True(retained[0] < 0.005f);

		var changed = Dynamics(
			thresholdDbFs: -10,
			ratio: 10,
			attackMilliseconds: 0.1,
			releaseMilliseconds: 1_000,
			limiterEnabled: false);
		engine.ApplyConfiguration(Configuration(changed, revision: 2));
		var reset = new float[2];
		engine.ProcessBus(
			AudioBusId.Program,
			961,
			1,
			Source,
			new[] { new AudioProductionSourceBuffer(Source, new float[] { 0.005f, 0.005f }) },
			reset);
		Assert.Equal(0.005f, reset[0]);
	}

	[Fact]
	public void Multiple_buses_keep_independent_dynamics_envelopes()
	{
		var dynamics = Dynamics(
			thresholdDbFs: -20,
			ratio: 10,
			attackMilliseconds: 0.1,
			releaseMilliseconds: 1_000,
			limiterEnabled: false);
		var engine = new AudioProductionEngine(new AudioProductionConfiguration(
			0,
			new[]
			{
				new AudioProductionBusConfiguration(AudioBusId.Program, 1, false, dynamics),
				new AudioProductionBusConfiguration(Aux, 1, false, dynamics)
			},
			new[]
			{
				new AudioProductionSourceConfiguration(Source, 1, false, false, new[] { AudioBusId.Program, Aux })
			}));
		var loud = Stereo(960, 0.8f, 0.8f);
		var low = Stereo(960, 0.01f, 0.01f);
		var output = new float[1_920];

		engine.ProcessBus(AudioBusId.Program, 0, 960, Source, new[] { new AudioProductionSourceBuffer(Source, loud) }, output);
		engine.ProcessBus(Aux, 0, 960, Source, new[] { new AudioProductionSourceBuffer(Source, low) }, output);

		var program = new float[2];
		var aux = new float[2];
		engine.ProcessBus(AudioBusId.Program, 960, 1, Source, new[] { new AudioProductionSourceBuffer(Source, new float[] { 0.01f, 0.01f }) }, program);
		engine.ProcessBus(Aux, 960, 1, Source, new[] { new AudioProductionSourceBuffer(Source, new float[] { 0.01f, 0.01f }) }, aux);

		Assert.True(program[0] < aux[0]);
		Assert.Equal(0.01f, aux[0], 6);
	}

	private static AudioProductionConfiguration Configuration(
		AudioBusDynamicsConfiguration? dynamics,
		ulong revision = 0) =>
		new(
			revision,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1, false, dynamics) },
			new[] { new AudioProductionSourceConfiguration(Source, 1, false, false, new[] { AudioBusId.Program }) });

	private static AudioBusDynamicsConfiguration Dynamics(
		bool compressorEnabled = true,
		double thresholdDbFs = -18,
		double ratio = 4,
		double attackMilliseconds = 5,
		double releaseMilliseconds = 100,
		double makeupGainDb = 0,
		bool limiterEnabled = true,
		double ceilingDbFs = -1) =>
		new(
			new AudioBusCompressorConfiguration(
				compressorEnabled,
				thresholdDbFs,
				ratio,
				attackMilliseconds,
				releaseMilliseconds,
				makeupGainDb),
			new AudioBusSamplePeakLimiterConfiguration(
				limiterEnabled,
				ceilingDbFs,
				releaseMilliseconds));

	private static float[] Stereo(int frames, float left, float right)
	{
		var samples = new float[frames * 2];
		for (var frame = 0; frame < frames; frame++)
		{
			samples[frame * 2] = left;
			samples[(frame * 2) + 1] = right;
		}
		return samples;
	}
}
