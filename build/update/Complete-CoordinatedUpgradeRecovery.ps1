# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[Parameter(Mandatory)]
	[string]$StateRoot,
	[switch]$AcknowledgeRollbackRetirement,
	[switch]$AllowRecoveredFailureClosure,
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
	if (-not [string]::IsNullOrWhiteSpace($directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	[System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Verify-Install {
	param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][bool]$Qualification)
	$verifier = Join-Path $Path 'tools/Test-OfflineReleaseBundle.ps1'
	Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Installation '$Path' does not contain its offline verifier."
	if ($Qualification) {
		& $verifier -BundlePath $Path | Out-Null
	} else {
		& $verifier -BundlePath $Path -RequireTrustedProductionKey | Out-Null
	}
}

Assert-Condition $AcknowledgeRollbackRetirement "Recovery evidence retirement requires explicit acknowledgement that the retained software rollback slot will be deleted."

$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
$stateRootFull = [System.IO.Path]::GetFullPath($StateRoot)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed rtaime release was not found at '$installRoot'."
Assert-Condition (Test-Path -LiteralPath $stateRootFull -PathType Container) "Persistent-state root was not found at '$stateRootFull'."

$updatePolicyPathCandidates = @((Join-Path $PSScriptRoot 'update-policy.json'), (Join-Path $PSScriptRoot '../update/update-policy.json'))
$updatePolicyPath = $updatePolicyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($updatePolicyPath)) "Update policy is unavailable."
$updatePolicy = Get-Content -LiteralPath $updatePolicyPath -Raw | ConvertFrom-Json

$coordinatorPolicyPathCandidates = @((Join-Path $PSScriptRoot 'coordinated-upgrade-policy.json'), (Join-Path $PSScriptRoot '../update/coordinated-upgrade-policy.json'))
$coordinatorPolicyPath = $coordinatorPolicyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($coordinatorPolicyPath)) "Coordinated-upgrade policy is unavailable."
$coordinatorPolicy = Get-Content -LiteralPath $coordinatorPolicyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$coordinatorPolicy.recoveryEvidenceLifecycle.cleanupMode -eq 'EXPLICIT_ROLLBACK_RETIREMENT') "Coordinated recovery evidence policy does not permit explicit retirement."

$recoveryRoot = "$installRoot$([string]$coordinatorPolicy.recoveryStateSuffix)"
$rollbackRoot = "$installRoot$([string]$updatePolicy.replacement.rollbackSlotSuffix)"
Assert-Condition (Test-Path -LiteralPath $recoveryRoot -PathType Container) "Coordinated recovery evidence was not found at '$recoveryRoot'."

$lifecyclePath = Join-Path $recoveryRoot 'recovery-lifecycle.json'
Assert-Condition (Test-Path -LiteralPath $lifecyclePath -PathType Leaf) "Coordinated recovery lifecycle evidence is missing."
$lifecycle = Get-Content -LiteralPath $lifecyclePath -Raw | ConvertFrom-Json
Assert-Condition ([System.IO.Path]::GetFullPath([string]$lifecycle.installPath) -eq $installRoot) "Recovery lifecycle evidence belongs to a different installation."
Assert-Condition ([System.IO.Path]::GetFullPath([string]$lifecycle.stateRoot) -eq $stateRootFull) "Recovery lifecycle evidence belongs to a different persistent-state root."

$transactionEvidencePath = $null
$transactionEvidenceSha256 = $null

$runtimeQualified = [string]$lifecycle.status -eq [string]$coordinatorPolicy.recoveryEvidenceLifecycle.runtimeQualifiedStatus -and
	[string]$lifecycle.runtimeReadiness -eq 'PASS' -and
	$lifecycle.cleanupEligible -eq $true
$recoveredFailure = [string]$lifecycle.status -eq 'RECOVERY_COMPLETE' -and
	$lifecycle.cleanupEligible -eq $true -and
	$AllowRecoveredFailureClosure

Assert-Condition ($runtimeQualified -or $recoveredFailure) "Recovery evidence cannot be retired before runtime readiness is qualified, unless an explicitly acknowledged fully recovered failed upgrade is being closed."

