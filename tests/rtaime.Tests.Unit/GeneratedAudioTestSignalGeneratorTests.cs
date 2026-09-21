// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Unit;

public sealed class GeneratedAudioTestSignalGeneratorTests
{
	private static readonly AudioFormat StereoFormat = AudioFormat.Stereo48kFloat32;
	private static readonly Timebase AudioTimebase = new(1, 48_000);

	[Fact]
	public void Silence_writes_exact_zero_signal_for_requested_sample_count()
	{
		var generator = Create(GeneratedAudioTestSignalMode.Silence);
		var samples = new float[960 * 2];

		var frame = generator.FillInterleavedFloat32(Timing(0, 960), samples);

		Assert.All(samples, sample => Assert.Equal(0f, sample));
		Assert.Equal(0, frame.PeakLevel);
		Assert.Equal("NONE", frame.ActiveChannel);
	}

	[Fact]
	public void Destination_length_must_match_sample_count_and_channel_count()
	{
		var generator = Create(GeneratedAudioTestSignalMode.Tone);

		Assert.Throws<ArgumentException>(() =>
			generator.FillInterleavedFloat32(Timing(0, 960), new float[(960 * 2) - 1]));
	}

	[Fact]
	public void One_kilohertz_tone_has_expected_quarter_cycle_samples_at_48k()
	{
		var generator = Create(GeneratedAudioTestSignalMode.Tone);
		var samples = new float[49 * 2];

		generator.FillInterleavedFloat32(Timing(0, 49), samples);

		Assert.Equal(0f, samples[0], 5);
		Assert.Equal(0.25f, samples[12 * 2], 5);
		Assert.Equal(0f, samples[24 * 2], 5);
		Assert.Equal(-0.25f, samples[36 * 2], 5);
		Assert.Equal(0f, samples[48 * 2], 5);
		Assert.Equal(samples[12 * 2], samples[(12 * 2) + 1], 6);
	}

	[Fact]
	public void Tone_phase_is_continuous_across_buffer_boundaries()
	{
		var generator = Create(GeneratedAudioTestSignalMode.Tone);
		var first = new float[17 * 2];
		var second = new float[31 * 2];
		var whole = new float[48 * 2];

		generator.FillInterleavedFloat32(Timing(0, 17), first);
		generator.FillInterleavedFloat32(Timing(17, 31), second);
		generator.FillInterleavedFloat32(Timing(0, 48), whole);

		Assert.Equal(whole[..first.Length], first);
		Assert.Equal(whole[first.Length..], second);
	}

	[Fact]
	public void Standard_and_maximum_configured_levels_stay_below_full_scale()
	{
		var standard = Create(GeneratedAudioTestSignalMode.Tone);
		var maximum = new GeneratedAudioTestSignalGenerator(
			new GeneratedAudioTestSignalConfiguration(
				StereoFormat,
				GeneratedAudioTestSignalMode.Tone,
				1_000,
				GeneratedAudioTestSignalConfiguration.MaximumPeakLevel));
		var standardSamples = new float[960 * 2];
		var maximumSamples = new float[960 * 2];

		standard.FillInterleavedFloat32(Timing(0, 960), standardSamples);
		maximum.FillInterleavedFloat32(Timing(0, 960), maximumSamples);

		Assert.InRange(standardSamples.Max(sample => Math.Abs(sample)), 0, 0.250001f);
		Assert.InRange(maximumSamples.Max(sample => Math.Abs(sample)), 0, 0.500001f);
	}

	[Fact]
	public void Stereo_identification_cycles_left_right_and_both()
	{
		var generator = Create(GeneratedAudioTestSignalMode.StereoIdentification);

		var left = Render(generator, 0, 96);
		var right = Render(generator, 48_000, 96);
		var both = Render(generator, 96_000, 96);

		Assert.Contains(left.Where((_, index) => index % 2 == 0), sample => Math.Abs(sample) > 0.1f);
		Assert.All(left.Where((_, index) => index % 2 == 1), sample => Assert.Equal(0f, sample));
		Assert.All(right.Where((_, index) => index % 2 == 0), sample => Assert.Equal(0f, sample));
		Assert.Contains(right.Where((_, index) => index % 2 == 1), sample => Math.Abs(sample) > 0.1f);
		Assert.Contains(Enumerable.Range(0, 96), frame => Math.Abs(both[frame * 2]) > 0.1f && Math.Abs(both[(frame * 2) + 1]) > 0.1f);

		Assert.Equal("LEFT", generator.Inspect(Timing(0, 96)).ActiveChannel);
		Assert.Equal("RIGHT", generator.Inspect(Timing(48_000, 96)).ActiveChannel);
		Assert.Equal("BOTH", generator.Inspect(Timing(96_000, 96)).ActiveChannel);
	}

