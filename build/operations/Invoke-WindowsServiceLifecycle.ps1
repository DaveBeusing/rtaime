# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[ValidateSet('Install', 'Uninstall', 'Start', 'Stop', 'Restart', 'Status', 'Qualify')]
	[string]$Action = 'Status',
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[string]$StateRoot = '',
	[string]$WorkPath = '',
	[string]$InstanceId = 'default',
	[string]$ServiceName = 'rtaime-engine',
	[ValidateSet('Automatic', 'Manual')]
	[string]$StartupType = 'Automatic'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
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
	$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
		'.',
		$Endpoint,
		[System.IO.Pipes.PipeDirection]::InOut,
		[System.IO.Pipes.PipeOptions]::Asynchronous)
	try {
		$pipe.Connect($TimeoutMs)
		return $pipe.IsConnected
	} catch {
		return $false
	} finally {
		$pipe.Dispose()
	}
}

function Invoke-ServiceControl {
	param([Parameter(Mandatory)][string[]]$Arguments)
	& sc.exe @Arguments | Out-Host
	if ($LASTEXITCODE -ne 0) {
		throw "sc.exe failed with exit code $LASTEXITCODE: $($Arguments -join ' ')"
	}
}

function Get-ServiceOrNull {
	return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Wait-ServiceState {
	param(
		[Parameter(Mandatory)][string]$ExpectedState,
		[Parameter(Mandatory)][int]$TimeoutMs
	)
	$deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMs)
	do {
		$service = Get-ServiceOrNull
		if ($null -ne $service) {
			$service.Refresh()
			if ([string]$service.Status -eq $ExpectedState) { return $true }
		}
		Start-Sleep -Milliseconds 200
	} while ([DateTimeOffset]::UtcNow -lt $deadline)
	return $false
}

function Get-EndpointSet {
	param([Parameter(Mandatory)]$Policy)
	Assert-Condition ($InstanceId -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') "InstanceId contains unsupported characters."
	if ($InstanceId -eq 'default') {
		return [ordered]@{
			control = [string]$Policy.endpoints.control
			runtime = [string]$Policy.endpoints.runtime
			ai = [string]$Policy.endpoints.ai
		}
	}
	return [ordered]@{
		control = ([string]$Policy.endpoints.control) -replace '\.default$', ".$InstanceId"
		runtime = ([string]$Policy.endpoints.runtime) -replace '\.default$', ".$InstanceId"
		ai = ([string]$Policy.endpoints.ai) -replace '\.default$', ".$InstanceId"
	}
}

if (-not [OperatingSystem]::IsWindows()) {
	throw "Windows service lifecycle management is supported only on Windows."
}
Assert-Condition ($ServiceName -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') "ServiceName contains unsupported characters."

$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed rtaime release was not found at '$installRoot'."
$applicationExecutable = Join-Path $installRoot 'rtaime.exe'
Assert-Condition (Test-Path -LiteralPath $applicationExecutable -PathType Leaf) "Canonical rtaime executable was not found at '$applicationExecutable'."

if ([string]::IsNullOrWhiteSpace($StateRoot)) {
	$StateRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'rtaime'
}
$stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
if ([string]::IsNullOrWhiteSpace($WorkPath)) {
	$WorkPath = Join-Path (Join-Path $stateRootFull 'service') $InstanceId
}
$workRoot = [System.IO.Path]::GetFullPath($WorkPath)
foreach ($path in @($installRoot, $stateRootFull, $workRoot)) {
	Assert-Condition (-not $path.Contains('"')) "Service paths must not contain double-quote characters."
}
New-Item -ItemType Directory -Path $stateRootFull -Force | Out-Null
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

$policyCandidates = @(
	(Join-Path $installRoot 'host-lifecycle-policy.json'),
	(Join-Path $installRoot 'tools/host-lifecycle-policy.json')
)
$policyPath = $policyCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Managed host lifecycle policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported managed host lifecycle policy schema version."
$endpoints = Get-EndpointSet -Policy $policy
$readinessPath = Join-Path $workRoot 'control-readiness.json'

function Get-RuntimeReadiness {
	$service = Get-ServiceOrNull
	$serviceState = if ($null -eq $service) { 'NOT_INSTALLED' } else { ([string]$service.Status).ToUpperInvariant() }
	$readiness = $null
	if (Test-Path -LiteralPath $readinessPath -PathType Leaf) {
		try { $readiness = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json } catch { $readiness = $null }
	}

	$controlPid = if ($null -ne $readiness -and $null -ne $readiness.processId) { [int]$readiness.processId } else { 0 }
	$runtimePid = if ($null -ne $readiness -and $null -ne $readiness.runtimeSupervision.processId) { [int]$readiness.runtimeSupervision.processId } else { 0 }
	$aiPid = if ($null -ne $readiness -and $null -ne $readiness.aiSupervision.processId) { [int]$readiness.aiSupervision.processId } else { 0 }
	$evidenceValid = $null -ne $readiness -and
		[string]$readiness.state -eq 'READY' -and
		[string]$readiness.health -eq 'HEALTHY' -and
		[string]$readiness.controlEndpoint -eq [string]$endpoints.control -and
		[string]$readiness.runtimeEndpoint -eq [string]$endpoints.runtime -and
		[string]$readiness.aiEndpoint -eq [string]$endpoints.ai -and
		[string]$readiness.runtimeSupervision.state -eq 'HEALTHY' -and
		[string]$readiness.aiSupervision.state -eq 'HEALTHY'
	$processesRunning = $controlPid -gt 0 -and $runtimePid -gt 0 -and $aiPid -gt 0 -and
		(Test-ProcessRunning -ProcessId $controlPid) -and
		(Test-ProcessRunning -ProcessId $runtimePid) -and
		(Test-ProcessRunning -ProcessId $aiPid)
	$controlPipe = $evidenceValid -and (Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.control) -TimeoutMs ([int]$policy.startup.probeTimeoutMs))
	$runtimePipe = $evidenceValid -and (Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.runtime) -TimeoutMs ([int]$policy.startup.probeTimeoutMs))
	$aiPipe = $evidenceValid -and (Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.ai) -TimeoutMs ([int]$policy.startup.probeTimeoutMs))
	$pass = $serviceState -eq 'RUNNING' -and $evidenceValid -and $processesRunning -and $controlPipe -and $runtimePipe -and $aiPipe

	return [pscustomobject][ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		serviceName = $ServiceName
		serviceState = $serviceState
		lifecycleOwnership = 'PersistentEngine'
		runtimeReadiness = if ($pass) { 'PASS' } elseif ($serviceState -eq 'RUNNING') { 'FAIL' } else { 'NOT_APPLICABLE' }
		installPath = $installRoot
		stateRoot = $stateRootFull
		workPath = $workRoot
		instanceId = $InstanceId
		controlProcessId = if ($controlPid -gt 0) { $controlPid } else { $null }
		runtimeProcessId = if ($runtimePid -gt 0) { $runtimePid } else { $null }
		aiProcessId = if ($aiPid -gt 0) { $aiPid } else { $null }
		checks = [ordered]@{
			readinessEvidence = $evidenceValid
			engineProcessesRunning = $processesRunning
			controlPipeReachable = $controlPipe
			runtimePipeReachable = $runtimePipe
			aiPipeReachable = $aiPipe
		}
		checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
}

