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

function Assert-DurableManagedIdentity {
	param([Parameter(Mandatory)][string]$LifecycleWorkRoot)
	$state = Get-Content -LiteralPath (Join-Path $LifecycleWorkRoot 'lifecycle-state.json') -Raw | ConvertFrom-Json
	Assert-Condition ([string]$state.runId -match '^[a-fA-F0-9]{32}$') 'Lifecycle state has no unique run identity.'
	foreach ($role in @('control', 'runtime', 'ai')) {
		$identity = $state.($role + 'Identity')
		Assert-Condition ($null -ne $identity) "Lifecycle state has no durable $role identity."
		Assert-Condition ([int]$identity.processId -eq [int]$state.($role + 'ProcessId')) "Recorded $role PID does not match its identity."
		Assert-Condition ([long]$identity.creationTimeUtcTicks -gt 0) "Recorded $role identity has no creation time."
		Assert-Condition ([System.IO.Path]::IsPathFullyQualified([string]$identity.executablePath)) "Recorded $role image path is not absolute."
		Assert-Condition ([System.IO.Path]::IsPathFullyQualified([string]$identity.hostPath)) "Recorded $role host path is not absolute."
		Assert-Condition ([string]$identity.lifecycleRunId -ceq [string]$state.runId) "Recorded $role identity belongs to another lifecycle run."
		Assert-Condition ([string]$identity.launchId -match '^[a-fA-F0-9]{32}$') "Recorded $role identity has no launch ID."
	}
	Assert-Condition ([string]$state.controlIdentity.launchId -ceq [string]$state.runId) 'ControlHost launch identity does not match the lifecycle run.'
	Assert-Condition (Test-Path -LiteralPath ([string]$state.childOwnershipPath) -PathType Leaf) 'Managed lifecycle did not retain independent child ownership.'
	$journal = Get-Content -LiteralPath ([string]$state.childOwnershipPath) -Raw | ConvertFrom-Json -Depth 32
	Assert-Condition ([string]$journal.lifecycleRunId -ceq [string]$state.runId) 'Child ownership journal belongs to another lifecycle run.'
	Assert-Condition ([int]$journal.controlIdentity.processId -eq [int]$state.controlProcessId) 'Child ownership journal belongs to another ControlHost.'
	foreach ($role in @('runtime', 'ai')) {
		$launchId = [string]$state.($role + 'Identity').launchId
		$generation = @($journal.generations | Where-Object { $_.role -ceq $role -and $_.launchId -ceq $launchId })
		Assert-Condition ($generation.Count -eq 1 -and $generation[0].state -ceq 'RUNNING') "Initial $role readiness was not preceded by durable launch ownership."
		Assert-Condition ([string]$generation[0].processIdentity.creationTimeUtcTicks -ceq [string]$state.($role + 'Identity').creationTimeUtcTicks) "Durable $role identity differs from startup evidence."
	}
	return $state
}

function Open-OwnedFixtureProcess {
	param([Parameter(Mandatory)]$Identity, [Parameter(Mandatory)][string]$RunId)
	Assert-Condition ([string]$Identity.lifecycleRunId -ceq $RunId) 'Crash fixture process belongs to another run.'
	$process = [Diagnostics.Process]::GetProcessById([int]$Identity.processId)
	try {
		$null = $process.SafeHandle
		Assert-Condition (-not $process.HasExited) 'Crash fixture process exited before its identity was retained.'
		Assert-Condition ($process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) -ceq [string]$Identity.creationTimeUtcTicks) 'Crash fixture encountered a reused process ID.'
		Assert-Condition ([string]::Equals([IO.Path]::GetFullPath($process.MainModule.FileName), [IO.Path]::GetFullPath([string]$Identity.executablePath), [StringComparison]::OrdinalIgnoreCase)) 'Crash fixture process has another executable image.'
		return $process
	} catch { $process.Dispose(); throw }
}

