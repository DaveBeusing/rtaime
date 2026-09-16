# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[ValidateSet('Start', 'Stop', 'Restart', 'Status')]
	[string]$Action = 'Status',
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[string]$StateRoot = '',
	[string]$WorkPath = '',
	[string]$InstanceId = 'default',
	[switch]$QualificationMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Write-JsonFile {
	param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
	$directory = Split-Path -Parent $Path
	if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
	[System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Test-ProcessRunning {
	param([Parameter(Mandatory)][int]$ProcessId)
	try {
		$process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
		try { return -not $process.HasExited } finally { $process.Dispose() }
	} catch [System.ArgumentException] {
		return $false
	}
}

function Test-NamedPipeEndpoint {
	param([Parameter(Mandatory)][string]$Endpoint, [Parameter(Mandatory)][int]$TimeoutMs)
	$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $Endpoint, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
	try {
		$pipe.Connect($TimeoutMs)
		return $pipe.IsConnected
	} catch {
		return $false
	} finally {
		$pipe.Dispose()
	}
}

function Find-HostAssembly {
	param([Parameter(Mandatory)][string]$InstallRoot, [Parameter(Mandatory)][string]$Name)
	$productRoot = Join-Path $InstallRoot 'product'
	Assert-Condition (Test-Path -LiteralPath $productRoot -PathType Container) "Installed product payload was not found at '$productRoot'."
	$matches = @(Get-ChildItem -LiteralPath $productRoot -Filter $Name -File -Recurse)
	Assert-Condition ($matches.Count -eq 1) "Expected exactly one '$Name' in installed product payload; found $($matches.Count)."
	return $matches[0].FullName
}

function Verify-InstalledRelease {
	param([Parameter(Mandatory)][string]$InstallRoot, [Parameter(Mandatory)][bool]$Qualification)
	$verifier = Join-Path $InstallRoot 'tools/Test-OfflineReleaseBundle.ps1'
	Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Installed release does not contain its offline verifier."
	if ($Qualification) {
		& $verifier -BundlePath $InstallRoot | Out-Null
	} else {
		& $verifier -BundlePath $InstallRoot -RequireTrustedProductionKey | Out-Null
	}
}

function Get-EndpointSet {
	param([Parameter(Mandatory)]$Policy, [Parameter(Mandatory)][string]$Id)
	Assert-Condition ($Id -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') "InstanceId must contain only letters, digits, '.', '_' or '-' and must be at most 64 characters."
	if ($Id -eq 'default') {
		return [ordered]@{
			control = [string]$Policy.endpoints.control
			runtime = [string]$Policy.endpoints.runtime
			ai = [string]$Policy.endpoints.ai
		}
	}
	return [ordered]@{
		control = ([string]$Policy.endpoints.control) -replace '\.default$', ".$Id"
		runtime = ([string]$Policy.endpoints.runtime) -replace '\.default$', ".$Id"
		ai = ([string]$Policy.endpoints.ai) -replace '\.default$', ".$Id"
	}
}

function Wait-ForExit {
	param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][int]$TimeoutMs)
	$deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMs)
	while ([DateTimeOffset]::UtcNow -lt $deadline) {
		if (-not (Test-ProcessRunning -ProcessId $ProcessId)) { return $true }
		Start-Sleep -Milliseconds 100
	}
	return -not (Test-ProcessRunning -ProcessId $ProcessId)
}

function Stop-ProcessTreeForcefully {
	param([Parameter(Mandatory)][int]$ProcessId)
	try {
		$process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
		try {
			if (-not $process.HasExited) {
				$process.Kill($true)
				$process.WaitForExit()
			}
		} finally { $process.Dispose() }
	} catch [System.ArgumentException] { }
}

$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed release was not found at '$installRoot'."
$policyCandidates = @((Join-Path $PSScriptRoot 'host-lifecycle-policy.json'), (Join-Path $installRoot 'tools/host-lifecycle-policy.json'))
$policyPath = $policyCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Managed host lifecycle policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported managed host lifecycle policy schema version."

if ([string]::IsNullOrWhiteSpace($WorkPath)) { $WorkPath = "$installRoot.host-lifecycle" }
$workRoot = [System.IO.Path]::GetFullPath($WorkPath)
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$statePath = Join-Path $workRoot 'lifecycle-state.json'
$latestReceiptPath = Join-Path $workRoot 'operational-receipt-latest.json'
$endpoints = Get-EndpointSet -Policy $policy -Id $InstanceId

