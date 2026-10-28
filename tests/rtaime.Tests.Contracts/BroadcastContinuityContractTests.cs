// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class BroadcastContinuityContractTests
{
	[Theory]
	[InlineData("program")]
	[InlineData("aux")]
	public void Conservative_role_policy_is_admitted_without_fabricating_provider_fallback(string role)
	{
		var policy = BroadcastFailurePolicy.Conservative(role);
		policy.Validate();
		policy.EnsureRuntimeAdmitted();
		Assert.Equal(BroadcastFailureAction.ReportAndPreserveAuthority, policy.Action);
		Assert.True(policy.RequireManualRearm);
		Assert.Equal(0u, policy.MaximumHoldFrames);
	}

	[Theory]
	[InlineData(BroadcastFailureAction.HoldLastCompleteFrameAndAudio, 50u)]
	[InlineData(BroadcastFailureAction.MuteAudio, 0u)]
	[InlineData(BroadcastFailureAction.ApprovedSlate, 0u)]
	public void Unqualified_fallback_modes_cannot_be_admitted_without_a_control_authorized_provider(
		BroadcastFailureAction action, uint maximum)
	{
		var policy = new BroadcastFailurePolicy("program", action, true, maximum);
		policy.Validate();
		Assert.Throws<NotSupportedException>(policy.EnsureRuntimeAdmitted);
	}

	[Fact]
	public void Failure_policy_rejects_unknown_role_unattended_rearm_and_unbounded_hold()
	{
		Assert.Throws<ArgumentException>(() =>
			new BroadcastFailurePolicy("preview", BroadcastFailureAction.ReportAndPreserveAuthority, true).Validate());
		Assert.Throws<ArgumentException>(() =>
			new BroadcastFailurePolicy("program", BroadcastFailureAction.ReportAndPreserveAuthority, false).Validate());
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new BroadcastFailurePolicy("program", BroadcastFailureAction.HoldLastCompleteFrameAndAudio, true, 301).Validate());
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new BroadcastFailurePolicy("aux", BroadcastFailureAction.HoldLastCompleteFrameAndAudio, true, 0).Validate());
		Assert.Throws<ArgumentException>(() =>
			new BroadcastFailurePolicy("aux", BroadcastFailureAction.MuteAudio, true, 3).Validate());
		Assert.Throws<ArgumentException>(() =>
			new BroadcastFailurePolicy("program", (BroadcastFailureAction)99, true).Validate());
	}

	[Fact]
	public void Incident_snapshots_require_consistent_latched_identity_and_reject_physical_evidence_without_provider()
	{
		var source = "92000000-0000-0000-0000-00000000000a";
		var sink = "92000000-0000-0000-0000-00000000000b";
		var state = "92000000-0000-0000-0000-000000000001";
		var before = new BroadcastContinuitySnapshot(
			"program", source, sink, BroadcastFailureAction.ReportAndPreserveAuthority,
			BroadcastContinuityPhase.AwaitingFirstBoundary, state, 0, 1,
			null, null, 0, null, null, null, false);
		before.Validate();
		Assert.Throws<InvalidDataException>(() =>
			(before with { HasQualifiedFallbackDelivery = true }).Validate());
		Assert.Throws<InvalidDataException>(() =>
			(before with { Phase = BroadcastContinuityPhase.Failed }).Validate());
		Assert.Throws<InvalidDataException>(() =>
			(before with { SourceId = "invalid" }).Validate());
		var fault = before with
		{
			Phase = BroadcastContinuityPhase.Failed,
			IncidentGeneration = 1,
			IncidentCode = "runtime.continuity.input_lost",
			IncidentDetail = "Software output evidence is incomplete.",
			IncidentStartedAtUtc = DateTimeOffset.UtcNow
		};
		fault.Validate();
		Assert.True(fault.RequiresManualRearm);
		(fault with { Phase = BroadcastContinuityPhase.RecoveredAwaitingRearm }).Validate();
	}
}
