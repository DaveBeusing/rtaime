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

$policyPath = Join-Path $PSScriptRoot 'coordinated-upgrade-policy.json'
$catalogPath = Join-Path $repositoryRoot 'build/state/state-upgrade-catalog.json'
$qualificationCatalogPath = Join-Path $repositoryRoot 'build/state/state-upgrade-qualification-catalog.json'
$bundlePolicyPath = Join-Path $repositoryRoot 'build/release/offline-bundle-policy.json'
$coordinatorPath = Join-Path $PSScriptRoot 'Invoke-CoordinatedUpgrade.ps1'
$verifiedUpdatePath = Join-Path $PSScriptRoot 'Invoke-VerifiedUpdate.ps1'
$rollbackPath = Join-Path $PSScriptRoot 'Invoke-SoftwareRollback.ps1'
$recoveryCompletionPath = Join-Path $PSScriptRoot 'Complete-CoordinatedUpgradeRecovery.ps1'
$serviceUpdatePath = Join-Path $PSScriptRoot 'Invoke-ServiceManagedUpdate.ps1'
$qualificationPath = Join-Path $PSScriptRoot 'Test-CoordinatedStateUpgradeQualification.ps1'
$updatePlanPath = Join-Path $PSScriptRoot 'New-UpdatePlan.ps1'
$updatePlanSchemaPath = Join-Path $repositoryRoot 'schemas/update/v1/update-plan.schema.json'
$updateDocumentationPath = Join-Path $repositoryRoot 'docs/UpdateDiscoveryAndRollback.md'
$programPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/Program.cs'
$cliPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/StateMaintenanceCli.cs'
$workflowPath = Join-Path $repositoryRoot '.github/workflows/required-gates.yml'

