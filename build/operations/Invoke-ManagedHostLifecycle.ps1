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

function Get-PropertyValue {
	param($Object, [Parameter(Mandatory)][string]$Name)
	if ($null -eq $Object) { return $null }
	if ($Object -is [System.Collections.IDictionary]) { return $Object[$Name] }
	$property = $Object.PSObject.Properties[$Name]
	if ($null -ne $property) { return $property.Value }
	return $null
}

function Assert-ProcessIdentity {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw "STALE_PROCESS_IDENTITY: $Message" }
}

function Test-SamePath {
	param([string]$Left, [string]$Right)
	if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
	$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
	return [string]::Equals([System.IO.Path]::GetFullPath($Left), [System.IO.Path]::GetFullPath($Right), $comparison)
}

function Get-ProcessExecutablePath {
	param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)
	$null = $Process.SafeHandle
	if (-not $IsWindows) { return $Process.MainModule.FileName }
	if ($null -eq ('Rtaime.ManagedHost.ProcessImage' -as [type])) {
		Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Rtaime.ManagedHost {
    public static class ProcessImage {
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
        public static string Read(SafeProcessHandle process) {
            var path = new StringBuilder(32768);
            var size = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return path.ToString();
        }
    }
}
'@
	}
	return [Rtaime.ManagedHost.ProcessImage]::Read($Process.SafeHandle)
}

function New-OwnedProcessIdentity {
	param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [Parameter(Mandatory)][string]$HostPath, [Parameter(Mandatory)][string]$RunId)
	# Keep the handle open so observations and later termination address the same process instance.
	$null = $Process.SafeHandle
	return [ordered]@{
		processId = $Process.Id
		creationTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
		executablePath = Get-ProcessExecutablePath -Process $Process
		hostPath = [System.IO.Path]::GetFullPath($HostPath)
		launchId = $RunId
		lifecycleRunId = $RunId
	}
}

function Open-VerifiedProcess {
	param($Identity, [Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$HostPath, [Parameter(Mandatory)][string]$RunId, [string]$LaunchId = '')
	Assert-ProcessIdentity ($null -ne $Identity) "PID $ProcessId has no durable launch identity; retain the state for manual recovery."
	Assert-ProcessIdentity ($ProcessId -gt 0 -and [int](Get-PropertyValue $Identity 'processId') -eq $ProcessId) 'Recorded process IDs disagree.'
	$creation = [string](Get-PropertyValue $Identity 'creationTimeUtcTicks')
	$creationTicks = 0L
	Assert-ProcessIdentity ([long]::TryParse($creation, [ref]$creationTicks) -and $creationTicks -gt 0) "PID $ProcessId has no valid creation time."
	Assert-ProcessIdentity ([string](Get-PropertyValue $Identity 'lifecycleRunId') -ceq $RunId) "PID $ProcessId belongs to another lifecycle run."
	$recordedLaunch = [string](Get-PropertyValue $Identity 'launchId')
	Assert-ProcessIdentity ($recordedLaunch -match '^[a-fA-F0-9]{32}$') "PID $ProcessId has no valid launch identity."
	if ($LaunchId) { Assert-ProcessIdentity ($recordedLaunch -ceq $LaunchId) "PID $ProcessId has an unexpected launch identity." }
	Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $Identity 'hostPath')) $HostPath) "PID $ProcessId is not bound to the expected installed host."
	$imagePath = [string](Get-PropertyValue $Identity 'executablePath')
	Assert-ProcessIdentity (-not [string]::IsNullOrWhiteSpace($imagePath) -and [System.IO.Path]::IsPathFullyQualified($imagePath)) "PID $ProcessId has no absolute executable identity."
	try { $process = [System.Diagnostics.Process]::GetProcessById($ProcessId) }
	catch [System.ArgumentException] { return $null }
	$retained = $false
	try {
		try {
			$null = $process.SafeHandle
			if ($process.HasExited) { return $null }
			$actualCreation = $process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
			$actualImage = Get-ProcessExecutablePath -Process $process
		} catch {
			# Exit during observation is benign; unreadable live ownership is not.
			if ($process.HasExited) { return $null }
			throw "STALE_PROCESS_IDENTITY: Cannot verify PID $($ProcessId): $($_.Exception.Message)"
		}
		Assert-ProcessIdentity ($actualCreation -ceq $creation) "PID $ProcessId was reused by a different process."
		Assert-ProcessIdentity (Test-SamePath $actualImage $imagePath) "PID $ProcessId has a different executable image."
		$retained = $true
		return $process
	} finally {
		if (-not $retained) { $process.Dispose() }
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
	param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [Parameter(Mandatory)][int]$TimeoutMs)
	return $Process.WaitForExit($TimeoutMs)
}

