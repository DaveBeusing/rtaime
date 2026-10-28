# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$artifacts = @{
	contracts = "src/Contracts/rtaime.Runtime.Contracts/BroadcastContinuityContracts.cs"
	tracker = "src/Hosts/rtaime.RuntimeHost/RuntimeBroadcastContinuityTracker.cs"
	runtime = "src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.cs"
	projection = "src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.BroadcastContinuity.cs"
	health = "src/Hosts/rtaime.Operator/OutputRoutingHealthViewModel.cs"
	xaml = "src/Hosts/rtaime.Operator/OutputRoutingHealthControl.xaml"
	contractsTests = "tests/rtaime.Tests.Contracts/BroadcastContinuityContractTests.cs"
	integrationTests = "tests/rtaime.Tests.Integration/BroadcastContinuityIntegrationTests.cs"
	operatorTests = "tests/rtaime.Tests.Operator/BroadcastContinuityOperatorTests.cs"
	documentation = "docs/BroadcastFailSafePlayout.md"
	outputDocumentation = "docs/OutputRoutingHealth.md"
	commitDocumentation = "docs/TransactionalRuntimeCommit.md"
}
$source = @{}
foreach ($entry in $artifacts.GetEnumerator()) {
	$path = Join-Path $root $entry.Value
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Missing broadcast continuity artifact: $($entry.Value)"
	$source[$entry.Key] = Get-Content -LiteralPath $path -Raw
}
Assert-Condition ($source.contracts.Contains("ReportAndPreserveAuthority") -and
	$source.contracts.Contains("HoldLastCompleteFrameAndAudio") -and
	$source.contracts.Contains("MuteAudio") -and
	$source.contracts.Contains("ApprovedSlate") -and
	$source.contracts.Contains("RequireManualRearm")) "Failure policy must model explicit bounded modes and independent manual re-arm."
Assert-Condition ($source.contracts.Contains("EnsureRuntimeAdmitted") -and
	$source.contracts.Contains("No qualified hold/mute/slate fallback provider")) "Unadmitted physical fallbacks may never be treated as ready."
Assert-Condition ($source.tracker.Contains("ConfirmCommittedExecution") -and
	$source.tracker.Contains("ObserveComplete") -and
	$source.tracker.Contains("ObserveFault") -and
	$source.tracker.Contains("HistoryLimit = 32") -and
	$source.tracker.Contains("RecoveredAwaitingRearm")) "Runtime must retain bounded source/revision-fenced incident evidence with a re-arm barrier."
Assert-Condition ($source.tracker.Contains("old.Source == source && old.Sink == sink") -and
	$source.tracker.Contains("old.IncidentGeneration")) "Unrelated Control revisions may not clear a latched active incident."
Assert-Condition ($source.runtime.Contains("ConfirmContinuityAfterRuntimeCommitUnsafe(preparedExecution, commit.ExecutionRevision.Value, programSinkId)") -and
	$source.projection.Contains("_broadcastContinuity.ConfirmCommittedExecution(prepared, executionRevision, programSinkId)") -and
	$source.projection.Contains("_broadcastContinuity.InvalidateActiveRoles()") -and
	$source.projection.Contains("runtime.continuity.observer_unverified") -and
	$source.runtime.Contains("TryObserveContinuityAtCompletedBoundaryUnsafe") -and
	$source.runtime.Contains("TryObserveContinuityOnFailedBoundary") -and
	$source.runtime.Contains('ContinuityHealthyUnsafe("program")') -and
	$source.runtime.Contains('ContinuityHealthyUnsafe("aux")')) "Runtime must observe actual committed and completed output, never staged or in-flight-only plans."
Assert-Condition ($source.runtime.Contains("ResetInputPipelinesAfterFailedBoundary") -and
	$source.runtime.Contains("RuntimePrepareStatus.Rejected") -and
	$source.runtime.Contains("RuntimeCommitStatus.Committed")) "Existing transactional rollback, input recovery and commit gating must remain intact."
Assert-Condition ($source.projection.Contains("runtime.continuity.input_lost") -and
	$source.projection.Contains("CONTINUITY FAILED") -and
	$source.projection.Contains("CONTINUITY RECOVERED AWAITING RE-ARM") -and
	$source.projection.Contains("HasQualifiedFallbackDelivery") -eq $false) "Source loss must invalidate false all-clear and cannot invent physical provider delivery."
Assert-Condition ($source.projection -notmatch "ApplyExecution\s*\(|CutProgramAsync\s*\(|RouteOutputRoleAsync\s*\(|SetVisualLayerMode\s*\(|WriteFrame\s*\(") "Continuity projection must never issue Program mutations or emit pixels."
Assert-Condition ($source.health.Contains("ContinuityStatus") -and
	$source.xaml.Contains("Binding SelectedOutput.ContinuityStatus") -and
	$source.xaml.Contains("A local notification acknowledgement does not authorize Program re-arm.")) "Operator must show Runtime-backed incident/re-arm status without independent authority."
foreach ($test in @(
	"Committed_program_tracks_only_complete_frame_and_audio_and_never_claims_hardware_fallback",
	"Program_input_loss_latches_an_incident_and_recovery_does_not_silently_rearm",
	"Lost_preview_or_aux_input_is_isolated_from_committed_program_authority",
	"Failed_boundaries_are_idempotent_and_incident_history_is_bounded_by_role_and_revision",
	"New_control_committed_source_or_sink_revokes_old_last_complete_evidence",
	"Rejected_software_prepare_cannot_rewrite_last_confirmed_program_or_incident",
	"Video_or_pcm_position_regression_is_an_incident_not_a_runtime_observer_exception",
	"Bounded_software_fault_injection_soak_preserves_committed_program_and_monotonic_AV_evidence"
)) {
	Assert-Condition ($source.integrationTests.Contains($test)) "Broadcast continuity negative regression is missing: $test"
}
Assert-Condition ($source.operatorTests.Contains("Output_ui_exposes_recovery_without_a_fake_operator_rearm") -and
	$source.contractsTests.Contains("Unqualified_fallback_modes_cannot_be_admitted")) "Program/Operator and fallback admission negative coverage is required."
Assert-Condition ($source.documentation.Contains("UNVERIFIED") -and
	$source.documentation.Contains("HoldLast") -and
	$source.documentation.Contains("ControlHost") -and
	$source.outputDocumentation.Contains("Broadcast continuity") -and
	$source.commitDocumentation.Contains("Broadcast continuity")) "Physical, authority and state migration limits must be documented."

Write-Host "Broadcast continuity source-bound policy, Runtime and Operator structural qualification PASS"
Write-Host "No automatic hold-last, mute, slate, device failover or physical NO-BLACK claim has been admitted"