function Get-State {
	if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
	return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

function Invoke-StatusInternal {
	param([bool]$ThrowOnFailure)
	$state = Get-State
	if ($null -eq $state) {
		$result = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			status = 'STOPPED'
			runtimeReadiness = 'NOT_APPLICABLE'
			installPath = $installRoot
			instanceId = $InstanceId
			checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		}
		return [pscustomobject]$result
	}

	$controlPid = [int]$state.controlProcessId
	$controlRunning = Test-ProcessRunning -ProcessId $controlPid
	$readinessPath = [string]$state.readinessPath
	$readiness = $null
	if (Test-Path -LiteralPath $readinessPath -PathType Leaf) {
		try { $readiness = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json } catch { $readiness = $null }
	}
	$readinessValid = $null -ne $readiness -and
		[int]$readiness.processId -eq $controlPid -and
		[string]$readiness.state -eq 'READY' -and
		[string]$readiness.health -eq 'HEALTHY' -and
		[string]$readiness.runtimeSupervision.state -eq 'HEALTHY' -and
		[string]$readiness.aiSupervision.state -eq 'HEALTHY'
	$runtimePid = if ($null -ne $readiness) { [int]$readiness.runtimeSupervision.processId } else { 0 }
	$aiPid = if ($null -ne $readiness) { [int]$readiness.aiSupervision.processId } else { 0 }
	$childrenRunning = $runtimePid -gt 0 -and $aiPid -gt 0 -and (Test-ProcessRunning -ProcessId $runtimePid) -and (Test-ProcessRunning -ProcessId $aiPid)
	$controlPipe = Test-NamedPipeEndpoint -Endpoint ([string]$state.endpoints.control) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)
	$runtimePipe = Test-NamedPipeEndpoint -Endpoint ([string]$state.endpoints.runtime) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)
	$aiPipe = Test-NamedPipeEndpoint -Endpoint ([string]$state.endpoints.ai) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)
	$pass = $controlRunning -and $readinessValid -and $childrenRunning -and $controlPipe -and $runtimePipe -and $aiPipe
	$result = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		status = if ($pass) { 'PASS' } else { 'FAIL' }
		runtimeReadiness = if ($pass) { 'PASS' } else { 'FAIL' }
		installPath = $installRoot
		instanceId = $InstanceId
		controlProcessId = $controlPid
		runtimeProcessId = if ($runtimePid -gt 0) { $runtimePid } else { $null }
		aiProcessId = if ($aiPid -gt 0) { $aiPid } else { $null }
		checks = [ordered]@{
			controlProcessRunning = $controlRunning
			controlReadinessEvidence = $readinessValid
			childProcessesRunning = $childrenRunning
			controlPipeReachable = $controlPipe
			runtimePipeReachable = $runtimePipe
			aiPipeReachable = $aiPipe
		}
		checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	Write-JsonFile -Value $result -Path (Join-Path $workRoot 'status-latest.json')
	if ($ThrowOnFailure -and -not $pass) { throw "Managed host lifecycle readiness qualification failed." }
	return [pscustomobject]$result
}

