# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath
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

try {
	New-Item -ItemType Directory -Path $root -Force | Out-Null
	& (Join-Path $repositoryRoot 'build/release/Install-OfflineRelease.ps1') -BundlePath $bundle -InstallPath $install
	$lifecycle = Join-Path $install 'tools/Invoke-ManagedHostLifecycle.ps1'
	Assert-Condition (Test-Path -LiteralPath $lifecycle -PathType Leaf) "Installed qualification bundle does not contain managed lifecycle controller."

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
