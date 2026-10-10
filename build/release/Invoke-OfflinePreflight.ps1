# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$BundlePath = "",
	[string]$InstallPath = "",
	[switch]$RequireNdiRuntime
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

function Read-BundleJson {
	param(
		[Parameter(Mandatory)][string]$Bundle,
		[Parameter(Mandatory)][string]$RelativePath
	)

	if (Test-Path -LiteralPath $Bundle -PathType Container) {
		$path = Join-Path $Bundle ($RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
		Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Bundle JSON '$RelativePath' was not found."
		return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
	}

	$archive = [System.IO.Compression.ZipFile]::OpenRead($Bundle)
	try {
		$matches = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $RelativePath })
		Assert-Condition ($matches.Count -eq 1) "Bundle archive must contain exactly one '$RelativePath' entry."
		$stream = $matches[0].Open()
		try {
			$reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
			try {
				return $reader.ReadToEnd() | ConvertFrom-Json
			} finally {
				$reader.Dispose()
			}
		} finally {
			$stream.Dispose()
		}
	} finally {
		$archive.Dispose()
	}
}

function Get-ExistingParent {
	param([Parameter(Mandatory)][string]$Path)
	$current = [System.IO.Path]::GetFullPath($Path)
	while (-not (Test-Path -LiteralPath $current)) {
		$parent = [System.IO.Directory]::GetParent($current)
		if ($null -eq $parent) {
			throw "Unable to resolve an existing parent for '$Path'."
		}
		$current = $parent.FullName
	}
	return $current
}

function Resolve-NdiRuntimePath {
	param([Parameter(Mandatory)]$Requirement)

	$directVariable = [string]$Requirement.directLibraryEnvironmentVariable
	$directPath = [Environment]::GetEnvironmentVariable($directVariable)
	if (-not [string]::IsNullOrWhiteSpace($directPath)) {
		try {
			$fullPath = [System.IO.Path]::GetFullPath($directPath)
			if (Test-Path -LiteralPath $fullPath -PathType Leaf) { return $fullPath }
		} catch { }
	}

	foreach ($directoryVariable in @($Requirement.runtimeDirectoryEnvironmentVariables)) {
		$directory = [Environment]::GetEnvironmentVariable([string]$directoryVariable)
		if ([string]::IsNullOrWhiteSpace($directory)) { continue }
		try {
			$candidate = [System.IO.Path]::GetFullPath((Join-Path $directory ([string]$Requirement.libraryFileName)))
			if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
		} catch { }
	}

	return $null
}

if ([string]::IsNullOrWhiteSpace($BundlePath)) {
	$candidate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
	if (Test-Path -LiteralPath (Join-Path $candidate "bundle-manifest.json") -PathType Leaf) {
		$BundlePath = $candidate
	} else {
		throw "BundlePath is required when preflight is not running from an unpacked offline bundle."
	}
}

$bundleInput = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition ((Test-Path -LiteralPath $bundleInput -PathType Container) -or (Test-Path -LiteralPath $bundleInput -PathType Leaf)) "Offline bundle was not found at '$bundleInput'."
if (Test-Path -LiteralPath $bundleInput -PathType Leaf) {
	Assert-Condition ([System.IO.Path]::GetExtension($bundleInput).Equals(".zip", [StringComparison]::OrdinalIgnoreCase)) "Offline bundle file must be a .zip archive."
}

$verifier = Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1"
Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Offline verifier was not found next to the preflight tool."
& $verifier -BundlePath $bundleInput

$manifest = Read-BundleJson -Bundle $bundleInput -RelativePath "bundle-manifest.json"
$requirements = Read-BundleJson -Bundle $bundleInput -RelativePath "metadata/runtime-requirements.json"

Assert-Condition ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) "Offline V1 preflight requires Windows."
Assert-Condition ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::X64) "Offline V1 preflight requires x64 Windows."
Assert-Condition ([string]$requirements.platform.osFamily -eq "Windows") "Bundle runtime requirements do not target Windows."
Assert-Condition ([string]$requirements.platform.architecture -eq "x64") "Bundle runtime requirements do not target x64."
Assert-Condition ([string]$requirements.platform.rid -eq "win-x64") "Bundle runtime requirements do not target win-x64."
Assert-Condition ($requirements.continuousInternetRequired -eq $false) "Bundle unexpectedly declares a continuous Internet requirement."

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
Assert-Condition ($null -ne $dotnet) "Required .NET host 'dotnet' was not found on PATH. The offline bundle is framework-dependent and does not silently download a runtime."
$runtimeOutput = @(& dotnet --list-runtimes 2>&1)
Assert-Condition ($LASTEXITCODE -eq 0) "Unable to query installed .NET runtimes."