function Invoke-ChildOwnershipCrashQualification {
	param([Parameter(Mandatory)][string]$Controller, [Parameter(Mandatory)][string]$InstallRoot, [Parameter(Mandatory)][string]$FixtureRoot, [Parameter(Mandatory)][string]$PersistentStateRoot)
	$work = Join-Path $FixtureRoot 'lifecycle'
	$instance = 'ownership-' + [Guid]::NewGuid().ToString('N')
	$tools = Join-Path $FixtureRoot 'controller'
	New-Item -ItemType Directory -Path $tools -Force | Out-Null
	$fixtureController = Join-Path $tools 'Invoke-ManagedHostLifecycle.ps1'
	Copy-Item -LiteralPath $Controller -Destination $fixtureController
	Assert-Condition ((Get-FileHash -LiteralPath $Controller).Hash -eq (Get-FileHash -LiteralPath $fixtureController).Hash) 'Crash fixture controller differs from the installed source.'
	$fixturePolicy = Get-Content -LiteralPath (Join-Path $InstallRoot 'tools/host-lifecycle-policy.json') -Raw | ConvertFrom-Json
	# Two attempts make the second AI exit terminal, so readiness withdrawal can be synchronized
	# without racing a third launch. The signed installation and all existing time limits stay intact.
	$fixturePolicy.startup.childMaxStartAttempts = 2
	[IO.File]::WriteAllText((Join-Path $tools 'host-lifecycle-policy.json'), ($fixturePolicy | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
	$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
	try {
		$start = & $fixtureController -Action Start -InstallPath $InstallRoot -StateRoot $PersistentStateRoot -WorkPath $work -InstanceId $instance -QualificationMode
		Assert-Condition ([string]$start.status -eq 'PASS') 'Crash fixture did not start its installed hosts.'
		$initial = Assert-DurableManagedIdentity -LifecycleWorkRoot $work
		$control = Open-OwnedFixtureProcess $initial.controlIdentity $initial.runId
		$owned.Add($control)
		foreach ($role in @('runtime', 'ai')) {
			$child = Open-OwnedFixtureProcess $initial.($role + 'Identity') $initial.runId
			$owned.Add($child)
			$child.Kill($false)
			Assert-Condition ($child.WaitForExit(5000)) "Initial $role did not exit for replacement qualification."
		}
		$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
		$replacement = $null
		do {
			Assert-Condition (-not $control.HasExited) 'ControlHost exited before replacement readiness.'
			try {
				$candidate = Get-Content -LiteralPath ([string]$initial.readinessPath) -Raw -ErrorAction Stop | ConvertFrom-Json
				if ([string]$candidate.state -ceq 'READY' -and
					[string]$candidate.runtimeSupervision.processIdentity.launchId -cne [string]$initial.runtimeIdentity.launchId -and
					[string]$candidate.aiSupervision.processIdentity.launchId -cne [string]$initial.aiIdentity.launchId) { $replacement = $candidate; break }
			} catch { }
			Start-Sleep -Milliseconds 25
		} while ([DateTimeOffset]::UtcNow -lt $deadline)
		Assert-Condition ($null -ne $replacement) 'Installed children did not publish their replacement identities before the deadline.'
		$runtime = Open-OwnedFixtureProcess $replacement.runtimeSupervision.processIdentity $initial.runId
		$ai = Open-OwnedFixtureProcess $replacement.aiSupervision.processIdentity $initial.runId
		$owned.Add($runtime)
		$owned.Add($ai)
		$journal = Get-Content -LiteralPath ([string]$initial.childOwnershipPath) -Raw | ConvertFrom-Json -Depth 32
		foreach ($role in @('runtime', 'ai')) {
			$generations = @($journal.generations | Where-Object { $_.role -ceq $role })
			Assert-Condition ($generations.Count -eq 2) "Durable journal did not retain both $role generations."
		}
		$ai.Kill($false)
		Assert-Condition ($ai.WaitForExit(5000)) 'Replacement AIHost did not exit.'
		$deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
		while (Test-Path -LiteralPath ([string]$initial.readinessPath)) {
			Assert-Condition (-not $control.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) 'ControlHost did not withdraw transient readiness after AIHost loss.'
			Start-Sleep -Milliseconds 10
		}
		Assert-Condition (-not $runtime.HasExited) 'Replacement RuntimeHost did not survive until the controlled owner crash.'
		$control.Kill($false)
		Assert-Condition ($control.WaitForExit(5000)) 'Only the owned ControlHost could not be terminated.'
		Assert-Condition (-not $runtime.HasExited) 'The owner crash unexpectedly removed the replacement RuntimeHost.'
		Assert-Condition (-not (Test-Path -LiteralPath ([string]$initial.readinessPath))) 'Crash fixture still has transient readiness evidence.'
		$stop = & $Controller -Action Stop -InstallPath $InstallRoot -WorkPath $work -InstanceId $instance -QualificationMode
		Assert-Condition ([string]$stop.status -eq 'PASS' -and [int]$stop.recoveredOwnedChildren -ge 1) 'Installed Stop did not recover the durable replacement ownership.'
		Assert-Condition ($runtime.WaitForExit(5000)) 'Replacement RuntimeHost survived a successful Stop receipt.'
		Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $work 'lifecycle-state.json'))) 'Successful orphan recovery retained active lifecycle state.'
		Assert-Condition (Test-Path -LiteralPath ([string]$initial.childOwnershipPath)) 'Successful orphan recovery discarded its generation evidence.'
		Write-Host 'Managed child ownership crash/replacement qualification PASS'
	} finally {
		$cleanupFailures = [Collections.Generic.List[Exception]]::new()
		foreach ($process in $owned) {
			try {
				if (-not $process.HasExited) {
					$process.Kill($true)
					Assert-Condition ($process.WaitForExit(10000)) 'A retained fixture process remained after crash qualification cleanup.'
				}
			} catch { $cleanupFailures.Add($_.Exception) }
			finally { $process.Dispose() }
		}
		if ($cleanupFailures.Count -gt 0) { throw [AggregateException]::new('Crash fixture ownership cleanup failed.', $cleanupFailures.ToArray()) }
	}
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

	$stateBeforeRestart = Assert-DurableManagedIdentity -LifecycleWorkRoot $workRoot

	$statusBeforeRestart = & $lifecycle -Action Status -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$statusBeforeRestart.status -eq 'PASS') "Managed lifecycle Status before restart did not return PASS."
	Assert-Condition ($statusBeforeRestart.checks.processIdentityVerified -eq $true) 'Status did not verify process ownership before restart.'
	Assert-Condition ([string]$statusBeforeRestart.runtimeReadiness -eq 'PASS') "Managed lifecycle Status before restart did not return runtimeReadiness PASS."

	$restart = & $lifecycle -Action Restart -InstallPath $install -StateRoot $stateRoot -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$restart.status -eq 'PASS') "Managed lifecycle Restart did not return PASS."
	Assert-Condition ([string]$restart.runtimeReadiness -eq 'PASS') "Managed lifecycle Restart did not re-qualify runtime readiness."
	Assert-Condition ([int]$restart.controlProcessId -ne [int]$start.controlProcessId) "Managed lifecycle Restart did not create a new ControlHost process identity."

	$stateAfterRestart = Assert-DurableManagedIdentity -LifecycleWorkRoot $workRoot
	Assert-Condition ([string]$stateAfterRestart.runId -cne [string]$stateBeforeRestart.runId) 'Restart reused the previous lifecycle run identity.'
	foreach ($role in @('control', 'runtime', 'ai')) {
		Assert-Condition ([string]$stateAfterRestart.($role + 'Identity').launchId -cne [string]$stateBeforeRestart.($role + 'Identity').launchId) "Restart reused the previous $role launch identity."
		Assert-Condition ([string]$stateAfterRestart.($role + 'Identity').creationTimeUtcTicks -cne [string]$stateBeforeRestart.($role + 'Identity').creationTimeUtcTicks) "Restart reused the previous $role process creation time."
	}

	Wait-ForStableManagedReadiness -LifecycleWorkRoot $workRoot -ExpectedControlProcessId ([int]$restart.controlProcessId)
	$statusAfterRestart = & $lifecycle -Action Status -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$statusAfterRestart.status -eq 'PASS') "Managed lifecycle Status after restart did not return PASS after stable readiness evidence. Last checks: $($statusAfterRestart.checks | ConvertTo-Json -Compress)."
	Assert-Condition ($statusAfterRestart.checks.processIdentityVerified -eq $true) 'Status did not verify process ownership after restart.'
	Assert-Condition ([string]$statusAfterRestart.runtimeReadiness -eq 'PASS') "Managed lifecycle Status after restart did not return runtimeReadiness PASS after stable readiness evidence."

	$stop = & $lifecycle -Action Stop -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$stop.status -eq 'PASS') "Managed lifecycle Stop did not return PASS."
	Assert-Condition ($stop.graceful -eq $true) "Managed lifecycle Stop was not graceful."
	Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workRoot 'lifecycle-state.json') -PathType Leaf)) "Lifecycle state remained after successful Stop."

	$stoppedStatus = & $lifecycle -Action Status -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode
	Assert-Condition ([string]$stoppedStatus.status -eq 'STOPPED') "Managed lifecycle Status after Stop did not report STOPPED."
	Assert-Condition ([string]$stoppedStatus.runtimeReadiness -eq 'NOT_APPLICABLE') "Stopped lifecycle must not report runtime readiness PASS."

	if ($legacyQualification) { Invoke-LegacyCheckpointFixture -Mode verify }
	else { Invoke-ChildOwnershipCrashQualification -Controller $lifecycle -InstallRoot $install -FixtureRoot (Join-Path $root 'ownership-crash') -PersistentStateRoot (Join-Path $root 'crash-state') }

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