function Stop-ProcessTreeForcefully {
	param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [Parameter(Mandatory)][int]$TimeoutMs)
	try {
		if (-not $Process.HasExited) {
			$Process.Kill($true)
			Assert-Condition ($Process.WaitForExit($TimeoutMs)) 'Verified process tree did not exit within the shutdown timeout.'
		}
	} catch [System.InvalidOperationException] {
		if (-not $Process.HasExited) { throw }
	}
}

$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed release was not found at '$installRoot'."
$policyCandidates = @((Join-Path $PSScriptRoot 'host-lifecycle-policy.json'), (Join-Path $installRoot 'tools/host-lifecycle-policy.json'))
$policyPath = $policyCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Managed host lifecycle policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported managed host lifecycle policy schema version."
Assert-Condition (-not [bool]$policy.shutdown.fallbackKillIsPass) 'Forced shutdown must never qualify as PASS.'

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

function Get-VerifiedOwnership {
	param([Parameter(Mandatory)]$State)
	$opened = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
	try {
		$runId = [string](Get-PropertyValue $State 'runId')
		Assert-ProcessIdentity ($runId -match '^[a-fA-F0-9]{32}$') 'Lifecycle state predates durable identities or has an invalid run ID.'
		Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $State 'installPath')) $installRoot) 'Lifecycle state belongs to another installation.'
		Assert-ProcessIdentity ([string](Get-PropertyValue $State 'instanceId') -ceq $InstanceId) 'Lifecycle state belongs to another instance.'
		$runRoot = Join-Path (Join-Path $workRoot 'runs') $runId
		$readinessPath = Join-Path $runRoot 'readiness.json'
		$stopPath = Join-Path $runRoot 'stop.signal'
		Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $State 'readinessPath')) $readinessPath) 'Readiness path does not belong to the recorded run.'
		Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $State 'stopPath')) $stopPath) 'Stop signal does not belong to the recorded run.'
		foreach ($role in @('control', 'runtime', 'ai')) {
			Assert-ProcessIdentity ([string](Get-PropertyValue (Get-PropertyValue $State 'endpoints') $role) -ceq [string]$endpoints[$role]) "Recorded $role endpoint belongs to another instance."
		}
		$controlPid = [int](Get-PropertyValue $State 'controlProcessId')
		$controlHost = Find-HostAssembly -InstallRoot $installRoot -Name 'rtaime.ControlHost.dll'
		$control = Open-VerifiedProcess -Identity (Get-PropertyValue $State 'controlIdentity') -ProcessId $controlPid -HostPath $controlHost -RunId $runId -LaunchId $runId
		if ($null -ne $control) { $opened.Add($control) }
		$readiness = $null
		if (Test-Path -LiteralPath $readinessPath -PathType Leaf) {
			$readiness = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json
			Assert-ProcessIdentity ([int](Get-PropertyValue $readiness 'processId') -eq $controlPid -and [string](Get-PropertyValue $readiness 'lifecycleRunId') -ceq $runId) 'Readiness evidence belongs to another ControlHost generation.'
		}
		$children = @{}
		$identities = @{}
		$processIds = [System.Collections.Generic.HashSet[int]]::new()
		$null = $processIds.Add($controlPid)
		foreach ($role in @('runtime', 'ai')) {
			$identity = Get-PropertyValue $State ($role + 'Identity')
			$childPid = [int](Get-PropertyValue $State ($role + 'ProcessId'))
			if ($null -ne $readiness) {
				$supervision = Get-PropertyValue $readiness ($role + 'Supervision')
				$identity = Get-PropertyValue $supervision 'processIdentity'
				$childPid = [int](Get-PropertyValue $supervision 'processId')
				Assert-ProcessIdentity ($null -ne $identity -and $childPid -gt 0) "Readiness does not prove owned $role process identity."
			}
			$child = $null
			if ($childPid -gt 0 -or $null -ne $identity) {
				Assert-ProcessIdentity ($processIds.Add($childPid)) 'Different managed hosts must not share the same recorded process ID.'
				$hostName = if ($role -eq 'runtime') { 'rtaime.RuntimeHost.dll' } else { 'rtaime.AIHost.dll' }
				$hostPath = Find-HostAssembly -InstallRoot $installRoot -Name $hostName
				$child = Open-VerifiedProcess -Identity $identity -ProcessId $childPid -HostPath $hostPath -RunId $runId
				if ($null -ne $child) { $opened.Add($child) }
			} else {
				Assert-ProcessIdentity $false "Lifecycle state has no $role identity; unrecorded children may still be running."
			}
			$children[$role] = $child
			$identities[$role] = $identity
		}
		return [pscustomobject]@{
			control = $control
			runtime = $children['runtime']
			ai = $children['ai']
			processes = $opened.ToArray()
			runtimeIdentity = $identities['runtime']
			aiIdentity = $identities['ai']
			readiness = $readiness
			readinessPath = $readinessPath
			stopPath = $stopPath
		}
	} catch {
		foreach ($process in $opened) { $process.Dispose() }
		throw "STALE_PROCESS_IDENTITY: $($_.Exception.Message)"
	}
}

