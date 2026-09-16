# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$BundlePath = "",
	[Parameter(Mandatory)]
	[string]$InstallPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) {
		throw $Message
	}
}

function Copy-DirectoryContent {
	param(
		[Parameter(Mandatory)][string]$Source,
		[Parameter(Mandatory)][string]$Destination
	)
	New-Item -ItemType Directory -Path $Destination -Force | Out-Null
	foreach ($file in @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force)) {
		$relative = [System.IO.Path]::GetRelativePath($Source, $file.FullName)
		$target = Join-Path $Destination $relative
		$targetDirectory = Split-Path -Parent $target
		if (-not (Test-Path -LiteralPath $targetDirectory)) {
			New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
		}
		Copy-Item -LiteralPath $file.FullName -Destination $target -Force
	}
}

if ([string]::IsNullOrWhiteSpace($BundlePath)) {
	$candidate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
	if (Test-Path -LiteralPath (Join-Path $candidate "bundle-manifest.json") -PathType Leaf) {
		$BundlePath = $candidate
	} else {
		throw "BundlePath is required when installation is not running from an unpacked offline bundle."
	}
}

$bundleInput = [System.IO.Path]::GetFullPath($BundlePath)
$target = [System.IO.Path]::GetFullPath($InstallPath)
Assert-Condition ((Test-Path -LiteralPath $bundleInput -PathType Container) -or (Test-Path -LiteralPath $bundleInput -PathType Leaf)) "Offline bundle was not found at '$bundleInput'."
Assert-Condition (-not [string]::IsNullOrWhiteSpace($target)) "InstallPath must not be empty."

if (Test-Path -LiteralPath $target -PathType Leaf) {
	throw "Install target '$target' is an existing file."
}
if (Test-Path -LiteralPath $target -PathType Container) {
	$existingEntries = @(Get-ChildItem -LiteralPath $target -Force)
	Assert-Condition ($existingEntries.Count -eq 0) "Clean offline installation refuses non-empty target '$target'. In-place upgrade is outside this foundation package."
}

if (Test-Path -LiteralPath $bundleInput -PathType Container) {
	$sourcePrefix = $bundleInput.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
	$targetPrefix = $target.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
	Assert-Condition (-not $targetPrefix.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) "Install target must not be inside the source bundle directory."
	Assert-Condition (-not $sourcePrefix.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) "Source bundle directory must not be inside the install target."
}

$targetParent = Split-Path -Parent $target
if ([string]::IsNullOrWhiteSpace($targetParent)) {
	throw "Install target must have a parent directory."
}
if (-not (Test-Path -LiteralPath $targetParent -PathType Container)) {
	New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
}

$verifier = Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1"
$preflight = Join-Path $PSScriptRoot "Invoke-OfflinePreflight.ps1"
Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Offline verifier was not found next to the installer."
Assert-Condition (Test-Path -LiteralPath $preflight -PathType Leaf) "Offline preflight tool was not found next to the installer."

$stage = Join-Path $targetParent (".{0}.rtaime-stage-{1}" -f ([System.IO.Path]::GetFileName($target)), [Guid]::NewGuid().ToString("N"))
$snapshotArchive = $null
$targetCreatedByThisRun = $false
try {
	if (Test-Path -LiteralPath $bundleInput -PathType Leaf) {
		Assert-Condition ([System.IO.Path]::GetExtension($bundleInput).Equals(".zip", [StringComparison]::OrdinalIgnoreCase)) "Offline bundle file must be a .zip archive."
		$snapshotArchive = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-offline-install-{0}.zip" -f [Guid]::NewGuid().ToString("N"))
		Copy-Item -LiteralPath $bundleInput -Destination $snapshotArchive -Force
		& $preflight -BundlePath $snapshotArchive -InstallPath $target
		New-Item -ItemType Directory -Path $stage -Force | Out-Null
		Expand-Archive -LiteralPath $snapshotArchive -DestinationPath $stage -Force
	} else {
		& $preflight -BundlePath $bundleInput -InstallPath $target
		Copy-DirectoryContent -Source $bundleInput -Destination $stage
	}

	& $verifier -BundlePath $stage
	& $preflight -BundlePath $stage -InstallPath $target

	if (Test-Path -LiteralPath $target -PathType Container) {
		$existingEntries = @(Get-ChildItem -LiteralPath $target -Force)
		Assert-Condition ($existingEntries.Count -eq 0) "Install target changed during staging and is no longer empty."
		Remove-Item -LiteralPath $target -Force
	}

	Move-Item -LiteralPath $stage -Destination $target
	$targetCreatedByThisRun = $true
	& (Join-Path $target "tools/Test-OfflineReleaseBundle.ps1") -BundlePath $target

	Write-Host "Offline clean installation PASS"
	Write-Host "Install path: $target"
	Write-Host "Windows service registration performed: false"
	Write-Host "Production Package activation performed: false"
	Write-Host "In-place upgrade performed: false"
} catch {
	if ($targetCreatedByThisRun -and (Test-Path -LiteralPath $target -PathType Container)) {
		Remove-Item -LiteralPath $target -Recurse -Force
	}
	throw
} finally {
	if (Test-Path -LiteralPath $stage -PathType Container) {
		Remove-Item -LiteralPath $stage -Recurse -Force
	}
	if ($null -ne $snapshotArchive -and (Test-Path -LiteralPath $snapshotArchive -PathType Leaf)) {
		Remove-Item -LiteralPath $snapshotArchive -Force
	}
}
