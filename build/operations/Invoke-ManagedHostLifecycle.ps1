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

function Assert-MatchingIdentity {
	param($Actual, $Expected, [string]$Context)
	Assert-ProcessIdentity ($null -ne $Actual -and $null -ne $Expected) "$Context has no durable identity."
	foreach ($field in @('processId', 'creationTimeUtcTicks', 'launchId', 'lifecycleRunId')) {
		Assert-ProcessIdentity ([string](Get-PropertyValue $Actual $field) -ceq [string](Get-PropertyValue $Expected $field)) "$Context disagrees with the durable ownership journal ($field)."
	}
	foreach ($field in @('hostPath', 'executablePath')) {
		Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $Actual $field)) ([string](Get-PropertyValue $Expected $field))) "$Context disagrees with the durable ownership journal ($field)."
	}
}

function Read-ChildOwnershipJournal {
	param([Parameter(Mandatory)]$State, [Parameter(Mandatory)][string]$RunRoot)
	$runId = [string](Get-PropertyValue $State 'runId')
	$ownershipDirectory = Join-Path $RunRoot 'child-ownership'
	$ownershipPath = Join-Path $ownershipDirectory 'ownership.json'
	Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $State 'childOwnershipPath')) $ownershipPath) 'Lifecycle state has no run-bound child ownership journal; replacement children cannot be ruled out.'
	Assert-ProcessIdentity (Test-Path -LiteralPath $ownershipPath -PathType Leaf) 'Durable child ownership is missing; readiness and initial PIDs cannot prove complete shutdown.'
	$directoryInfo = Get-Item -LiteralPath $ownershipDirectory -Force
	$fileInfo = Get-Item -LiteralPath $ownershipPath -Force
	Assert-ProcessIdentity (($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and ($fileInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Durable child ownership must not redirect outside its run directory.'
	Assert-ProcessIdentity ($fileInfo.Length -gt 0 -and $fileInfo.Length -le 4MB) 'Durable child ownership is empty or exceeds the 4 MiB evidence limit.'
	# Share deletion so a live supervisor can atomically publish the next complete journal while
	# this observation retains the previous file instance. Stop rereads after that writer exits.
	$stream = [IO.FileStream]::new($ownershipPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
	try {
		Assert-ProcessIdentity ($stream.Length -gt 0 -and $stream.Length -le 4MB) 'Opened child ownership evidence exceeds the bounded read limit.'
		$reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
		try { $journal = $reader.ReadToEnd() | ConvertFrom-Json -Depth 32 }
		finally { $reader.Dispose() }
	} finally { $stream.Dispose() }
	Assert-ProcessIdentity ([string](Get-PropertyValue $journal 'schemaVersion') -ceq '1.0' -and [string](Get-PropertyValue $journal 'lifecycleRunId') -ceq $runId) 'Child ownership journal belongs to another schema or lifecycle run.'
	Assert-MatchingIdentity (Get-PropertyValue $journal 'controlIdentity') (Get-PropertyValue $State 'controlIdentity') 'Child ownership ControlHost owner'
	$definitions = @{}
	$children = @(Get-PropertyValue $journal 'children')
	Assert-ProcessIdentity ($children.Count -eq 2) 'Child ownership must declare RuntimeHost and AIHost.'
	foreach ($child in $children) {
		$role = [string](Get-PropertyValue $child 'role')
		Assert-ProcessIdentity ($role -cin @('runtime', 'ai') -and -not $definitions.ContainsKey($role)) 'Child ownership has an unexpected or duplicate role.'
		$hostName = if ($role -eq 'runtime') { 'rtaime.RuntimeHost.dll' } else { 'rtaime.AIHost.dll' }
		$hostPath = Find-HostAssembly -InstallRoot $installRoot -Name $hostName
		Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $child 'hostPath')) $hostPath) "Child ownership $role belongs to another installed host."
		Assert-ProcessIdentity ([string](Get-PropertyValue $child 'endpoint') -ceq [string]$endpoints[$role]) "Child ownership $role belongs to another endpoint."
		Assert-ProcessIdentity ([int](Get-PropertyValue $child 'maxStartAttempts') -gt 0) "Child ownership $role has no bounded launch budget."
		$definitions[$role] = $child
	}
	$generationProperty = $journal.PSObject.Properties['generations']
	Assert-ProcessIdentity ($null -ne $generationProperty -and $generationProperty.Value -is [Array]) 'Child ownership has no complete generation collection.'
	$generations = @($generationProperty.Value)
	$launches = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
	$attempts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
	$records = @{}
	foreach ($generation in $generations) {
		$role = [string](Get-PropertyValue $generation 'role')
		$launchId = [string](Get-PropertyValue $generation 'launchId')
		$attempt = [int](Get-PropertyValue $generation 'attempt')
		Assert-ProcessIdentity ($role -cin @('runtime', 'ai') -and $definitions.ContainsKey($role)) 'Child generation has an undeclared role.'
		Assert-ProcessIdentity ($launchId -cmatch '^[a-f0-9]{32}$' -and $launches.Add($launchId)) 'Child generation has an invalid or duplicate launch ID.'
		Assert-ProcessIdentity ($attempt -gt 0 -and $attempt -le [int](Get-PropertyValue $definitions[$role] 'maxStartAttempts') -and $attempts.Add("${role}:$attempt")) 'Child generation exceeds or duplicates the lifetime launch budget.'
		$generationState = [string](Get-PropertyValue $generation 'state')
		Assert-ProcessIdentity ($generationState -cin @('RUNNING', 'EXITED', 'NOT_STARTED')) "Child launch $role/$launchId has an incomplete outcome; retain recovery evidence."
		$identity = Get-PropertyValue $generation 'processIdentity'
		if ($generationState -ceq 'RUNNING') { Assert-ProcessIdentity ($null -ne $identity) "Running child $role/$launchId has no durable identity." }
		if ($null -ne $identity) {
			Assert-ProcessIdentity ([string](Get-PropertyValue $identity 'launchId') -ceq $launchId -and [string](Get-PropertyValue $identity 'lifecycleRunId') -ceq $runId) 'Child identity does not belong to its recorded launch/run.'
			Assert-ProcessIdentity (Test-SamePath ([string](Get-PropertyValue $identity 'hostPath')) ([string](Get-PropertyValue $definitions[$role] 'hostPath'))) 'Child identity does not belong to its declared installed host.'
		}
		$records[$launchId] = $generation
	}
	foreach ($role in @('runtime', 'ai')) {
		Assert-ProcessIdentity (@($generations | Where-Object { $_.role -ceq $role -and $_.state -ceq 'RUNNING' }).Count -le 1) "Child ownership contains overlapping active $role generations."
		$identity = Get-PropertyValue $State ($role + 'Identity')
		$processId = [int](Get-PropertyValue $State ($role + 'ProcessId'))
		if ($null -ne $identity -or $processId -gt 0) {
			$launchId = [string](Get-PropertyValue $identity 'launchId')
			Assert-ProcessIdentity ($records.ContainsKey($launchId) -and [string](Get-PropertyValue $records[$launchId] 'role') -ceq $role) "Initial $role ownership is missing from the durable journal."
			Assert-MatchingIdentity $identity (Get-PropertyValue $records[$launchId] 'processIdentity') "Initial $role identity"
			Assert-ProcessIdentity ($processId -eq [int](Get-PropertyValue $identity 'processId')) "Initial $role PID and identity disagree."
		}
	}
	return [pscustomobject]@{ definitions = $definitions; records = $records; generations = $generations; directory = $ownershipDirectory }
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
		$journal = Read-ChildOwnershipJournal -State $State -RunRoot $runRoot
		$children = @{}
		$identities = @{}
		$ownedChildren = [Collections.Generic.List[object]]::new()
		$readinessMatchesCurrent = $null -ne $readiness
		$processIds = [System.Collections.Generic.HashSet[int]]::new()
		$null = $processIds.Add($controlPid)
		foreach ($generation in @($journal.generations | Sort-Object { [int]$_.attempt })) {
			$role = [string]$generation.role
			$identity = Get-PropertyValue $generation 'processIdentity'
			$identities[$role] = $identity
			if ([string]$generation.state -cne 'RUNNING') { continue }
			$childPid = [int](Get-PropertyValue $identity 'processId')
			Assert-ProcessIdentity ($processIds.Add($childPid)) 'Active managed host generations must not share a recorded process ID.'
			$hostPath = [string](Get-PropertyValue $journal.definitions[$role] 'hostPath')
			$child = Open-VerifiedProcess -Identity $identity -ProcessId $childPid -HostPath $hostPath -RunId $runId -LaunchId ([string]$generation.launchId)
			if ($null -ne $child) {
				$opened.Add($child)
				$children[$role] = $child
				$childStopPath = Join-Path $journal.directory ("$role-$($generation.launchId).stop")
				$existingStop = Get-Item -LiteralPath $childStopPath -Force -ErrorAction SilentlyContinue
				Assert-ProcessIdentity ($null -eq $existingStop -or (-not $existingStop.PSIsContainer -and ($existingStop.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) 'Child stop signal is redirected or is not a regular file.'
				$ownedChildren.Add([pscustomobject]@{ process = $child; stopPath = $childStopPath; identity = $identity })
			}
		}
		foreach ($role in @('runtime', 'ai')) {
			if ($null -ne $readiness) {
				$supervision = Get-PropertyValue $readiness ($role + 'Supervision')
				$publishedIdentity = Get-PropertyValue $supervision 'processIdentity'
				$launchId = [string](Get-PropertyValue $publishedIdentity 'launchId')
				Assert-ProcessIdentity ($null -ne $publishedIdentity -and $journal.records.ContainsKey($launchId) -and [string](Get-PropertyValue $journal.records[$launchId] 'role') -ceq $role) "Readiness has no journalled $role launch ownership."
				Assert-MatchingIdentity $publishedIdentity (Get-PropertyValue $journal.records[$launchId] 'processIdentity') "Readiness $role identity"
				Assert-ProcessIdentity ([int](Get-PropertyValue $supervision 'processId') -eq [int](Get-PropertyValue $publishedIdentity 'processId')) "Readiness $role PID and identity disagree."
				if ($launchId -cne [string](Get-PropertyValue $identities[$role] 'launchId')) { $readinessMatchesCurrent = $false }
			}
		}
		return [pscustomobject]@{
			control = $control
			runtime = $children['runtime']
			ai = $children['ai']
			ownedChildren = $ownedChildren.ToArray()
			processes = $opened.ToArray()
			runtimeIdentity = $identities['runtime']
			aiIdentity = $identities['ai']
			readiness = $readiness
			readinessMatchesCurrent = $readinessMatchesCurrent
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
			$readinessValid = $null -ne $readiness -and $ownership.readinessMatchesCurrent -and
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
		recoveredOwnedChildren = 0
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
		# A replacement may have been launched after the first observation. ControlHost has exited,
		# so its durable generation journal is now stable and must be read again before proving Stop.
		Close-VerifiedOwnership -Ownership $ownership
		$ownership = $null
		$ownership = Get-VerifiedOwnership -State $state
		foreach ($ownedChild in $ownership.ownedChildren) {
			$child = $ownedChild.process
			if (-not $child.HasExited) {
				$receipt.recoveredOwnedChildren++
				[IO.File]::WriteAllText($ownedChild.stopPath, "stopRequestedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))$([Environment]::NewLine)")
				if (-not (Wait-ForExit -Process $child -TimeoutMs ([int]$policy.shutdown.timeoutMs))) {
					$forced = $true
					Stop-ProcessTreeForcefully -Process $child -TimeoutMs ([int]$policy.shutdown.timeoutMs)
				}
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
	$childOwnershipPath = Join-Path (Join-Path $runRoot 'child-ownership') 'ownership.json'
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
	$startInfo.Environment['RTAIME_CHILD_OWNERSHIP_FILE'] = $childOwnershipPath
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
			childOwnershipPath = $childOwnershipPath
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