function Close-VerifiedOwnership {
	param($Ownership)
	if ($null -ne $Ownership) {
		foreach ($process in $Ownership.processes) { $process.Dispose() }
	}
}

function Invoke-StatusInternal {
	param([bool]$ThrowOnFailure)
	$state = Get-State
	if ($null -eq $state) {
		return [pscustomobject]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			status = 'STOPPED'
			runtimeReadiness = 'NOT_APPLICABLE'
			installPath = $installRoot
			instanceId = $InstanceId
			checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		}
	}

	$ownership = $null
	$identityFailure = $null
	$controlRunning = $false
	$childrenRunning = $false
	$readinessValid = $false
	$controlPipe = $false
	$runtimePipe = $false
	$aiPipe = $false
	$runtimeIdentity = $null
	$aiIdentity = $null
	try {
		try { $ownership = Get-VerifiedOwnership -State $state } catch { $identityFailure = $_.Exception.Message }
		if ($null -ne $ownership) {
			$controlRunning = $null -ne $ownership.control -and -not $ownership.control.HasExited
			$childrenRunning = $null -ne $ownership.runtime -and $null -ne $ownership.ai -and -not $ownership.runtime.HasExited -and -not $ownership.ai.HasExited
			$readiness = $ownership.readiness
			$readinessValid = $null -ne $readiness -and
				[string](Get-PropertyValue $readiness 'state') -eq 'READY' -and
				[string](Get-PropertyValue $readiness 'health') -eq 'HEALTHY' -and
				[string](Get-PropertyValue (Get-PropertyValue $readiness 'runtimeSupervision') 'state') -eq 'HEALTHY' -and
				[string](Get-PropertyValue (Get-PropertyValue $readiness 'aiSupervision') 'state') -eq 'HEALTHY'
			$runtimeIdentity = $ownership.runtimeIdentity
			$aiIdentity = $ownership.aiIdentity
			if ($controlRunning -and $childrenRunning) {
				$controlPipe = Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.control) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)
				$runtimePipe = Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.runtime) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)
				$aiPipe = Test-NamedPipeEndpoint -Endpoint ([string]$endpoints.ai) -TimeoutMs ([int]$policy.startup.probeTimeoutMs)
			}
		}
		$pass = $null -eq $identityFailure -and $controlRunning -and $readinessValid -and $childrenRunning -and $controlPipe -and $runtimePipe -and $aiPipe
		$result = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			status = if ($pass) { 'PASS' } else { 'FAIL' }
			runtimeReadiness = if ($pass) { 'PASS' } else { 'FAIL' }
			installPath = $installRoot
			instanceId = $InstanceId
			runId = [string](Get-PropertyValue $state 'runId')
			controlProcessId = [int](Get-PropertyValue $state 'controlProcessId')
			runtimeProcessId = Get-PropertyValue $runtimeIdentity 'processId'
			aiProcessId = Get-PropertyValue $aiIdentity 'processId'
			runtimeIdentity = $runtimeIdentity
			aiIdentity = $aiIdentity
			reasonCode = if ($null -ne $identityFailure) { 'STALE_PROCESS_IDENTITY' } else { $null }
			detail = $identityFailure
			checks = [ordered]@{
				processIdentityVerified = $null -eq $identityFailure
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
		if ($ThrowOnFailure -and -not $pass) { throw "Managed host lifecycle readiness qualification failed. $identityFailure" }
		return [pscustomobject]$result
	} finally {
		Close-VerifiedOwnership -Ownership $ownership
	}
}

