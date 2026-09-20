// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class AudioOperatorWorkflowIntegrationTests
{
	[Fact]
	public async Task Program_audio_uses_real_external_stereo_payload_with_gain_mute_health_and_afv_switching()
	{
		await using var fixture = await Fixture.CreateAsync();

		var sourceASamples = StereoSamples(960, 0.40f, -0.20f);
		fixture.Runtime.SetExternalAudioInput(fixture.SourceA, sourceASamples);
		fixture.Runtime.SetAudioInputState(fixture.SourceA, new AudioGain(0.5), muted: false);

		var gained = fixture.Runtime.ProcessNextBoundary();
		Assert.True(gained.Audio.Emitted);
		Assert.Equal(fixture.SourceA, gained.CommittedProgramSourceId);
		Assert.Equal(0.20, gained.Audio.LeftPeakLevel, 5);
		Assert.Equal(0.10, gained.Audio.RightPeakLevel, 5);
		Assert.Equal(checked((int)(gained.Audio.SampleCount * 2U * sizeof(float))), gained.ProgramAudioPayload.Length);
		Assert.Equal(0.20f, ReadFloat(gained.ProgramAudioPayload, 0), 5);
		Assert.Equal(-0.10f, ReadFloat(gained.ProgramAudioPayload, 1), 5);
		Assert.Equal(V1AudioHealthState.Healthy, fixture.Runtime.Snapshot.AudioProgram.Health);

		fixture.Runtime.SetAudioInputState(fixture.SourceA, AudioGain.Unity, muted: true);
		fixture.Runtime.SetExternalAudioInput(fixture.SourceA, sourceASamples);
		var muted = fixture.Runtime.ProcessNextBoundary();
		Assert.True(muted.Audio.Muted);
		Assert.Equal(0, muted.Audio.PeakLevel);
		Assert.All(
			Enumerable.Range(0, muted.ProgramAudioPayload.Length / sizeof(float)),
			index => Assert.Equal(0f, ReadFloat(muted.ProgramAudioPayload, index)));
		Assert.Equal(V1AudioHealthState.Muted, fixture.Runtime.Snapshot.AudioProgram.Health);

		fixture.Runtime.SetAudioInputState(fixture.SourceA, new AudioGain(4), muted: false);
		fixture.Runtime.SetExternalAudioInput(fixture.SourceA, sourceASamples);
		var clipping = fixture.Runtime.ProcessNextBoundary();
		Assert.True(clipping.Audio.Clipping);
		Assert.Equal(1, clipping.Audio.LeftPeakLevel);
		Assert.Equal(0.8, clipping.Audio.RightPeakLevel, 5);
		Assert.Equal(1f, ReadFloat(clipping.ProgramAudioPayload, 0));
		Assert.Equal(-0.8f, ReadFloat(clipping.ProgramAudioPayload, 1), 5);
		Assert.Equal(V1AudioHealthState.Clipping, fixture.Runtime.Snapshot.AudioProgram.Health);

		fixture.Runtime.SetAudioInputState(fixture.SourceA, AudioGain.Unity, muted: false);
		fixture.Runtime.SetExternalAudioMeter(fixture.SourceA, 0, 0, available: true);
		var silence = fixture.Runtime.ProcessNextBoundary();
		Assert.True(silence.Audio.Emitted);
		Assert.Equal(0, silence.Audio.PeakLevel);
		Assert.Equal(V1AudioHealthState.Silence, fixture.Runtime.Snapshot.AudioProgram.Health);

		fixture.Runtime.SetExternalAudioMeter(fixture.SourceA, 0, 0, available: false);
		var underrun = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(AudioFollowVideoStatus.Underrun, underrun.Audio.Status);
		Assert.Empty(underrun.ProgramAudioPayload);
		Assert.Equal(V1AudioHealthState.Underrun, fixture.Runtime.Snapshot.AudioProgram.Health);

		Commit(
			fixture.Control.SelectPreview(new SelectPreviewCommand(Metadata(fixture), new ProductionSourceId(fixture.SourceB.Value))),
			fixture);
		Commit(
			fixture.Control.CutProgram(new CutProgramCommand(Metadata(fixture), new ProductionSourceId(fixture.SourceB.Value))),
			fixture);

		fixture.Runtime.SetExternalAudioInput(fixture.SourceB, StereoSamples(960, 0.30f, 0.60f));
		var switched = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(fixture.SourceB, switched.CommittedProgramSourceId);
		Assert.Equal(fixture.SourceB, fixture.Runtime.Snapshot.AudioProgram.ActiveVideoSourceId);
		Assert.Equal(0.30, switched.Audio.LeftPeakLevel, 5);
		Assert.Equal(0.60, switched.Audio.RightPeakLevel, 5);
	}

	[Fact]
	public async Task Generated_audio_test_signal_uses_regular_afv_path_and_restores_preserved_external_input()
	{
		await using var fixture = await Fixture.CreateAsync();

		fixture.Runtime.SetExternalAudioInput(fixture.SourceA, StereoSamples(960, 0.10f, -0.20f));
		var configured = fixture.Runtime.SetGeneratedAudioTestSignal(
			fixture.SourceA,
			true,
			GeneratedAudioTestSignalMode.Tone,
			1_000,
			0.25);

		Assert.True(configured.TestSignalEnabled);
		Assert.Equal(GeneratedAudioTestSignalMode.Tone, configured.TestSignalMode);
		Assert.Equal("ALL", configured.TestSignalActiveChannel);
		Assert.Equal(V1AudioHealthState.Healthy, configured.Health);

		var generated = fixture.Runtime.ProcessNextBoundary();
		Assert.True(generated.Audio.Emitted);
		Assert.Equal(fixture.SourceA, generated.Audio.VideoSourceId);
		Assert.Equal(0.25, generated.Audio.PeakLevel, 5);
		Assert.InRange(
			generated.ProgramAudioPayload
				.Chunk(sizeof(float))
				.Select(bytes => Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(bytes)))
				.Max(),
			0.249f,
			0.251f);
		Assert.Equal(V1AudioHealthState.Healthy, fixture.Runtime.Snapshot.AudioProgram.Health);

		var disabled = fixture.Runtime.SetGeneratedAudioTestSignal(fixture.SourceA, false);
		Assert.False(disabled.TestSignalEnabled);

		var restored = fixture.Runtime.ProcessNextBoundary();
		Assert.True(restored.Audio.Emitted);
		Assert.Equal(0.10f, ReadFloat(restored.ProgramAudioPayload, 0), 5);
		Assert.Equal(-0.20f, ReadFloat(restored.ProgramAudioPayload, 1), 5);
	}

	[Fact]
	public async Task Generated_stereo_identification_reports_active_channel_and_drives_real_meter_values()
	{
		await using var fixture = await Fixture.CreateAsync();

		fixture.Runtime.SetGeneratedAudioTestSignal(
			fixture.SourceA,
			true,
			GeneratedAudioTestSignalMode.StereoIdentification);

		var boundary = fixture.Runtime.ProcessNextBoundary();
		var snapshot = fixture.Runtime.Snapshot.AudioInputs[fixture.SourceA];

		Assert.True(boundary.Audio.Emitted);
		Assert.Equal("LEFT", snapshot.TestSignalActiveChannel);
		Assert.True(snapshot.LeftPeak > 0.2);
		Assert.Equal(0, snapshot.RightPeak);
		Assert.True(boundary.Audio.LeftPeakLevel > 0.2);
		Assert.Equal(0, boundary.Audio.RightPeakLevel);
		Assert.All(
			Enumerable.Range(0, checked((int)boundary.Audio.SampleCount)),
			frame => Assert.Equal(0f, ReadFloat(boundary.ProgramAudioPayload, (frame * 2) + 1)));
	}

	[Fact]
	public async Task Generated_silence_remains_available_and_reports_silence_health()
	{
		await using var fixture = await Fixture.CreateAsync();

		fixture.Runtime.SetInputSignalState(fixture.SourceA, V1InputSignalState.Lost);
		var configured = fixture.Runtime.SetGeneratedAudioTestSignal(
			fixture.SourceA,
			true,
			GeneratedAudioTestSignalMode.Silence);

		Assert.True(configured.TestSignalEnabled);
		Assert.Equal(V1AudioHealthState.Silence, configured.Health);

		var boundary = fixture.Runtime.ProcessNextBoundary();
		Assert.True(boundary.Audio.Emitted);
		Assert.Equal(0, boundary.Audio.PeakLevel);
		Assert.All(
			Enumerable.Range(0, boundary.ProgramAudioPayload.Length / sizeof(float)),
			index => Assert.Equal(0f, ReadFloat(boundary.ProgramAudioPayload, index)));

		var disabled = fixture.Runtime.SetGeneratedAudioTestSignal(fixture.SourceA, false);
		Assert.False(disabled.TestSignalEnabled);
		Assert.Equal(V1AudioHealthState.Error, disabled.Health);
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

	private static ControlCommandMetadata Metadata(Fixture fixture) =>
		new(
			ControlContractVersion.Current,
			CommandId.New(),
			fixture.Control.State.ProductionId,
			fixture.Control.State.Revision);

	private static void Commit(ControlHostOperationResult staged, Fixture fixture)
	{
		Assert.True(staged.Accepted, staged.Failure?.ToString());
		Assert.NotNull(staged.Execution);
		var execution = staged.Execution!;
		var applied = fixture.Runtime.ApplyExecution(
			execution.PreparedExecution,
			execution.ProgramSinkId,
			execution.ProgramTransition);
		Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
		Assert.NotNull(applied.Commit);
		var confirmed = fixture.Control.ConfirmRuntimeCommit(
			execution.PreparedExecution.PreparedExecutionId,
			applied.Commit!);
		Assert.True(confirmed.Committed, confirmed.Failure?.ToString());
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(
			MediaSourceId sourceA,
			MediaSourceId sourceB,
			BoundedProductionJournal journal,
			ControlHostService control,
			V1RuntimeHostService runtime)
		{
			SourceA = sourceA;
			SourceB = sourceB;
			Journal = journal;
			Control = control;
			Runtime = runtime;
		}

		public MediaSourceId SourceA { get; }
		public MediaSourceId SourceB { get; }
		public BoundedProductionJournal Journal { get; }
		public ControlHostService Control { get; }
		public V1RuntimeHostService Runtime { get; }

		public static ValueTask<Fixture> CreateAsync()
		{
			var sourceA = new MediaSourceId(Identity.Parse("7d000000-0000-0000-0000-00000000000a"));
			var sourceB = new MediaSourceId(Identity.Parse("7d000000-0000-0000-0000-00000000000b"));
			var productionSourceA = new ProductionSourceId(sourceA.Value);
			var productionSourceB = new ProductionSourceId(sourceB.Value);
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("7d000000-0000-0000-0000-000000000001")),
				"AP-50 Audio Operator",
				new[]
				{
					new ProductionSourceSpecification(productionSourceA, "Input 1"),
					new ProductionSourceSpecification(productionSourceB, "Input 2")
				},
				new ProductionRoutingState(productionSourceB, productionSourceA));

			var runtime = new V1RuntimeHostService(
				sourceA,
				sourceB,
				VideoFormat.Hd1080p50Rgba8,
				new NullRecordingWriter());
			var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
			var fixture = new Fixture(sourceA, sourceB, journal, control, runtime);
			Commit(control.Initialize(), fixture);
			return ValueTask.FromResult(fixture);
		}

		public async ValueTask DisposeAsync()
		{
			await Runtime.DisposeAsync();
			await Journal.DisposeAsync();
		}
	}

	private sealed class NullRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
