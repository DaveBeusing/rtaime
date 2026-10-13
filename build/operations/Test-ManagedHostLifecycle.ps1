# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath,
	[string]$LegacyAuthorityCheckpointPath = '',
	[string]$LegacyShowProjectPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Wait-ForStableManagedReadiness {
	param(
		[Parameter(Mandatory)][string]$LifecycleWorkRoot,
		[Parameter(Mandatory)][int]$ExpectedControlProcessId,
		[int]$TimeoutMs = 5000,
		[int]$StableDurationMs = 1000,
		[int]$PollIntervalMs = 100
	)

	$statePath = Join-Path $LifecycleWorkRoot 'lifecycle-state.json'
	$deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMs)
	$stableSince = $null
	$lastEvidence = 'unavailable'

	do {
		$valid = $false
		if (Test-Path -LiteralPath $statePath -PathType Leaf) {
			try {
				$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
				$readinessPath = [string]$state.readinessPath
				if (Test-Path -LiteralPath $readinessPath -PathType Leaf) {
					$readiness = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json
					$lastEvidence = $readiness | ConvertTo-Json -Compress -Depth 8
					$valid =
						[int]$state.controlProcessId -eq $ExpectedControlProcessId -and
						[int]$readiness.processId -eq $ExpectedControlProcessId -and
						[string]$readiness.state -eq 'READY' -and
						[string]$readiness.health -eq 'HEALTHY' -and
						[string]$readiness.runtimeSupervision.state -eq 'HEALTHY' -and
						[string]$readiness.aiSupervision.state -eq 'HEALTHY'
				}
			} catch {
				$lastEvidence = "invalid readiness evidence: $($_.Exception.Message)"
			}
		}

		if ($valid) {
			$stableSince ??= [DateTimeOffset]::UtcNow
			if (([DateTimeOffset]::UtcNow - $stableSince).TotalMilliseconds -ge $StableDurationMs) {
				return
			}
		} else {
			$stableSince = $null
		}

		Start-Sleep -Milliseconds $PollIntervalMs
	} while ([DateTimeOffset]::UtcNow -lt $deadline)

	throw "Managed lifecycle readiness evidence did not remain stable for $StableDurationMs ms within the bounded settle window. Last evidence: $lastEvidence"
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$bundle = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition ((Test-Path -LiteralPath $bundle -PathType Leaf) -or (Test-Path -LiteralPath $bundle -PathType Container)) "Qualification bundle was not found at '$bundle'."

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-host-lifecycle-{0}" -f [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
$stateRoot = Join-Path $root 'state'
$workRoot = Join-Path $root 'lifecycle'
$instanceId = "qualification-$([Guid]::NewGuid().ToString('N'))"
$lifecycle = $null

$legacyQualification = -not [string]::IsNullOrWhiteSpace($LegacyAuthorityCheckpointPath)
Assert-Condition ($legacyQualification -eq (-not [string]::IsNullOrWhiteSpace($LegacyShowProjectPath))) 'Legacy authority and show-project fixtures must be supplied together.'
$legacyDatabase = Join-Path $stateRoot ("70000000-0000-0000-0000-000000000001-rtaime.v1.control.{0}/management.db" -f $instanceId)
$python = $null

function Invoke-LegacyCheckpointFixture {
	param([Parameter(Mandatory)][ValidateSet('seed', 'verify')][string]$Mode)
	if ($null -eq $script:python) {
		$script:python = Get-Command python3, python -ErrorAction SilentlyContinue |
			Where-Object { $_.CommandType -eq 'Application' } | Select-Object -First 1
		Assert-Condition ($null -ne $script:python) 'Python 3 is required for disposable legacy-checkpoint qualification.'
	}
	$fixtureCode = @'
import hashlib, json, pathlib, sqlite3, sys
mode, database_path, authority_path, project_path = sys.argv[1:]
database = pathlib.Path(database_path)
payload = pathlib.Path(authority_path).read_bytes()
project_json = pathlib.Path(project_path).read_text(encoding='utf-8')
authority = json.loads(payload)
project = json.loads(project_json)
assert authority['Version'] == '1.0' and authority['Revision'] == 8
assert authority['ProductionId'] == '70000000-0000-0000-0000-000000000001'
assert project['productionId'] == authority['ProductionId']
checksum = hashlib.sha256(payload).hexdigest().upper()
if mode == 'seed':
    assert not database.exists(), 'Refusing to replace an existing qualification database'
    database.parent.mkdir(parents=True, exist_ok=True)
    c = sqlite3.connect(database)
    c.executescript("""
        CREATE TABLE schema_metadata(component TEXT PRIMARY KEY, schema_version INTEGER NOT NULL);
        INSERT INTO schema_metadata VALUES('management', 1);
        CREATE TABLE management_documents(
            area TEXT NOT NULL, document_key TEXT NOT NULL, version TEXT NOT NULL,
            updated_utc TEXT NOT NULL, payload_json TEXT NOT NULL, checksum_sha256 TEXT NOT NULL,
            PRIMARY KEY(area, document_key));
        CREATE TABLE production_checkpoints(
            checkpoint_id TEXT PRIMARY KEY, production_id TEXT NOT NULL, authoritative_revision TEXT NOT NULL,
            created_utc TEXT NOT NULL, format TEXT NOT NULL, payload BLOB NOT NULL, checksum_sha256 TEXT NOT NULL,
            UNIQUE(production_id, authoritative_revision));
    """)
    c.execute("INSERT INTO management_documents VALUES(?,?,?,?,?,?)",
        ('show.project', authority['ProductionId'], '00000000000000000001',
         '2026-09-26T19:51:13.1453784+00:00', project_json,
         hashlib.sha256(project_json.encode('utf-8')).hexdigest().upper()))
    c.execute("INSERT INTO production_checkpoints VALUES(?,?,?,?,?,?,?)",
        ('73000000-0000-0000-0000-000000000001', authority['ProductionId'], '00000000000000000008',
         '2026-09-26T19:51:13.1453784+00:00', 'rtaime.control.authority.v1', payload, checksum))
    c.commit()
else:
    c = sqlite3.connect(database.as_uri() + '?mode=ro', uri=True)
    row = c.execute("SELECT authoritative_revision,payload,checksum_sha256 FROM production_checkpoints WHERE checkpoint_id=?",
        ('73000000-0000-0000-0000-000000000001',)).fetchone()
    assert row == ('00000000000000000008', payload, checksum), 'Original legacy authority was changed'
    assert c.execute("SELECT COUNT(*) FROM production_checkpoints").fetchone()[0] == 1, 'Lifecycle invented a new authority revision'
    assert c.execute("PRAGMA integrity_check").fetchone()[0] == 'ok'
    print('Legacy checkpoint preservation PASS: contract 1.0, revision 8, unchanged payload/checksum')
c.close()
'@
	& $script:python.Source -c $fixtureCode $Mode $legacyDatabase ([System.IO.Path]::GetFullPath($LegacyAuthorityCheckpointPath)) ([System.IO.Path]::GetFullPath($LegacyShowProjectPath))
	if ($LASTEXITCODE -ne 0) { throw "Legacy checkpoint fixture $Mode failed." }
}

