// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class AudioProductionEngineTests
{
	private static readonly MediaSourceId SourceA = new(Identity.Parse("aa000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId SourceB = new(Identity.Parse("aa000000-0000-0000-0000-000000000002"));

	[Fact]
	public void Silence_is_deterministic()
	{
		var engine = new AudioProductionEngine(Configuration(
			Source(SourceA, AudioGain.Unity, false),
			Source(SourceB, AudioGain.Unity, false)));
		var output = new float[8];

		var result = engine.ProcessBus(
			AudioBusId.Program,
			0,
			4,
			SourceA,
			new[]
			{
				Buffer(SourceA, 4, 0, 0),
				Buffer(SourceB, 4, 0, 0)
			},
			output);

		Assert.All(output, sample => Assert.Equal(0f, sample));
		Assert.Equal(0, result.MasterPeak);
		Assert.False(result.Clipping);
	}

	[Fact]
	public void Legacy_compatible_configuration_only_emits_routed_source()
	{
		var engine = new AudioProductionEngine(
			AudioProductionConfiguration.CreateLegacyCompatible(new[] { SourceA, SourceB }));
		var output = new float[4];

		engine.ProcessBus(
			AudioBusId.Program,
			0,
			2,
			SourceB,
			new[]
			{
				Buffer(SourceA, 2, 0.75f, 0.75f),
				Buffer(SourceB, 2, 0.25f, -0.5f)
			},
			output);

		Assert.Equal(new[] { 0.25f, -0.5f, 0.25f, -0.5f }, output);
	}

	[Fact]
	public void Two_source_mix_applies_gain_and_mute_in_stable_order()
	{
		var engine = new AudioProductionEngine(Configuration(
			Source(SourceA, new AudioGain(0.5), false),
			Source(SourceB, AudioGain.Unity, false)));
		var output = new float[4];

		engine.ProcessBus(
			AudioBusId.Program,
			0,
			2,
			SourceA,
			new[]
			{
				Buffer(SourceB, 2, 0.1f, 0.2f),
				Buffer(SourceA, 2, 0.4f, -0.2f)
			},
			output);

		Assert.Equal(0.3f, output[0], 5);
		Assert.Equal(0.1f, output[1], 5);
		Assert.Equal(0.3f, output[2], 5);
		Assert.Equal(0.1f, output[3], 5);

		engine.ApplyConfiguration(Configuration(
			Source(SourceA, new AudioGain(0.5), true),
			Source(SourceB, AudioGain.Unity, false),
			revision: 1));
		engine.ProcessBus(
			AudioBusId.Program,
			2,
			2,
			SourceA,
			new[]
			{
				Buffer(SourceA, 2, 0.4f, -0.2f),
				Buffer(SourceB, 2, 0.1f, 0.2f)
			},
			output);

		Assert.Equal(new[] { 0.1f, 0.2f, 0.1f, 0.2f }, output);
	}

	[Fact]
	public void Master_gain_uses_explicit_hard_clip_and_reports_overload()
	{
		var engine = new AudioProductionEngine(new AudioProductionConfiguration(
			0,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 2d, false) },
			new[]
			{
				Source(SourceA, AudioGain.Unity, false),
				Source(SourceB, AudioGain.Unity, false)
			}));
		var output = new float[2];

		var result = engine.ProcessBus(
			AudioBusId.Program,
			0,
			1,
			SourceA,
			new[]
			{
				Buffer(SourceA, 1, 0.4f, -0.4f),
				Buffer(SourceB, 1, 0.4f, -0.4f)
			},
			output);

		Assert.Equal(1f, output[0]);
		Assert.Equal(-1f, output[1]);
		Assert.True(result.Clipping);
		Assert.Equal(2UL, result.ClippedSampleValues);
		Assert.Equal(1.6, result.PreClipPeak, 5);
	}

	[Fact]
	public void Equal_power_crossfade_has_exact_endpoints_and_expected_midpoint()
	{
		var crossfade = new AudioCrossfadeConfiguration(
			AudioBusId.Program,
			SourceA,
			SourceB,
			startSamplePosition: 100,
			durationSamples: 100,
			AudioCrossfadeLaw.EqualPower);
		var engine = new AudioProductionEngine(Configuration(
			Source(SourceA, AudioGain.Unity, false),
			Source(SourceB, AudioGain.Unity, false),
			crossfade: crossfade));
		var output = new float[2];
		var buffers = new[]
		{
			Buffer(SourceA, 1, 1f, 1f),
			Buffer(SourceB, 1, 0f, 0f)
		};

		var start = engine.ProcessBus(AudioBusId.Program, 100, 1, SourceA, buffers, output);
		Assert.Equal(1f, output[0], 6);
		Assert.Equal(0d, start.CrossfadeProgress);

		var midpoint = engine.ProcessBus(AudioBusId.Program, 150, 1, SourceA, buffers, output);
		Assert.Equal(Math.Sqrt(0.5), output[0], 5);
		Assert.Equal(0.5d, midpoint.CrossfadeProgress);

		var end = engine.ProcessBus(AudioBusId.Program, 200, 1, SourceA, buffers, output);
		Assert.Equal(0f, output[0], 6);
		Assert.Equal(1d, end.CrossfadeProgress);
	}

	[Fact]
	public void Ducking_attack_hold_release_and_sidechain_loss_are_sample_deterministic()
	{
		var ducking = new AudioDuckingConfiguration(
			AudioBusId.Program,
			true,
			SourceA,
			new[] { SourceB },
			threshold: 0.5,
			attenuation: 0.5,
			attackSamples: 2,
			holdSamples: 2,
			releaseSamples: 2);
		var engine = new AudioProductionEngine(Configuration(
			Source(SourceA, AudioGain.Unity, false),
			Source(SourceB, AudioGain.Unity, false),
			ducking: ducking));
		var output = new float[2];

		var active = new[]
		{
			Buffer(SourceA, 1, 1f, 1f),
			Buffer(SourceB, 1, 0.4f, 0.4f)
		};
		var inactive = new[]
		{
			Buffer(SourceA, 1, 0f, 0f),
			Buffer(SourceB, 1, 0.4f, 0.4f)
		};

		var attack1 = engine.ProcessBus(AudioBusId.Program, 0, 1, SourceA, active, output);
		Assert.Equal(0.75, attack1.DuckingGain, 6);
		var attack2 = engine.ProcessBus(AudioBusId.Program, 1, 1, SourceA, active, output);
		Assert.Equal(0.5, attack2.DuckingGain, 6);

		var hold1 = engine.ProcessBus(AudioBusId.Program, 2, 1, SourceA, inactive, output);
		var hold2 = engine.ProcessBus(AudioBusId.Program, 3, 1, SourceA, inactive, output);
		Assert.Equal(0.5, hold1.DuckingGain, 6);
		Assert.Equal(0.5, hold2.DuckingGain, 6);

		var release1 = engine.ProcessBus(AudioBusId.Program, 4, 1, SourceA, inactive, output);
		Assert.Equal(0.75, release1.DuckingGain, 6);

		var sidechainLost = new[]
		{
			new AudioProductionSourceBuffer(SourceA, ReadOnlyMemory<float>.Empty, Available: false),
			Buffer(SourceB, 1, 0.4f, 0.4f)
		};
		var release2 = engine.ProcessBus(AudioBusId.Program, 5, 1, SourceA, sidechainLost, output);
		Assert.Equal(1, release2.DuckingGain, 6);
		Assert.False(release2.SidechainAvailable);
	}

	[Fact]
	public void Crossfade_can_be_replaced_by_newer_configuration_revision()
	{
		var initial = Configuration(
			Source(SourceA, AudioGain.Unity, false),
			Source(SourceB, AudioGain.Unity, false),
			crossfade: new AudioCrossfadeConfiguration(AudioBusId.Program, SourceA, SourceB, 0, 100));
		var engine = new AudioProductionEngine(initial);
		var replacement = Configuration(
			Source(SourceA, AudioGain.Unity, false),
			Source(SourceB, AudioGain.Unity, false),
			revision: 1,
			crossfade: new AudioCrossfadeConfiguration(AudioBusId.Program, SourceB, SourceA, 50, 25));

		engine.ApplyConfiguration(replacement);

		Assert.Equal(1UL, engine.Configuration.Revision);
		Assert.Equal(SourceB, engine.Configuration.Crossfade!.FromSourceId);
		Assert.Throws<InvalidOperationException>(() => engine.ApplyConfiguration(initial));
	}

	[Fact]
	public void Steady_state_processing_does_not_allocate_per_block()
	{
		var engine = new AudioProductionEngine(Configuration(
			Source(SourceA, AudioGain.Unity, false),
			Source(SourceB, AudioGain.Unity, false)));
		var buffers = new[]
		{
			Buffer(SourceA, 960, 0.25f, -0.25f),
			Buffer(SourceB, 960, 0.1f, 0.1f)
		};
		var output = new float[1920];

		for (var warmup = 0; warmup < 64; warmup++)
			engine.ProcessBus(AudioBusId.Program, (ulong)(warmup * 960), 960, SourceA, buffers, output);

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var block = 0; block < 1000; block++)
			engine.ProcessBus(AudioBusId.Program, (ulong)((block + 64) * 960), 960, SourceA, buffers, output);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(0, allocated);
	}


	[Fact]
	public void Multiple_buses_mix_independently_and_allow_zero_or_multiple_assignments()
	{
		var aux = new AudioBusId("aux");
		var clean = new AudioBusId("clean");
		var engine = new AudioProductionEngine(new AudioProductionConfiguration(
			1,
			new[]
			{
				new AudioProductionBusConfiguration(AudioBusId.Program, 1d, false),
				new AudioProductionBusConfiguration(aux, 0.5d, false),
				new AudioProductionBusConfiguration(clean, 1d, true)
			},
			new[]
			{
				new AudioProductionSourceConfiguration(SourceA, 1d, false, false, new[] { AudioBusId.Program, aux }),
				new AudioProductionSourceConfiguration(SourceB, 1d, false, false, Array.Empty<AudioBusId>())
			}));

		var buffers = new[]
		{
			Buffer(SourceA, 2, 0.8f, -0.4f),
			Buffer(SourceB, 2, 0.7f, 0.7f)
		};
		var program = new float[4];
		var auxMix = new float[4];
		var cleanMix = new float[4];

		var programResult = engine.ProcessBus(AudioBusId.Program, 0, 2, SourceA, buffers, program);
		var auxResult = engine.ProcessBus(aux, 0, 2, SourceA, buffers, auxMix);
		var cleanResult = engine.ProcessBus(clean, 0, 2, SourceA, buffers, cleanMix);

		Assert.Equal(new[] { 0.8f, -0.4f, 0.8f, -0.4f }, program);
		Assert.Equal(new[] { 0.4f, -0.2f, 0.4f, -0.2f }, auxMix);
		Assert.All(cleanMix, sample => Assert.Equal(0f, sample));
		Assert.Equal(1, programResult.ActiveSourceCount);
		Assert.Equal(1, auxResult.ActiveSourceCount);
		Assert.Equal(0, cleanResult.ActiveSourceCount);
	}

	[Fact]
	public void More_than_four_buses_are_rejected()
	{
		var buses = new[]
		{
			new AudioProductionBusConfiguration(AudioBusId.Program, 1d, false),
			new AudioProductionBusConfiguration(new AudioBusId("aux"), 1d, false),
			new AudioProductionBusConfiguration(new AudioBusId("clean"), 1d, false),
			new AudioProductionBusConfiguration(new AudioBusId("iso"), 1d, false),
			new AudioProductionBusConfiguration(new AudioBusId("monitor"), 1d, false)
		};

		Assert.Throws<ArgumentException>(() => new AudioProductionConfiguration(
			1,
			buses,
			new[] { new AudioProductionSourceConfiguration(SourceA, 1d, false, false, new[] { AudioBusId.Program }) }));
	}

	private static AudioProductionConfiguration Configuration(
		AudioProductionSourceConfiguration first,
		AudioProductionSourceConfiguration second,
		ulong revision = 0,
		AudioCrossfadeConfiguration? crossfade = null,
		AudioDuckingConfiguration? ducking = null) =>
		new(
			revision,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1d, false) },
			new[] { first, second },
			crossfade,
			ducking);

	private static AudioProductionSourceConfiguration Source(
		MediaSourceId sourceId,
		AudioGain gain,
		bool muted) =>
		new(
			sourceId,
			gain.Linear,
			muted,
			followRoutedSource: false,
			new[] { AudioBusId.Program });

	private static AudioProductionSourceBuffer Buffer(
		MediaSourceId sourceId,
		int frames,
		float left,
		float right)
	{
		var samples = new float[frames * 2];
		for (var frame = 0; frame < frames; frame++)
		{
			samples[frame * 2] = left;
			samples[(frame * 2) + 1] = right;
		}
		return new AudioProductionSourceBuffer(sourceId, samples);
	}
}
