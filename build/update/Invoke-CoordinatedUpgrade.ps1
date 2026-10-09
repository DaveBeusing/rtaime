# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[Parameter(Mandatory)]
	[string]$StateRoot,
	[ValidateSet('PREVIEW', 'STABLE')]
	[string]$Channel = 'PREVIEW',
	[string]$Repository = 'DaveBeusing/rtaime',
	[string]$PinnedVersion = '',
	[string]$WorkPath = '',
	[switch]$AcknowledgeProcessesStopped,
	[switch]$QualificationMode,
	[string]$QualificationBundlePath = '',
	[string]$QualificationStateCatalogPath = '',
	[ValidateSet('NONE', 'AFTER_SOFTWARE_ACTIVATION', 'AFTER_STATE_MIGRATION')]
	[string]$QualificationFailurePoint = 'NONE'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not [string]::IsNullOrWhiteSpace($directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	[System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 64) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Write-RecoveryLifecycle {
	param(
		[Parameter(Mandatory)][string]$Root,
		[Parameter(Mandatory)][string]$Status,
		[Parameter(Mandatory)][string]$RuntimeReadiness,
		[Parameter(Mandatory)][bool]$CleanupEligible,
		[Parameter(Mandatory)][string]$Detail
	)
	New-Item -ItemType Directory -Path $Root -Force | Out-Null
	$document = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		status = $Status
		runtimeReadiness = $RuntimeReadiness
		cleanupEligible = $CleanupEligible
		detail = $Detail
		updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	Write-JsonFile -Value $document -Path (Join-Path $Root 'recovery-lifecycle.json')
}

function Resolve-ControlHostDll {
	param([Parameter(Mandatory)][string]$Root)
	$matches = @(Get-ChildItem -LiteralPath $Root -Filter 'rtaime.ControlHost.dll' -File -Recurse -ErrorAction Stop)
	if ($matches.Count -ne 1) {
		throw "Expected exactly one rtaime.ControlHost.dll below '$Root', found $($matches.Count)."
	}
	return $matches[0].FullName
}

function Invoke-StateMaintenance {
	param(
		[Parameter(Mandatory)][string]$ControlHostDll,
		[Parameter(Mandatory)][string[]]$Arguments
	)
	$previousNativePreference = $null
	$hasNativePreference = Test-Path variable:PSNativeCommandUseErrorActionPreference
	if ($hasNativePreference) {
		$previousNativePreference = $PSNativeCommandUseErrorActionPreference
		$PSNativeCommandUseErrorActionPreference = $false
	}
	try {
		& dotnet $ControlHostDll 'state-maintenance' @Arguments
		$exitCode = $LASTEXITCODE
	} finally {
		if ($hasNativePreference) { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
	}
	if ($exitCode -ne 0) {
		throw "ControlHost state-maintenance command failed with exit code $exitCode: $($Arguments -join ' ')"
	}
}

function Verify-Bundle {
	param(
		[Parameter(Mandatory)][string]$Verifier,
		[Parameter(Mandatory)][string]$BundlePath,
		[Parameter(Mandatory)][bool]$Qualification
	)
	if ($Qualification) {
		& $Verifier -BundlePath $BundlePath
	} else {
		& $Verifier -BundlePath $BundlePath -RequireTrustedProductionKey
	}
}

function Get-ComponentVersion {
	param(
		[Parameter(Mandatory)]$Inspection,
		[Parameter(Mandatory)][string]$Component
	)
	$matches = @($Inspection.schema | Where-Object { [string]$_.component -eq $Component })
	if ($matches.Count -ne 1) {
		throw "Expected exactly one schema component '$Component' in '$($Inspection.databasePath)', found $($matches.Count)."
	}
	return [int]$matches[0].version
}

function Get-MigrationChain {
	param(
		[Parameter(Mandatory)]$DatabaseKind,
		[Parameter(Mandatory)][int]$CurrentVersion
	)
	$targetVersion = [int]$DatabaseKind.targetSchemaVersion
	if ($CurrentVersion -gt $targetVersion) {
		throw "Database '$($DatabaseKind.id)' schema $CurrentVersion is newer than target schema $targetVersion. Downgrade is not supported."
	}
	$chain = @()
	$version = $CurrentVersion
	while ($version -lt $targetVersion) {
		$matches = @($DatabaseKind.migrations | Where-Object { [int]$_.fromVersion -eq $version })
		if ($matches.Count -ne 1) {
			throw "Exactly one signed migration is required for '$($DatabaseKind.id)' from schema $version; found $($matches.Count)."
		}
		$step = $matches[0]
		if ([int]$step.toVersion -ne ($version + 1)) {
			throw "Migration '$($DatabaseKind.id)' $version->$($step.toVersion) is not a single forward schema step."
		}
		if ([string]::IsNullOrWhiteSpace([string]$step.sql)) {
			throw "Migration '$($DatabaseKind.id)' $version->$($step.toVersion) has an empty SQL payload."
		}
		$chain += $step
		$version = [int]$step.toVersion
	}
	return @($chain)
}

Assert-Condition $AcknowledgeProcessesStopped "Coordinated upgrade requires explicit acknowledgement that all rtaime processes/services using software or persistent state are stopped."
$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
$stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Existing installation was not found at '$installRoot'."
Assert-Condition (Test-Path -LiteralPath $stateRootFull -PathType Container) "Persistent-state root was not found at '$stateRootFull'."

$coordinatorPolicyPathCandidates = @((Join-Path $PSScriptRoot 'coordinated-upgrade-policy.json'), (Join-Path $PSScriptRoot '../update/coordinated-upgrade-policy.json'))
$coordinatorPolicyPath = $coordinatorPolicyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($coordinatorPolicyPath)) "Coordinated-upgrade policy is unavailable."
$coordinatorPolicy = Get-Content -LiteralPath $coordinatorPolicyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$coordinatorPolicy.schemaVersion -eq '1.0') "Unsupported coordinated-upgrade policy schema version."

