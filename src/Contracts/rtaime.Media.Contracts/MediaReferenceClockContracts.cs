// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media.Contracts;

// A reference clock is an observed provider capability, never a replacement
// for ControlHost production authority or the rational Runtime frame schedule.
public enum MediaReferenceClockSource { Unknown = 0, Virtual = 1, HardwarePtp = 2, ExternalGenlock = 3 }
public enum MediaReferenceLockState { Unverified = 0, Locked = 1, Holdover = 2, Lost = 3 }
public enum MediaReferenceEvidence { Unverified = 0, Simulated = 1, HardwareQualified = 2 }
public enum MediaReferenceClockRequirement { Asynchronous = 0, HardwareLocked = 1, QualifiedHoldover = 2 }

public sealed record MediaReferenceClockObservation(
	int FormatVersion,
	string ProviderId,
	string ClockDomainId,
	Guid EpochId,
	MediaReferenceClockSource Source,
	MediaReferenceLockState LockState,
	MediaReferenceEvidence Evidence,
	int? PtpDomain,
	string? GrandmasterId,
	DateTimeOffset ObservedAtUtc,
	DateTimeOffset? LastSyncAtUtc,
	long? OffsetNanoseconds,
	double? DriftPartsPerBillion,
	TimeSpan HoldoverElapsed,
	TimeSpan MaximumHoldover,
	string Detail)
{
	public const int CurrentFormatVersion = 1;
	public static readonly TimeSpan MaximumQualifiedHoldover = TimeSpan.FromMinutes(5);
	public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromSeconds(3);

	public static MediaReferenceClockObservation Unverified(Guid epoch) => new(
		CurrentFormatVersion, "runtime-unqualified-clock", "runtime-monotonic",
		epoch, MediaReferenceClockSource.Unknown, MediaReferenceLockState.Unverified,
		MediaReferenceEvidence.Unverified, null, null,
		DateTimeOffset.UtcNow, null, null, null,
		TimeSpan.Zero, MaximumQualifiedHoldover,
		"No hardware clock or reference lock measurement is available.");

	public void Validate()
	{
		if (FormatVersion != CurrentFormatVersion ||
			!SafeIdentity(ProviderId, 80) || !SafeIdentity(ClockDomainId, 80) ||
			EpochId == Guid.Empty || !Enum.IsDefined(Source) ||
			!Enum.IsDefined(LockState) || !Enum.IsDefined(Evidence) ||
			PtpDomain is < 0 or > 127 ||
			GrandmasterId is not null && (GrandmasterId.Length != 16 ||
				GrandmasterId.Any(c => !Uri.IsHexDigit(c))) ||
			ObservedAtUtc.Offset != TimeSpan.Zero ||
			(LastSyncAtUtc.HasValue && LastSyncAtUtc.Value.Offset != TimeSpan.Zero) ||
			LastSyncAtUtc > ObservedAtUtc ||
			DriftPartsPerBillion is double drift && (!double.IsFinite(drift) || Math.Abs(drift) > 1000000) ||
			OffsetNanoseconds is long offset && (offset < -1000000000L || offset > 1000000000L) ||
			HoldoverElapsed < TimeSpan.Zero || MaximumHoldover < TimeSpan.Zero ||
			MaximumHoldover > MaximumQualifiedHoldover || (HoldoverElapsed > MaximumHoldover &&
				LockState == MediaReferenceLockState.Holdover) ||
			string.IsNullOrWhiteSpace(Detail) || Detail.Length > 256)
			throw new ArgumentException("Invalid or unbounded media reference clock observation.");

		if (Source == MediaReferenceClockSource.HardwarePtp && (PtpDomain is null || GrandmasterId is null) ||
			Source == MediaReferenceClockSource.Virtual && Evidence == MediaReferenceEvidence.Simulated &&
				((PtpDomain is null) != (GrandmasterId is null)) ||
			(Source is MediaReferenceClockSource.Unknown or MediaReferenceClockSource.ExternalGenlock) &&
				(PtpDomain is not null || GrandmasterId is not null) ||
			Evidence == MediaReferenceEvidence.HardwareQualified &&
				(Source is not (MediaReferenceClockSource.HardwarePtp or MediaReferenceClockSource.ExternalGenlock) ||
				 LastSyncAtUtc is null || OffsetNanoseconds is null || DriftPartsPerBillion is null) ||
			Evidence == MediaReferenceEvidence.Simulated && Source != MediaReferenceClockSource.Virtual ||
			Source == MediaReferenceClockSource.Virtual && Evidence == MediaReferenceEvidence.HardwareQualified ||
			(LockState is MediaReferenceLockState.Locked or MediaReferenceLockState.Holdover) &&
				(Evidence == MediaReferenceEvidence.Unverified || LastSyncAtUtc is null) ||
			LockState == MediaReferenceLockState.Holdover && HoldoverElapsed == TimeSpan.Zero)
			throw new ArgumentException("Clock state lacks its claimed provider/sync provenance.");
	}

	public bool IsCurrent(DateTimeOffset now) =>
		now.Offset == TimeSpan.Zero && ObservedAtUtc <= now &&
		now - ObservedAtUtc <= MaximumObservationAge;

	public bool HardwareLockAdmissible(DateTimeOffset now, bool allowHoldover)
	{
		Validate();
		if (!IsCurrent(now) || Evidence != MediaReferenceEvidence.HardwareQualified ||
			Source is not (MediaReferenceClockSource.HardwarePtp or MediaReferenceClockSource.ExternalGenlock))
			return false;
		if (LockState == MediaReferenceLockState.Locked)
			return LastSyncAtUtc.HasValue && now - LastSyncAtUtc.Value <= MaximumObservationAge;
		return allowHoldover && LockState == MediaReferenceLockState.Holdover &&
			LastSyncAtUtc.HasValue && HoldoverElapsed <= MaximumQualifiedHoldover &&
			now - LastSyncAtUtc.Value <= MaximumQualifiedHoldover;
	}

	private static bool SafeIdentity(string? value, int maximum) =>
		!string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
		value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public static class MediaReferenceAdmission
{
	public static bool Allows(MediaReferenceClockRequirement requirement,
		MediaReferenceClockObservation? observation, DateTimeOffset now)
	{
		if (!Enum.IsDefined(requirement))
			throw new ArgumentOutOfRangeException(nameof(requirement));
		if (requirement == MediaReferenceClockRequirement.Asynchronous)
			return true; // Existing local and asynchronous Program routes unchanged.
		try
		{
			return observation?.HardwareLockAdmissible(now,
				requirement == MediaReferenceClockRequirement.QualifiedHoldover) == true;
		}
		catch (ArgumentException) { return false; }
	}
}
