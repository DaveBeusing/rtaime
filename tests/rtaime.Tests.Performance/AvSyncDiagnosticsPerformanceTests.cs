// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Numerics;
using rtaime.Core;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Performance;

public sealed class AvSyncDiagnosticsPerformanceTests
{
	[Fact]
	public void Twenty_four_hour_fractional_rate_schedule_has_no_cumulative_drift()
	{
		var timeline = new AvSyncEventTimeline();
		var rate = FrameRate.Fps59_94;
		const uint sampleRate = 48_000;
		const ulong finalEventId = 24UL * 60UL * 60UL;

		AvSyncScheduledEvent previous = default;
		for (ulong eventId = 0; eventId <= finalEventId; eventId++)
		{
			var scheduled = timeline.GetEvent(eventId, rate, sampleRate);
			var expectedFrame = (ulong)(
				((BigInteger)eventId * rate.Numerator + rate.Denominator - 1) /
				rate.Denominator);

			Assert.Equal(eventId, scheduled.EventId);
			Assert.Equal(new Rational((long)eventId, 1), scheduled.ExpectedMediaTime);
			Assert.Equal(eventId * sampleRate, scheduled.TargetAudioSamplePosition);
			Assert.Equal(expectedFrame, scheduled.TargetVideoFrameSequence);
			Assert.InRange(
				scheduled.ScheduledVideoOffsetMilliseconds,
				0d,
				(rate.Denominator / (double)rate.Numerator) * 1_000d);

			if (eventId > 0)
			{
				Assert.Equal(sampleRate, scheduled.TargetAudioSamplePosition - previous.TargetAudioSamplePosition);
				Assert.True(scheduled.TargetVideoFrameSequence > previous.TargetVideoFrameSequence);
			}

			previous = scheduled;
		}

		Assert.Equal(finalEventId * sampleRate, previous.TargetAudioSamplePosition);
		Assert.Equal(
			(ulong)(((BigInteger)finalEventId * 60_000 + 1_001 - 1) / 1_001),
			previous.TargetVideoFrameSequence);
	}
}
