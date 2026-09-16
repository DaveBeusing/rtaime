# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath,
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[switch]$QualificationMode,
	[switch]$SimulatePostCommitFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Invoke-BundleVerification {
	param([string]$Verifier, [string]$Path, [bool]$Qualification)
	if ($Qualification) { & $Verifier -BundlePath $Path } else { & $Verifier -BundlePath $Path -RequireTrustedProductionKey }
}

function Test-PathWithin {
	param([string]$Path, [string]$Root)
	$fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	$fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($fullRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$target = [System.IO.Path]::GetFullPath($InstallPath)
$bundle = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition (Test-Path -LiteralPath $target -PathType Container) "Managed update requires an existing installation at '$target'."
Assert-Condition (Test-Path -LiteralPath $bundle -PathType Leaf) "Update bundle was not found at '$bundle'."
Assert-Condition ([System.IO.Path]::GetExtension($bundle).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) "Update bundle must be a .zip archive."

$policyPathCandidates = @((Join-Path $PSScriptRoot 'update-policy.json'), (Join-Path $PSScriptRoot '../update/update-policy.json'))
$policyPath = $policyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Update policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$rollback = "$target$([string]$policy.replacement.rollbackSlotSuffix)"
Assert-Condition (-not (Test-Path -LiteralPath $rollback)) "Rollback slot '$rollback' already exists. Resolve it before another update."

$currentVerifier = Join-Path $target 'tools/Test-OfflineReleaseBundle.ps1'
Assert-Condition (Test-Path -LiteralPath $currentVerifier -PathType Leaf) "Current installation does not contain its offline verifier."
Invoke-BundleVerification -Verifier $currentVerifier -Path $target -Qualification $QualificationMode
$currentManifestHash = (Get-FileHash -LiteralPath (Join-Path $target 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()

$parent = Split-Path -Parent $target
Assert-Condition (-not [string]::IsNullOrWhiteSpace($parent)) "Install path must have a parent directory."
$originalLocation = (Get-Location).Path
$relocatedLocation = $false
if (Test-PathWithin -Path $originalLocation -Root $target) {
	Set-Location -LiteralPath $parent
	$relocatedLocation = $true
}

$stage = Join-Path $parent ('.{0}.rtaime-update-stage-{1}' -f [System.IO.Path]::GetFileName($target), [Guid]::NewGuid().ToString('N'))
$snapshotArchive = Join-Path ([System.IO.Path]::GetTempPath()) ('rtaime-update-{0}.zip' -f [Guid]::NewGuid().ToString('N'))
$rollbackCreated = $false
try {
	Copy-Item -LiteralPath $bundle -Destination $snapshotArchive -Force
	Invoke-BundleVerification -Verifier $currentVerifier -Path $snapshotArchive -Qualification $QualificationMode

	New-Item -ItemType Directory -Path $stage -Force | Out-Null
	Expand-Archive -LiteralPath $snapshotArchive -DestinationPath $stage -Force

	$stageVerifier = Join-Path $stage 'tools/Test-OfflineReleaseBundle.ps1'
	$stagePreflight = Join-Path $stage 'tools/Invoke-OfflinePreflight.ps1'
	Assert-Condition (Test-Path -LiteralPath $stageVerifier -PathType Leaf) "Staged bundle does not contain offline verifier."
	Assert-Condition (Test-Path -LiteralPath $stagePreflight -PathType Leaf) "Staged bundle does not contain offline preflight."
	Invoke-BundleVerification -Verifier $stageVerifier -Path $stage -Qualification $QualificationMode
	& $stagePreflight -BundlePath $stage -InstallPath $target

	Move-Item -LiteralPath $target -Destination $rollback
	$rollbackCreated = $true
	Move-Item -LiteralPath $stage -Destination $target

	$newVerifier = Join-Path $target 'tools/Test-OfflineReleaseBundle.ps1'
	Invoke-BundleVerification -Verifier $newVerifier -Path $target -Qualification $QualificationMode

	if ($SimulatePostCommitFailure) { throw 'Simulated post-commit failure requested for rollback qualification.' }

	Write-Host "Atomic software replacement PASS"
	Write-Host "Install path: $target"
	Write-Host "Rollback path retained: $rollback"
	Write-Host "Persistent state migration performed: false"
	Write-Host "Production Package activation performed: false"
} catch {
	$originalError = $_
	if ($rollbackCreated -and (Test-Path -LiteralPath $rollback -PathType Container)) {
		if (Test-Path -LiteralPath $target -PathType Container) { Remove-Item -LiteralPath $target -Recurse -Force }
		Move-Item -LiteralPath $rollback -Destination $target
		$restoredVerifier = Join-Path $target 'tools/Test-OfflineReleaseBundle.ps1'
		Invoke-BundleVerification -Verifier $restoredVerifier -Path $target -Qualification $QualificationMode
		$restoredManifestHash = (Get-FileHash -LiteralPath (Join-Path $target 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
		Assert-Condition ($restoredManifestHash -eq $currentManifestHash) "Automatic rollback restored a different installation manifest."
		Write-Host "Automatic software rollback PASS"
	}
	throw $originalError
} finally {
	if (Test-Path -LiteralPath $stage -PathType Container) { Remove-Item -LiteralPath $stage -Recurse -Force }
	if (Test-Path -LiteralPath $snapshotArchive -PathType Leaf) { Remove-Item -LiteralPath $snapshotArchive -Force }
	if ($relocatedLocation) {
		if (Test-Path -LiteralPath $originalLocation -PathType Container) { Set-Location -LiteralPath $originalLocation } else { Set-Location -LiteralPath $parent }
	}
}
