# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$InstallPath = "",
	[string]$StateRoot = "",
	[string]$WorkPath = "",
	[string]$InstanceId = "showcase",
	[switch]$QualificationMode,
	[switch]$HeadlessAcceptance,
	[string]$HeadlessReadyFile = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Find-ProductAssembly {
	param(
		[Parameter(Mandatory)][string]$InstallRoot,
		[Parameter(Mandatory)][string]$Name
	)
	$productRoot = Join-Path $InstallRoot "product"
	Assert-Condition (Test-Path -LiteralPath $productRoot -PathType Container) "Installed product payload was not found at '$productRoot'."
	$matches = @(Get-ChildItem -LiteralPath $productRoot -Filter $Name -File -Recurse)
	Assert-Condition ($matches.Count -eq 1) "Expected exactly one '$Name' in installed product payload; found $($matches.Count)."
	return $matches[0].FullName
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
	[System.IO.File]::WriteAllText(
		$Path,
		($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine,
		[System.Text.UTF8Encoding]::new($false))
}

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
	$InstallPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed release was not found at '$installRoot'."

if ([string]::IsNullOrWhiteSpace($StateRoot)) {
	$StateRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "rtaime\showcase-state"
}
if ([string]::IsNullOrWhiteSpace($WorkPath)) {
	$WorkPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "rtaime\showcase-lifecycle"
}

$stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
$workRoot = [System.IO.Path]::GetFullPath($WorkPath)
New-Item -ItemType Directory -Path $stateRootFull -Force | Out-Null
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

$lifecycle = Join-Path $installRoot "tools\Invoke-ManagedHostLifecycle.ps1"
Assert-Condition (Test-Path -LiteralPath $lifecycle -PathType Leaf) "Installed release does not contain the managed host lifecycle controller."

function Invoke-Lifecycle {
	param(
		[Parameter(Mandatory)][ValidateSet("Start", "Stop", "Restart", "Status")][string]$Action,
		[switch]$NeedsStateRoot
	)
	$parameters = @{
		Action = $Action
		InstallPath = $installRoot
		WorkPath = $workRoot
		InstanceId = $InstanceId
	}
	if ($NeedsStateRoot) {
		$parameters.StateRoot = $stateRootFull
	}
	if ($QualificationMode) {
		$parameters.QualificationMode = $true
	}
	return & $lifecycle @parameters
}

$startedLifecycle = $false
$operator = $null
$operatorExitCode = $null
$operatorReadiness = if ($HeadlessAcceptance) { "PENDING" } else { "INTERACTIVE" }
$failureDetail = $null
$startedAtUtc = [DateTimeOffset]::UtcNow
$stopStatus = "NOT_REQUIRED"

try {
	$status = Invoke-Lifecycle -Action Status
	if ([string]$status.status -eq "STOPPED") {
		$status = Invoke-Lifecycle -Action Start -NeedsStateRoot
		$startedLifecycle = $true
	} elseif ([string]$status.status -ne "PASS" -or [string]$status.runtimeReadiness -ne "PASS") {
		$status = Invoke-Lifecycle -Action Restart -NeedsStateRoot
		$startedLifecycle = $true
	}

	Assert-Condition ([string]$status.status -eq "PASS") "Showcase host lifecycle did not reach PASS."
	Assert-Condition ([string]$status.runtimeReadiness -eq "PASS") "Showcase host lifecycle did not reach runtimeReadiness PASS."

	$lifecycleStatePath = Join-Path $workRoot "lifecycle-state.json"
	Assert-Condition (Test-Path -LiteralPath $lifecycleStatePath -PathType Leaf) "Managed lifecycle state was not published."
	$lifecycleState = Get-Content -LiteralPath $lifecycleStatePath -Raw | ConvertFrom-Json
	$controlEndpoint = [string]$lifecycleState.endpoints.control
	$runtimeEndpoint = [string]$lifecycleState.endpoints.runtime
	$aiEndpoint = [string]$lifecycleState.endpoints.ai
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($controlEndpoint)) "Lifecycle state did not provide the Control endpoint."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($runtimeEndpoint)) "Lifecycle state did not provide the Runtime endpoint."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($aiEndpoint)) "Lifecycle state did not provide the AI endpoint."

	$operatorAssembly = Find-ProductAssembly -InstallRoot $installRoot -Name "rtaime.Operator.dll"
	$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = "dotnet"
	$startInfo.UseShellExecute = $false
	$startInfo.CreateNoWindow = $true
	$startInfo.WorkingDirectory = Split-Path -Parent $operatorAssembly
	$startInfo.ArgumentList.Add($operatorAssembly)
	$startInfo.Environment["RTAIME_CONTROL_ENDPOINT"] = $controlEndpoint
	$startInfo.Environment["RTAIME_RUNTIME_ENDPOINT"] = $runtimeEndpoint
	$startInfo.Environment["RTAIME_AI_ENDPOINT"] = $aiEndpoint
	$startInfo.Environment["RTAIME_MONITOR_ENDPOINT"] = "$runtimeEndpoint.monitor"

	$readyPath = $null
	if ($HeadlessAcceptance) {
		$readyPath = if ([string]::IsNullOrWhiteSpace($HeadlessReadyFile)) {
			Join-Path $workRoot "operator-ready.json"
		} else {
			[System.IO.Path]::GetFullPath($HeadlessReadyFile)
		}
		Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
		$startInfo.ArgumentList.Add("--headless-once")
		$startInfo.ArgumentList.Add("--control-endpoint=$controlEndpoint")
		$startInfo.ArgumentList.Add("--ready-file=$readyPath")
	}

	$operator = [System.Diagnostics.Process]::Start($startInfo)
	Assert-Condition ($null -ne $operator) "Unable to start rtaime Operator."

	if ($HeadlessAcceptance) {
		$deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
		while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $operator.HasExited -and -not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
			Start-Sleep -Milliseconds 100
		}
		Assert-Condition (Test-Path -LiteralPath $readyPath -PathType Leaf) "Headless Operator did not publish readiness evidence."
		$ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
		Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$ready.HostInstanceId)) "Operator readiness evidence did not identify the ControlHost session."
		Assert-Condition ([string]$ready.RuntimeStatus -eq "READY") "Operator readiness evidence did not observe Runtime READY."
		$operatorReadiness = "PASS"

		if (-not $operator.WaitForExit(10000)) {
			throw "Headless Operator did not exit after publishing one-shot readiness evidence."
		}
	} else {
		$operator.WaitForExit()
	}

	$operatorExitCode = $operator.ExitCode
	Assert-Condition ($operatorExitCode -eq 0) "Operator exited with code $operatorExitCode."
} catch {
	$failureDetail = $_.Exception.Message
	throw
} finally {
	if ($null -ne $operator) {
		try {
			if (-not $operator.HasExited) {
				$operator.Kill($true)
				$operator.WaitForExit()
			}
		} catch { }
		$operator.Dispose()
	}

	if ($startedLifecycle) {
		try {
			$stop = Invoke-Lifecycle -Action Stop
			$stopStatus = [string]$stop.status
			if ($stopStatus -ne "PASS" -and $null -eq $failureDetail) {
				$failureDetail = "Managed showcase lifecycle did not stop cleanly."
			}
		} catch {
			$stopStatus = "FAIL"
			if ($null -eq $failureDetail) {
				$failureDetail = $_.Exception.Message
			}
		}
	}

	$shutdownAccepted = -not $startedLifecycle -or $stopStatus -eq "PASS"
	$receipt = [ordered]@{
		copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
		schemaVersion = "1.0"
		scenario = "INVESTOR_DEMO"
		status = if ($null -eq $failureDetail -and $operatorExitCode -eq 0 -and $shutdownAccepted) { "PASS" } else { "FAIL" }
		serviceReadiness = if ($null -ne $operatorExitCode) { "PASS" } else { "FAIL" }
		operatorReadiness = $operatorReadiness
		operatorExitCode = $operatorExitCode
		lifecycleOwnedByLauncher = $startedLifecycle
		lifecycleStopStatus = $stopStatus
		instanceId = $InstanceId
		installPath = $installRoot
		startedAtUtc = $startedAtUtc.ToString("O")
		completedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
		detail = $failureDetail
	}
	Write-JsonFile -Value $receipt -Path (Join-Path $workRoot "showcase-receipt-latest.json")
}

$finalReceipt = Get-Content -LiteralPath (Join-Path $workRoot "showcase-receipt-latest.json") -Raw | ConvertFrom-Json
if ([string]$finalReceipt.status -ne "PASS") {
	throw "Investor demo launcher failed: $([string]$finalReceipt.detail)"
}

Write-Host "rtaime investor demo launcher PASS"
Write-Host "Service readiness: $([string]$finalReceipt.serviceReadiness)"
Write-Host "Operator readiness: $([string]$finalReceipt.operatorReadiness)"
return $finalReceipt
