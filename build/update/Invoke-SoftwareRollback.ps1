# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[switch]$AcknowledgeProcessesStopped,
	[switch]$QualificationMode,
	[switch]$CoordinatedStateHandled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Verify-Install {
	param([string]$Path, [bool]$Qualification)
	$verifier = Join-Path $Path 'tools/Test-OfflineReleaseBundle.ps1'
	Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Installation '$Path' does not contain its verifier."
	if ($Qualification) { & $verifier -BundlePath $Path } else { & $verifier -BundlePath $Path -RequireTrustedProductionKey }
}

function Test-PathWithin {
	param([string]$Path, [string]$Root)
	$fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	$fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($fullRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

Assert-Condition $AcknowledgeProcessesStopped "Rollback requires explicit acknowledgement that rtaime processes/services using the software installation have been stopped."
$target = [System.IO.Path]::GetFullPath($InstallPath)
$policyPathCandidates = @((Join-Path $PSScriptRoot 'update-policy.json'), (Join-Path $PSScriptRoot '../update/update-policy.json'))
$policyPath = $policyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Update policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$rollback = "$target$([string]$policy.replacement.rollbackSlotSuffix)"
$coordinatedRecoverySuffix = if ($policy.replacement.PSObject.Properties.Name -contains 'coordinatedRecoverySuffix') { [string]$policy.replacement.coordinatedRecoverySuffix } else { '.upgrade-recovery' }
$coordinatedRecoveryRoot = "$target$coordinatedRecoverySuffix"
if ((Test-Path -LiteralPath $coordinatedRecoveryRoot) -and -not $CoordinatedStateHandled) {
	throw "Software-only rollback is blocked because coordinated persistent-state recovery evidence exists at '$coordinatedRecoveryRoot'. Use the coordinated recovery path so software and state cannot diverge."
}

Assert-Condition (Test-Path -LiteralPath $target -PathType Container) "Current installation was not found."
Assert-Condition (Test-Path -LiteralPath $rollback -PathType Container) "Rollback installation was not found at '$rollback'."
Verify-Install -Path $target -Qualification $QualificationMode
Verify-Install -Path $rollback -Qualification $QualificationMode

$parent = Split-Path -Parent $target
Assert-Condition (-not [string]::IsNullOrWhiteSpace($parent)) "Install path must have a parent directory."
$originalLocation = (Get-Location).Path
$relocatedLocation = $false
if ((Test-PathWithin -Path $originalLocation -Root $target) -or (Test-PathWithin -Path $originalLocation -Root $rollback)) {
	Set-Location -LiteralPath $parent
	$relocatedLocation = $true
}

$swap = Join-Path $parent ('.{0}.rtaime-rollback-swap-{1}' -f [System.IO.Path]::GetFileName($target), [Guid]::NewGuid().ToString('N'))
$targetMoved = $false
$rollbackActivated = $false
try {
	Move-Item -LiteralPath $target -Destination $swap
	$targetMoved = $true
	Move-Item -LiteralPath $rollback -Destination $target
	$rollbackActivated = $true
	Verify-Install -Path $target -Qualification $QualificationMode
	Move-Item -LiteralPath $swap -Destination $rollback
	$targetMoved = $false
	Verify-Install -Path $rollback -Qualification $QualificationMode

	Write-Host "Software rollback PASS"
	Write-Host "Active installation: $target"
	Write-Host "Previous active version retained in rollback slot: $rollback"
	Write-Host "Persistent state migration performed: false"
} catch {
	$originalError = $_
	if ($rollbackActivated -and (Test-Path -LiteralPath $target -PathType Container) -and -not (Test-Path -LiteralPath $rollback)) {
		Move-Item -LiteralPath $target -Destination $rollback
	}
	if ($targetMoved -and (Test-Path -LiteralPath $swap -PathType Container) -and -not (Test-Path -LiteralPath $target)) {
		Move-Item -LiteralPath $swap -Destination $target
	}
	throw $originalError
} finally {
	if (Test-Path -LiteralPath $swap -PathType Container) { Remove-Item -LiteralPath $swap -Recurse -Force }
	if ($relocatedLocation) {
		if (Test-Path -LiteralPath $originalLocation -PathType Container) { Set-Location -LiteralPath $originalLocation } else { Set-Location -LiteralPath $parent }
	}
}
