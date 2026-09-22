// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ProfessionalRecordingMp4IntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Program_recording_finalizes_as_independently_decodable_mp4(bool fractionalRate)
	{
		if (!OperatingSystem.IsWindows())
			return;

		var root = CreateTemporaryRoot();
		try
		{
			var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
			var writer = new WindowsMediaFoundationMp4RecordingWriter(root);
			await using var fixture = await RuntimeFixture.CreateAsync(format, writer);

			var start = await fixture.Runtime.StartRecordingAsync(
				RecordingSessionId.New(),
				RecordingOutputId.New(),
				root,
				"program");
			Assert.True(start.Succeeded, start.Failure?.ToString());

			for (var index = 0; index < 12; index++)
			{
				var boundary = fixture.Runtime.ProcessNextBoundary();
				Assert.True(boundary.Audio.Emitted, boundary.Audio.Failure?.ToString());
				await Task.Delay(5);
			}

			var stop = await fixture.Runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
			Assert.Equal(RecordingLifecycleState.Completed, fixture.Runtime.Snapshot.Recording.State);

			var finalPath = Path.Combine(root, "program.mp4");
			Assert.Equal(finalPath, writer.FinalPath);
			Assert.True(File.Exists(finalPath));
			Assert.False(File.Exists(Path.Combine(root, "program.partial.mp4")));
			Assert.False(File.Exists(finalPath + ".lock"));

			using var decoded = OpenIndependently(finalPath, format);
			Assert.Equal(MediaContainerFormat.Mp4, decoded.Probe.Container);
			Assert.Equal(MediaVideoCodec.H264, decoded.Probe.VideoCodec);
			Assert.Equal(MediaAudioCodec.Aac, decoded.Probe.AudioCodec);
			Assert.Equal(AudioFormat.Stereo48kFloat32, decoded.Probe.AudioFormat);

			LocalMediaDecodedFrame? decodedFrame = null;
			for (ulong sequence = 0; sequence < 20 && decodedFrame is null; sequence++)
			{
				var result = decoded.ReadNext(sequence);
				Assert.NotEqual(LocalMediaFrameReadStatus.Failed, result.Status);
				if (result.Status == LocalMediaFrameReadStatus.Frame && result.Frame?.Audio is not null)
					decodedFrame = result.Frame;
			}

			Assert.NotNull(decodedFrame);
			Assert.NotEmpty(decodedFrame!.RgbaPixels.ToArray());
			Assert.NotNull(decodedFrame.Audio);
			Assert.NotEmpty(decodedFrame.AudioPayload.ToArray());

			var videoSeconds =
				decodedFrame.Video.Timing.PresentationTimestamp *
				decodedFrame.Video.Timing.Timebase.SecondsPerTick;
			var audioSeconds =
				decodedFrame.Audio!.Timing.PresentationTimestamp *
				decodedFrame.Audio.Timing.Timebase.SecondsPerTick;
			Assert.InRange(Math.Abs(videoSeconds - audioSeconds), 0.0, 0.050);
		}
		finally
		{
			DeleteTemporaryRoot(root);
		}
	}

	[Fact]
	public async Task Repeated_recordings_finalize_distinct_mp4_files_in_one_runtime_lifecycle()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var root = CreateTemporaryRoot();
		try
		{
			var writer = new WindowsMediaFoundationMp4RecordingWriter(root);
			await using var fixture = await RuntimeFixture.CreateAsync(VideoFormat.Hd1080p50Rgba8, writer);

			foreach (var name in new[] { "take-a", "take-b" })
			{
				var start = await fixture.Runtime.StartRecordingAsync(
					RecordingSessionId.New(),
					RecordingOutputId.New(),
					root,
					name);
				Assert.True(start.Succeeded, start.Failure?.ToString());

				for (var index = 0; index < 6; index++)
				{
					fixture.Runtime.ProcessNextBoundary();
					await Task.Delay(5);
				}

				var stop = await fixture.Runtime.StopRecordingAsync();
				Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
				var path = Path.Combine(root, name + ".mp4");
				Assert.True(File.Exists(path));

				using var decoded = OpenIndependently(path, VideoFormat.Hd1080p50Rgba8);
				Assert.Equal(MediaVideoCodec.H264, decoded.Probe.VideoCodec);
				Assert.Equal(MediaAudioCodec.Aac, decoded.Probe.AudioCodec);
			}
		}
		finally
		{
			DeleteTemporaryRoot(root);
		}
	}

	[Fact]
	public async Task Mp4_recording_sustains_five_seconds_of_program_timeline_with_bounded_backlog()
	{
		if (!OperatingSystem.IsWindows())
			return;

		const int targetFrames = 250;
		var root = CreateTemporaryRoot();
		try
		{
			var writer = new WindowsMediaFoundationMp4RecordingWriter(root);
			await using var fixture = await RuntimeFixture.CreateAsync(VideoFormat.Hd1080p50Rgba8, writer);

			var start = await fixture.Runtime.StartRecordingAsync(
				RecordingSessionId.New(),
				RecordingOutputId.New(),
				root,
				"sustained");
			Assert.True(start.Succeeded, start.Failure?.ToString());

			for (var index = 0; index < targetFrames; index++)
			{
				var boundary = fixture.Runtime.ProcessNextBoundary();
				Assert.NotNull(boundary.Recording);
				Assert.True(boundary.Recording!.Accepted, boundary.Recording.Failure?.ToString());

				if (fixture.Runtime.Snapshot.Recording.Statistics.Accepted -
					fixture.Runtime.Snapshot.Recording.Statistics.Written >= 8)
				{
					await WaitForRecordingBacklogAsync(fixture.Runtime, maximumBacklog: 4);
				}
			}

			var stop = await fixture.Runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Stopped, stop.Status);

			var statistics = fixture.Runtime.Snapshot.Recording.Statistics;
			Assert.Equal((ulong)targetFrames, statistics.Accepted);
			Assert.Equal((ulong)targetFrames, statistics.Written);
			Assert.Equal(0UL, statistics.Dropped);
			Assert.Equal(0UL, statistics.WriterFailures);

			var path = Path.Combine(root, "sustained.mp4");
			Assert.True(File.Exists(path));
			using var decoded = OpenIndependently(path, VideoFormat.Hd1080p50Rgba8);
			Assert.Equal(MediaVideoCodec.H264, decoded.Probe.VideoCodec);
			Assert.Equal(MediaAudioCodec.Aac, decoded.Probe.AudioCodec);
			Assert.True(decoded.Probe.Duration >= TimeSpan.FromSeconds(4.9));
		}
		finally
		{
			DeleteTemporaryRoot(root);
		}
	}

	[Fact]
	public async Task Mp4_storage_failure_is_isolated_from_committed_program_execution()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var root = CreateTemporaryRoot();
		try
		{
			var writer = new WindowsMediaFoundationMp4RecordingWriter(root, maximumPayloadBytes: 1);
			await using var fixture = await RuntimeFixture.CreateAsync(VideoFormat.Hd1080p50Rgba8, writer);

			var start = await fixture.Runtime.StartRecordingAsync(
				RecordingSessionId.New(),
				RecordingOutputId.New(),
				root,
				"quota-failure");
			Assert.True(start.Succeeded, start.Failure?.ToString());

			var failedBoundary = fixture.Runtime.ProcessNextBoundary();
			Assert.NotNull(failedBoundary.Recording);
			Assert.True(failedBoundary.Recording!.Accepted);

			for (var attempt = 0; attempt < 100 &&
				fixture.Runtime.Snapshot.Recording.State != RecordingLifecycleState.Failed;
				attempt++)
			{
				await Task.Delay(10);
			}

			Assert.Equal(RecordingLifecycleState.Failed, fixture.Runtime.Snapshot.Recording.State);
			Assert.True(fixture.Runtime.HasCommittedExecution);

			var laterBoundary = fixture.Runtime.ProcessNextBoundary();
			Assert.True(laterBoundary.Audio.Emitted, laterBoundary.Audio.Failure?.ToString());
			Assert.True(fixture.Runtime.HasCommittedExecution);

			var stop = await fixture.Runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Failed, stop.Status);
			Assert.False(File.Exists(Path.Combine(root, "quota-failure.mp4")));
			Assert.False(File.Exists(Path.Combine(root, "quota-failure.partial.mp4")));
			Assert.False(File.Exists(Path.Combine(root, "quota-failure.mp4.lock")));
		}
		finally
		{
			DeleteTemporaryRoot(root);
		}
	}

	private static async Task WaitForRecordingBacklogAsync(
		V1RuntimeHostService runtime,
		ulong maximumBacklog)
	{
		for (var attempt = 0; attempt < 1_000; attempt++)
		{
			var snapshot = runtime.Snapshot.Recording;
			Assert.NotEqual(RecordingLifecycleState.Failed, snapshot.State);
			var backlog = snapshot.Statistics.Accepted - snapshot.Statistics.Written;
			if (backlog <= maximumBacklog)
				return;

			await Task.Delay(10);
		}

		throw new TimeoutException("Professional recording writer did not drain its bounded sustained-test backlog.");
	}

	private static LocalMediaFileSource OpenIndependently(string path, VideoFormat outputFormat)
	{
		var provider = new LocalMediaFileProvider(outputFormat);
		var result = provider.TryOpen(path, MediaSourceId.New());
		Assert.True(result.Succeeded, result.Failure?.ToString());
		return result.Source!;
	}

	private static string CreateTemporaryRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-professional-recording", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteTemporaryRoot(string root)
	{
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	private sealed class RuntimeFixture : IAsyncDisposable
	{
		private readonly BoundedProductionJournal _journal;

		private RuntimeFixture(V1RuntimeHostService runtime, BoundedProductionJournal journal)
		{
			Runtime = runtime;
			_journal = journal;
		}

		public V1RuntimeHostService Runtime { get; }

		public static async ValueTask<RuntimeFixture> CreateAsync(
			VideoFormat format,
			IProgramRecordingWriter writer)
		{
			var sourceA = new ProductionSourceId(Identity.Parse("7c000000-0000-0000-0000-00000000000a"));
			var sourceB = new ProductionSourceId(Identity.Parse("7c000000-0000-0000-0000-00000000000b"));
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("7c000000-0000-0000-0000-000000000001")),
				"Professional MP4 Recording",
				new[]
				{
					new ProductionSourceSpecification(sourceA, "Input 1"),
					new ProductionSourceSpecification(sourceB, "Input 2")
				},
				new ProductionRoutingState(sourceA, sourceA));

			var runtime = new V1RuntimeHostService(
				new MediaSourceId(sourceA.Value),
				new MediaSourceId(sourceB.Value),
				format,
				writer);
			var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);

			try
			{
				var staged = control.Initialize();
				Assert.True(staged.Accepted, staged.Failure?.ToString());
				Assert.NotNull(staged.Execution);
				var execution = staged.Execution!;
				var applied = runtime.ApplyExecution(
					execution.PreparedExecution,
					execution.ProgramSinkId,
					execution.ProgramTransition);
				Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
				Assert.NotNull(applied.Commit);
				var confirmed = control.ConfirmRuntimeCommit(
					execution.PreparedExecution.PreparedExecutionId,
					applied.Commit!);
				Assert.True(confirmed.Committed, confirmed.Failure?.ToString());

				return new RuntimeFixture(runtime, journal);
			}
			catch
			{
				await runtime.DisposeAsync();
				await journal.DisposeAsync();
				throw;
			}
		}

		public async ValueTask DisposeAsync()
		{
			await Runtime.DisposeAsync();
			await _journal.DisposeAsync();
		}
	}
}
