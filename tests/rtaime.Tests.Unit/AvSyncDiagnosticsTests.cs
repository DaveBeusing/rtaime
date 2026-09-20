// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Unit;

public sealed class AvSyncDiagnosticsTests
{
	private static readonly Timebase AudioTimebase = new(1, 48_000);

	[Theory]
	[InlineData(25, 1, 1UL, 25UL, 48_000UL, 0d)]
	[InlineData(50, 1, 1UL, 50UL, 48_000UL, 0d)]
	[InlineData(30_000, 1_001, 1UL, 30UL, 48_000UL, 1d)]
	[InlineData(60_000, 1_001, 1UL, 60UL, 48_000UL, 1d)]
	public void Event_targets_are_derived_from_one_exact_media_time(
		long rateNumerator,
		long rateDenominator,
		ulong eventId,
		ulong expectedFrame,
		ulong expectedSample,
		double expectedVideoOffsetMilliseconds)
	{
		var timeline = new AvSyncEventTimeline();

		var scheduled = timeline.GetEvent(
			eventId,
			new FrameRate(rateNumerator, rateDenominator),
			48_000);

		Assert.Equal(eventId, scheduled.EventId);
		Assert.Equal(expectedFrame, scheduled.TargetVideoFrameSequence);
		Assert.Equal(expectedSample, scheduled.TargetAudioSamplePosition);
		Assert.Equal(new Rational(1, 1), scheduled.ExpectedMediaTime);
		Assert.Equal(expectedVideoOffsetMilliseconds, scheduled.ScheduledVideoOffsetMilliseconds, 6);
	}

	[Fact]
	public void Video_flash_and_audio_pulse_share_the_same_event_identifier()
	{
		var timeline = new AvSyncEventTimeline();
		var rate = FrameRate.Fps59_94;
		var video = timeline.InspectVideo(
			new FrameTiming(60, 60, new Timebase(1_001, 60_000)),
			rate,
			48_000);
		var audio = timeline.InspectAudio(
			new AudioBufferTiming(47_952, 96, 47_952, AudioTimebase),
			rate,
			48_000);

		Assert.True(video.IsFlashFrame);
		Assert.True(audio.ContainsPulse);
		Assert.Equal(1UL, video.Event.EventId);
		Assert.Equal(video.Event.EventId, audio.Event.EventId);
		Assert.Equal(48U, audio.SampleOffset);
		Assert.Equal(video.Event.TargetVideoFrameSequence, audio.Event.TargetVideoFrameSequence);
		Assert.Equal(video.Event.TargetAudioSamplePosition, audio.Event.TargetAudioSamplePosition);
	}

	[Fact]
	public void Long_running_event_schedule_has_no_cumulative_rounding_drift()
	{
		var timeline = new AvSyncEventTimeline();
		var rate = FrameRate.Fps59_94;
		const ulong eventId = 10_000_000;

		var scheduled = timeline.GetEvent(eventId, rate, 48_000);
		var next = timeline.GetEvent(eventId + 1, rate, 48_000);

		Assert.Equal(eventId * 48_000UL, scheduled.TargetAudioSamplePosition);
		Assert.Equal((eventId + 1) * 48_000UL, next.TargetAudioSamplePosition);
		Assert.Equal(
			(ulong)(((System.Numerics.BigInteger)eventId * 60_000 + 1_001 - 1) / 1_001),
			scheduled.TargetVideoFrameSequence);
		Assert.Equal(
			(ulong)(((System.Numerics.BigInteger)(eventId + 1) * 60_000 + 1_001 - 1) / 1_001),
			next.TargetVideoFrameSequence);
		Assert.InRange(
			next.ScheduledVideoOffsetMilliseconds,
			0d,
			(1_001d / 60_000d) * 1_000d);
	}

	[Fact]
	public void Recreated_timeline_produces_identical_restart_state()
	{
		var first = new AvSyncEventTimeline();
		var second = new AvSyncEventTimeline();

		Assert.Equal(
			first.GetEvent(123_456, FrameRate.Fps59_94, 48_000),
			second.GetEvent(123_456, FrameRate.Fps59_94, 48_000));
	}

	[Fact]
	public void Tracker_reports_only_internal_submit_offset_and_drift_for_matched_events()
	{
		var timeline = new AvSyncEventTimeline();
		var tracker = new AvSyncDiagnosticsTracker();
		var first = timeline.GetEvent(1, FrameRate.Fps50, 48_000);
		var second = timeline.GetEvent(2, FrameRate.Fps50, 48_000);
		var tick = Stopwatch.Frequency / 1_000;

		var partial = tracker.RecordVideoSubmit(new AvSyncVideoEventObservation(first, true), 10 * Stopwatch.Frequency);
		var measured = tracker.RecordAudioSubmit(new AvSyncAudioEventObservation(first, true, 0), 10 * Stopwatch.Frequency + tick);
		tracker.RecordVideoSubmit(new AvSyncVideoEventObservation(second, true), 20 * Stopwatch.Frequency);
		var drifted = tracker.RecordAudioSubmit(new AvSyncAudioEventObservation(second, true, 0), 20 * Stopwatch.Frequency + (2 * tick));

		Assert.Equal(AvSyncMeasurementState.Partial, partial.State);
		Assert.Equal(AvSyncMeasurementState.Measured, measured.State);
		Assert.Equal(1d, measured.SubmitOffsetMilliseconds!.Value, 3);
		Assert.Equal(0d, measured.DriftFromBaselineMilliseconds!.Value, 3);
		Assert.Equal(2d, drifted.SubmitOffsetMilliseconds!.Value, 3);
		Assert.Equal(1d, drifted.DriftFromBaselineMilliseconds!.Value, 3);
		Assert.Contains("physical output latency is not measured", drifted.Detail, StringComparison.OrdinalIgnoreCase);
	}
}
