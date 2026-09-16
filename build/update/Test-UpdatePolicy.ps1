# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$policyPath = Join-Path $PSScriptRoot 'update-policy.json'
$bundlePolicyPath = Join-Path $repositoryRoot 'build/release/offline-bundle-policy.json'
$requiredGatesPath = Join-Path $repositoryRoot '.github/workflows/required-gates.yml'
$releaseWorkflowPath = Join-Path $repositoryRoot '.github/workflows/release-pipeline.yml'

foreach ($path in @($policyPath, $bundlePolicyPath, $requiredGatesPath, $releaseWorkflowPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required update governance file is missing: '$path'."
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported update policy schema version."
Assert-Condition ([string]$policy.repository -eq 'DaveBeusing/rtaime') "Update repository policy drifted."
Assert-Condition (@($policy.supportedCurrentChannels).Count -eq 2) "Managed updates must support exactly PREVIEW and STABLE current channels."
Assert-Condition (@($policy.supportedCurrentChannels) -contains 'PREVIEW') "PREVIEW current channel is missing."
Assert-Condition (@($policy.supportedCurrentChannels) -contains 'STABLE') "STABLE current channel is missing."

$previewTargets = @($policy.targetChannelTransitions.PREVIEW)
$stableTargets = @($policy.targetChannelTransitions.STABLE)
Assert-Condition ($previewTargets.Count -eq 2 -and $previewTargets -contains 'PREVIEW' -and $previewTargets -contains 'STABLE') "PREVIEW transition policy must allow PREVIEW and promotion to STABLE only."
Assert-Condition ($stableTargets.Count -eq 1 -and $stableTargets[0] -eq 'STABLE') "STABLE transition policy must remain STABLE-only."

Assert-Condition ($policy.trust.requirePublishedRelease -eq $true) "Managed updates must require published releases."
Assert-Condition ([string]$policy.trust.requiredSignerClass -eq 'EXTERNAL_CONTROLLED') "Managed updates must require EXTERNAL_CONTROLLED signing."
Assert-Condition ($policy.trust.requireProductionTrust -eq $true) "Managed updates must require production trust."
Assert-Condition ($policy.trust.requireCandidatePublicationReadinessPass -eq $true) "Managed updates must require Candidate publication readiness PASS."
Assert-Condition ($policy.trust.requireTrustedProductionBundle -eq $true) "Managed updates must require trusted production bundle verification."
Assert-Condition ($policy.selection.allowDowngrade -eq $false) "Managed updates must reject downgrades."
Assert-Condition ($policy.selection.allowReinstallSameVersion -eq $false) "Managed updates must reject same-version reinstall."
Assert-Condition ($policy.selection.allowDraftRelease -eq $false) "Managed updates must reject draft releases."
Assert-Condition ($policy.replacement.requireExplicitQuiescenceAcknowledgement -eq $true) "Update activation must require explicit process quiescence acknowledgement."
Assert-Condition ([string]$policy.replacement.rollbackSlotSuffix -eq '.rollback') "Unexpected rollback slot suffix."
Assert-Condition ($policy.replacement.retainRollbackAfterSuccess -eq $true) "Successful updates must retain one rollback slot."
Assert-Condition ($policy.replacement.failIfRollbackSlotExists -eq $true) "Managed updates must fail closed when a rollback slot already exists."
Assert-Condition ([string]$policy.replacement.persistentStateMigration -eq 'COORDINATED_ONLY') "Persistent-state migration must be available only through coordinated upgrade orchestration."
Assert-Condition ([string]$policy.replacement.coordinatedRecoverySuffix -eq '.upgrade-recovery') "Unexpected coordinated recovery suffix."

$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
$requiredTools = @(
	'build/release/Test-OfflineReleaseBundle.ps1',
	'build/update/update-policy.json',
	'build/update/coordinated-upgrade-policy.json',
	'build/state/state-upgrade-catalog.json',
	'build/update/Get-InstalledReleaseState.ps1',
	'build/update/Resolve-UpdateDiscovery.ps1',
	'build/update/Get-VerifiedUpdateCandidate.ps1',
	'build/update/Test-DownloadedRelease.ps1',
	'build/update/New-UpdatePlan.ps1',
	'build/update/Invoke-AtomicSoftwareReplacement.ps1',
	'build/update/Invoke-SoftwareRollback.ps1',
	'build/update/Invoke-CoordinatedUpgrade.ps1',
	'build/update/Invoke-VerifiedUpdate.ps1'
)
foreach ($tool in $requiredTools) {
	Assert-Condition (@($bundlePolicy.offlineTools) -contains $tool) "Offline software bundle does not carry required update tool '$tool'."
}

$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
Assert-Condition ($requiredGates -match 'Test-UpdatePolicy\.ps1') "Quality gate must verify update policy."
Assert-Condition ($requiredGates -match 'Test-UpdateFoundation\.ps1') "Packaged E2E must qualify replacement/rollback mechanics."
Assert-Condition ($requiredGates -match 'Test-CoordinatedUpgradePolicy\.ps1') "Quality gate must verify coordinated upgrade policy."

$releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
foreach ($forbidden in @('Invoke-VerifiedUpdate\.ps1', 'Invoke-CoordinatedUpgrade\.ps1', 'Invoke-AtomicSoftwareReplacement\.ps1', 'Invoke-SoftwareRollback\.ps1')) {
	Assert-Condition ($releaseWorkflow -notmatch $forbidden) "Release publication workflow must not perform software updates, state migration or rollback."
}

$verifiedUpdatePath = Join-Path $PSScriptRoot 'Invoke-VerifiedUpdate.ps1'
$verifiedUpdate = Get-Content -LiteralPath $verifiedUpdatePath -Raw
Assert-Condition ($verifiedUpdate -match 'AcknowledgeProcessesStopped') "Managed update must require process-quiescence acknowledgement."
Assert-Condition ($verifiedUpdate -match 'Invoke-CoordinatedUpgrade\.ps1') "Managed production update must delegate to coordinated orchestration."
Assert-Condition ($verifiedUpdate -match '\$StateRoot') "Managed production update must require persistent-state root context."
Assert-Condition ($verifiedUpdate -notmatch '(?i)Start-ScheduledTask|Register-ScheduledTask|New-Service') "Managed update must not install automatic background update scheduling or services."

Write-Host "Update policy verification PASS"
Write-Host "Channels: PREVIEW → PREVIEW/STABLE; STABLE → STABLE"
Write-Host "Production trust: required"
Write-Host "Downgrade/reinstall: blocked"
Write-Host "Rollback slot: retained"
Write-Host "Persistent state migration: COORDINATED_ONLY"
Write-Host "Automatic update scheduling: disabled"
