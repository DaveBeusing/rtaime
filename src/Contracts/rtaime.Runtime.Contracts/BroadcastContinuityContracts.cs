// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Runtime.Contracts;

/// <summary>
/// Explicit output-role intent. Only ReportAndPreserveAuthority is currently admitted;
/// alternative pixel/audio fallback modes require separately verified sink capabilities.
/// </summary>
public enum BroadcastFailureAction
{
	ReportAndPreserveAuthority = 1,
	HoldLastCompleteFrameAndAudio = 2,
	MuteAudio = 3,
	ApprovedSlate = 4
}

public enum BroadcastContinuityPhase
{
	AwaitingFirstBoundary = 1,
	ObservedComplete = 2,
	Failed = 3,
	RecoveredAwaitingRearm = 4
}

public sealed record BroadcastFailurePolicy(
	string RoleId,
	BroadcastFailureAction Action,
	bool RequireManualRearm,
	uint MaximumHoldFrames = 0)
{
	public static BroadcastFailurePolicy Conservative(string roleId) =>
		new(roleId, BroadcastFailureAction.ReportAndPreserveAuthority, true);

	public void Validate()
	{
		if (RoleId is not ("program" or "aux") || !Enum.IsDefined(Action))
			throw new ArgumentException("Broadcast failure policy requires a known committed output role and action.");
		if (!RequireManualRearm)
			throw new ArgumentException("Unattended broadcast re-arm is unsupported without independently qualified provider admission.");
		if (Action == BroadcastFailureAction.HoldLastCompleteFrameAndAudio)
		{
			if (MaximumHoldFrames is < 1 or > 300)
				throw new ArgumentOutOfRangeException(nameof(MaximumHoldFrames),
					"Hold-last requires a bounded explicit lifetime of 1..300 frames.");
		}
		else if (MaximumHoldFrames != 0)
			throw new ArgumentException("Only a hold-last policy may declare a hold frame limit.");
	}

	public void EnsureRuntimeAdmitted()
	{
		Validate();
		if (Action != BroadcastFailureAction.ReportAndPreserveAuthority)
			throw new NotSupportedException(
				"Hold-last, mute and slate require ControlHost approval and provider-qualified frame/audio/clock delivery; no such fallback is admitted.");
	}
}

/// <summary>
/// Runtime-local exact-source evidence, not a proof of uninterrupted hardware scan-out.
/// IncidentGeneration identifies a latched fault; acknowledgement never enables a sink.
/// </summary>
public sealed record BroadcastContinuitySnapshot(
	string RoleId,
	BroadcastFailureAction ConfiguredAction,
	BroadcastContinuityPhase Phase,
	string AuthorityStateId,
	ulong AuthorityRevision,
	ulong RuntimeExecutionRevision,
	ulong? LastCompleteFrameSequence,
	ulong? LastCompleteAudioSamplePosition,
	ulong IncidentGeneration,
	string? IncidentCode,
	string? IncidentDetail,
	DateTimeOffset? IncidentStartedAtUtc,
	bool HasQualifiedFallbackDelivery)
{
	public bool RequiresManualRearm =>
		Phase is BroadcastContinuityPhase.Failed or BroadcastContinuityPhase.RecoveredAwaitingRearm;

	public void Validate()
	{
		if (RoleId is not ("program" or "aux") || !Enum.IsDefined(ConfiguredAction) ||
			!Enum.IsDefined(Phase) || !Guid.TryParse(AuthorityStateId, out var id) || id == Guid.Empty ||
			AuthorityRevision == 0 || RuntimeExecutionRevision == 0)
			throw new InvalidDataException("Continuity observation has invalid role, authority identity or execution revision.");
		if (HasQualifiedFallbackDelivery)
			throw new InvalidDataException("No qualified hold/mute/slate fallback provider exists in this release.");
		if ((Phase is BroadcastContinuityPhase.Failed or BroadcastContinuityPhase.RecoveredAwaitingRearm) !=
			(IncidentGeneration != 0 && !string.IsNullOrWhiteSpace(IncidentCode) && IncidentStartedAtUtc.HasValue))
			throw new InvalidDataException("Latched incidents must carry generation, failure code and UTC origin.");
		if (IncidentCode is { Length: > 96 } || IncidentDetail is { Length: > 256 } ||
			IncidentStartedAtUtc is { Offset: not TimeSpan.Zero })
			throw new InvalidDataException("Continuity incident provenance exceeds its bounded record.");
	}
}
