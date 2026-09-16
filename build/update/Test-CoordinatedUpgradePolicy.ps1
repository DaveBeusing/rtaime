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
$bundlePolicyPath = Join-Path $repositoryRoot 'build/release/offline-bundle-policy.json'
$coordinatorPath = Join-Path $PSScriptRoot 'Invoke-CoordinatedUpgrade.ps1'
$verifiedUpdatePath = Join-Path $PSScriptRoot 'Invoke-VerifiedUpdate.ps1'
$rollbackPath = Join-Path $PSScriptRoot 'Invoke-SoftwareRollback.ps1'
$programPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/Program.cs'
$cliPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/StateMaintenanceCli.cs'
$workflowPath = Join-Path $repositoryRoot '.github/workflows/required-gates.yml'

foreach ($path in @($policyPath, $catalogPath, $bundlePolicyPath, $coordinatorPath, $verifiedUpdatePath, $rollbackPath, $programPath, $cliPath, $workflowPath)) {
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
Assert-Condition ($journal.Count -eq 1 -and [int]$journal[0].targetSchemaVersion -eq 1 -and @($journal[0].migrations).Count -eq 0) "Production-journal production schema must remain v1 with no migration in AP-25."

$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
foreach ($requiredTool in @(
	'build/update/coordinated-upgrade-policy.json',
	'build/state/state-upgrade-catalog.json',
	'build/update/Invoke-CoordinatedUpgrade.ps1',
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
	'runtimeReadiness = ''UNVERIFIED'''
)) {
	Assert-Condition ($coordinator -match [Regex]::Escape($marker)) "Coordinator is missing required behavior marker '$marker'."
}
Assert-Condition ($coordinator -match 'External state catalog override is allowed only in QualificationMode') "External migration catalogs must remain qualification-only."

$verifiedUpdate = Get-Content -LiteralPath $verifiedUpdatePath -Raw
Assert-Condition ($verifiedUpdate -match 'Invoke-CoordinatedUpgrade\.ps1') "Legacy verified-update entrypoint must delegate to the coordinator."
Assert-Condition ($verifiedUpdate -match '\$StateRoot') "Legacy verified-update entrypoint must require StateRoot."

$rollback = Get-Content -LiteralPath $rollbackPath -Raw
Assert-Condition ($rollback -match 'coordinatedRecoverySuffix') "Software rollback does not guard coordinated recovery evidence."
Assert-Condition ($rollback -match 'CoordinatedStateHandled') "Coordinator rollback bypass seam is missing."

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-Condition ($workflow -match 'Test-CoordinatedUpgradePolicy\.ps1') "Quality gate must validate coordinated-upgrade policy."

Write-Host 'Coordinated upgrade policy PASS'
Write-Host 'Signed state catalog: required'
Write-Host 'State backup before software activation: required'
Write-Host 'State migration after software activation: coordinated only'
Write-Host 'Failure recovery: software rollback then reverse-order state restore'
Write-Host 'Runtime readiness after maintenance commit: UNVERIFIED'
