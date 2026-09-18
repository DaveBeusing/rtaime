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
	[string]$OperatorPrincipal = '',
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

function Resolve-OperatorPrincipal {
	param([string]$Principal)

	try {
		if ([string]::IsNullOrWhiteSpace($Principal)) {
			$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
			Assert-Condition ($null -ne $identity.User) "Current Windows identity has no user SID."
			return [pscustomobject]@{
				Name = [string]$identity.Name
				Sid = [string]$identity.User.Value
			}
		}

		if ($Principal -match '^S-1-(?:[0-9]+-){1,14}[0-9]+$') {
			$sid = [System.Security.Principal.SecurityIdentifier]::new($Principal)
			return [pscustomobject]@{
				Name = [string]$Principal
				Sid = [string]$sid.Value
			}
		}

		$account = [System.Security.Principal.NTAccount]::new($Principal)
		$sid = $account.Translate([System.Security.Principal.SecurityIdentifier])
		return [pscustomobject]@{
			Name = [string]$Principal
			Sid = [string]$sid.Value
		}
	} catch {
		throw "OperatorPrincipal '$Principal' could not be resolved to a Windows SID: $($_.Exception.Message)"
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

function Read-JsonOrNull {
	param([Parameter(Mandatory)][string]$Path)
	if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
	try {
		return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
	} catch {
		return $null
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
$controlReadinessPath = Join-Path $workRoot 'control-readiness.json'
$serviceReadinessPath = Join-Path $workRoot 'apphost-readiness.json'
$shutdownEvidencePath = Join-Path $workRoot 'apphost-shutdown.json'
$installationEvidencePath = Join-Path $workRoot 'service-installation.json'

function Get-RuntimeReadiness {
	$service = Get-ServiceOrNull
	$serviceState = if ($null -eq $service) { 'NOT_INSTALLED' } else { ([string]$service.Status).ToUpperInvariant() }
	$controlReadiness = Read-JsonOrNull -Path $controlReadinessPath
	$serviceReadiness = Read-JsonOrNull -Path $serviceReadinessPath
	$installationEvidence = Read-JsonOrNull -Path $installationEvidencePath

	$servicePid = 0
	$controlPid = 0
	$runtimePid = 0
	$aiPid = 0
	$controlEvidenceValid = $false
	$serviceEvidenceValid = $false
	$serviceEvidenceFresh = $false
	$identitiesMatch = $false
	$internalPipeQualification = $false

	try {
		if ($null -ne $controlReadiness) {
			$controlPid = [int]$controlReadiness.processId
			$runtimePid = [int]$controlReadiness.runtimeSupervision.processId
			$aiPid = [int]$controlReadiness.aiSupervision.processId
			$controlEvidenceValid =
				[string]$controlReadiness.state -eq 'READY' -and
				[string]$controlReadiness.health -eq 'HEALTHY' -and
				[string]$controlReadiness.controlEndpoint -eq [string]$endpoints.control -and
				[string]$controlReadiness.runtimeEndpoint -eq [string]$endpoints.runtime -and
				[string]$controlReadiness.aiEndpoint -eq [string]$endpoints.ai -and
				[string]$controlReadiness.runtimeSupervision.state -eq 'HEALTHY' -and
				[string]$controlReadiness.aiSupervision.state -eq 'HEALTHY'
		}

		if ($null -ne $serviceReadiness) {
			$servicePid = [int]$serviceReadiness.serviceProcessId
			$serviceEvidenceValid =
				[string]$serviceReadiness.status -eq 'PASS' -and
				[string]$serviceReadiness.lifecycleOwnership -eq 'PersistentEngine' -and
				[string]$serviceReadiness.serviceName -eq $ServiceName -and
				[string]$serviceReadiness.instanceId -eq $InstanceId -and
				[string]$serviceReadiness.controlEndpoint -eq [string]$endpoints.control -and
				[string]$serviceReadiness.runtimeEndpoint -eq [string]$endpoints.runtime -and
				[string]$serviceReadiness.aiEndpoint -eq [string]$endpoints.ai -and
				[string]$serviceReadiness.internalPipeQualification -eq 'PASS'

			$verifiedAt = [DateTimeOffset]::Parse([string]$serviceReadiness.verifiedAtUtc, [System.Globalization.CultureInfo]::InvariantCulture)
			$maxAgeMs = [Math]::Max(5000, ([int]$policy.startup.probeIntervalMs * 10))
			$now = [DateTimeOffset]::UtcNow
			$serviceEvidenceFresh =
				$verifiedAt -le $now.AddSeconds(5) -and
				($now - $verifiedAt) -le [TimeSpan]::FromMilliseconds($maxAgeMs)

			$identitiesMatch =
				[int]$serviceReadiness.controlProcessId -eq $controlPid -and
				[int]$serviceReadiness.runtimeProcessId -eq $runtimePid -and
				[int]$serviceReadiness.aiProcessId -eq $aiPid
			$internalPipeQualification = [string]$serviceReadiness.internalPipeQualification -eq 'PASS'
		}
	} catch {
		$controlEvidenceValid = $false
		$serviceEvidenceValid = $false
		$serviceEvidenceFresh = $false
		$identitiesMatch = $false
		$internalPipeQualification = $false
	}

	$processesRunning =
		$servicePid -gt 0 -and
		$controlPid -gt 0 -and
		$runtimePid -gt 0 -and
		$aiPid -gt 0 -and
		(Test-ProcessRunning -ProcessId $servicePid) -and
		(Test-ProcessRunning -ProcessId $controlPid) -and
		(Test-ProcessRunning -ProcessId $runtimePid) -and
		(Test-ProcessRunning -ProcessId $aiPid)

	$pass =
		$serviceState -eq 'RUNNING' -and
		$controlEvidenceValid -and
		$serviceEvidenceValid -and
		$serviceEvidenceFresh -and
		$identitiesMatch -and
		$internalPipeQualification -and
		$processesRunning

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
		operatorPrincipal = if ($null -ne $installationEvidence) { [string]$installationEvidence.operatorPrincipal } else { $null }
		operatorPipeSid = if ($null -ne $installationEvidence) { [string]$installationEvidence.operatorPipeSid } else { $null }
		serviceProcessId = if ($servicePid -gt 0) { $servicePid } else { $null }
		controlProcessId = if ($controlPid -gt 0) { $controlPid } else { $null }
		runtimeProcessId = if ($runtimePid -gt 0) { $runtimePid } else { $null }
		aiProcessId = if ($aiPid -gt 0) { $aiPid } else { $null }
		checks = [ordered]@{
			controlReadinessEvidence = $controlEvidenceValid
			serviceReadinessEvidence = $serviceEvidenceValid
			serviceReadinessFresh = $serviceEvidenceFresh
			processIdentitiesMatch = $identitiesMatch
			internalPipeQualification = $internalPipeQualification
			engineProcessesRunning = $processesRunning
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
	Remove-Item -LiteralPath $shutdownEvidencePath -Force -ErrorAction SilentlyContinue
	Stop-Service -Name $ServiceName -ErrorAction Stop
	$timeoutMs = [int]$policy.shutdown.timeoutMs + 10000
	Assert-Condition (Wait-ServiceState -ExpectedState 'Stopped' -TimeoutMs $timeoutMs) "Windows service did not stop before the shutdown timeout."
	Assert-Condition (Test-Path -LiteralPath $shutdownEvidencePath -PathType Leaf) "Windows service stopped without AppHost shutdown evidence."
	$shutdownEvidence = Get-Content -LiteralPath $shutdownEvidencePath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$shutdownEvidence.status -eq 'PASS' -and [bool]$shutdownEvidence.graceful -and -not [bool]$shutdownEvidence.forcedTermination) "Windows service shutdown required forced termination and is not a graceful PASS."
	foreach ($processId in @($before.controlProcessId, $before.runtimeProcessId, $before.aiProcessId)) {
		if ($null -ne $processId) {
			Assert-Condition (-not (Test-ProcessRunning -ProcessId ([int]$processId))) "Engine process $processId remained alive after Windows service shutdown."
		}
	}
}

switch ($Action) {
	'Install' {
		Assert-Condition ($null -eq (Get-ServiceOrNull)) "Windows service '$ServiceName' is already installed."
		$operator = Resolve-OperatorPrincipal -Principal $OperatorPrincipal
		$startValue = if ($StartupType -eq 'Automatic') { 'auto' } else { 'demand' }
		$binaryPath = ('"{0}" --windows-service --service-name="{1}" --operator-pipe-sid="{2}" --profile=HeadlessEngine --ownership=PersistentEngine --install-root="{3}" --state-root="{4}" --work-root="{5}" --instance-id="{6}"' -f
			$applicationExecutable, $ServiceName, $operator.Sid, $installRoot, $stateRootFull, $workRoot, $InstanceId)
		Invoke-ServiceControl -Arguments @('create', $ServiceName, 'binPath=', $binaryPath, 'start=', $startValue, 'obj=', 'LocalSystem', 'DisplayName=', 'rtaime Engine')
		Invoke-ServiceControl -Arguments @('description', $ServiceName, 'rtaime persistent production engine')
		Invoke-ServiceControl -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
		Invoke-ServiceControl -Arguments @('failureflag', $ServiceName, '1')

		$installationEvidence = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			serviceName = $ServiceName
			instanceId = $InstanceId
			startupType = $StartupType
			serviceAccount = 'LocalSystem'
			operatorPrincipal = [string]$operator.Name
			operatorPipeSid = [string]$operator.Sid
			installedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		}
		[System.IO.File]::WriteAllText(
			$installationEvidencePath,
			($installationEvidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
			[System.Text.UTF8Encoding]::new($false))

		return (Get-RuntimeReadiness)
	}
	'Uninstall' {
		if ($null -eq (Get-ServiceOrNull)) {
			Remove-Item -LiteralPath $installationEvidencePath -Force -ErrorAction SilentlyContinue
			return [pscustomobject]@{ serviceName = $ServiceName; serviceState = 'NOT_INSTALLED'; runtimeReadiness = 'NOT_APPLICABLE' }
		}
		Stop-ServiceGracefully
		Invoke-ServiceControl -Arguments @('delete', $ServiceName)
		Remove-Item -LiteralPath $installationEvidencePath -Force -ErrorAction SilentlyContinue
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
