# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath,
	[string]$SourceCommit = $env:RTAIME_SOURCE_COMMIT,
	[string]$EvidencePath = 'artifacts/release-evidence/coordinated-state-upgrade-qualification.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Write-JsonFile {
	param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
	$directory = Split-Path -Parent $Path
	if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
	[System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 64) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Resolve-Python {
	$command = Get-Command python -ErrorAction SilentlyContinue
	if ($null -ne $command) { return $command.Source }
	$command = Get-Command python3 -ErrorAction SilentlyContinue
	if ($null -ne $command) { return $command.Source }
	throw 'Python 3 is required only for disposable SQLite qualification-fixture creation and inspection.'
}

function Invoke-Python {
	param(
		[Parameter(Mandatory)][string]$Python,
		[Parameter(Mandatory)][string]$Code,
		[string[]]$Arguments = @()
	)
	$output = & $Python '-c' $Code @Arguments
	if ($LASTEXITCODE -ne 0) { throw "Python qualification helper failed with exit code $LASTEXITCODE." }
	return $output
}

function New-QualificationDatabase {
	param([Parameter(Mandatory)][string]$Python, [Parameter(Mandatory)][string]$Path)
	$directory = Split-Path -Parent $Path
	New-Item -ItemType Directory -Path $directory -Force | Out-Null
	$code = @'
import sqlite3, sys
path = sys.argv[1]
con = sqlite3.connect(path)
try:
    con.executescript("""
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        CREATE TABLE schema_metadata(component TEXT PRIMARY KEY, schema_version INTEGER NOT NULL);
        INSERT INTO schema_metadata(component, schema_version) VALUES('qualification', 1);
        CREATE TABLE durable_state(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
        INSERT INTO durable_state(id, payload) VALUES(1, 'before');
    """)
    con.commit()
finally:
    con.close()
'@
	Invoke-Python -Python $Python -Code $code -Arguments @($Path) | Out-Null
}

function Get-QualificationDatabaseState {
	param([Parameter(Mandatory)][string]$Python, [Parameter(Mandatory)][string]$Path)
	$code = @'
import json, sqlite3, sys
con = sqlite3.connect(sys.argv[1])
try:
    version = con.execute("SELECT schema_version FROM schema_metadata WHERE component='qualification'").fetchone()[0]
    payload = con.execute("SELECT payload FROM durable_state WHERE id=1").fetchone()[0]
    marker_table = con.execute("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='qualification_upgrade_marker'").fetchone()[0]
    marker = None
    if marker_table:
        marker = con.execute("SELECT value FROM qualification_upgrade_marker WHERE id=1").fetchone()[0]
    integrity = con.execute("PRAGMA integrity_check").fetchone()[0]
    print(json.dumps({"version": version, "payload": payload, "markerTable": bool(marker_table), "marker": marker, "integrity": integrity}))
finally:
    con.close()
'@
	$json = (Invoke-Python -Python $Python -Code $code -Arguments @($Path)) -join [Environment]::NewLine
	return $json | ConvertFrom-Json
}

function Assert-RollbackGuard {
	param([Parameter(Mandatory)][string]$InstallPath)
	$rollback = Join-Path $InstallPath 'tools/Invoke-SoftwareRollback.ps1'
	$blocked = $false
	try {
		& $rollback -InstallPath $InstallPath -AcknowledgeProcessesStopped -QualificationMode | Out-Null
	} catch {
		$blocked = $_.Exception.Message -match 'Software-only rollback is blocked'
	}
	Assert-Condition $blocked 'Direct software-only rollback was not blocked while coordinated recovery evidence was active.'
}

function Install-QualificationService {
	param(
		[Parameter(Mandatory)][string]$InstallPath,
		[Parameter(Mandatory)][string]$StateRoot,
		[Parameter(Mandatory)][string]$WorkRoot,
		[Parameter(Mandatory)][string]$ServiceName,
		[Parameter(Mandatory)][string]$InstanceId
	)
	$serviceTool = Join-Path $InstallPath 'tools/Invoke-WindowsServiceLifecycle.ps1'
	& $serviceTool -Action Install -InstallPath $InstallPath -StateRoot $StateRoot -WorkPath $WorkRoot -ServiceName $ServiceName -InstanceId $InstanceId -StartupType Manual | Out-Null
	$started = & $serviceTool -Action Start -InstallPath $InstallPath -StateRoot $StateRoot -WorkPath $WorkRoot -ServiceName $ServiceName -InstanceId $InstanceId
	Assert-Condition ([string]$started.runtimeReadiness -eq 'PASS') "Qualification service '$ServiceName' did not reach initial runtime readiness."
}

