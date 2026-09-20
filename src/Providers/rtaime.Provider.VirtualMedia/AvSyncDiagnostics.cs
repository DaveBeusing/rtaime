// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Numerics;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.VirtualMedia;

public readonly record struct AvSyncScheduledEvent(
	ulong EventId,
	ulong TargetVideoFrameSequence,
	ulong TargetAudioSamplePosition,
	Rational ExpectedMediaTime,
	double ScheduledVideoOffsetMilliseconds);

public readonly record struct AvSyncVideoEventObservation(
	AvSyncScheduledEvent Event,
	bool IsFlashFrame);

public readonly record struct AvSyncAudioEventObservation(
	AvSyncScheduledEvent Event,
	bool ContainsPulse,
	uint SampleOffset);

public enum AvSyncMeasurementState
{
	Unavailable = 1,
	Partial = 2,
	Measured = 3
}

public sealed record AvSyncDiagnosticsSnapshot(
	AvSyncMeasurementState State,
	ulong? EventId,
	Rational? ExpectedMediaTime,
	ulong? TargetVideoFrameSequence,
	ulong? TargetAudioSamplePosition,
	double? ScheduledVideoOffsetMilliseconds,
	double? SubmitOffsetMilliseconds,
	double? DriftFromBaselineMilliseconds,
	string Detail)
{
	public static AvSyncDiagnosticsSnapshot Unavailable { get; } = new(
		AvSyncMeasurementState.Unavailable,
		null,
		null,
		null,
		null,
		null,
		null,
		null,
		"No synchronized A/V event has been observed.");
}

/// <summary>
/// Exact periodic A/V event timeline. Video and audio scheduling are derived from one rational
/// media-time period and never from independent timers.
/// </summary>
public sealed class AvSyncEventTimeline
{
	public const long DefaultPeriodNumerator = 1;
	public const long DefaultPeriodDenominator = 1;

	private readonly Rational _periodSeconds;

	public AvSyncEventTimeline(
		long periodNumerator = DefaultPeriodNumerator,
		long periodDenominator = DefaultPeriodDenominator)
	{
		if (periodNumerator <= 0)
			throw new ArgumentOutOfRangeException(nameof(periodNumerator), "A/V sync event period must be greater than zero.");
		_periodSeconds = new Rational(periodNumerator, periodDenominator);
	}

	public Rational PeriodSeconds => _periodSeconds;

	public AvSyncScheduledEvent GetEvent(
		ulong eventId,
		FrameRate videoFrameRate,
		uint audioSampleRate)
	{
		if (audioSampleRate == 0)
			throw new ArgumentOutOfRangeException(nameof(audioSampleRate));

		var eventNumerator = new BigInteger(eventId) * _periodSeconds.Numerator;
		var eventDenominator = new BigInteger(_periodSeconds.Denominator);
		var targetFrame = CeilingDivide(
			eventNumerator * videoFrameRate.Numerator,
			eventDenominator * videoFrameRate.Denominator);
		var targetSample = CeilingDivide(
			eventNumerator * audioSampleRate,
			eventDenominator);

		if (targetFrame < BigInteger.Zero || targetFrame > ulong.MaxValue)
			throw new OverflowException("A/V sync target video frame exceeds UInt64 range.");
		if (targetSample < BigInteger.Zero || targetSample > ulong.MaxValue)
			throw new OverflowException("A/V sync target audio sample exceeds UInt64 range.");
		if (eventNumerator < long.MinValue || eventNumerator > long.MaxValue)
			throw new OverflowException("A/V sync event media time exceeds Rational range.");

		var frameSequence = (ulong)targetFrame;
		var samplePosition = (ulong)targetSample;
		var expected = new Rational((long)eventNumerator, _periodSeconds.Denominator);
		var videoSeconds = (double)frameSequence * videoFrameRate.Denominator / videoFrameRate.Numerator;
		var audioSeconds = (double)samplePosition / audioSampleRate;

		return new AvSyncScheduledEvent(
			eventId,
			frameSequence,
			samplePosition,
			expected,
			(videoSeconds - audioSeconds) * 1_000d);
	}

	public AvSyncVideoEventObservation InspectVideo(
		FrameTiming timing,
		FrameRate videoFrameRate,
		uint audioSampleRate)
	{
		if (timing.PresentationTimestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timing), "A/V sync video media time must not be negative.");