function Wait-RuntimeReadiness {
	$deadline = [DateTimeOffset]::UtcNow.AddMilliseconds([int]$policy.startup.timeoutMs)
	do {
		$status = Get-RuntimeReadiness
		if ([string]$status.runtimeReadiness -eq 'PASS') { return $status }
		Start-Sleep -Milliseconds ([int]$policy.startup.probeIntervalMs)
	} while ([DateTimeOffset]::UtcNow -lt $deadline)
	throw "Windows service reached no qualified engine readiness before the configured startup timeout."
}

function Stop-ServiceGracefully {
	$service = Get-ServiceOrNull
	if ($null -eq $service -or [string]$service.Status -eq 'Stopped') { return }
	$before = Get-RuntimeReadiness
	Stop-Service -Name $ServiceName -ErrorAction Stop
	$timeoutMs = [int]$policy.shutdown.timeoutMs + 10000
	Assert-Condition (Wait-ServiceState -ExpectedState 'Stopped' -TimeoutMs $timeoutMs) "Windows service did not stop before the shutdown timeout."
	foreach ($processId in @($before.controlProcessId, $before.runtimeProcessId, $before.aiProcessId)) {
		if ($null -ne $processId) {
			Assert-Condition (-not (Test-ProcessRunning -ProcessId ([int]$processId))) "Engine process $processId remained alive after Windows service shutdown."
		}
	}
}

switch ($Action) {
	'Install' {
		Assert-Condition ($null -eq (Get-ServiceOrNull)) "Windows service '$ServiceName' is already installed."
		$startValue = if ($StartupType -eq 'Automatic') { 'auto' } else { 'demand' }
		$binaryPath = ('"{0}" --windows-service --profile=HeadlessEngine --ownership=PersistentEngine --install-root="{1}" --state-root="{2}" --work-root="{3}" --instance-id="{4}"' -f
			$applicationExecutable, $installRoot, $stateRootFull, $workRoot, $InstanceId)
		Invoke-ServiceControl -Arguments @('create', $ServiceName, 'binPath=', $binaryPath, 'start=', $startValue, 'obj=', 'LocalSystem', 'DisplayName=', 'rtaime Engine')
		Invoke-ServiceControl -Arguments @('description', $ServiceName, 'rtaime persistent production engine')
		Invoke-ServiceControl -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
		Invoke-ServiceControl -Arguments @('failureflag', $ServiceName, '1')
		return (Get-RuntimeReadiness)
	}
	'Uninstall' {
		if ($null -eq (Get-ServiceOrNull)) {
			return [pscustomobject]@{ serviceName = $ServiceName; serviceState = 'NOT_INSTALLED'; runtimeReadiness = 'NOT_APPLICABLE' }
		}
		Stop-ServiceGracefully
		Invoke-ServiceControl -Arguments @('delete', $ServiceName)
		return [pscustomobject]@{ serviceName = $ServiceName; serviceState = 'REMOVED'; runtimeReadiness = 'NOT_APPLICABLE' }
	}
	'Start' {
		Assert-Condition ($null -ne (Get-ServiceOrNull)) "Windows service '$ServiceName' is not installed."
		Start-Service -Name $ServiceName -ErrorAction Stop
		Assert-Condition (Wait-ServiceState -ExpectedState 'Running' -TimeoutMs ([int]$policy.startup.timeoutMs + 10000)) "Windows service did not reach Running state."
		return (Wait-RuntimeReadiness)
	}
	'Stop' {
		Stop-ServiceGracefully
		return (Get-RuntimeReadiness)
	}
	'Restart' {
		Stop-ServiceGracefully
		Start-Service -Name $ServiceName -ErrorAction Stop
		Assert-Condition (Wait-ServiceState -ExpectedState 'Running' -TimeoutMs ([int]$policy.startup.timeoutMs + 10000)) "Windows service did not reach Running state."
		return (Wait-RuntimeReadiness)
	}
	'Qualify' {
		$status = Get-RuntimeReadiness
		Assert-Condition ([string]$status.runtimeReadiness -eq 'PASS') "Windows service engine readiness qualification failed."
		return $status
	}
	'Status' {
		return (Get-RuntimeReadiness)
	}
}
