// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ExternalPcmUnderrunIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Partial_and_complete_starvation_preserve_Program_windows_and_downstream_pcm(bool fractionalRate)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-pcm-underrun-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
			var sourceA = new ProductionSourceId(Identity.New());
			var sourceB = new ProductionSourceId(Identity.New());
			var mediaA = new MediaSourceId(sourceA.Value);
			var recording = new ReferenceRecordingPayloadWriter(root);
			var replayWriter = new ObservedReplayWriter();
			await using var replay = new RuntimeReplayService(
				Path.Combine(root, "replay"), format, writerFactory: () => replayWriter, queueCapacity: 8);
			var network = new ObservedNetworkRegistry();
			var target = new RuntimeNetworkOutputTarget("program", new NetworkOutputConfiguration(
				"pcm-underrun", NetworkOutputProtocolFamily.Ndi, format, AudioFormat.Stereo48kFloat32,
				new NdiNetworkOutputSettings("PCM underrun qualification")));
			await using var runtime = new RuntimeProgramExecutionService(
				mediaA, new MediaSourceId(sourceB.Value), format, recording,
				gpuBackend: null, networkOutputs: [target], replayService: replay,
				pipelineTimingProbe: null, networkOutputProviderRegistry: network);
			await using var journal = new BoundedProductionJournal(64);
			CommitInitialization(runtime, journal, sourceA, sourceB);
			var started = await runtime.StartRecordingAsync(RecordingSessionId.New(), RecordingOutputId.New());
			Assert.True(started.Succeeded, started.Failure?.ToString());

			var expected = new List<float[]>();
			var actual = new List<byte[]>();
			for (ulong slot = 0; slot < 5; slot++)
			{
				var window = AudioVideoTimingRelationship.GetSampleWindow(format.FrameRate, 48_000, slot);
				var expectedSamples = new float[checked((int)window.SampleCount * 2)];
				if (slot == 0)
				{
					expectedSamples = StereoSamples(window.SampleCount, 0.25f, -0.125f);
					// One packet crosses a boundary but cannot fill the next window.
					runtime.SetExternalAudioInput(mediaA,
						expectedSamples.Concat(StereoSamples(100, 0.75f, -0.375f)).ToArray());
				}
				else if (slot == 1)
				{
					StereoSamples(100, 0.75f, -0.375f).CopyTo(expectedSamples, 0);
				}
				else if (slot == 3)
				{
					expectedSamples = StereoSamples(window.SampleCount, -0.1f, 0.2f);
					runtime.SetExternalAudioInput(mediaA, expectedSamples);
				}
				expected.Add(expectedSamples);

				using var boundary = runtime.ProcessNextBoundary();
				Assert.Equal(slot, boundary.SequenceNumber);
				Assert.Equal(window.SamplePosition, boundary.ProgramAudioBuffer.Timing.SamplePosition);
				Assert.Equal(window.SampleCount, boundary.ProgramAudioBuffer.Timing.SampleCount);
				Assert.Equal(window.Timebase, boundary.ProgramAudioBuffer.Timing.Timebase);
				AssertPcm(expectedSamples, boundary.ProgramAudioPayload);
				actual.Add(boundary.ProgramAudioPayload);
				Assert.True(boundary.Recording?.Accepted, boundary.Recording?.Failure?.ToString());

				var underrun = slot is 1 or 2 or 4;
				Assert.Equal(underrun ? AudioFollowVideoStatus.Underrun : AudioFollowVideoStatus.Emitted,
					boundary.Audio.Status);
				var snapshot = runtime.Snapshot;
				Assert.Equal(underrun ? RuntimeAudioHealthState.Underrun : RuntimeAudioHealthState.Healthy,
					snapshot.AudioInputs[mediaA].Health);
				Assert.Equal(underrun ? RuntimeAudioHealthState.Underrun : RuntimeAudioHealthState.Healthy,
					snapshot.AudioProgram.Health);
				Assert.Equal(slot is 2 or 4 ? 0 : 1, snapshot.AudioProduction!.ActiveSourceCount);
				Assert.Equal(underrun ? 1 : 0, snapshot.AudioProduction.MissingSourceCount);
				Assert.Equal(expectedSamples.Where((_, index) => (index & 1) == 0).Max(value => Math.Abs(value)),
					snapshot.AudioInputs[mediaA].LeftPeak, 6);
				Assert.Equal(expectedSamples.Where((_, index) => (index & 1) != 0).Max(value => Math.Abs(value)),
					snapshot.AudioInputs[mediaA].RightPeak, 6);
			}

			Assert.Equal(3UL, runtime.Snapshot.Audio.Underruns);
			Assert.Equal(RecordingStopStatus.Stopped, (await runtime.StopRecordingAsync()).Status);
			await replay.DisposeAsync();
			var recordingSamples = ReferenceRecordingPayloadReader.Read(recording.FinalPath!).Samples;
			Assert.Equal(5, recordingSamples.Count);
			Assert.Equal(5, replayWriter.Samples.Count);
			Assert.Equal(5, network.Session!.Samples.Count);
			Assert.Null(replay.Snapshot.Failure);
			for (var index = 0; index < expected.Count; index++)
			{
				var slot = checked((ulong)index);
				var window = AudioVideoTimingRelationship.GetSampleWindow(format.FrameRate, 48_000, slot);
				// Later boundaries must not overwrite a returned or queued PCM snapshot.
				AssertPcm(expected[index], actual[index]);
				AssertPcm(expected[index], recordingSamples[index].AudioPayload);
				Assert.Equal(window.SamplePosition, recordingSamples[index].AudioSamplePosition);
				AssertPcm(expected[index], replayWriter.Samples[slot].Payload);
				Assert.Equal(window.SamplePosition, replayWriter.Samples[slot].Timing.SamplePosition);
				AssertPcm(expected[index], network.Session.Samples[slot].Payload);
				Assert.Equal(window.SamplePosition, network.Session.Samples[slot].Timing.SamplePosition);
			}
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Only_an_explicit_meter_reference_can_generate_audio_without_pcm()
	{
		var sourceA = new ProductionSourceId(Identity.New());
		var sourceB = new ProductionSourceId(Identity.New());
		var mediaA = new MediaSourceId(sourceA.Value);
		await using var runtime = new RuntimeProgramExecutionService(
			mediaA, new MediaSourceId(sourceB.Value), VideoFormat.Hd1080p50Rgba8, new NoopRecordingWriter());
		await using var journal = new BoundedProductionJournal(64);
		CommitInitialization(runtime, journal, sourceA, sourceB);

		runtime.SetExternalAudioMeter(mediaA, 0.5, 0.25);
		using (var reference = runtime.ProcessNextBoundary())
		{
			Assert.True(reference.Audio.Emitted);
			Assert.Contains(MemoryMarshal.Cast<byte, float>(reference.ProgramAudioPayload).ToArray(), value => value == -0.5f);
		}

		runtime.SetExternalAudioInput(mediaA, ReadOnlySpan<float>.Empty);
		using (var empty = runtime.ProcessNextBoundary())
		{
			Assert.Equal(AudioFollowVideoStatus.Underrun, empty.Audio.Status);
			AssertPcm(new float[960 * 2], empty.ProgramAudioPayload);
		}

		// A disconnected producer withdraws its previously queued samples.
		runtime.SetExternalAudioInput(mediaA, StereoSamples(960 * 2, 0.75f, -0.75f));
		runtime.SetExternalAudioInput(mediaA, ReadOnlySpan<float>.Empty, available: false);
		var fresh = StereoSamples(960, 0.125f, -0.25f);
		runtime.SetExternalAudioInput(mediaA, fresh);
		using var recovered = runtime.ProcessNextBoundary();
		Assert.True(recovered.Audio.Emitted);
		AssertPcm(fresh, recovered.ProgramAudioPayload);
	}

	private static float[] StereoSamples(uint frames, float left, float right)
	{
		var samples = new float[checked((int)frames * 2)];
		for (var index = 0; index < samples.Length; index += 2)
		{
			samples[index] = left;
			samples[index + 1] = right;
		}
		return samples;
	}

	private static void AssertPcm(float[] expected, byte[] payload) =>
		Assert.Equal(expected, MemoryMarshal.Cast<byte, float>(payload).ToArray());

	private static void CommitInitialization(RuntimeProgramExecutionService runtime,
		BoundedProductionJournal journal, ProductionSourceId sourceA, ProductionSourceId sourceB)
	{
		var specification = new ProductionSpecification(
			ControlContractVersion.Current, ProductionId.New(), "External PCM qualification",
			[new ProductionSourceSpecification(sourceA, "Input A"), new ProductionSourceSpecification(sourceB, "Input B")],
			new ProductionRoutingState(sourceA, sourceA));
		var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
		var staged = control.Initialize();
		Assert.True(staged.Accepted, staged.Failure?.ToString());
		var execution = Assert.IsType<ControlHostExecutionPackage>(staged.Execution);
		var applied = runtime.ApplyExecution(execution.PreparedExecution, execution.ProgramSinkId, execution.ProgramTransition);
		Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
		var confirmed = control.ConfirmRuntimeCommit(execution.PreparedExecution.PreparedExecutionId, applied.Commit!);
		Assert.True(confirmed.Committed, confirmed.Failure?.ToString());
	}

	private sealed record CapturedAudio(AudioBufferTiming Timing, byte[] Payload);

	private sealed class ObservedReplayWriter : IReplaySegmentWriter
	{
		private readonly ConcurrentDictionary<ulong, byte[]> _staged = new();
		public ConcurrentDictionary<ulong, CapturedAudio> Samples { get; } = new();
		public string? FinalPath { get; private set; }
		public string ConfigureTarget(string directory, string name)
		{
			FinalPath = Path.Combine(directory, name);
			return name;
		}
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken token) => ValueTask.CompletedTask;
		public void StagePayload(ulong sequence, IProgramRecordingPayloadLease video, ReadOnlyMemory<byte> audio)
		{
			try { Assert.True(_staged.TryAdd(sequence, audio.ToArray())); }
			finally { video.Dispose(); }
		}
		public void DiscardPayload(ulong sequence) => _staged.TryRemove(sequence, out _);
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken token)
		{
			Assert.True(_staged.TryRemove(sample.SequenceNumber, out var audio));
			Assert.True(Samples.TryAdd(sample.SequenceNumber, new CapturedAudio(sample.Audio!.Timing, audio!)));
			return ValueTask.CompletedTask;
		}
		public ValueTask FinalizeAsync(CancellationToken token)
		{
			File.WriteAllBytes(FinalPath!, [1]);
			return ValueTask.CompletedTask;
		}
		public ValueTask AbortAsync(CancellationToken token) => ValueTask.CompletedTask;
	}

	private sealed class ObservedNetworkRegistry : IRuntimeNetworkOutputProviderRegistry
	{
		public ObservedNetworkSession? Session { get; private set; }
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptorsFor(IReadOnlyList<RuntimeNetworkOutputTarget> targets) => [];
		public INetworkOutputSession CreateSession(RuntimeNetworkOutputTarget target) =>
			Session = new ObservedNetworkSession(target.Configuration);
	}

	private sealed class ObservedNetworkSession(NetworkOutputConfiguration configuration) : INetworkOutputSession
	{
		public ConcurrentDictionary<ulong, CapturedAudio> Samples { get; } = new();
		public NetworkOutputConfiguration Configuration => configuration;
		public NetworkOutputHealthSnapshot Snapshot => new(
			configuration.TargetId, "PCM observer", configuration.Protocol, configuration.Settings.SafeTargetIdentity,
			NetworkOutputLifecycleState.Connected, true, configuration.VideoFormat, configuration.AudioFormat,
			NetworkOutputVideoCodec.NdiHighBandwidth, NetworkOutputAudioCodec.Float32,
			0, 0, 0, new NetworkOutputStatistics(0, 0, 0, 0, 0, 0, 0, 0), null, null);
		public NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample)
		{
			try
			{
				Assert.True(Samples.TryAdd(sample.SequenceNumber, new CapturedAudio(sample.AudioTiming, sample.Audio.ToArray())));
				return NetworkOutputEnqueueResult.AcceptedSample();
			}
			finally { sample.Video.Dispose(); }
		}
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class NoopRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken token) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken token) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken token) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken token) => ValueTask.CompletedTask;
	}
}