foreach ($path in @($policyPath, $catalogPath, $qualificationCatalogPath, $bundlePolicyPath, $coordinatorPath, $verifiedUpdatePath, $rollbackPath, $recoveryCompletionPath, $serviceUpdatePath, $qualificationPath, $updatePlanPath, $updatePlanSchemaPath, $updateDocumentationPath, $programPath, $cliPath, $workflowPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required coordinated-upgrade file is missing: '$path'."
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported coordinated-upgrade policy schema version."
Assert-Condition ($policy.requireExplicitQuiescenceAcknowledgement -eq $true) "Coordinated upgrade must require explicit quiescence acknowledgement."
Assert-Condition ([string]$policy.productionStateCatalog -eq 'tools/state-upgrade-catalog.json') "Production state catalog must come from the signed offline bundle."
Assert-Condition ($policy.qualificationExternalCatalogAllowed -eq $true) "Qualification override seam is missing."
Assert-Condition ($policy.preBackupBeforeSoftwareActivation -eq $true) "State backup must occur before software activation."
Assert-Condition ($policy.migrationAfterSoftwareActivation -eq $true) "State migration must occur only after software activation."
Assert-Condition ($policy.retainPreUpgradeSnapshotsAfterSuccess -eq $true) "Successful state-changing upgrades must retain pre-upgrade snapshots."
Assert-Condition ([string]$policy.directSoftwareRollbackWhenStateChanged -eq 'BLOCKED') "Direct software-only rollback must be blocked after state migration."
Assert-Condition ($policy.recoveryEvidenceLifecycle.activeUntilRuntimeReadiness -eq $true) "Recovery evidence must remain active until runtime readiness is qualified."
Assert-Condition ([string]$policy.recoveryEvidenceLifecycle.runtimeQualifiedStatus -eq 'RUNTIME_QUALIFIED') "Unexpected runtime-qualified recovery status."
Assert-Condition ([string]$policy.recoveryEvidenceLifecycle.cleanupMode -eq 'EXPLICIT_ROLLBACK_RETIREMENT') "Recovery cleanup must require explicit rollback retirement."
Assert-Condition ($policy.recoveryEvidenceLifecycle.preserveClosureReceiptOutsideRecoveryRoot -eq $true) "Recovery cleanup must preserve closure evidence outside the retired recovery root."
Assert-Condition ([string]$policy.processLifecycle.runtimeReadiness -eq 'UNVERIFIED_AFTER_COORDINATED_COMMIT') "Runtime readiness must remain explicitly UNVERIFIED after the maintenance commit."
Assert-Condition ($policy.processLifecycle.automaticStop -eq $false -and $policy.processLifecycle.automaticStart -eq $false) "AP-25 must not silently add automatic process lifecycle control."
Assert-Condition ([string]$policy.productionPackageActivation -eq 'OUT_OF_SCOPE') "Production Package activation must remain out of AP-25 scope."

$recoveryOrder = @($policy.failureRecoveryOrder)
$expectedRecovery = @('SOFTWARE_ROLLBACK', 'VERIFY_RESTORED_SOFTWARE', 'RESTORE_STATE_REVERSE_ORDER', 'VERIFY_RESTORED_STATE')
Assert-Condition ($recoveryOrder.Count -eq $expectedRecovery.Count) "Unexpected coordinated recovery step count."
for ($i = 0; $i -lt $expectedRecovery.Count; $i++) {
	Assert-Condition ([string]$recoveryOrder[$i] -eq $expectedRecovery[$i]) "Coordinated recovery ordering drifted at position $i."
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$catalog.schemaVersion -eq '1.0') "Unsupported state-upgrade catalog schema version."
$kinds = @($catalog.databaseKinds)
Assert-Condition ($kinds.Count -eq 2) "Current V1 state-upgrade catalog must contain management and production-journal kinds."
$management = @($kinds | Where-Object { [string]$_.id -eq 'management' })
$journal = @($kinds | Where-Object { [string]$_.id -eq 'production-journal' })
Assert-Condition ($management.Count -eq 1 -and [int]$management[0].targetSchemaVersion -eq 1 -and @($management[0].migrations).Count -eq 0) "Management production schema must remain v1 with no migration in AP-25."
Assert-Condition ($journal.Count -eq 1 -and [int]$journal[0].targetSchemaVersion -eq 1 -and @($journal[0].migrations).Count -eq 0) "Production-journal production schema must remain v1 with no migration."
Assert-Condition (@($kinds | Where-Object { [int]$_.targetSchemaVersion -ne 1 -or @($_.migrations).Count -ne 0 }).Count -eq 0) "Qualification work must not manufacture a production schema migration."

$qualificationCatalog = Get-Content -LiteralPath $qualificationCatalogPath -Raw | ConvertFrom-Json
Assert-Condition ($qualificationCatalog.qualificationOnly -eq $true) "Qualification catalog must be explicitly qualification-only."
$qualificationKind = @($qualificationCatalog.databaseKinds | Where-Object { [string]$_.id -eq 'qualification-state' })
Assert-Condition ($qualificationKind.Count -eq 1 -and [int]$qualificationKind[0].targetSchemaVersion -eq 2) "Qualification catalog must target disposable schema v2."
$qualificationMigrations = @($qualificationKind[0].migrations)
Assert-Condition ($qualificationMigrations.Count -eq 1 -and [int]$qualificationMigrations[0].fromVersion -eq 1 -and [int]$qualificationMigrations[0].toVersion -eq 2) "Qualification catalog must contain one forward v1 -> v2 migration."

$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
foreach ($requiredTool in @(
	'build/update/coordinated-upgrade-policy.json',
	'build/state/state-upgrade-catalog.json',
	'build/state/state-upgrade-qualification-catalog.json',
	'build/update/Invoke-CoordinatedUpgrade.ps1',
	'build/update/Complete-CoordinatedUpgradeRecovery.ps1',
	'build/update/Invoke-VerifiedUpdate.ps1',
	'build/update/Invoke-SoftwareRollback.ps1'
)) {
	Assert-Condition (@($bundlePolicy.offlineTools) -contains $requiredTool) "Signed offline bundle is missing coordinated-upgrade tool '$requiredTool'."
}

$program = Get-Content -LiteralPath $programPath -Raw
Assert-Condition ($program -match 'StateMaintenanceCli\.IsRequested') "ControlHost does not route state-maintenance mode before normal host startup."
$cli = Get-Content -LiteralPath $cliPath -Raw
foreach ($command in @('inspect', 'backup', 'migrate', 'restore')) {
	Assert-Condition ($cli -match ('"' + [Regex]::Escape($command) + '"')) "ControlHost state-maintenance CLI is missing '$command'."
}
Assert-Condition ($cli -match 'acknowledge-exclusive-access') "ControlHost state-maintenance mutation acknowledgement is missing."

$coordinator = Get-Content -LiteralPath $coordinatorPath -Raw
foreach ($marker in @(
	'AcknowledgeProcessesStopped',
	'productionStateCatalog',
	'Invoke-AtomicSoftwareReplacement.ps1',
	'Invoke-SoftwareRollback.ps1',
	'CoordinatedStateHandled',
	'Sort-Object index -Descending',
	'runtimeReadiness = ''UNVERIFIED''',
	'QualificationFailurePoint',
	'coordinated-upgrade-failure.json',
	'preUpgradeManifestSha256',
	'preBackupSha256'
)) {
	Assert-Condition ($coordinator -match [Regex]::Escape($marker)) "Coordinator is missing required behavior marker '$marker'."
}
Assert-Condition ($coordinator -match 'External state catalog override is allowed only in QualificationMode') "External migration catalogs must remain qualification-only."
Assert-Condition ($coordinator -match 'Qualification failure injection is permitted only in QualificationMode') "Failure injection must remain qualification-only."

$verifiedUpdate = Get-Content -LiteralPath $verifiedUpdatePath -Raw
Assert-Condition ($verifiedUpdate -match 'Invoke-CoordinatedUpgrade\.ps1') "Legacy verified-update entrypoint must delegate to the coordinator."
Assert-Condition ($verifiedUpdate -match '\$StateRoot') "Legacy verified-update entrypoint must require StateRoot."
Assert-Condition ($verifiedUpdate -match 'QualificationMode' -and $verifiedUpdate -match 'QualificationFailurePoint') "Verified-update entrypoint must forward qualification-only packaged test seams."

$rollback = Get-Content -LiteralPath $rollbackPath -Raw
Assert-Condition ($rollback -match 'coordinatedRecoverySuffix') "Software rollback does not guard coordinated recovery evidence."
Assert-Condition ($rollback -match 'CoordinatedStateHandled') "Coordinator rollback bypass seam is missing."

$recoveryCompletion = Get-Content -LiteralPath $recoveryCompletionPath -Raw
Assert-Condition ($recoveryCompletion -match 'AcknowledgeRollbackRetirement') "Recovery retirement must require explicit rollback-retirement acknowledgement."
Assert-Condition ($recoveryCompletion -match 'Remove-Item -LiteralPath \$rollbackRoot' -and $recoveryCompletion -match 'Remove-Item -LiteralPath \$recoveryRoot') "Recovery retirement must close rollback before recovery evidence."
Assert-Condition ($recoveryCompletion -match 'coordinated-upgrade-recovery-closure.json') "Recovery retirement must preserve an external closure receipt."

$serviceUpdate = Get-Content -LiteralPath $serviceUpdatePath -Raw
Assert-Condition ($serviceUpdate -match 'RUNTIME_QUALIFIED' -or $serviceUpdate -match 'runtimeQualifiedStatus') "Service-managed update must bind post-maintenance runtime readiness to recovery evidence."
Assert-Condition ($serviceUpdate -match 'QualificationFailurePoint') "Service-managed update must expose packaged failure injection only through its qualification seam."

$qualification = Get-Content -LiteralPath $qualificationPath -Raw
foreach ($marker in @('qualification-state.db', 'AFTER_STATE_MIGRATION', 'runtimeReadiness', 'recoveryStatus', 'serviceState', 'Complete-CoordinatedUpgradeRecovery.ps1')) {
	Assert-Condition ($qualification -match [Regex]::Escape($marker)) "Packaged coordinated-state qualification is missing '$marker'."
}

foreach ($legacyPath in @($updatePlanPath, $updatePlanSchemaPath, $updateDocumentationPath)) {
	$legacyText = Get-Content -LiteralPath $legacyPath -Raw
	Assert-Condition ($legacyText -notmatch 'persistentStateMigration\s*[=:]\s*["'']?NOT_IMPLEMENTED' -and $legacyText -notmatch 'Persistent state migration:\s*NOT_IMPLEMENTED') "Obsolete persistent-state migration NOT_IMPLEMENTED text remains in '$legacyPath'."
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-Condition ($workflow -match 'Test-CoordinatedUpgradePolicy\.ps1') "Quality gate must validate coordinated-upgrade policy."
Assert-Condition ($workflow -match 'Test-CoordinatedStateUpgradeQualification\.ps1') "Packaged E2E must execute the real disposable state-upgrade and recovery qualification."

Write-Host 'Coordinated upgrade policy PASS'
Write-Host 'Signed state catalog: required'
Write-Host 'State backup before software activation: required'
Write-Host 'State migration after software activation: coordinated only'
Write-Host 'Failure recovery: software rollback then reverse-order state restore'
Write-Host 'Runtime readiness after maintenance commit: UNVERIFIED until service-managed qualification'
Write-Host 'Disposable packaged migration qualification: v1 -> v2 required'