	[Fact]
	public void Channel_identification_follows_existing_mono_and_stereo_layout_order()
	{
		var stereo = Create(GeneratedAudioTestSignalMode.ChannelIdentification);
		Assert.Equal("LEFT", stereo.Inspect(Timing(0, 96)).ActiveChannel);
		Assert.Equal("RIGHT", stereo.Inspect(Timing(48_000, 96)).ActiveChannel);

		var monoFormat = new AudioFormat(48_000, AudioChannelLayout.Mono, AudioSampleFormat.Float32, 1);
		var mono = new GeneratedAudioTestSignalGenerator(
			new GeneratedAudioTestSignalConfiguration(
				monoFormat,
				GeneratedAudioTestSignalMode.ChannelIdentification));
		var monoSamples = new float[96];
		var monoFrame = mono.FillInterleavedFloat32(
			new AudioBufferTiming(0, 96, 0, AudioTimebase),
			monoSamples);

		Assert.Equal("MONO", monoFrame.ActiveChannel);
		Assert.Contains(monoSamples, sample => Math.Abs(sample) > 0.1f);
	}

	[Fact]
	public void Pulse_is_short_periodic_and_sample_clock_aligned()
	{
		var generator = Create(GeneratedAudioTestSignalMode.Pulse);
		var pulse = Render(generator, 0, 480);
		var silence = Render(generator, 1_000, 96);
		var repeated = Render(generator, 48_000, 480);

		Assert.Contains(pulse, sample => Math.Abs(sample) > 0.1f);
		Assert.All(silence, sample => Assert.Equal(0f, sample));
		Assert.Equal(pulse.Length, repeated.Length);
		for (var index = 0; index < pulse.Length; index++)
			Assert.Equal(pulse[index], repeated[index], 6);
		Assert.Equal(0, generator.Inspect(Timing(1_000, 96)).PeakLevel);
		Assert.Equal(0.25, generator.Inspect(Timing(48_000, 480)).PeakLevel, 6);
	}

	[Fact]
	public void Warm_fill_loop_does_not_allocate_on_generator_hot_path()
	{
		var generator = Create(GeneratedAudioTestSignalMode.Tone);
		var samples = new float[960 * 2];
		for (ulong index = 0; index < 8; index++)
			generator.FillInterleavedFloat32(Timing(index * 960, 960), samples);

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (ulong index = 8; index < 1_008; index++)
			generator.FillInterleavedFloat32(Timing(index * 960, 960), samples);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.InRange(allocated, 0, 1_024);
	}

	[Fact]
	public void Recreated_generator_is_bit_stable_at_the_same_sample_clock_position()
	{
		var configuration = new GeneratedAudioTestSignalConfiguration(
			StereoFormat,
			GeneratedAudioTestSignalMode.Tone,
			997,
			0.25);
		var firstGenerator = new GeneratedAudioTestSignalGenerator(configuration);
		var secondGenerator = new GeneratedAudioTestSignalGenerator(configuration);
		var first = new float[257 * 2];
		var second = new float[257 * 2];
		var timing = Timing(12_345, 257);

		firstGenerator.FillInterleavedFloat32(timing, first);
		secondGenerator.FillInterleavedFloat32(timing, second);

		Assert.Equal(first, second);
	}

	[Fact]
	public void PcmS16_configuration_fails_closed_until_runtime_payload_supports_it()
	{
		var pcm = new AudioFormat(48_000, AudioChannelLayout.Stereo, AudioSampleFormat.PcmS16, 2);

		Assert.Throws<NotSupportedException>(() =>
			new GeneratedAudioTestSignalConfiguration(
				pcm,
				GeneratedAudioTestSignalMode.Tone));
	}

	[Fact]
	public void Unsafe_level_and_invalid_stereo_layout_are_rejected()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new GeneratedAudioTestSignalConfiguration(
				StereoFormat,
				GeneratedAudioTestSignalMode.Tone,
				1_000,
				0.5001));

		var mono = new AudioFormat(48_000, AudioChannelLayout.Mono, AudioSampleFormat.Float32, 1);
		Assert.Throws<ArgumentException>(() =>
			new GeneratedAudioTestSignalConfiguration(
				mono,
				GeneratedAudioTestSignalMode.StereoIdentification));
	}

	private static GeneratedAudioTestSignalGenerator Create(GeneratedAudioTestSignalMode mode) =>
		new(new GeneratedAudioTestSignalConfiguration(StereoFormat, mode));

	private static float[] Render(
		GeneratedAudioTestSignalGenerator generator,
		ulong samplePosition,
		uint sampleCount)
	{
		var samples = new float[checked((int)sampleCount * checked((int)generator.Configuration.Format.ChannelCount))];
		generator.FillInterleavedFloat32(Timing(samplePosition, sampleCount), samples);
		return samples;
	}

	private static AudioBufferTiming Timing(ulong samplePosition, uint sampleCount) =>
		new(samplePosition, sampleCount, checked((long)samplePosition), AudioTimebase);
}
