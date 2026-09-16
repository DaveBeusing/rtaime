# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$policyPath = Join-Path $PSScriptRoot "state-maintenance-policy.json"
$maintenancePath = Join-Path $repositoryRoot "src/Persistence/rtaime.Persistence/SqliteStateMaintenance.cs"
$integrationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/PersistentStateRecoveryIntegrationTests.cs"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$updatePolicyPath = Join-Path $repositoryRoot "build/update/update-policy.json"

foreach ($path in @($policyPath, $maintenancePath, $integrationTestsPath, $workflowPath, $updatePolicyPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required persistent-state maintenance file is missing: '$path'."
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Unsupported state-maintenance policy schema version."
Assert-Condition ([string]$policy.databaseFamily -eq "SQLITE") "AP-24 state-maintenance foundation must remain SQLite-scoped."
Assert-Condition ($policy.backup.requiredBeforeSchemaMutation -eq $true) "Schema mutation must require a prior backup."
Assert-Condition ($policy.backup.overwriteExistingBackup -eq $false) "Existing backup evidence must never be overwritten implicitly."
Assert-Condition ($policy.backup.verifyIntegrity -eq $true) "Backup integrity verification is mandatory."
Assert-Condition ($policy.backup.verifySchemaInventory -eq $true) "Backup schema inventory verification is mandatory."
Assert-Condition ([string]$policy.backup.hashAlgorithm -eq "SHA256") "Persistent-state backup evidence must use SHA256."
Assert-Condition ([string]$policy.migration.direction -eq "FORWARD_ONLY") "Migration policy must remain forward-only."
Assert-Condition ([string]$policy.migration.registration -eq "EXPLICIT") "Migration registration must remain explicit."
Assert-Condition ([string]$policy.migration.unknownMigrationAction -eq "FAIL_CLOSED") "Unknown migrations must fail closed."
Assert-Condition ($policy.migration.allowDowngrade -eq $false) "Schema downgrade must remain blocked."
Assert-Condition ($policy.migration.transactional -eq $true) "Schema migrations must remain transactional."
Assert-Condition ($policy.migration.singleVersionStepsOnly -eq $true) "Schema migration steps must remain single-version transitions."
Assert-Condition ($policy.migration.postMigrationVerificationRequired -eq $true) "Post-migration verification is mandatory."
Assert-Condition ($policy.recovery.requireExclusiveAccessAcknowledgement -eq $true) "Restore/migration must require exclusive-access acknowledgement."
Assert-Condition ($policy.recovery.restoreOnMigrationFailure -eq $true) "Migration failure must restore the verified snapshot."
Assert-Condition ($policy.recovery.restoreOnPostMigrationVerificationFailure -eq $true) "Post-migration verification failure must restore the verified snapshot."
Assert-Condition ($policy.recovery.verifyBackupBeforeRestore -eq $true) "Restore must verify backup evidence before replacement."
Assert-Condition ($policy.recovery.verifyRestoredState -eq $true) "Restore must verify the activated state."
Assert-Condition ([string]$policy.integration.softwareUpdateStateOrchestration -eq "EXPLICIT_ORCHESTRATION_REQUIRED") "Software update/state recovery must not be implicitly coupled."
Assert-Condition ($policy.integration.automaticBackgroundMigration -eq $false) "Automatic background migration must remain disabled."
Assert-Condition ([string]$policy.integration.productionPackageActivation -eq "OUT_OF_SCOPE") "Production Package activation must remain outside AP-24."

$maintenance = Get-Content -LiteralPath $maintenancePath -Raw
foreach ($requiredToken in @(
	"CreateBackupAsync",
	"RestoreBackupAsync",
	"MigrateAsync",
	"PRAGMA integrity_check",
	"BackupDatabase",
	"acknowledgeExclusiveAccess",
	"Exactly one registered migration",
	"SQLite schema downgrade"
)) {
	Assert-Condition ($maintenance -match [Regex]::Escape($requiredToken)) "SqliteStateMaintenance is missing required behavior marker '$requiredToken'."
}

$tests = Get-Content -LiteralPath $integrationTestsPath -Raw
foreach ($requiredCase in @(
	"Verified_backup_can_restore_previous_management_state",
	"Registered_forward_migration_requires_backup_and_commits_transactionally",
	"Post_migration_verification_failure_restores_snapshot",
	"Unknown_migration_chain_fails_before_backup_or_mutation",
	"Tampered_backup_is_rejected_without_modifying_current_state",
	"Restore_and_migration_require_explicit_exclusive_access_acknowledgement"
)) {
	Assert-Condition ($tests -match [Regex]::Escape($requiredCase)) "Persistent-state recovery qualification is missing '$requiredCase'."
}

$updatePolicy = Get-Content -LiteralPath $updatePolicyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$updatePolicy.replacement.persistentStateMigration -eq "NOT_IMPLEMENTED") "AP-24 foundation must not silently claim that AP-23 managed software updates already orchestrate persistent-state migration."

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-Condition ($workflow -match 'Test-StateMaintenancePolicy\.ps1') "Quality gate must validate persistent-state maintenance policy."

Write-Host "Persistent-state maintenance policy PASS"
Write-Host "Database family: SQLite"
Write-Host "Backup before schema mutation: required"
Write-Host "Migration: registered forward-only, transactional"
Write-Host "Failure recovery: verified snapshot restore"
Write-Host "Automatic software-update migration orchestration: not claimed"