$updatePolicyPathCandidates = @((Join-Path $PSScriptRoot 'update-policy.json'), (Join-Path $PSScriptRoot '../update/update-policy.json'))
$updatePolicyPath = $updatePolicyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($updatePolicyPath)) "Update policy is unavailable."
$updatePolicy = Get-Content -LiteralPath $updatePolicyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$updatePolicy.replacement.persistentStateMigration -eq 'COORDINATED_ONLY') "Update policy does not permit coordinated persistent-state migration."
Assert-Condition ($QualificationMode -or $QualificationFailurePoint -eq 'NONE') "Qualification failure injection is permitted only in QualificationMode."

if ([string]::IsNullOrWhiteSpace($WorkPath)) { $WorkPath = "$installRoot$([string]$coordinatorPolicy.workStateSuffix)" }
$workRoot = [System.IO.Path]::GetFullPath($WorkPath)
$recoveryRoot = "$installRoot$([string]$coordinatorPolicy.recoveryStateSuffix)"
Assert-Condition (-not (Test-Path -LiteralPath $recoveryRoot)) "Coordinated recovery evidence already exists at '$recoveryRoot'. Resolve it before another coordinated upgrade."
if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

$currentControlHost = Resolve-ControlHostDll -Root $installRoot
$currentControlHostRelative = [System.IO.Path]::GetRelativePath($installRoot, $currentControlHost)
$currentVerifier = Join-Path $installRoot 'tools/Test-OfflineReleaseBundle.ps1'
Assert-Condition (Test-Path -LiteralPath $currentVerifier -PathType Leaf) "Current installation does not contain its offline verifier."
$currentManifestPath = Join-Path $installRoot 'bundle-manifest.json'
Assert-Condition (Test-Path -LiteralPath $currentManifestPath -PathType Leaf) "Current installation does not contain its bundle manifest."
$preUpgradeManifestSha256 = (Get-FileHash -LiteralPath $currentManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

$bundlePath = ''
$updatePlan = $null
if ($QualificationMode) {
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($QualificationBundlePath)) "QualificationMode requires QualificationBundlePath."
	$bundlePath = [System.IO.Path]::GetFullPath($QualificationBundlePath)
	Assert-Condition (Test-Path -LiteralPath $bundlePath -PathType Leaf) "Qualification bundle was not found at '$bundlePath'."
	Verify-Bundle -Verifier $currentVerifier -BundlePath $bundlePath -Qualification $true
} else {
	$currentStatePath = Join-Path $workRoot 'installed-release.json'
	$current = & (Join-Path $PSScriptRoot 'Get-InstalledReleaseState.ps1') -InstallPath $installRoot -OutputPath $currentStatePath
	$discoveryPath = Join-Path $workRoot 'discovery.json'
	& (Join-Path $PSScriptRoot 'Resolve-UpdateDiscovery.ps1') -Channel $Channel -Repository $Repository -CurrentVersion ([string]$current.productVersion) -PinnedVersion $PinnedVersion -OutputPath $discoveryPath | Out-Null
	$downloadRoot = Join-Path $workRoot 'downloaded-release'
	$download = & (Join-Path $PSScriptRoot 'Get-VerifiedUpdateCandidate.ps1') -DiscoveryPath $discoveryPath -OutputPath $downloadRoot
	$bundlePath = [string]$download.bundlePath
	$planPath = Join-Path $workRoot 'update-plan.json'
	$updatePlan = & (Join-Path $PSScriptRoot 'New-UpdatePlan.ps1') -CurrentStatePath $currentStatePath -CandidatePath ([string]$download.candidatePath) -OutputPath $planPath -PinnedVersion $PinnedVersion
	Verify-Bundle -Verifier $currentVerifier -BundlePath $bundlePath -Qualification $false
}

