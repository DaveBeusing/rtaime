// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ReferenceRecordingPayloadTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task V1_runtime_persists_actual_program_video_and_audio_payloads(bool fractionalRate)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-reference-recording", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
			var sourceA = new ProductionSourceId(Identity.Parse("7b000000-0000-0000-0000-00000000000a"));
			var sourceB = new ProductionSourceId(Identity.Parse("7b000000-0000-0000-0000-00000000000b"));
			var specification = CreateSpecification(sourceA, sourceB);
			var writer = new ReferenceRecordingPayloadWriter(root);
			await using var runtime = new V1RuntimeHostService(
				new MediaSourceId(sourceA.Value),
				new MediaSourceId(sourceB.Value),
				format,
				writer);
			await using var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
			CommitInitialization(control, runtime);
			var breakawaySource = new MediaSourceId(sourceB.Value);
			runtime.SetAudioRouting(AudioRoutingMode.Breakaway, breakawaySource);
			runtime.SetGeneratedAudioTestSignal(
				breakawaySource,
				true,
				GeneratedAudioTestSignalMode.Tone,
				750,
				0.2);

			var outputId = RecordingOutputId.New();
			var start = await runtime.StartRecordingAsync(RecordingSessionId.New(), outputId);
			Assert.True(start.Succeeded, start.Failure?.ToString());

			using var boundary = runtime.ProcessNextBoundary();
			Assert.NotNull(boundary.Recording);
			Assert.True(boundary.Recording!.Accepted, boundary.Recording.Failure?.ToString());
			Assert.True(boundary.Audio.Emitted, boundary.Audio.Failure?.ToString());
			Assert.Equal(new MediaSourceId(sourceA.Value), boundary.CommittedProgramSourceId);
			Assert.Equal(AudioRoutingMode.Breakaway, boundary.Audio.RoutingMode);
			Assert.Equal(breakawaySource, boundary.Audio.AudioSourceId);

			var stop = await runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
			Assert.NotNull(writer.FinalPath);
			Assert.False(File.Exists(writer.PartialPath));
			Assert.True(File.Exists(writer.FinalPath));

			var artifact = ReferenceRecordingPayloadReader.Read(writer.FinalPath!);
			Assert.Equal(RecordingContractVersion.Current, artifact.RecordingVersion);
			Assert.Equal(outputId.ToString(), artifact.OutputId);
			Assert.Equal(1UL, artifact.VideoSampleCount);
			Assert.Equal(1UL, artifact.AudioSampleCount);
			Assert.NotEqual(new string('0', 64), artifact.PayloadSha256);

			var sample = Assert.Single(artifact.Samples);
			Assert.Equal(boundary.SequenceNumber, sample.SequenceNumber);
			Assert.Equal(format, sample.VideoFormat);
			Assert.Equal(boundary.ProgramFrame.Timing.PresentationTimestamp, sample.VideoPresentationTimestamp);
			Assert.Equal(boundary.ProgramFrame.Timing.Timebase, sample.VideoTimebase);
			Assert.True(boundary.ProgramPixels.Span.SequenceEqual(sample.VideoPayload));
			Assert.Equal(checked((int)((long)format.Width * format.Height * 4)), sample.VideoPayload.Length);

			Assert.Equal(AudioFormat.Stereo48kFloat32, sample.AudioFormat);
			Assert.Equal(boundary.Audio.SamplePosition, sample.AudioSamplePosition);
			Assert.Equal(boundary.Audio.SampleCount, sample.AudioSampleCount);
			Assert.NotEmpty(sample.AudioPayload);
			Assert.Equal(checked((int)(boundary.Audio.SampleCount * 2U * sizeof(float))), sample.AudioPayload.Length);
			Assert.Equal(boundary.ProgramAudioPayload, sample.AudioPayload);
			var audioSamples = MemoryMarshal.Cast<byte, float>(sample.AudioPayload);
			var recordedPeak = audioSamples.ToArray().Max(value => Math.Abs(value));
			Assert.Equal((float)boundary.Audio.PeakLevel, recordedPeak, 5);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Recording_persists_the_final_multi_source_Program_mix()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-advanced-audio-recording", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var sourceA = new ProductionSourceId(Identity.Parse("7b100000-0000-0000-0000-00000000000a"));
			var sourceB = new ProductionSourceId(Identity.Parse("7b100000-0000-0000-0000-00000000000b"));
			var mediaA = new MediaSourceId(sourceA.Value);
			var mediaB = new MediaSourceId(sourceB.Value);
			var specification = CreateSpecification(sourceA, sourceB);
			var writer = new ReferenceRecordingPayloadWriter(root);
			await using var runtime = new V1RuntimeHostService(mediaA, mediaB, VideoFormat.Hd1080p50Rgba8, writer);
			await using var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
			CommitInitialization(control, runtime);

			var current = runtime.Snapshot.AudioProduction!;
			var dynamics = new AudioBusDynamicsConfiguration(
				new AudioBusCompressorConfiguration(false, -18, 4, 5, 100, 0),
				new AudioBusSamplePeakLimiterConfiguration(true, -9, 100));
			runtime.SetAudioProductionConfiguration(new AudioProductionConfiguration(
				checked(current.Revision + 1),
				new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1d, muted: false, dynamics) },
				new[]
				{
					new AudioProductionSourceConfiguration(mediaA, 1d, muted: false, followRoutedSource: false, new[] { AudioBusId.Program }),
					new AudioProductionSourceConfiguration(mediaB, 1d, muted: false, followRoutedSource: false, new[] { AudioBusId.Program })
				}));

			runtime.SetExternalAudioInput(mediaA, StereoSamples(960, 0.20f, -0.10f));
			runtime.SetExternalAudioInput(mediaB, StereoSamples(960, 0.30f, -0.20f));

			var outputId = RecordingOutputId.New();
			var start = await runtime.StartRecordingAsync(RecordingSessionId.New(), outputId);
			Assert.True(start.Succeeded, start.Failure?.ToString());

			using var boundary = runtime.ProcessNextBoundary();
			var limiterCeiling = (float)Math.Pow(10d, -9d / 20d);
			Assert.Equal(limiterCeiling, ReadFloat(boundary.ProgramAudioPayload, 0), 5);
			Assert.Equal(limiterCeiling * (-0.30f / 0.50f), ReadFloat(boundary.ProgramAudioPayload, 1), 5);
			var audioProduction = runtime.Snapshot.AudioProduction!;
			Assert.Equal(2, audioProduction.ActiveSourceCount);
			var programBus = Assert.Single(audioProduction.Buses);
			Assert.True(programBus.LimiterHitCount > 0);
			Assert.True(programBus.LimiterGainReductionDb > 0);
			Assert.Equal(0UL, programBus.ClippedSampleValues);

			var stop = await runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Stopped, stop.Status);

			var artifact = ReferenceRecordingPayloadReader.Read(writer.FinalPath!);
			var sample = Assert.Single(artifact.Samples);
			Assert.Equal(boundary.ProgramAudioPayload, sample.AudioPayload);
			Assert.Equal(limiterCeiling, ReadFloat(sample.AudioPayload, 0), 5);
			Assert.Equal(limiterCeiling * (-0.30f / 0.50f), ReadFloat(sample.AudioPayload, 1), 5);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static float[] StereoSamples(int frames, float left, float right)
	{
		var samples = new float[checked(frames * 2)];
		for (var frame = 0; frame < frames; frame++)
		{
			samples[frame * 2] = left;
			samples[(frame * 2) + 1] = right;
		}
		return samples;
	}

	private static float ReadFloat(byte[] payload, int sampleIndex) =>
		BinaryPrimitives.ReadSingleLittleEndian(
			payload.AsSpan(checked(sampleIndex * sizeof(float)), sizeof(float)));

	private static ProductionSpecification CreateSpecification(
		ProductionSourceId sourceA,
		ProductionSourceId sourceB) =>
		new(
			ControlContractVersion.Current,
			new ProductionId(Identity.Parse("7b000000-0000-0000-0000-000000000001")),
			"V1 Reference Recording Payload",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Input 1"),
				new ProductionSourceSpecification(sourceB, "Input 2")
			},
			new ProductionRoutingState(sourceA, sourceA));

	private static void CommitInitialization(ControlHostService control, V1RuntimeHostService runtime)
	{
		var staged = control.Initialize();
		Assert.True(staged.Accepted, staged.Failure?.ToString());
		Assert.NotNull(staged.Execution);
		var execution = staged.Execution!;
		var applied = runtime.ApplyExecution(execution.PreparedExecution, execution.ProgramSinkId, execution.ProgramTransition);
		Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
		Assert.NotNull(applied.Commit);
		var confirmed = control.ConfirmRuntimeCommit(execution.PreparedExecution.PreparedExecutionId, applied.Commit!);
		Assert.True(confirmed.Committed, confirmed.Failure?.ToString());
	}
}