$installedRuntimes = @(
	foreach ($line in $runtimeOutput) {
		$text = [string]$line
		if ($text -match '^([^\s]+)\s+([0-9]+\.[0-9]+\.[0-9]+(?:-[^\s]+)?)\s+\[') {
			$coreVersion = $matches[2].Split('-')[0]
			[pscustomobject]@{
				Name = $matches[1]
				Version = [Version]$coreVersion
				RawVersion = $matches[2]
			}
		}
	}
)

foreach ($requirement in @($requirements.dotnetRuntimes)) {
	$minimum = [Version]([string]$requirement.minimumVersion)
	$majorMinor = [string]$requirement.majorMinor
	$candidates = @($installedRuntimes | Where-Object {
		$_.Name -eq [string]$requirement.name -and
		"$($_.Version.Major).$($_.Version.Minor)" -eq $majorMinor -and
		$_.Version -ge $minimum
	})
	Assert-Condition ($candidates.Count -gt 0) "Required runtime '$($requirement.name)' $majorMinor >= $minimum is not installed."
	$selected = $candidates | Sort-Object Version -Descending | Select-Object -First 1
	Write-Host "Runtime PASS: $($requirement.name) $($selected.RawVersion)"
}

$ndiRequirements = @($requirements.externalProviderRuntimes | Where-Object { [string]$_.provider -eq "NDI" })
Assert-Condition ($ndiRequirements.Count -eq 1) "Offline runtime requirements must declare exactly one NDI external-runtime boundary."
$ndiRequirement = $ndiRequirements[0]
Assert-Condition ($ndiRequirement.requiredWhenConfigured -eq $true) "NDI runtime must be required when NDI output is configured."
Assert-Condition ($ndiRequirement.bundled -eq $false) "NDI runtime must remain external to the rtaime offline bundle."
Assert-Condition ([string]$ndiRequirement.redistributionStatus -eq "EXTERNAL_RUNTIME_NOT_BUNDLED") "NDI redistribution boundary is invalid."
Assert-Condition ([string]$ndiRequirement.interoperabilityStatus -eq "UNVERIFIED") "NDI interoperability must remain UNVERIFIED without declared environment evidence."

if ($RequireNdiRuntime) {
	$ndiRuntimePath = Resolve-NdiRuntimePath -Requirement $ndiRequirement
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($ndiRuntimePath)) "NDI output was requested but a compatible external NDI runtime was not found through the declared runtime environment variables."
	Write-Host "NDI runtime PASS: external runtime discovered"
}

if (-not [string]::IsNullOrWhiteSpace($InstallPath)) {
	$installFullPath = [System.IO.Path]::GetFullPath($InstallPath)
	$existingParent = Get-ExistingParent -Path $installFullPath
	$volumeRoot = [System.IO.Path]::GetPathRoot($existingParent)
	$drive = [System.IO.DriveInfo]::new($volumeRoot)
	$payloadBytes = [long]0
	foreach ($entry in @($manifest.payload)) {
		$payloadBytes += [long]$entry.size
	}
	$reserveBytes = 268435456L
	$requiredFreeBytes = ($payloadBytes * 2L) + $reserveBytes
	Assert-Condition ($drive.AvailableFreeSpace -ge $requiredFreeBytes) "Insufficient free space for verified staging installation. Required at least $requiredFreeBytes bytes; available $($drive.AvailableFreeSpace) bytes on '$volumeRoot'."
	Write-Host "Install target volume PASS: $($drive.AvailableFreeSpace) bytes free"
	Write-Host "Install staging requirement: $requiredFreeBytes bytes"
}

Write-Host "Offline deployment preflight PASS"
Write-Host "Bundle: $($manifest.bundleName)"
Write-Host "Platform: Windows x64 / win-x64"
Write-Host "Continuous Internet required: false"
Write-Host "Production Package activation performed: false"
