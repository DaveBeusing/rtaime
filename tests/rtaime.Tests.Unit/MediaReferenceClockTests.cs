// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime;

namespace rtaime.Tests.Unit;

public sealed class MediaReferenceClockTests
{
	private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData(25, 1, 25UL, 90000UL, 48000UL)]
	[InlineData(50, 1, 50UL, 90000UL, 48000UL)]
	[InlineData(60000, 1001, 60UL, 90090UL, 48048UL)]
	public void Video_RTP_and_PCM_positions_share_an_exact_rational_epoch(
		long numerator, long denominator, ulong boundary, ulong video, ulong audio)
	{
		var rate = new FrameRate(numerator, denominator);
		Assert.Equal(video, RationalMediaClockMap.VideoTicks(rate, boundary));
		Assert.Equal(audio, RationalMediaClockMap.AudioSamples(rate, boundary));
		Assert.Equal(video % (1UL << 32),
			RationalMediaClockMap.VideoRtpTimestamp(rate, boundary));
		Assert.Equal(audio % (1UL << 32),
			RationalMediaClockMap.AudioRtpTimestamp(rate, boundary));
	}

	[Fact]
	public void Fractional_rate_progress_does_not_accumulate_independent_per_frame_rounding()
	{
		var rate = new FrameRate(60000, 1001);
		Assert.Equal(1501UL, RationalMediaClockMap.VideoTicks(rate, 1));
		Assert.Equal(3003UL, RationalMediaClockMap.VideoTicks(rate, 2));
		Assert.Equal(800UL, RationalMediaClockMap.AudioSamples(rate, 1));
		Assert.Equal(1601UL, RationalMediaClockMap.AudioSamples(rate, 2));
		for (ulong slot = 1; slot < 1_000_000; slot++)
		{
			var video = RationalMediaClockMap.VideoTicks(rate, slot);
			var next = RationalMediaClockMap.VideoTicks(rate, slot + 1);
			Assert.True(next > video);
			var sample = RationalMediaClockMap.AudioSamples(rate, slot);
			var nextSample = RationalMediaClockMap.AudioSamples(rate, slot + 1);
			Assert.True(nextSample > sample);
		}
		Assert.Equal(uint.MaxValue, RationalMediaClockMap.VideoRtpTimestamp(
			FrameRate.Fps50, 0, uint.MaxValue));
		Assert.Equal(1799U, RationalMediaClockMap.VideoRtpTimestamp(
			FrameRate.Fps50, 1, uint.MaxValue)); // 2^32 modulo 1800
	}

	[Fact]
	public void Virtual_reference_qualifies_five_minutes_of_simulated_holdover_but_never_hardware()
	{
		var source = new VirtualMediaReferenceClock(42, "0011223344556677");
		var initial = source.Observe(TimeSpan.Zero, Start);
		Assert.Equal(MediaReferenceLockState.Unverified, initial.LockState);
		Assert.Equal(MediaReferenceEvidence.Simulated, initial.Evidence);
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked, initial, Start));
		source.ObserveSync(TimeSpan.Zero, Start, "0011223344556677", 100, 4.5);
		var locked = source.Observe(TimeSpan.FromSeconds(1), Start.AddSeconds(1));
		Assert.Equal(MediaReferenceLockState.Locked, locked.LockState);
		Assert.Equal(42, locked.PtpDomain);
		Assert.Equal("0011223344556677", locked.GrandmasterId);
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked, locked, Start.AddSeconds(1)));
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.QualifiedHoldover, locked, Start.AddSeconds(1)));
		var holdover = source.Observe(TimeSpan.FromSeconds(302), Start.AddSeconds(302));
		Assert.Equal(MediaReferenceLockState.Holdover, holdover.LockState);
		Assert.Equal(TimeSpan.FromMinutes(5), holdover.HoldoverElapsed);
		var lost = source.Observe(TimeSpan.FromSeconds(303), Start.AddSeconds(303));
		Assert.Equal(MediaReferenceLockState.Lost, lost.LockState);
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.QualifiedHoldover, lost, Start.AddSeconds(303)));
	}

	[Fact]
	public void Grandmaster_change_fences_old_epoch_and_requires_new_lock()
	{
		var source = new VirtualMediaReferenceClock();
		source.ObserveSync(TimeSpan.Zero, Start, "0011223344556677", 1, 0);
		var first = source.Observe(TimeSpan.FromSeconds(1), Start.AddSeconds(1));
		source.SwitchGrandmaster(TimeSpan.FromSeconds(4), "8899AABBCCDDEEFF");
		var switched = source.Observe(TimeSpan.FromSeconds(5), Start.AddSeconds(5));
		Assert.NotEqual(first.EpochId, switched.EpochId);
		Assert.Equal(MediaReferenceLockState.Unverified, switched.LockState);
		Assert.Null(switched.LastSyncAtUtc);
		source.ObserveSync(TimeSpan.FromSeconds(6), Start.AddSeconds(6),
			"8899AABBCCDDEEFF", 2, 0.3);
		var recovered = source.Observe(TimeSpan.FromSeconds(7), Start.AddSeconds(7));
		Assert.Equal(switched.EpochId, recovered.EpochId);
		Assert.Equal(MediaReferenceLockState.Locked, recovered.LockState);
		Assert.Equal("8899AABBCCDDEEFF", recovered.GrandmasterId);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			source.Observe(TimeSpan.FromSeconds(6), Start.AddSeconds(8)));
		source.Reset(TimeSpan.FromSeconds(8));
		Assert.NotEqual(recovered.EpochId, source.EpochId);
	}

	[Fact]
	public void Hardware_route_admission_requires_current_source_bound_provider_proof()
	{
		var obs = new MediaReferenceClockObservation(
			1, "ptp-hardware-provider", "ptp-domain-0", Guid.NewGuid(),
			MediaReferenceClockSource.HardwarePtp, MediaReferenceLockState.Locked,
			MediaReferenceEvidence.HardwareQualified, 0, "0102030405060708",
			Start, Start, 120, 0.8, TimeSpan.Zero, TimeSpan.FromMinutes(5),
			"Source-bound device reference measurement.");
		obs.Validate();
		Assert.True(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked, obs, Start));
		Assert.True(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.QualifiedHoldover, obs, Start));
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked, obs, Start.AddSeconds(4)));
		var holdover = obs with {
			LockState = MediaReferenceLockState.Holdover,
			ObservedAtUtc = Start.AddMinutes(4),
			HoldoverElapsed = TimeSpan.FromMinutes(4)
		};
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked, holdover, Start.AddMinutes(4)));
		Assert.True(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.QualifiedHoldover, holdover, Start.AddMinutes(4)));
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.QualifiedHoldover,
			holdover with { ObservedAtUtc = Start.AddMinutes(5).AddSeconds(1) },
			Start.AddMinutes(5).AddSeconds(1)));
	}

	[Fact]
	public void Unknown_simulated_and_malformed_external_reference_fail_closed()
	{
		var unknown = MediaReferenceClockObservation.Unverified(Guid.NewGuid());
		unknown.Validate();
		Assert.True(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.Asynchronous, null, Start));
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked, null, Start));
		Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.QualifiedHoldover, unknown, Start));
		Assert.Throws<ArgumentException>(() =>
			(unknown with { Source = MediaReferenceClockSource.Virtual,
				LockState = MediaReferenceLockState.Locked,
				Evidence = MediaReferenceEvidence.HardwareQualified }).Validate());
		Assert.Throws<ArgumentException>(() =>
			(unknown with { DriftPartsPerBillion = double.NaN }).Validate());
		Assert.Throws<ArgumentException>(() =>
			(unknown with { ProviderId = "credential://secret" }).Validate());
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			MediaReferenceAdmission.Allows((MediaReferenceClockRequirement)999, null, Start));
	}
}
