# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[Parameter(Mandatory)]
	[string]$StateRoot,
	[ValidateSet('PREVIEW', 'STABLE')]
	[string]$Channel = 'PREVIEW',
	[string]$Repository = 'DaveBeusing/rtaime',
	[string]$PinnedVersion = '',
	[string]$WorkPath = '',
	[string]$InstanceId = 'default',
	[string]$ServiceName = 'rtaime-engine',
	[switch]$AcknowledgeExternalProcessesStopped
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Write-Receipt {
	param(
		[Parameter(Mandatory)][string]$Status,
		[Parameter(Mandatory)][string]$RuntimeReadiness,
		[Parameter(Mandatory)][string]$Detail
	)
	$receiptRoot = Join-Path ([System.IO.Path]::GetFullPath($StateRoot)) 'maintenance'
	New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
	$receipt = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		operation = 'SERVICE_MANAGED_UPDATE'
		status = $Status
		runtimeReadiness = $RuntimeReadiness
		installPath = [System.IO.Path]::GetFullPath($InstallPath)
		stateRoot = [System.IO.Path]::GetFullPath($StateRoot)
		instanceId = $InstanceId
		serviceName = $ServiceName
		detail = $Detail
		completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	$path = Join-Path $receiptRoot 'service-managed-update-latest.json'
	[System.IO.File]::WriteAllText($path, ($receipt | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	return [pscustomobject]$receipt
}

Assert-Condition $AcknowledgeExternalProcessesStopped "Service-managed update requires acknowledgement that Operator and third-party/provider processes using the installation are stopped."

$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
$stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed rtaime release was not found at '$installRoot'."
Assert-Condition (Test-Path -LiteralPath $stateRootFull -PathType Container) "Persistent-state root was not found at '$stateRootFull'."

$serviceTool = Join-Path $installRoot 'tools/Invoke-WindowsServiceLifecycle.ps1'
$updateTool = Join-Path $installRoot 'tools/Invoke-VerifiedUpdate.ps1'
Assert-Condition (Test-Path -LiteralPath $serviceTool -PathType Leaf) "Windows service lifecycle tool is unavailable."
Assert-Condition (Test-Path -LiteralPath $updateTool -PathType Leaf) "Verified update tool is unavailable."

$serviceArguments = @{
	InstallPath = $installRoot
	StateRoot = $stateRootFull
	InstanceId = $InstanceId
	ServiceName = $ServiceName
}
if (-not [string]::IsNullOrWhiteSpace($WorkPath)) { $serviceArguments.WorkPath = $WorkPath }

$initialStatus = & $serviceTool -Action Status @serviceArguments
Assert-Condition ([string]$initialStatus.serviceState -ne 'NOT_INSTALLED') "Persistent Windows service '$ServiceName' is not installed."

try {
	& $serviceTool -Action Stop @serviceArguments | Out-Null

	$updateArguments = @{
		InstallPath = $installRoot
		StateRoot = $stateRootFull
		Channel = $Channel
		Repository = $Repository
		AcknowledgeProcessesStopped = $true
	}
	if (-not [string]::IsNullOrWhiteSpace($PinnedVersion)) { $updateArguments.PinnedVersion = $PinnedVersion }
	& $updateTool @updateArguments | Out-Null

	$serviceTool = Join-Path $installRoot 'tools/Invoke-WindowsServiceLifecycle.ps1'
	Assert-Condition (Test-Path -LiteralPath $serviceTool -PathType Leaf) "Updated installation does not contain Windows service lifecycle tooling."
	$started = & $serviceTool -Action Start @serviceArguments
	Assert-Condition ([string]$started.runtimeReadiness -eq 'PASS') "Updated persistent engine did not return to qualified readiness."
	$qualified = & $serviceTool -Action Qualify @serviceArguments
	Assert-Condition ([string]$qualified.runtimeReadiness -eq 'PASS') "Post-update engine readiness qualification failed."

	return (Write-Receipt -Status 'PASS' -RuntimeReadiness 'PASS' -Detail 'Verified update completed and persistent engine returned to qualified readiness.')
} catch {
	$failure = $_
	Write-Receipt -Status 'FAIL' -RuntimeReadiness 'FAIL' -Detail $failure.Exception.Message | Out-Null
	throw $failure
}