$catalogExtract = Join-Path $workRoot 'catalog-extract'
New-Item -ItemType Directory -Path $catalogExtract -Force | Out-Null
Expand-Archive -LiteralPath $bundlePath -DestinationPath $catalogExtract -Force
if (-not [string]::IsNullOrWhiteSpace($QualificationStateCatalogPath)) {
	Assert-Condition $QualificationMode "External state catalog override is allowed only in QualificationMode."
	$catalogPath = [System.IO.Path]::GetFullPath($QualificationStateCatalogPath)
} else {
	$catalogPath = Join-Path $catalogExtract ([string]$coordinatorPolicy.productionStateCatalog)
}
Assert-Condition (Test-Path -LiteralPath $catalogPath -PathType Leaf) "State-upgrade catalog was not found at '$catalogPath'."
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$catalog.schemaVersion -eq '1.0') "Unsupported state-upgrade catalog schema version."
$databaseKinds = @($catalog.databaseKinds)
Assert-Condition ($databaseKinds.Count -gt 0) "State-upgrade catalog contains no database kinds."

$stateChanges = @()
$index = 0
foreach ($kind in $databaseKinds) {
	$fileName = [string]$kind.fileName
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($fileName)) "State-upgrade database filename is empty."
	Assert-Condition ([System.IO.Path]::GetFileName($fileName) -eq $fileName) "State-upgrade database filename '$fileName' must be a basename."
	$databases = @(Get-ChildItem -LiteralPath $stateRootFull -Filter $fileName -File -Recurse)
	foreach ($database in $databases) {
		$inspectionPath = Join-Path $workRoot ("state-inspection-{0:D4}.json" -f $index)
		Invoke-StateMaintenance -ControlHostDll $currentControlHost -Arguments @('inspect', "--database=$($database.FullName)", "--output=$inspectionPath")
		$inspection = Get-Content -LiteralPath $inspectionPath -Raw | ConvertFrom-Json
		$currentVersion = Get-ComponentVersion -Inspection $inspection -Component ([string]$kind.component)
		$chain = @(Get-MigrationChain -DatabaseKind $kind -CurrentVersion $currentVersion)
		if ($chain.Count -gt 0) {
			if (-not (Test-Path -LiteralPath $recoveryRoot)) { New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null }
			$preRoot = Join-Path $recoveryRoot 'pre-state'
			New-Item -ItemType Directory -Path $preRoot -Force | Out-Null
			$backupPath = Join-Path $preRoot ("state-{0:D4}.db" -f $index)
			$snapshotPath = Join-Path $preRoot ("state-{0:D4}.snapshot.json" -f $index)
			Invoke-StateMaintenance -ControlHostDll $currentControlHost -Arguments @('backup', "--database=$($database.FullName)", "--backup=$backupPath", "--output=$snapshotPath")
			$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
			$stateChanges += [pscustomobject]@{
				index = $index
				databasePath = $database.FullName
				databaseKind = [string]$kind.id
				component = [string]$kind.component
				fromVersion = $currentVersion
				targetVersion = [int]$kind.targetSchemaVersion
				migrations = $chain
				preSnapshotPath = $snapshotPath
				preBackupPath = [string]$snapshot.backupPath
				preBackupSha256 = ([string]$snapshot.backupSha256).ToLowerInvariant()
			}
		}
		$index++
	}
}