if ($runtimeQualified) {
	$transactionEvidencePath = Join-Path $recoveryRoot 'coordinated-upgrade-receipt.json'
	Assert-Condition (Test-Path -LiteralPath $transactionEvidencePath -PathType Leaf) "Successful coordinated-upgrade receipt is missing."
	$receipt = Get-Content -LiteralPath $transactionEvidencePath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$receipt.status -eq 'PASS' -and [string]$receipt.runtimeReadiness -eq 'UNVERIFIED') "Coordinator receipt does not represent a successful maintenance transaction with separately qualified runtime readiness."
} elseif ($recoveredFailure) {
	$transactionEvidencePath = Join-Path $recoveryRoot 'coordinated-upgrade-failure.json'
	Assert-Condition (Test-Path -LiteralPath $transactionEvidencePath -PathType Leaf) "Recovered-failure evidence is missing."
	$failure = Get-Content -LiteralPath $transactionEvidencePath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$failure.status -eq 'FAIL' -and [string]$failure.recoveryStatus -eq 'PASS' -and $failure.processesRemainStopped -eq $true) "Recovered-failure evidence does not prove a fully successful recovery with processes stopped."
}
$transactionEvidenceSha256 = (Get-FileHash -LiteralPath $transactionEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()

Verify-Install -Path $installRoot -Qualification $QualificationMode

$maintenanceRoot = Join-Path $stateRootFull 'maintenance'
New-Item -ItemType Directory -Path $maintenanceRoot -Force | Out-Null
$closurePath = Join-Path $maintenanceRoot 'coordinated-upgrade-recovery-closure.json'
$recoveryLifecycleSha256 = (Get-FileHash -LiteralPath $lifecyclePath -Algorithm SHA256).Hash.ToLowerInvariant()
$rollbackManifestSha256 = $null

if (Test-Path -LiteralPath $rollbackRoot -PathType Container) {
	Verify-Install -Path $rollbackRoot -Qualification $QualificationMode
	$rollbackManifestPath = Join-Path $rollbackRoot 'bundle-manifest.json'
	$rollbackManifestSha256 = (Get-FileHash -LiteralPath $rollbackManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
} elseif (Test-Path -LiteralPath $closurePath -PathType Leaf) {
	$priorClosure = Get-Content -LiteralPath $closurePath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$priorClosure.status -eq 'RETIRING') "Rollback slot is missing without a resumable recovery-retirement receipt."
	$rollbackManifestSha256 = [string]$priorClosure.rollbackManifestSha256
} else {
	throw "Retained rollback slot was not found at '$rollbackRoot'."
}

$closure = [ordered]@{
	copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
	schemaVersion = '1.0'
	status = 'RETIRING'
	installPath = $installRoot
	stateRoot = $stateRootFull
	closureReason = if ($runtimeQualified) { 'RUNTIME_QUALIFIED' } else { 'RECOVERED_FAILED_UPGRADE' }
	recoveryLifecycleSha256 = $recoveryLifecycleSha256
	transactionEvidenceSha256 = $transactionEvidenceSha256
	rollbackManifestSha256 = $rollbackManifestSha256
	rollbackRetired = $false
	recoveryEvidenceRetired = $false
	updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
Write-JsonFile -Value $closure -Path $closurePath

if (Test-Path -LiteralPath $rollbackRoot -PathType Container) {
	Remove-Item -LiteralPath $rollbackRoot -Recurse -Force
}
Assert-Condition (-not (Test-Path -LiteralPath $rollbackRoot)) "Rollback slot retirement did not complete."
$closure.rollbackRetired = $true
Write-JsonFile -Value $closure -Path $closurePath

Remove-Item -LiteralPath $recoveryRoot -Recurse -Force
Assert-Condition (-not (Test-Path -LiteralPath $recoveryRoot)) "Recovery evidence retirement did not complete."

$closure.status = 'PASS'
$closure.recoveryEvidenceRetired = $true
$closure.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$closure.updatedAtUtc = $closure.completedAtUtc
Write-JsonFile -Value $closure -Path $closurePath

Write-Host 'Coordinated recovery evidence retirement PASS'
Write-Host "Closure receipt: $closurePath"
return [pscustomobject]$closure