function Invoke-StopInternal {
	$state = Get-State
	if ($null -eq $state) {
		return [pscustomobject]@{ status = 'NOT_APPLICABLE'; runtimeReadiness = 'NOT_APPLICABLE'; installPath = $installRoot; instanceId = $InstanceId }
	}
	$controlPid = [int]$state.controlProcessId
	$readiness = $null
	if (Test-Path -LiteralPath ([string]$state.readinessPath) -PathType Leaf) {
		try { $readiness = Get-Content -LiteralPath ([string]$state.readinessPath) -Raw | ConvertFrom-Json } catch { }
	}
	$childPids = @()
	if ($null -ne $readiness) {
		if ($null -ne $readiness.aiSupervision.processId) { $childPids += [int]$readiness.aiSupervision.processId }
		if ($null -ne $readiness.runtimeSupervision.processId) { $childPids += [int]$readiness.runtimeSupervision.processId }
	}
	$forced = $false
	if (Test-ProcessRunning -ProcessId $controlPid) {
		$stopPath = [string]$state.stopPath
		$stopDirectory = Split-Path -Parent $stopPath
		if (-not [string]::IsNullOrWhiteSpace($stopDirectory)) { New-Item -ItemType Directory -Path $stopDirectory -Force | Out-Null }
		[System.IO.File]::WriteAllText($stopPath, "stopRequestedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))$([Environment]::NewLine)")
		if (-not (Wait-ForExit -ProcessId $controlPid -TimeoutMs ([int]$policy.shutdown.timeoutMs))) {
			$forced = $true
			Stop-ProcessTreeForcefully -ProcessId $controlPid
		}
	}
	foreach ($childPid in $childPids) {
		if (Test-ProcessRunning -ProcessId $childPid) {
			$forced = $true
			Stop-ProcessTreeForcefully -ProcessId $childPid
		}
	}
	try { Remove-Item -LiteralPath ([string]$state.stopPath) -Force -ErrorAction SilentlyContinue } catch { }
	try { Remove-Item -LiteralPath ([string]$state.readinessPath) -Force -ErrorAction SilentlyContinue } catch { }
	Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
	$receipt = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		operation = 'STOP'
		status = if ($forced) { 'FAIL' } else { 'PASS' }
		runtimeReadiness = 'NOT_APPLICABLE'
		installPath = $installRoot
		instanceId = $InstanceId
		graceful = -not $forced
		completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	Write-JsonFile -Value $receipt -Path $latestReceiptPath
	if ($forced -and -not [bool]$policy.shutdown.fallbackKillIsPass) { throw "Managed host shutdown required forceful process termination." }
	return [pscustomobject]$receipt
}