function Invoke-StopInternal {
	$state = Get-State
	if ($null -eq $state) {
		return [pscustomobject]@{ status = 'NOT_APPLICABLE'; runtimeReadiness = 'NOT_APPLICABLE'; installPath = $installRoot; instanceId = $InstanceId }
	}
	$ownership = $null
	$forced = $false
	$receipt = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		operation = 'STOP'
		status = 'FAIL'
		runtimeReadiness = 'NOT_APPLICABLE'
		installPath = $installRoot
		instanceId = $InstanceId
		runId = [string](Get-PropertyValue $state 'runId')
		graceful = $false
		reasonCode = $null
		detail = $null
		completedAtUtc = $null
	}
	try {
		# Verify every known host before writing a stop signal or terminating any process.
		$ownership = Get-VerifiedOwnership -State $state
		if ($null -ne $ownership.control -and -not $ownership.control.HasExited) {
			$stopDirectory = Split-Path -Parent $ownership.stopPath
			New-Item -ItemType Directory -Path $stopDirectory -Force | Out-Null
			[System.IO.File]::WriteAllText($ownership.stopPath, "stopRequestedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))$([Environment]::NewLine)")
			if (-not (Wait-ForExit -Process $ownership.control -TimeoutMs ([int]$policy.shutdown.timeoutMs))) {
				$forced = $true
				Stop-ProcessTreeForcefully -Process $ownership.control -TimeoutMs ([int]$policy.shutdown.timeoutMs)
			}
		}
		foreach ($child in @($ownership.ai, $ownership.runtime)) {
			if ($null -ne $child -and -not $child.HasExited) {
				$forced = $true
				Stop-ProcessTreeForcefully -Process $child -TimeoutMs ([int]$policy.shutdown.timeoutMs)
			}
		}
		if ($forced) {
			$receipt.reasonCode = 'FORCEFUL_TERMINATION'
			throw 'Managed host shutdown required forceful process termination; recovery state has been retained.'
		}
		foreach ($path in @($ownership.stopPath, $ownership.readinessPath, $statePath)) {
			if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
		}
		$receipt.status = 'PASS'
		$receipt.graceful = $true
	} catch {
		$receipt.detail = $_.Exception.Message
		if ($receipt.detail -like '*STALE_PROCESS_IDENTITY*') {
			$receipt.reasonCode = 'STALE_PROCESS_IDENTITY'
		} elseif ($null -eq $receipt.reasonCode) {
			$receipt.reasonCode = 'LIFECYCLE_STOP_FAILED'
		}
		throw
	} finally {
		$receipt.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
		Write-JsonFile -Value $receipt -Path $latestReceiptPath
		Close-VerifiedOwnership -Ownership $ownership
	}
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
	$startInfo.Environment['RTAIME_LIFECYCLE_RUN_ID'] = $runId
	$startInfo.Environment['RTAIME_SUPERVISION_PROBE_TIMEOUT_MS'] = [string]$policy.startup.probeTimeoutMs
	$startInfo.Environment['RTAIME_SUPERVISION_PROBE_INTERVAL_MS'] = [string]$policy.startup.probeIntervalMs
	$startInfo.Environment['RTAIME_SUPERVISION_RESTART_BACKOFF_MS'] = [string]$policy.startup.childRestartBackoffMs
	$startInfo.Environment['RTAIME_SUPERVISION_MAX_START_ATTEMPTS'] = [string]$policy.startup.childMaxStartAttempts
	$process = [System.Diagnostics.Process]::Start($startInfo)
	Assert-Condition ($null -ne $process) "Failed to start managed ControlHost process."
	try {
		$controlIdentity = New-OwnedProcessIdentity -Process $process -HostPath $controlAssembly -RunId $runId
		$state = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			status = 'STARTING'
			installPath = $installRoot
			stateRoot = $stateRootFull
			instanceId = $InstanceId
			runId = $runId
			controlIdentity = $controlIdentity
			runtimeIdentity = $null
			aiIdentity = $null
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
					[string](Get-PropertyValue $ready 'lifecycleRunId') -ceq $runId -and
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
		$qualified = Invoke-StatusInternal -ThrowOnFailure $true
		$state.status = 'RUNNING'
		$state.runtimeProcessId = $qualified.runtimeProcessId
		$state.aiProcessId = $qualified.aiProcessId
		$state.runtimeIdentity = $qualified.runtimeIdentity
		$state.aiIdentity = $qualified.aiIdentity
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
			runId = $runId
			runtimeProcessId = $qualified.runtimeProcessId
			aiProcessId = $qualified.aiProcessId
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
		$cleanupFailure = $null
		try {
			# This is the original Process.Start instance, never a lookup using a saved PID.
			$null = $process.SafeHandle
			[System.IO.File]::WriteAllText($stopPath, "stopRequestedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))$([Environment]::NewLine)")
			if (-not (Wait-ForExit -Process $process -TimeoutMs ([int]$policy.shutdown.timeoutMs))) {
				Stop-ProcessTreeForcefully -Process $process -TimeoutMs ([int]$policy.shutdown.timeoutMs)
			}
		} catch { $cleanupFailure = $_.Exception.Message }
		# Preserve any recorded ownership evidence; a failed start can leave orphaned children.
		# An explicit Stop may clear it only after all recorded identities have been verified.
		$failedReceipt = [ordered]@{
			copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
			schemaVersion = '1.0'
			operation = 'START'
			status = 'FAIL'
			runtimeReadiness = 'FAIL'
			installPath = $installRoot
			instanceId = $InstanceId
			detail = $failure.Exception.Message
			cleanupFailure = $cleanupFailure
			recoveryStateRetained = Test-Path -LiteralPath $statePath -PathType Leaf
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
