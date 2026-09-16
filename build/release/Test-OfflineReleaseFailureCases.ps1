# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundleDirectory,
	[Parameter(Mandatory)]
	[string]$ArchivePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bundleRoot = [System.IO.Path]::GetFullPath($BundleDirectory)
$archive = [System.IO.Path]::GetFullPath($ArchivePath)
$verifier = Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1"
$installer = Join-Path $PSScriptRoot "Install-OfflineRelease.ps1"

if (-not (Test-Path -LiteralPath $bundleRoot -PathType Container)) {
	throw "Bundle directory was not found at '$bundleRoot'."
}
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
	throw "Bundle archive was not found at '$archive'."
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
		$parent = Split-Path -Parent $target
		if (-not (Test-Path -LiteralPath $parent)) {
			New-Item -ItemType Directory -Path $parent -Force | Out-Null
		}
		Copy-Item -LiteralPath $file.FullName -Destination $target -Force
	}
}

function Invoke-NativePowerShell {
	param([Parameter(Mandatory)][string[]]$Arguments)
	$nativePreferenceVariable = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
	$previousNativePreference = $null
	if ($null -ne $nativePreferenceVariable) {
		$previousNativePreference = [bool]$nativePreferenceVariable.Value
		Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
	}
	try {
		& pwsh @Arguments *> $null
		return $LASTEXITCODE
	} finally {
		if ($null -ne $nativePreferenceVariable) {
			Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $previousNativePreference
		}
	}
}

function Invoke-ExpectedFailure {
	param(
		[Parameter(Mandatory)][string]$CaseName,
		[Parameter(Mandatory)][string[]]$Arguments
	)
	$exitCode = Invoke-NativePowerShell -Arguments $Arguments
	if ($exitCode -eq 0) {
		throw "Negative offline-deployment case '$CaseName' unexpectedly succeeded."
	}
	Write-Host "Negative case PASS: $CaseName"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-offline-failure-{0}" -f [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
	$tamperedProduct = Join-Path $tempRoot "tampered-product"
	Copy-DirectoryContent -Source $bundleRoot -Destination $tamperedProduct
	$productFile = Get-ChildItem -LiteralPath (Join-Path $tamperedProduct "product") -File -Recurse | Select-Object -First 1
	if ($null -eq $productFile) {
		throw "No product file was available for tamper qualification."
	}
	$stream = [System.IO.File]::Open($productFile.FullName, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
	try {
		$stream.WriteByte(0x7F)
	} finally {
		$stream.Dispose()
	}
	Invoke-ExpectedFailure -CaseName "tampered product payload" -Arguments @("-NoProfile", "-File", $verifier, "-BundlePath", $tamperedProduct)

	$tamperedManifest = Join-Path $tempRoot "tampered-manifest"
	Copy-DirectoryContent -Source $bundleRoot -Destination $tamperedManifest
	$manifestPath = Join-Path $tamperedManifest "bundle-manifest.json"
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	$manifest.productVersion = "tampered-version"
	$json = $manifest | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -CaseName "tampered bundle manifest" -Arguments @("-NoProfile", "-File", $verifier, "-BundlePath", $tamperedManifest)

	$injectedPayload = Join-Path $tempRoot "injected-payload"
	Copy-DirectoryContent -Source $bundleRoot -Destination $injectedPayload
	[System.IO.File]::WriteAllText((Join-Path $injectedPayload "unexpected.txt"), "unlisted payload", [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -CaseName "unlisted injected payload" -Arguments @("-NoProfile", "-File", $verifier, "-BundlePath", $injectedPayload)

	$traversalArchive = Join-Path $tempRoot "traversal.zip"
	$zip = [System.IO.Compression.ZipFile]::Open($traversalArchive, [System.IO.Compression.ZipArchiveMode]::Create)
	try {
		$entry = $zip.CreateEntry("../escape.txt")
		$entryStream = $entry.Open()
		try {
			$writer = [System.IO.StreamWriter]::new($entryStream, [System.Text.UTF8Encoding]::new($false), 1024, $true)
			try {
				$writer.Write("must never extract")
				$writer.Flush()
			} finally {
				$writer.Dispose()
			}
		} finally {
			$entryStream.Dispose()
		}
	} finally {
		$zip.Dispose()
	}
	Invoke-ExpectedFailure -CaseName "archive path traversal" -Arguments @("-NoProfile", "-File", $verifier, "-BundlePath", $traversalArchive)

	$truncatedArchive = Join-Path $tempRoot "truncated.zip"
	Copy-Item -LiteralPath $archive -Destination $truncatedArchive -Force
	$archiveLength = (Get-Item -LiteralPath $truncatedArchive).Length
	$truncateTo = [Math]::Max(1L, [long]($archiveLength / 2L))
	$truncateStream = [System.IO.File]::Open($truncatedArchive, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
	try {
		$truncateStream.SetLength($truncateTo)
	} finally {
		$truncateStream.Dispose()
	}
	Invoke-ExpectedFailure -CaseName "truncated archive" -Arguments @("-NoProfile", "-File", $verifier, "-BundlePath", $truncatedArchive)

	$nonEmptyTarget = Join-Path $tempRoot "non-empty-install"
	New-Item -ItemType Directory -Path $nonEmptyTarget -Force | Out-Null
	$sentinelPath = Join-Path $nonEmptyTarget "sentinel.txt"
	[System.IO.File]::WriteAllText($sentinelPath, "preserve", [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -CaseName "non-empty install target" -Arguments @("-NoProfile", "-File", $installer, "-BundlePath", $archive, "-InstallPath", $nonEmptyTarget)
	if (([System.IO.File]::ReadAllText($sentinelPath)) -ne "preserve") {
		throw "Rejected installation modified existing target content."
	}

	$cleanTarget = Join-Path $tempRoot "clean-install"
	$exitCode = Invoke-NativePowerShell -Arguments @("-NoProfile", "-File", $installer, "-BundlePath", $archive, "-InstallPath", $cleanTarget)
	if ($exitCode -ne 0) {
		throw "Positive clean offline installation qualification failed."
	}
	$exitCode = Invoke-NativePowerShell -Arguments @("-NoProfile", "-File", (Join-Path $cleanTarget "tools/Test-OfflineReleaseBundle.ps1"), "-BundlePath", $cleanTarget)
	if ($exitCode -ne 0) {
		throw "Installed offline bundle failed post-install verification."
	}
	Write-Host "Positive case PASS: verified clean offline installation"
	Write-Host "Offline deployment failure qualification PASS"
} finally {
	if (Test-Path -LiteralPath $tempRoot) {
		Remove-Item -LiteralPath $tempRoot -Recurse -Force
	}
}