function Remove-QualificationService {
	param(
		[Parameter(Mandatory)][string]$InstallPath,
		[Parameter(Mandatory)][string]$StateRoot,
		[Parameter(Mandatory)][string]$WorkRoot,
		[Parameter(Mandatory)][string]$ServiceName,
		[Parameter(Mandatory)][string]$InstanceId
	)
	$serviceTool = Join-Path $InstallPath 'tools/Invoke-WindowsServiceLifecycle.ps1'
	if (Test-Path -LiteralPath $serviceTool -PathType Leaf) {
		try {
			& $serviceTool -Action Uninstall -InstallPath $InstallPath -StateRoot $StateRoot -WorkPath $WorkRoot -ServiceName $ServiceName -InstanceId $InstanceId | Out-Null
		} catch {
			Write-Warning "Qualification service cleanup failed for '$ServiceName': $($_.Exception.Message)"
		}
	}
}

$bundle = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition (Test-Path -LiteralPath $bundle -PathType Leaf) "Qualification bundle was not found at '$bundle'."
& (Join-Path $repositoryRoot 'build/release/Test-OfflineReleaseBundle.ps1') -BundlePath $bundle | Out-Null

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
	$SourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
}
$SourceCommit = $SourceCommit.Trim().ToLowerInvariant()
Assert-Condition ($SourceCommit -match '^[0-9a-f]{40,64}$') 'Qualification source commit must be a full hexadecimal commit id.'

$productionCatalog = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/state/state-upgrade-catalog.json') -Raw | ConvertFrom-Json
Assert-Condition (@($productionCatalog.databaseKinds | Where-Object { [int]$_.targetSchemaVersion -ne 1 -or @($_.migrations).Count -ne 0 }).Count -eq 0) 'Production state catalog must remain at schema v1 with no manufactured migration.'

$python = Resolve-Python
$baseRoot = if ([string]::IsNullOrWhiteSpace($env:ProgramData)) { [System.IO.Path]::GetTempPath() } else { $env:ProgramData }
$root = Join-Path $baseRoot ("rtaime-state-upgrade-qualification-{0}" -f [Guid]::NewGuid().ToString('N'))
$successRoot = Join-Path $root 'success'
$failureRoot = Join-Path $root 'failure'
$successServiceName = "rtaime-qual-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$failureServiceName = "rtaime-qual-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$successInstalled = $false
$failureInstalled = $false

