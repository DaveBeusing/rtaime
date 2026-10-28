// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Runtime;

/// <summary>
/// Test-only PTP/reference discipline. All time advances are explicit input,
/// never DateTime.Now or a second production scheduler. Virtual lock is not
/// hardware-qualified and cannot admit a hardware-synchronized route.
/// </summary>
public sealed class VirtualMediaReferenceClock : IMediaReferenceClockProvider
{
	public static readonly TimeSpan LockObservationBudget = TimeSpan.FromSeconds(2);
	public static readonly TimeSpan HoldoverBudget = TimeSpan.FromMinutes(5);

	private readonly int _ptpDomain;
	private Guid _epoch = Guid.NewGuid();
	private string _grandmasterId;
	private TimeSpan _lastMonotonic;
	private TimeSpan _lastSyncMonotonic;
	private DateTimeOffset? _lastSyncAtUtc;
	private long _offsetNanoseconds;
	private double _driftPpb;
	private bool _hasSync;

	public VirtualMediaReferenceClock(int ptpDomain = 0,
		string grandmasterId = "0011223344556677")
	{
		if (ptpDomain is < 0 or > 127)
			throw new ArgumentOutOfRangeException(nameof(ptpDomain));
		ValidateGrandmaster(grandmasterId);
		_ptpDomain = ptpDomain;
		_grandmasterId = grandmasterId.ToUpperInvariant();
	}

	public MediaReferenceClockCapabilities Capabilities { get; } =
		new("virtual-reference-clock", MediaReferenceClockSource.Virtual,
			SupportsHoldover: true, HardwareTimestamping: false,
			Qualification: "SIMULATED — hardware PTP/Genlock unverified");

	public Guid EpochId => _epoch;

	public void ObserveSync(TimeSpan monotonicAt, DateTimeOffset utc,
		string grandmasterId, long offsetNanoseconds, double driftPpb)
	{
		ValidateMonotonic(monotonicAt);
		ValidateGrandmaster(grandmasterId);
		if (utc.Offset != TimeSpan.Zero ||
			offsetNanoseconds is < -1_000_000_000 or > 1_000_000_000 ||
			!double.IsFinite(driftPpb) || Math.Abs(driftPpb) > 1_000_000)
			throw new ArgumentOutOfRangeException(nameof(utc), "Simulated sync evidence is invalid.");
		if (!string.Equals(grandmasterId, _grandmasterId, StringComparison.OrdinalIgnoreCase) ||
			(_hasSync && monotonicAt - _lastSyncMonotonic > LockObservationBudget + HoldoverBudget))
		{
			// A different GM or a recovered LOST reference invalidates the
			// previous RTP/PTP mapping, even when the same GM returns.
			_epoch = Guid.NewGuid();
			_grandmasterId = grandmasterId.ToUpperInvariant();
		}
		_lastMonotonic = monotonicAt;
		_lastSyncMonotonic = monotonicAt;
		_lastSyncAtUtc = utc;
		_offsetNanoseconds = offsetNanoseconds;
		_driftPpb = driftPpb;
		_hasSync = true;
	}

	public void SwitchGrandmaster(TimeSpan monotonicAt, string grandmasterId)
	{
		ValidateMonotonic(monotonicAt);
		ValidateGrandmaster(grandmasterId);
		_lastMonotonic = monotonicAt;
		_grandmasterId = grandmasterId.ToUpperInvariant();
		_epoch = Guid.NewGuid();
		_hasSync = false;
		_lastSyncAtUtc = null;
	}

	public void Reset(TimeSpan monotonicAt)
	{
		ValidateMonotonic(monotonicAt);
		_lastMonotonic = monotonicAt;
		_epoch = Guid.NewGuid();
		_hasSync = false;
		_lastSyncAtUtc = null;
	}

	public MediaReferenceClockObservation Observe(
		TimeSpan runtimeMonotonicTime, DateTimeOffset observedAtUtc)
	{
		ValidateMonotonic(runtimeMonotonicTime);
		if (observedAtUtc.Offset != TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(observedAtUtc));
		_lastMonotonic = runtimeMonotonicTime;
		var sinceSync = _hasSync ? runtimeMonotonicTime - _lastSyncMonotonic : TimeSpan.Zero;
		var lockState = !_hasSync ? MediaReferenceLockState.Unverified :
			sinceSync <= LockObservationBudget ? MediaReferenceLockState.Locked :
			sinceSync <= LockObservationBudget + HoldoverBudget ? MediaReferenceLockState.Holdover :
			MediaReferenceLockState.Lost;
		var holdover = lockState == MediaReferenceLockState.Holdover
			? sinceSync - LockObservationBudget : TimeSpan.Zero;
		var result = new MediaReferenceClockObservation(
			MediaReferenceClockObservation.CurrentFormatVersion,
			Capabilities.ProviderId, "virtual-ptp-" + _ptpDomain, _epoch,
			MediaReferenceClockSource.Virtual, lockState, MediaReferenceEvidence.Simulated,
			_ptpDomain, _grandmasterId, observedAtUtc,
			_lastSyncAtUtc, _hasSync ? _offsetNanoseconds : null,
			_hasSync ? _driftPpb : null, holdover, HoldoverBudget,
			lockState switch
			{
				MediaReferenceLockState.Locked => "Synthetic GM sync observed; not physical PTP lock.",
				MediaReferenceLockState.Holdover => "Synthetic GM lost; testing finite holdover.",
				MediaReferenceLockState.Lost => "Synthetic holdover expired; reference lost.",
				_ => "No virtual reference lock has been observed in this epoch."
			});
		result.Validate();
		return result;
	}

	private void ValidateMonotonic(TimeSpan at)
	{
		if (at < _lastMonotonic || at < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(at), "Virtual reference time must be monotonic.");
	}

	private static void ValidateGrandmaster(string? value)
	{
		if (value is null || value.Length != 16 || value.Any(c => !Uri.IsHexDigit(c)))
			throw new ArgumentException("Grandmaster identity must be sixteen hexadecimal digits.");
	}
}

/// <summary>
/// RTP 90 kHz video ticks and 48 kHz PCM positions are projections of the
/// same existing rational frame index, never separately accrued clocks.
/// Values use floor rounding from the epoch (no per-frame rounding drift).
/// </summary>
public static class RationalMediaClockMap
{
	public const uint VideoRtpClockRate = 90_000;
	public const uint AudioSampleRate = 48_000;

	public static ulong VideoTicks(FrameRate frameRate, ulong zeroBasedBoundary) =>
		Scale(frameRate, zeroBasedBoundary, VideoRtpClockRate);

	public static ulong AudioSamples(FrameRate frameRate, ulong zeroBasedBoundary) =>
		Scale(frameRate, zeroBasedBoundary, AudioSampleRate);

	public static uint VideoRtpTimestamp(FrameRate frameRate, ulong boundary,
		uint epochRtpTimestamp = 0) =>
		unchecked(epochRtpTimestamp + (uint)VideoTicks(frameRate, boundary));

	public static uint AudioRtpTimestamp(FrameRate frameRate, ulong boundary,
		uint epochRtpTimestamp = 0) =>
		unchecked(epochRtpTimestamp + (uint)AudioSamples(frameRate, boundary));

	private static ulong Scale(FrameRate rate, ulong boundary, uint ticksPerSecond)
	{
		if (rate.Numerator <= 0 || rate.Denominator <= 0)
			throw new ArgumentOutOfRangeException(nameof(rate));
		var value = checked((Int128)boundary * rate.Denominator * ticksPerSecond / rate.Numerator);
		return checked((ulong)value);
	}
}