function Invoke-StartInternal {
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($StateRoot)) "StateRoot is required for Start and Restart."
	Assert-Condition (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) "Managed host lifecycle state already exists at '$statePath'. Use Status, Stop or Restart before another Start."
	Verify-InstalledRelease -InstallRoot $installRoot -Qualification $QualificationMode.IsPresent
	$stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
	New-Item -ItemType Directory -Path $stateRootFull -Force | Out-Null
	$controlAssembly = Find-HostAssembly -InstallRoot $installRoot -Name 'rtaime.ControlHost.dll'
	$runtimeAssembly = Find-HostAssembly -InstallRoot $installRoot -Name 'rtaime.RuntimeHost.dll'
	$aiAssembly = Find-HostAssembly -InstallRoot $installRoot -Name 'rtaime.AIHost.dll'
	$runId = [Guid]::NewGuid().ToString('N')
	$runRoot = Join-Path (Join-Path $workRoot 'runs') $runId
	New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
	$readinessPath = Join-Path $runRoot 'readiness.json'
	$stopPath = Join-Path $runRoot 'stop.signal'
	$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = 'dotnet'
	$startInfo.UseShellExecute = $false
	$startInfo.CreateNoWindow = $true
	$startInfo.WorkingDirectory = Split-Path -Parent $controlAssembly
	$startInfo.ArgumentList.Add($controlAssembly)
	$startInfo.Environment['RTAIME_CONTROL_ENDPOINT'] = [string]$endpoints.control
	$startInfo.Environment['RTAIME_RUNTIME_ENDPOINT'] = [string]$endpoints.runtime
	$startInfo.Environment['RTAIME_AI_ENDPOINT'] = [string]$endpoints.ai
	$startInfo.Environment['RTAIME_CONTROL_DURABILITY_ROOT'] = $stateRootFull
	$startInfo.Environment['RTAIME_RUNTIME_EXECUTABLE'] = $runtimeAssembly
	$startInfo.Environment['RTAIME_AI_EXECUTABLE'] = $aiAssembly
	$startInfo.Environment['RTAIME_HOST_READINESS_FILE'] = $readinessPath
	$startInfo.Environment['RTAIME_HOST_STOP_FILE'] = $stopPath
	$startInfo.Environment['RTAIME_SUPERVISION_PROBE_TIMEOUT_MS'] = [string]$policy.startup.probeTimeoutMs
	$startInfo.Environment['RTAIME_SUPERVISION_PROBE_INTERVAL_MS'] = [string]$policy.startup.probeIntervalMs
	$startInfo.Environment['RTAIME_SUPERVISION_RESTART_BACKOFF_MS'] = [string]$policy.startup.childRestartBackoffMs
	$startInfo.Environment['RTAIME_SUPERVISION_MAX_START_ATTEMPTS'] = [string]$policy.startup.childMaxStartAttempts
	$process = [System.Diagnostics.Process]::Start($startInfo)
	Assert-Condition ($null -ne $process) "Failed to start managed ControlHost process."
	try {
		$state = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			status = 'STARTING'
			installPath = $installRoot
			stateRoot = $stateRootFull
			instanceId = $InstanceId
			controlProcessId = $process.Id
			runtimeProcessId = $null
			aiProcessId = $null
			readinessPath = $readinessPath
			stopPath = $stopPath
			endpoints = $endpoints
			startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
			readyAtUtc = $null
		}
		Write-JsonFile -Value $state -Path $statePath
		$deadline = [DateTimeOffset]::UtcNow.AddMilliseconds([int]$policy.startup.timeoutMs)
		$ready = $null
		while ([DateTimeOffset]::UtcNow -lt $deadline) {
			if ($process.HasExited) { throw "ControlHost exited before readiness with exit code $($process.ExitCode)." }
			if (Test-Path -LiteralPath $readinessPath -PathType Leaf) {
				try { $ready = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json } catch { $ready = $null }
				if ($null -ne $ready -and
					[int]$ready.processId -eq $process.Id -and
					[string]$ready.state -eq 'READY' -and
					[string]$ready.health -eq 'HEALTHY' -and
					[string]$ready.runtimeSupervision.state -eq 'HEALTHY' -and
					[string]$ready.aiSupervision.state -eq 'HEALTHY' -and
					(Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.control) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)) -and
					(Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.runtime) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)) -and
					(Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.ai) -TimeoutMs ([int]$policy.startup.probeTimeoutMs))) {
					break
				}
			}
			Start-Sleep -Milliseconds ([int]$policy.startup.probeIntervalMs)
		}
		Assert-Condition ($null -ne $ready -and [int]$ready.processId -eq $process.Id -and [string]$ready.state -eq 'READY') "Managed service hosts did not reach readiness before the configured startup timeout."
		Invoke-StatusInternal -ThrowOnFailure $true | Out-Null
		$state.status = 'RUNNING'
		$state.runtimeProcessId = [int]$ready.runtimeSupervision.processId
		$state.aiProcessId = [int]$ready.aiSupervision.processId
		$state.readyAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		Write-JsonFile -Value $state -Path $statePath
		$manifest = Get-Content -LiteralPath (Join-Path $installRoot 'bundle-manifest.json') -Raw | ConvertFrom-Json
		$receipt = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			operation = 'START'
			status = 'PASS'
			runtimeReadiness = 'PASS'
			installPath = $installRoot
			instanceId = $InstanceId
			productVersion = [string]$manifest.productVersion
			sourceCommit = [string]$manifest.sourceCommit
			controlProcessId = $process.Id
			runtimeProcessId = [int]$ready.runtimeSupervision.processId
			aiProcessId = [int]$ready.aiSupervision.processId
			endpoints = $endpoints
			operatorClientManaged = $false
			productionPackageActivation = 'NOT_PERFORMED'
			completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		}
		Write-JsonFile -Value $receipt -Path (Join-Path $runRoot 'operational-receipt.json')
		Write-JsonFile -Value $receipt -Path $latestReceiptPath
		Write-Host "Managed host lifecycle START PASS"
		Write-Host "Runtime readiness: PASS"
		return [pscustomobject]$receipt
	} catch {
		$failure = $_
		try {
			[System.IO.File]::WriteAllText($stopPath, "stopRequestedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))$([Environment]::NewLine)")
			if (-not (Wait-ForExit -ProcessId $process.Id -TimeoutMs ([int]$policy.shutdown.timeoutMs))) { Stop-ProcessTreeForcefully -ProcessId $process.Id }
		} catch { }
		Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
		$failedReceipt = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			operation = 'START'
			status = 'FAIL'
			runtimeReadiness = 'FAIL'
			installPath = $installRoot
			instanceId = $InstanceId
			detail = $failure.Exception.Message
			completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		}
		Write-JsonFile -Value $failedReceipt -Path (Join-Path $runRoot 'operational-receipt.json')
		Write-JsonFile -Value $failedReceipt -Path $latestReceiptPath
		throw $failure
	} finally {
		$process.Dispose()
	}
}

switch ($Action) {
	'Start' { return (Invoke-StartInternal) }
	'Stop' { return (Invoke-StopInternal) }
	'Restart' {
		Invoke-StopInternal | Out-Null
		return (Invoke-StartInternal)
	}
	'Status' { return (Invoke-StatusInternal -ThrowOnFailure $false) }
}