try {
	New-Item -ItemType Directory -Path $root -Force | Out-Null

	# Success path: clean install -> v1 state -> service-managed coordinated update -> v2 -> runtime readiness.
	$successInstall = Join-Path $successRoot 'install'
	$successState = Join-Path $successRoot 'state'
	$successWork = Join-Path $successRoot 'service'
	$successInstance = "qualification-$([Guid]::NewGuid().ToString('N'))"
	& (Join-Path $repositoryRoot 'build/release/Install-OfflineRelease.ps1') -BundlePath $bundle -InstallPath $successInstall | Out-Null
	$successDatabase = Join-Path $successState 'qualification-state.db'
	New-QualificationDatabase -Python $python -Path $successDatabase
	$successBefore = Get-QualificationDatabaseState -Python $python -Path $successDatabase
	Assert-Condition ([int]$successBefore.version -eq 1 -and [string]$successBefore.integrity -eq 'ok') 'Disposable success database did not start as valid schema v1.'

	$qualificationCatalog = Join-Path $successInstall 'tools/state-upgrade-qualification-catalog.json'
	Assert-Condition (Test-Path -LiteralPath $qualificationCatalog -PathType Leaf) 'Signed qualification-only state catalog is missing from the installed target bundle.'
	$qualificationCatalogDocument = Get-Content -LiteralPath $qualificationCatalog -Raw | ConvertFrom-Json
	Assert-Condition ($qualificationCatalogDocument.qualificationOnly -eq $true) 'Qualification state catalog is not explicitly qualification-only.'
	Assert-Condition ([int](@($qualificationCatalogDocument.databaseKinds)[0].targetSchemaVersion) -eq 2) 'Qualification state catalog must exercise target schema v2.'

	Install-QualificationService -InstallPath $successInstall -StateRoot $successState -WorkRoot $successWork -ServiceName $successServiceName -InstanceId $successInstance
	$successInstalled = $true

	$serviceUpdate = Join-Path $successInstall 'tools/Invoke-ServiceManagedUpdate.ps1'
	$successReceipt = & $serviceUpdate -InstallPath $successInstall -StateRoot $successState -WorkPath $successWork -ServiceName $successServiceName -InstanceId $successInstance -AcknowledgeExternalProcessesStopped -QualificationMode -QualificationBundlePath $bundle -QualificationStateCatalogPath $qualificationCatalog
	Assert-Condition ([string]$successReceipt.status -eq 'PASS' -and [string]$successReceipt.runtimeReadiness -eq 'PASS') 'Service-managed coordinated success path did not end in runtime readiness PASS.'

	$successAfter = Get-QualificationDatabaseState -Python $python -Path $successDatabase
	Assert-Condition ([int]$successAfter.version -eq 2) 'Disposable success database did not migrate to schema v2.'
	Assert-Condition ([string]$successAfter.payload -eq 'before' -and $successAfter.markerTable -eq $true -and [string]$successAfter.marker -eq 'migrated' -and [string]$successAfter.integrity -eq 'ok') 'Disposable success database content or integrity did not match the qualified v2 state.'

	$successRecoveryRoot = "$successInstall.upgrade-recovery"
	$successLifecyclePath = Join-Path $successRecoveryRoot 'recovery-lifecycle.json'
	$successLifecycle = Get-Content -LiteralPath $successLifecyclePath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$successLifecycle.status -eq 'RUNTIME_QUALIFIED' -and [string]$successLifecycle.runtimeReadiness -eq 'PASS' -and $successLifecycle.cleanupEligible -eq $true) 'Successful service-managed update did not retain runtime-qualified recovery evidence.'
	Assert-RollbackGuard -InstallPath $successInstall

	$completionTool = Join-Path $successInstall 'tools/Complete-CoordinatedUpgradeRecovery.ps1'
	$closure = & $completionTool -InstallPath $successInstall -StateRoot $successState -AcknowledgeRollbackRetirement -QualificationMode
	Assert-Condition ([string]$closure.status -eq 'PASS' -and $closure.rollbackRetired -eq $true -and $closure.recoveryEvidenceRetired -eq $true) 'Successful coordinated recovery evidence retirement did not complete safely.'
	Assert-Condition (-not (Test-Path -LiteralPath "$successInstall.rollback") -and -not (Test-Path -LiteralPath $successRecoveryRoot)) 'Successful recovery retirement left rollback or active recovery state behind.'

	# Failure path: fail after real migration, then require exact software rollback and state restore while service stays stopped.
	$failureInstall = Join-Path $failureRoot 'install'
	$failureState = Join-Path $failureRoot 'state'
	$failureWork = Join-Path $failureRoot 'service'
	$failureInstance = "qualification-$([Guid]::NewGuid().ToString('N'))"
	& (Join-Path $repositoryRoot 'build/release/Install-OfflineRelease.ps1') -BundlePath $bundle -InstallPath $failureInstall | Out-Null
	$failureDatabase = Join-Path $failureState 'qualification-state.db'
	New-QualificationDatabase -Python $python -Path $failureDatabase
	$failureBefore = Get-QualificationDatabaseState -Python $python -Path $failureDatabase
	$failureManifestBefore = (Get-FileHash -LiteralPath (Join-Path $failureInstall 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
	$failureCatalog = Join-Path $failureInstall 'tools/state-upgrade-qualification-catalog.json'

	Install-QualificationService -InstallPath $failureInstall -StateRoot $failureState -WorkRoot $failureWork -ServiceName $failureServiceName -InstanceId $failureInstance
	$failureInstalled = $true

	$failureObserved = $false
	try {
		& (Join-Path $failureInstall 'tools/Invoke-ServiceManagedUpdate.ps1') -InstallPath $failureInstall -StateRoot $failureState -WorkPath $failureWork -ServiceName $failureServiceName -InstanceId $failureInstance -AcknowledgeExternalProcessesStopped -QualificationMode -QualificationBundlePath $bundle -QualificationStateCatalogPath $failureCatalog -QualificationFailurePoint AFTER_STATE_MIGRATION | Out-Null
	} catch {
		$failureObserved = $_.Exception.Message -match 'Qualification failure injected after state migration'
	}
	Assert-Condition $failureObserved 'Packaged failure injection did not preserve the original post-migration upgrade failure.'

	$failureAfter = Get-QualificationDatabaseState -Python $python -Path $failureDatabase
	Assert-Condition ([int]$failureAfter.version -eq 1 -and [string]$failureAfter.payload -eq [string]$failureBefore.payload -and $failureAfter.markerTable -eq $false -and [string]$failureAfter.integrity -eq 'ok') 'Failure recovery did not restore the disposable database to its prior v1 state.'
	$failureManifestAfter = (Get-FileHash -LiteralPath (Join-Path $failureInstall 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
	Assert-Condition ($failureManifestAfter -eq $failureManifestBefore) 'Failure recovery did not restore the exact previous software manifest.'

	$failureEvidencePath = Join-Path "$failureInstall.upgrade-recovery" 'coordinated-upgrade-failure.json'
	$failureEvidence = Get-Content -LiteralPath $failureEvidencePath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$failureEvidence.status -eq 'FAIL' -and [string]$failureEvidence.recoveryStatus -eq 'PASS') 'Failure evidence must retain original FAIL while reporting recovery PASS.'
	Assert-Condition ([string]$failureEvidence.softwareRecovery -eq 'PASS' -and [string]$failureEvidence.stateRecovery -eq 'PASS' -and $failureEvidence.processesRemainStopped -eq $true) 'Failure evidence does not prove complete software/state recovery with processes stopped.'

	$failureStatus = & (Join-Path $failureInstall 'tools/Invoke-WindowsServiceLifecycle.ps1') -Action Status -InstallPath $failureInstall -StateRoot $failureState -WorkPath $failureWork -ServiceName $failureServiceName -InstanceId $failureInstance
	Assert-Condition ([string]$failureStatus.serviceState -eq 'Stopped') 'Failed coordinated update restarted an uncertain service state.'
	Assert-Condition ([string]$failureStatus.runtimeReadiness -ne 'PASS') 'Failed coordinated update must not report runtime readiness PASS.'
	Assert-RollbackGuard -InstallPath $failureInstall

	$failureClosure = & (Join-Path $failureInstall 'tools/Complete-CoordinatedUpgradeRecovery.ps1') -InstallPath $failureInstall -StateRoot $failureState -AcknowledgeRollbackRetirement -AllowRecoveredFailureClosure -QualificationMode
	Assert-Condition ([string]$failureClosure.status -eq 'PASS' -and [string]$failureClosure.closureReason -eq 'RECOVERED_FAILED_UPGRADE') 'Recovered failed-upgrade evidence could not be explicitly and safely retired.'

	$evidence = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		sourceCommit = $SourceCommit
		overallStatus = 'PASS'
		productionSchemaCatalogUnchanged = $true
		successPath = [ordered]@{
			status = 'PASS'
			fromSchemaVersion = 1
			toSchemaVersion = 2
			runtimeReadiness = 'PASS'
			directSoftwareRollbackBlockedWhileRecoveryActive = $true
			recoveryEvidenceRetirement = 'PASS'
		}
		failureRecoveryPath = [ordered]@{
			status = 'PASS'
			originalUpgradeStatus = 'FAIL'
			failurePoint = 'AFTER_STATE_MIGRATION'
			softwareRecovery = 'PASS'
			stateRecovery = 'PASS'
			serviceRestarted = $false
			runtimeReadiness = 'NOT_PASS'
			directSoftwareRollbackBlockedWhileRecoveryActive = $true
			recoveryEvidenceRetirement = 'PASS'
		}
		completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	$evidenceFullPath = Resolve-RepositoryPath $EvidencePath
	Write-JsonFile -Value $evidence -Path $evidenceFullPath

	Write-Host 'Coordinated state upgrade release qualification PASS'
	Write-Host 'Disposable migration: v1 -> v2 PASS'
	Write-Host 'Service-managed post-maintenance runtime readiness: PASS'
	Write-Host 'Injected post-migration failure recovery: PASS'
	Write-Host "Evidence: $evidenceFullPath"
	return [pscustomobject]$evidence
} finally {
	if ($successInstalled) { Remove-QualificationService -InstallPath (Join-Path $successRoot 'install') -StateRoot (Join-Path $successRoot 'state') -WorkRoot (Join-Path $successRoot 'service') -ServiceName $successServiceName -InstanceId $successInstance }
	if ($failureInstalled) { Remove-QualificationService -InstallPath (Join-Path $failureRoot 'install') -StateRoot (Join-Path $failureRoot 'state') -WorkRoot (Join-Path $failureRoot 'service') -ServiceName $failureServiceName -InstanceId $failureInstance }
	if (Test-Path -LiteralPath $root) {
		for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $root); $attempt++) {
			try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 300 }
		}
	}
}