try {
	New-Item -ItemType Directory -Path $root -Force | Out-Null
	& (Join-Path $repositoryRoot 'build/release/Install-OfflineRelease.ps1') -BundlePath $bundle -InstallPath $install
	$lifecycle = Join-Path $install 'tools/Invoke-ManagedHostLifecycle.ps1'
	Assert-Condition (Test-Path -LiteralPath $lifecycle -PathType Leaf) "Installed qualification bundle does not contain managed lifecycle controller."

	if ($legacyQualification) { Invoke-LegacyCheckpointFixture -Mode seed }

	$start = & $lifecycle -Action Start -InstallPath $install -StateRoot $stateRoot -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$start.status -eq 'PASS') "Managed lifecycle Start did not return PASS."
	Assert-Condition ([string]$start.runtimeReadiness -eq 'PASS') "Managed lifecycle Start did not qualify runtime readiness."
	Assert-Condition ([int]$start.controlProcessId -gt 0 -and [int]$start.runtimeProcessId -gt 0 -and [int]$start.aiProcessId -gt 0) "Managed lifecycle Start did not record all service-host process identities."

	$statusBeforeRestart = & $lifecycle -Action Status -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$statusBeforeRestart.status -eq 'PASS') "Managed lifecycle Status before restart did not return PASS."
	Assert-Condition ([string]$statusBeforeRestart.runtimeReadiness -eq 'PASS') "Managed lifecycle Status before restart did not return runtimeReadiness PASS."

	$restart = & $lifecycle -Action Restart -InstallPath $install -StateRoot $stateRoot -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$restart.status -eq 'PASS') "Managed lifecycle Restart did not return PASS."
	Assert-Condition ([string]$restart.runtimeReadiness -eq 'PASS') "Managed lifecycle Restart did not re-qualify runtime readiness."
	Assert-Condition ([int]$restart.controlProcessId -ne [int]$start.controlProcessId) "Managed lifecycle Restart did not create a new ControlHost process identity."

	Wait-ForStableManagedReadiness -LifecycleWorkRoot $workRoot -ExpectedControlProcessId ([int]$restart.controlProcessId)
	$statusAfterRestart = & $lifecycle -Action Status -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$statusAfterRestart.status -eq 'PASS') "Managed lifecycle Status after restart did not return PASS after stable readiness evidence. Last checks: $($statusAfterRestart.checks | ConvertTo-Json -Compress)."
	Assert-Condition ([string]$statusAfterRestart.runtimeReadiness -eq 'PASS') "Managed lifecycle Status after restart did not return runtimeReadiness PASS after stable readiness evidence."

	$stop = & $lifecycle -Action Stop -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$stop.status -eq 'PASS') "Managed lifecycle Stop did not return PASS."
	Assert-Condition ($stop.graceful -eq $true) "Managed lifecycle Stop was not graceful."
	Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workRoot 'lifecycle-state.json') -PathType Leaf)) "Lifecycle state remained after successful Stop."

	$stoppedStatus = & $lifecycle -Action Status -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$stoppedStatus.status -eq 'STOPPED') "Managed lifecycle Status after Stop did not report STOPPED."
	Assert-Condition ([string]$stoppedStatus.runtimeReadiness -eq 'NOT_APPLICABLE') "Stopped lifecycle must not report runtime readiness PASS."

	if ($legacyQualification) { Invoke-LegacyCheckpointFixture -Mode verify }

	Write-Host 'Managed host lifecycle qualification PASS'
	Write-Host 'Sequence: Start -> Status -> Restart -> Status -> Stop'
	Write-Host 'Runtime readiness observed: PASS while running'
	Write-Host 'Shutdown observed: graceful'
} finally {
	if ($null -ne $lifecycle -and (Test-Path -LiteralPath $lifecycle -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $workRoot 'lifecycle-state.json') -PathType Leaf)) {
		try { & $lifecycle -Action Stop -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode | Out-Null } catch { }
	}
	for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $root); $attempt++) {
		try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 200 }
	}
}
