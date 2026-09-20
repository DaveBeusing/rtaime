// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class BroadcastTestPatternRuntimeIntegrationTests
{
	[Fact]
	public async Task Test_pattern_flows_through_program_pipeline_and_restores_underlying_signal_state()
	{
		await using var fixture = await Fixture.CreateAsync();
		var pattern = new BroadcastTestPatternGenerator(
			new BroadcastTestPatternConfiguration(fixture.Format));

		Assert.True(fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceA, true));
		Assert.False(fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceA, true));

		var active = fixture.Runtime.ProcessNextBoundary();
		AssertPixelMatches(pattern, active.ProgramPixels, fixture.Format, 40, 100);
		AssertPixelMatches(pattern, active.ProgramPixels, fixture.Format, 672, 100);
		Assert.Contains(fixture.MediaSourceA, fixture.Runtime.Snapshot.BroadcastTestPatternSources);
		Assert.Equal(V1InputSignalState.Valid, fixture.Runtime.Snapshot.InputSignals[fixture.MediaSourceA]);

		fixture.Runtime.SetInputSignalState(fixture.MediaSourceA, V1InputSignalState.Lost);
		Assert.Equal(V1InputSignalState.Valid, fixture.Runtime.Snapshot.InputSignals[fixture.MediaSourceA]);
		var activeWhileUnderlyingInputIsLost = fixture.Runtime.ProcessNextBoundary();
		AssertPixelMatches(pattern, activeWhileUnderlyingInputIsLost.ProgramPixels, fixture.Format, 40, 100);

		Assert.True(fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceA, false));
		Assert.DoesNotContain(fixture.MediaSourceA, fixture.Runtime.Snapshot.BroadcastTestPatternSources);
		Assert.Equal(V1InputSignalState.Lost, fixture.Runtime.Snapshot.InputSignals[fixture.MediaSourceA]);

		var fallback = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(new PixelValue(0, 0, 0, 255), Pixel(fallback.ProgramPixels, fixture.Format, 40, 100));

		fixture.Runtime.SetInputSignalState(fixture.MediaSourceA, V1InputSignalState.Valid);
		Assert.True(fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceA, true));
		var reactivated = fixture.Runtime.ProcessNextBoundary();
		AssertPixelMatches(pattern, reactivated.ProgramPixels, fixture.Format, 40, 100);
	}

	[Fact]
	public async Task Test_pattern_can_be_enabled_independently_for_each_runtime_source()
	{
		await using var fixture = await Fixture.CreateAsync();

		fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceB, true);

		Assert.DoesNotContain(fixture.MediaSourceA, fixture.Runtime.Snapshot.BroadcastTestPatternSources);
		Assert.Contains(fixture.MediaSourceB, fixture.Runtime.Snapshot.BroadcastTestPatternSources);

		fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceA, true);
		Assert.Equal(
			new[] { fixture.MediaSourceA, fixture.MediaSourceB }.OrderBy(source => source.ToString(), StringComparer.Ordinal),
			fixture.Runtime.Snapshot.BroadcastTestPatternSources);
	}

	[Fact]
	public async Task Motion_timing_mode_changes_program_content_on_each_authoritative_boundary()
	{
		await using var fixture = await Fixture.CreateAsync();

		Assert.True(fixture.Runtime.SetBroadcastTestPattern(
			fixture.MediaSourceA,
			true,
			V1BroadcastTestPatternMode.MotionTiming));

		var first = fixture.Runtime.ProcessNextBoundary();
		var firstHash = System.Security.Cryptography.SHA256.HashData(first.ProgramPixels);
		var second = fixture.Runtime.ProcessNextBoundary();
		var secondHash = System.Security.Cryptography.SHA256.HashData(second.ProgramPixels);

		Assert.NotEqual(firstHash, secondHash);
		Assert.Equal(
			V1BroadcastTestPatternMode.MotionTiming,
			fixture.Runtime.Snapshot.BroadcastTestPatternModes[fixture.MediaSourceA]);
		Assert.Equal((ulong)2, fixture.Runtime.Snapshot.NextSequenceNumber);
	}

	[Fact]
	public async Task Motion_timing_with_pulse_audio_reports_one_synchronized_event_and_resets_cleanly()
	{
		await using var fixture = await Fixture.CreateAsync();
		var motion = new MotionTimingTestSignalGenerator(fixture.Format);

		fixture.Runtime.SetGeneratedAudioTestSignal(
			fixture.MediaSourceA,
			true,
			GeneratedAudioTestSignalMode.Pulse);
		fixture.Runtime.SetBroadcastTestPattern(
			fixture.MediaSourceA,
			true,
			V1BroadcastTestPatternMode.MotionTiming);

		var first = fixture.Runtime.ProcessNextBoundary();
		var firstDiagnostics = fixture.Runtime.Snapshot.AvSyncDiagnostics;
		var flashPixel = Pixel(first.ProgramPixels, fixture.Format, 0, motion.RegionY);

		Assert.NotNull(firstDiagnostics);
		Assert.True(firstDiagnostics!.Enabled);
		Assert.Equal("MEASURED", firstDiagnostics.State);
		Assert.Equal(0UL, firstDiagnostics.EventId);
		Assert.Equal("0/1", firstDiagnostics.ExpectedMediaTime);
		Assert.Equal(0UL, firstDiagnostics.TargetVideoFrameSequence);
		Assert.Equal(0UL, firstDiagnostics.TargetAudioSamplePosition);
		Assert.NotNull(firstDiagnostics.SubmitOffsetMilliseconds);
		Assert.NotNull(firstDiagnostics.DriftFromBaselineMilliseconds);
		Assert.Equal(new PixelValue(255, 196, 64, 255), flashPixel);

		for (var index = 0; index < 50; index++)
			fixture.Runtime.ProcessNextBoundary();

		var secondDiagnostics = fixture.Runtime.Snapshot.AvSyncDiagnostics;
		Assert.NotNull(secondDiagnostics);
		Assert.Equal(1UL, secondDiagnostics!.EventId);
		Assert.Equal("1/1", secondDiagnostics.ExpectedMediaTime);
		Assert.Equal(50UL, secondDiagnostics.TargetVideoFrameSequence);
		Assert.Equal(48_000UL, secondDiagnostics.TargetAudioSamplePosition);

		fixture.Runtime.SetGeneratedAudioTestSignal(fixture.MediaSourceA, false);
		var stopped = fixture.Runtime.Snapshot.AvSyncDiagnostics;
		Assert.NotNull(stopped);
		Assert.False(stopped!.Enabled);
		Assert.Equal("UNAVAILABLE", stopped.State);
		Assert.Null(stopped.EventId);
	}

	[Fact]
	public async Task Switching_between_static_and_motion_modes_preserves_static_reference_and_input_health()
	{
		await using var fixture = await Fixture.CreateAsync();

		fixture.Runtime.SetBroadcastTestPattern(
			fixture.MediaSourceA,
			true,
			V1BroadcastTestPatternMode.Static);
		var staticBefore = fixture.Runtime.ProcessNextBoundary();

		fixture.Runtime.SetInputSignalState(fixture.MediaSourceA, V1InputSignalState.Lost);
		Assert.Equal(V1InputSignalState.Valid, fixture.Runtime.Snapshot.InputSignals[fixture.MediaSourceA]);

		fixture.Runtime.SetBroadcastTestPattern(
			fixture.MediaSourceA,
			true,
			V1BroadcastTestPatternMode.MotionTiming);
		var motion = fixture.Runtime.ProcessNextBoundary();

		fixture.Runtime.SetBroadcastTestPattern(
			fixture.MediaSourceA,
			true,
			V1BroadcastTestPatternMode.Static);
		var staticAfter = fixture.Runtime.ProcessNextBoundary();

		Assert.NotEqual(
			System.Security.Cryptography.SHA256.HashData(staticBefore.ProgramPixels),
			System.Security.Cryptography.SHA256.HashData(motion.ProgramPixels));
		Assert.Equal(staticBefore.ProgramPixels, staticAfter.ProgramPixels);
		Assert.Equal(V1InputSignalState.Valid, fixture.Runtime.Snapshot.InputSignals[fixture.MediaSourceA]);

		fixture.Runtime.SetBroadcastTestPattern(fixture.MediaSourceA, false);
		Assert.Equal(V1InputSignalState.Lost, fixture.Runtime.Snapshot.InputSignals[fixture.MediaSourceA]);
	}

	private static void AssertPixelMatches(
		BroadcastTestPatternGenerator pattern,
		byte[] actualPixels,
		VideoFormat format,
		int x,
		int y)
	{
		var expected = pattern.GetPixel((uint)x, (uint)y);
		Assert.Equal(
			new PixelValue(expected.Red, expected.Green, expected.Blue, expected.Alpha),
			Pixel(actualPixels, format, x, y));
	}

	private static PixelValue Pixel(byte[] pixels, VideoFormat format, int x, int y)
	{
		var offset = checked((y * (int)format.Width + x) * 4);
		return new PixelValue(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
	}

	private readonly record struct PixelValue(byte Red, byte Green, byte Blue, byte Alpha);

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(
			MediaSourceId mediaSourceA,
			MediaSourceId mediaSourceB,
			VideoFormat format,
			BoundedProductionJournal journal,
			V1RuntimeHostService runtime)
		{
			MediaSourceA = mediaSourceA;
			MediaSourceB = mediaSourceB;
			Format = format;
			Journal = journal;
			Runtime = runtime;
		}

		public MediaSourceId MediaSourceA { get; }
		public MediaSourceId MediaSourceB { get; }
		public VideoFormat Format { get; }
		public BoundedProductionJournal Journal { get; }
		public V1RuntimeHostService Runtime { get; }

		public static ValueTask<Fixture> CreateAsync()
		{
			var format = VideoFormat.Hd1080p50Rgba8;
			var sourceA = new ProductionSourceId(Identity.Parse("c2000000-0000-0000-0000-00000000000a"));
			var sourceB = new ProductionSourceId(Identity.Parse("c2000000-0000-0000-0000-00000000000b"));
			var mediaA = new MediaSourceId(sourceA.Value);
			var mediaB = new MediaSourceId(sourceB.Value);
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("c2000000-0000-0000-0000-000000000001")),
				"Broadcast Test Pattern",
				new[]
				{
					new ProductionSourceSpecification(sourceA, "Input 1"),
					new ProductionSourceSpecification(sourceB, "Input 2")
				},
				new ProductionRoutingState(sourceA, sourceA));

			var runtime = new V1RuntimeHostService(mediaA, mediaB, format, new CollectingRecordingWriter());
			var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
			var fixture = new Fixture(mediaA, mediaB, format, journal, runtime);
			var staged = control.Initialize();
			Assert.True(staged.Accepted, staged.Failure?.ToString());
			Assert.NotNull(staged.Execution);
			var execution = staged.Execution!;
			var applied = runtime.ApplyExecution(
				execution.PreparedExecution,
				execution.ProgramSinkId,
				execution.ProgramTransition);
			Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
			return ValueTask.FromResult(fixture);
		}

		public async ValueTask DisposeAsync()
		{
			await Runtime.DisposeAsync();
			await Journal.DisposeAsync();
		}
	}

	private sealed class CollectingRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