		var eventId = EventIdAtOrBefore(
			new BigInteger(timing.PresentationTimestamp) * timing.Timebase.Numerator,
			timing.Timebase.Denominator);
		var scheduled = GetEvent(eventId, videoFrameRate, audioSampleRate);
		return new AvSyncVideoEventObservation(
			scheduled,
			timing.SequenceNumber == scheduled.TargetVideoFrameSequence);
	}

	public AvSyncAudioEventObservation InspectAudio(
		AudioBufferTiming timing,
		FrameRate videoFrameRate,
		uint audioSampleRate)
	{
		if (timing.PresentationTimestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timing), "A/V sync audio media time must not be negative.");
		if (audioSampleRate == 0)
			throw new ArgumentOutOfRangeException(nameof(audioSampleRate));

		var firstEventId = FirstEventAtOrAfterSample(timing.SamplePosition, audioSampleRate);
		var scheduled = GetEvent(firstEventId, videoFrameRate, audioSampleRate);
		var endExclusive = new BigInteger(timing.SamplePosition) + timing.SampleCount;
		var contains = scheduled.TargetAudioSamplePosition >= timing.SamplePosition &&
			new BigInteger(scheduled.TargetAudioSamplePosition) < endExclusive;
		var offset = contains
			? checked((uint)(scheduled.TargetAudioSamplePosition - timing.SamplePosition))
			: 0;

		return new AvSyncAudioEventObservation(scheduled, contains, offset);
	}

	private ulong EventIdAtOrBefore(BigInteger mediaTimeNumerator, BigInteger mediaTimeDenominator)
	{
		if (mediaTimeNumerator < BigInteger.Zero)
			throw new ArgumentOutOfRangeException(nameof(mediaTimeNumerator));
		var numerator = mediaTimeNumerator * _periodSeconds.Denominator;
		var denominator = mediaTimeDenominator * _periodSeconds.Numerator;
		var result = numerator / denominator;
		if (result > ulong.MaxValue)
			throw new OverflowException("A/V sync event identifier exceeds UInt64 range.");
		return (ulong)result;
	}

	private ulong FirstEventAtOrAfterSample(ulong samplePosition, uint sampleRate)
	{
		var numerator = new BigInteger(samplePosition) * _periodSeconds.Denominator;
		var denominator = new BigInteger(sampleRate) * _periodSeconds.Numerator;
		var result = CeilingDivide(numerator, denominator);
		if (result > ulong.MaxValue)
			throw new OverflowException("A/V sync event identifier exceeds UInt64 range.");
		return (ulong)result;
	}

	private static BigInteger CeilingDivide(BigInteger numerator, BigInteger denominator)
	{
		if (numerator < BigInteger.Zero)
			throw new ArgumentOutOfRangeException(nameof(numerator));
		if (denominator <= BigInteger.Zero)
			throw new ArgumentOutOfRangeException(nameof(denominator));
		return numerator.IsZero ? BigInteger.Zero : (numerator + denominator - BigInteger.One) / denominator;
	}
}

/// <summary>
/// Correlates observable internal video/audio submit timestamps for the same scheduled event.
/// These measurements describe the internal pipeline only and do not claim physical display/speaker latency.
/// </summary>
public sealed class AvSyncDiagnosticsTracker
{
	private readonly object _gate = new();
	private ulong? _pendingEventId;
	private AvSyncScheduledEvent _pendingEvent;
	private long? _videoSubmitTimestamp;
	private long? _audioSubmitTimestamp;
	private double? _baselineSubmitOffsetMilliseconds;
	private AvSyncDiagnosticsSnapshot _snapshot = AvSyncDiagnosticsSnapshot.Unavailable;

	public AvSyncDiagnosticsSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return _snapshot;
		}
	}

	public void Reset()
	{
		lock (_gate)
		{
			_pendingEventId = null;
			_pendingEvent = default;
			_videoSubmitTimestamp = null;
			_audioSubmitTimestamp = null;
			_baselineSubmitOffsetMilliseconds = null;
			_snapshot = AvSyncDiagnosticsSnapshot.Unavailable;
		}
	}

	public AvSyncDiagnosticsSnapshot RecordVideoSubmit(
		AvSyncVideoEventObservation observation,
		long stopwatchTimestamp)
	{
		if (!observation.IsFlashFrame)
			return Snapshot;

		lock (_gate)
		{
			SelectEvent(observation.Event);
			_videoSubmitTimestamp = stopwatchTimestamp;
			return CompleteUnsafe();
		}
	}

	public AvSyncDiagnosticsSnapshot RecordAudioSubmit(
		AvSyncAudioEventObservation observation,
		long stopwatchTimestamp)
	{
		if (!observation.ContainsPulse)
			return Snapshot;

		lock (_gate)
		{
			SelectEvent(observation.Event);
			_audioSubmitTimestamp = stopwatchTimestamp;
			return CompleteUnsafe();
		}
	}

	private void SelectEvent(AvSyncScheduledEvent scheduled)
	{
		if (_pendingEventId == scheduled.EventId)
			return;

		_pendingEventId = scheduled.EventId;
		_pendingEvent = scheduled;
		_videoSubmitTimestamp = null;
		_audioSubmitTimestamp = null;
		_snapshot = new AvSyncDiagnosticsSnapshot(
			AvSyncMeasurementState.Partial,
			scheduled.EventId,
			scheduled.ExpectedMediaTime,
			scheduled.TargetVideoFrameSequence,
			scheduled.TargetAudioSamplePosition,
			scheduled.ScheduledVideoOffsetMilliseconds,
			null,
			null,
			"One synchronized event side has been observed; waiting for its paired submit timestamp.");
	}

	private AvSyncDiagnosticsSnapshot CompleteUnsafe()
	{
		if (_pendingEventId is null || _videoSubmitTimestamp is null || _audioSubmitTimestamp is null)
			return _snapshot;

		var submitOffsetMilliseconds =
			(_audioSubmitTimestamp.Value - _videoSubmitTimestamp.Value) * 1_000d / Stopwatch.Frequency;
		_baselineSubmitOffsetMilliseconds ??= submitOffsetMilliseconds;
		var drift = submitOffsetMilliseconds - _baselineSubmitOffsetMilliseconds.Value;

		_snapshot = new AvSyncDiagnosticsSnapshot(
			AvSyncMeasurementState.Measured,
			_pendingEvent.EventId,
			_pendingEvent.ExpectedMediaTime,
			_pendingEvent.TargetVideoFrameSequence,
			_pendingEvent.TargetAudioSamplePosition,
			_pendingEvent.ScheduledVideoOffsetMilliseconds,
			submitOffsetMilliseconds,
			drift,
			"Internal submit delta is measured from monotonic RuntimeHost timestamps; physical output latency is not measured.");
		return _snapshot;
	}
}
