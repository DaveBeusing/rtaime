// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class ProgrammeLoudnessAnalyzerTests
{
	private static readonly MediaSourceId A = new(Identity.Parse("aa000000-0000-0000-0000-0000000000a1"));
	private static readonly MediaSourceId B = new(Identity.Parse("aa000000-0000-0000-0000-0000000000b1"));

	[Fact]
	public void Calibration_at_997_hz_and_channel_swap_respects_stereo_weighting()
	{
		var mono = MeasureSine(997, 0.1, left: true, right: false, seconds: 6);
		var swapped = MeasureSine(997, 0.1, left: false, right: true, seconds: 6);
		var stereo = MeasureSine(997, 0.1, left: true, right: true, seconds: 6);
		Assert.Equal("CURRENT", mono.State);
		Assert.InRange(mono.IntegratedLufs!.Value, -24.0, -22.0);
		Assert.InRange(Math.Abs(mono.IntegratedLufs.Value - swapped.IntegratedLufs!.Value), 0, 0.05);
		Assert.InRange(stereo.IntegratedLufs!.Value - mono.IntegratedLufs.Value, 2.9, 3.1);
		Assert.NotNull(mono.MomentaryLufs);
		Assert.NotNull(mono.ShortTermLufs);
		Assert.Equal(1, mono.FormatVersion);
		Assert.Equal("UNVERIFIED", mono.Qualification);
	}

	[Fact]
	public void Gated_integrated_level_excludes_silence_without_making_up_zeros()
	{
		var analyzer = new ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy.EbuR128Linear);
		var block = new float[9600];
		uint blockFrames = 4800;
		ulong position = 0;
		for (var secondTenth = 0; secondTenth < 50; secondTenth++)
		{
			WriteSine(block, 997, 0.1, position, true, false);
			analyzer.Process(block, position, A);
			position += blockFrames;
		}
		var initial = analyzer.Read("program");
		Assert.NotNull(initial.IntegratedLufs);
		Array.Clear(block);
		for (var secondTenth = 0; secondTenth < 50; secondTenth++)
		{
			analyzer.Process(block, position, A);
			position += blockFrames;
		}
		var after = analyzer.Read("program");
		Assert.InRange(Math.Abs(after.IntegratedLufs!.Value - initial.IntegratedLufs.Value), 0, 0.3);
		Assert.Null(MeasureSilence().IntegratedLufs);
		Assert.Equal("SILENCE", MeasureSilence().State);
	}

	[Fact]
	public void Four_times_interpolation_catches_an_intersample_peak_above_sample_peak()
	{
		var analyzer = new ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy.EbuR128Linear);
		var block = new float[9600];
		var samplePeak = 0d;
		ulong position = 0;
		for (var index = 0; index < 20; index++)
		{
			for (var frame = 0; frame < 4800; frame++)
			{
				var value = (float)(0.9 * Math.Sin(2 * Math.PI * 12000 * (position + (ulong)frame) / 48000 + Math.PI / 4));
				block[frame * 2] = value;
				block[frame * 2 + 1] = value;
				samplePeak = Math.Max(samplePeak, Math.Abs(value));
			}
			analyzer.Process(block, position, A);
			position += 4800;
		}
		var reading = analyzer.Read("program");
		Assert.NotNull(reading.TruePeakDbtp);
		Assert.True(reading.TruePeakDbtp.Value > 20 * Math.Log10(samplePeak) + 1,
			"True peak must detect reconstructed inter-sample overshoot.");
		Assert.True(reading.TruePeakDbtp.Value < 1.0);
	}

	[Fact]
	public void Source_change_gap_and_underrun_invalidate_previous_integrated_session()
	{
		var analyzer = new ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy.EbuR128Linear);
		var block = new float[9600];
		WriteSine(block, 997, 0.1, 0, true, true);
		for (ulong n = 0; n < 50; n++)
			analyzer.Process(block, n * 4800, A);
		Assert.NotNull(analyzer.Read("program").IntegratedLufs);
		var before = analyzer.ResetGeneration;
		analyzer.Process(block, 50 * 4800, B);
		Assert.True(analyzer.ResetGeneration > before);
		Assert.Null(analyzer.Read("program").IntegratedLufs);
		before = analyzer.ResetGeneration;
		analyzer.Process(block, 80 * 4800, B);
		Assert.True(analyzer.ResetGeneration > before);
		analyzer.Interrupt();
		Assert.Equal("UNVERIFIED", analyzer.Read("program").State);
		Assert.Null(analyzer.Read("program").TruePeakDbtp);
	}

	[Fact]
	public void Post_mix_analysis_does_not_modify_original_program_samples_or_allocate_per_block()
	{
		var engine = new AudioProductionEngine(AudioProductionConfiguration.CreateLegacyCompatible([A, B]));
		var source = new float[] { 0.1f, -0.2f, 0.4f, -0.3f };
		var output = new float[4];
		var sources = new[] { new AudioProductionSourceBuffer(A, source.AsMemory()), new AudioProductionSourceBuffer(B, source.AsMemory()) };
		engine.ProcessBus(AudioBusId.Program, 0, 2, A, sources, output);
		var expected = output.ToArray();
		var analyzer = new ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy.EbuR128Linear);
		analyzer.Process(output, 0, A);
		Assert.Equal(expected, output);

		var block = new float[9600];
		analyzer.Process(block, 2, A);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		for (var n = 0; n < 20; n++)
			analyzer.Process(block, 4802UL + (ulong)n * 4800UL, A);
		var allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		Assert.Equal(0, allocation);
	}

	[Fact]
	public void Long_window_lra_requires_enough_observed_material_and_cannot_be_faked_by_silence()
	{
		var silent = MeasureSilence();
		Assert.Null(silent.LoudnessRangeLu);
		var shortSignal = MeasureSine(997, 0.1, true, true, 5);
		Assert.Null(shortSignal.LoudnessRangeLu);
		Assert.Equal("NOT EVALUATED", shortSignal.PolicyStatus);
	}

	private static ProgrammeLoudnessSnapshot MeasureSilence()
	{
		var analyzer = new ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy.EbuR128Linear);
		var block = new float[9600];
		for (ulong tenth = 0; tenth < 40; tenth++)
			analyzer.Process(block, tenth * 4800, A);
		return analyzer.Read("program");
	}

	private static ProgrammeLoudnessSnapshot MeasureSine(
		double frequency, double amplitude, bool left, bool right, int seconds)
	{
		var analyzer = new ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy.EbuR128Linear);
		var block = new float[9600];
		ulong position = 0;
		for (var tenth = 0; tenth < seconds * 10; tenth++)
		{
			WriteSine(block, frequency, amplitude, position, left, right);
			analyzer.Process(block, position, A);
			position += 4800;
		}
		return analyzer.Read("program");
	}

	private static void WriteSine(float[] destination, double frequency, double amplitude,
		ulong position, bool left, bool right)
	{
		for (var index = 0; index < destination.Length / 2; index++)
		{
			var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (position + (ulong)index) / 48000d));
			destination[index * 2] = left ? value : 0;
			destination[index * 2 + 1] = right ? value : 0;
		}
	}
}