if ($stateChanges.Count -gt 0) {
	Write-RecoveryLifecycle -Root $recoveryRoot -Status 'PRE_UPGRADE_BACKUP_COMPLETE' -RuntimeReadiness 'NOT_APPLICABLE' -CleanupEligible $false -Detail 'Verified pre-upgrade state snapshots exist before software activation.'
}

$softwareActivated = $false
try {
	$replacementArguments = @{
		BundlePath = $bundlePath
		InstallPath = $installRoot
	}
	if ($QualificationMode) { $replacementArguments.QualificationMode = $true }
	& (Join-Path $PSScriptRoot 'Invoke-AtomicSoftwareReplacement.ps1') @replacementArguments
	$softwareActivated = $true
	if ($stateChanges.Count -gt 0) {
		Write-RecoveryLifecycle -Root $recoveryRoot -Status 'SOFTWARE_ACTIVATED' -RuntimeReadiness 'NOT_APPLICABLE' -CleanupEligible $false -Detail 'Target software is active; coordinated state migration remains in progress.'
	}
	if ($QualificationFailurePoint -eq 'AFTER_SOFTWARE_ACTIVATION') {
		throw 'Qualification failure injected after software activation.'
	}

	$newControlHost = Resolve-ControlHostDll -Root $installRoot
	foreach ($change in $stateChanges) {
		$migrationRoot = Join-Path $recoveryRoot 'migration-evidence'
		New-Item -ItemType Directory -Path $migrationRoot -Force | Out-Null
		$runtimePlanPath = Join-Path $migrationRoot ("state-{0:D4}.plan.json" -f $change.index)
		$migrationReceiptPath = Join-Path $migrationRoot ("state-{0:D4}.receipt.json" -f $change.index)
		$migrationBackupPath = Join-Path $migrationRoot ("state-{0:D4}.migration-backup.db" -f $change.index)
		$runtimePlan = [ordered]@{
			schemaVersion = '1.0'
			databasePath = [string]$change.databasePath
			backupPath = $migrationBackupPath
			component = [string]$change.component
			targetVersion = [int]$change.targetVersion
			migrations = @($change.migrations | ForEach-Object {
				[ordered]@{
					component = [string]$change.component
					fromVersion = [int]$_.fromVersion
					toVersion = [int]$_.toVersion
					sql = [string]$_.sql
				}
			})
		}
		[System.IO.File]::WriteAllText($runtimePlanPath, ($runtimePlan | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
		Invoke-StateMaintenance -ControlHostDll $newControlHost -Arguments @('migrate', "--plan=$runtimePlanPath", "--output=$migrationReceiptPath", '--acknowledge-exclusive-access')
		$postInspectionPath = Join-Path $migrationRoot ("state-{0:D4}.post.json" -f $change.index)
		Invoke-StateMaintenance -ControlHostDll $newControlHost -Arguments @('inspect', "--database=$($change.databasePath)", "--output=$postInspectionPath")
		$postInspection = Get-Content -LiteralPath $postInspectionPath -Raw | ConvertFrom-Json
		$postVersion = Get-ComponentVersion -Inspection $postInspection -Component ([string]$change.component)
		Assert-Condition ($postVersion -eq [int]$change.targetVersion) "Post-upgrade state schema for '$($change.databasePath)' is $postVersion, expected $($change.targetVersion)."
	}
	if ($stateChanges.Count -gt 0) {
		Write-RecoveryLifecycle -Root $recoveryRoot -Status 'STATE_MIGRATION_COMPLETE' -RuntimeReadiness 'UNVERIFIED' -CleanupEligible $false -Detail 'State migration and post-migration inspection passed; runtime readiness remains unverified.'
	}
	if ($QualificationFailurePoint -eq 'AFTER_STATE_MIGRATION') {
		throw 'Qualification failure injected after state migration.'
	}

	$receiptRoot = if ($stateChanges.Count -gt 0) { $recoveryRoot } else { $workRoot }
	$receipt = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		status = 'PASS'
		installPath = $installRoot
		stateRoot = $stateRootFull
		softwareRollbackPath = "$installRoot$([string]$updatePolicy.replacement.rollbackSlotSuffix)"
		persistentStateMigration = if ($stateChanges.Count -gt 0) { 'PASS' } else { 'NOT_APPLICABLE' }
		stateChanges = @($stateChanges | ForEach-Object {
			[ordered]@{
				databasePath = [string]$_.databasePath
				databaseKind = [string]$_.databaseKind
				component = [string]$_.component
				fromVersion = [int]$_.fromVersion
				targetVersion = [int]$_.targetVersion
				preSnapshotPath = [string]$_.preSnapshotPath
			}
		})
		softwarePlan = $updatePlan
		preUpgradeManifestSha256 = $preUpgradeManifestSha256
		runtimeReadiness = 'UNVERIFIED'
		processesRemainStopped = $true
		productionPackageActivation = 'NOT_PERFORMED'
		completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	$receiptPath = Join-Path $receiptRoot 'coordinated-upgrade-receipt.json'
	Write-JsonFile -Value $receipt -Path $receiptPath
	if ($stateChanges.Count -gt 0) {
		Write-RecoveryLifecycle -Root $recoveryRoot -Status 'MAINTENANCE_COMPLETE' -RuntimeReadiness 'UNVERIFIED' -CleanupEligible $false -Detail 'Coordinated maintenance passed. Retain recovery evidence until post-maintenance runtime readiness is qualified.'
	}

	Write-Host "Coordinated software/state upgrade PASS"
	Write-Host "State migrations: $($stateChanges.Count)"
	Write-Host "Runtime readiness: UNVERIFIED (processes intentionally remain stopped)"
	Write-Host "Recovery evidence: $receiptPath"
	return $receipt
} catch {
	$originalError = $_
	$recoveryErrors = [System.Collections.Generic.List[string]]::new()
	$softwareRecoveryStatus = if ($softwareActivated) { 'PENDING' } else { 'NOT_REQUIRED' }
	$stateRecoveryStatus = if ($softwareActivated -and $stateChanges.Count -gt 0) { 'PENDING' } else { 'NOT_REQUIRED' }
	$restoredManifestSha256 = $null

	if ($softwareActivated) {
		try {
			$rollbackArguments = @{
				InstallPath = $installRoot
				AcknowledgeProcessesStopped = $true
				CoordinatedStateHandled = $true
			}
			if ($QualificationMode) { $rollbackArguments.QualificationMode = $true }
			& (Join-Path $PSScriptRoot 'Invoke-SoftwareRollback.ps1') @rollbackArguments
			$restoredManifestPath = Join-Path $installRoot 'bundle-manifest.json'
			$restoredManifestSha256 = (Get-FileHash -LiteralPath $restoredManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
			Assert-Condition ($restoredManifestSha256 -eq $preUpgradeManifestSha256) "Software rollback restored a different installation manifest."
			$softwareRecoveryStatus = 'PASS'
		} catch {
			$softwareRecoveryStatus = 'FAIL'
			$recoveryErrors.Add("Software rollback failed: $($_.Exception.Message)")
		}

		if ($recoveryErrors.Count -eq 0 -and $stateChanges.Count -gt 0) {
			try {
				$restoredControlHost = Resolve-ControlHostDll -Root $installRoot
				foreach ($change in @($stateChanges | Sort-Object index -Descending)) {
					$currentBackupSha256 = (Get-FileHash -LiteralPath ([string]$change.preBackupPath) -Algorithm SHA256).Hash.ToLowerInvariant()
					Assert-Condition ($currentBackupSha256 -eq [string]$change.preBackupSha256) "Pre-upgrade backup hash changed before recovery for '$($change.databasePath)'."
					Invoke-StateMaintenance -ControlHostDll $restoredControlHost -Arguments @('restore', "--snapshot=$($change.preSnapshotPath)", "--database=$($change.databasePath)", '--acknowledge-exclusive-access')
					$verifyPath = Join-Path $workRoot ("recovery-state-{0:D4}.json" -f $change.index)
					Invoke-StateMaintenance -ControlHostDll $restoredControlHost -Arguments @('inspect', "--database=$($change.databasePath)", "--output=$verifyPath")
					$inspection = Get-Content -LiteralPath $verifyPath -Raw | ConvertFrom-Json
					$restoredVersion = Get-ComponentVersion -Inspection $inspection -Component ([string]$change.component)
					Assert-Condition ($restoredVersion -eq [int]$change.fromVersion) "Recovered state schema for '$($change.databasePath)' is $restoredVersion, expected $($change.fromVersion)."
				}
				$stateRecoveryStatus = 'PASS'
			} catch {
				$stateRecoveryStatus = 'FAIL'
				$recoveryErrors.Add("Persistent-state recovery failed: $($_.Exception.Message)")
			}
		} elseif ($stateRecoveryStatus -eq 'PENDING') {
			$stateRecoveryStatus = 'SKIPPED'
		}
	}

	$recoveryStatus = if ($recoveryErrors.Count -eq 0) { 'PASS' } else { 'FAIL' }
	if ($softwareActivated -or $stateChanges.Count -gt 0) {
		New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
		$failureEvidence = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			status = 'FAIL'
			originalFailure = $originalError.Exception.Message
			softwareActivated = $softwareActivated
			softwareRecovery = $softwareRecoveryStatus
			stateRecovery = $stateRecoveryStatus
			recoveryStatus = $recoveryStatus
			preUpgradeManifestSha256 = $preUpgradeManifestSha256
			restoredManifestSha256 = $restoredManifestSha256
			processesRemainStopped = $true
			completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		}
		Write-JsonFile -Value $failureEvidence -Path (Join-Path $recoveryRoot 'coordinated-upgrade-failure.json')
		Write-RecoveryLifecycle -Root $recoveryRoot -Status $(if ($recoveryStatus -eq 'PASS') { 'RECOVERY_COMPLETE' } else { 'RECOVERY_INCOMPLETE' }) -RuntimeReadiness 'NOT_APPLICABLE' -CleanupEligible ($recoveryStatus -eq 'PASS') -Detail $(if ($recoveryStatus -eq 'PASS') { 'Original upgrade failed and coordinated recovery completed. Processes remain stopped.' } else { 'Original upgrade failed and recovery is incomplete. Processes must remain stopped.' })
	}

	if ($recoveryErrors.Count -gt 0) {
		throw "Coordinated upgrade failed: $($originalError.Exception.Message) Recovery is incomplete and processes must remain stopped. $($recoveryErrors -join ' | ')"
	}
	throw $originalError
}
