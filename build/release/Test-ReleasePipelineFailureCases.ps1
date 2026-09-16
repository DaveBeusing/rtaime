# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$CandidatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$verifier = Join-Path $PSScriptRoot "Test-ReleaseCandidate.ps1"

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
		throw "Negative release-pipeline case '$CaseName' unexpectedly succeeded."
	}
	Write-Host "Negative case PASS: $CaseName"
}

$candidateRoot = [System.IO.Path]::GetFullPath($CandidatePath)
if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) {
	throw "Release candidate directory was not found at '$candidateRoot'."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-release-pipeline-failure-{0}" -f [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
	$tamperedBundle = Join-Path $tempRoot "tampered-bundle"
	Copy-DirectoryContent -Source $candidateRoot -Destination $tamperedBundle
	$manifest = Get-Content -LiteralPath (Join-Path $tamperedBundle "release-candidate.json") -Raw | ConvertFrom-Json
	$bundlePath = Join-Path $tamperedBundle ([string]$manifest.bundle.fileName)
	$stream = [System.IO.File]::Open($bundlePath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
	try {
		$stream.WriteByte(0x7F)
	} finally {
		$stream.Dispose()
	}
	Invoke-ExpectedFailure -CaseName "tampered candidate bundle" -Arguments @("-NoProfile", "-File", $verifier, "-CandidatePath", $tamperedBundle)

	$tamperedManifest = Join-Path $tempRoot "tampered-manifest"
	Copy-DirectoryContent -Source $candidateRoot -Destination $tamperedManifest
	$manifestPath = Join-Path $tamperedManifest "release-candidate.json"
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	$manifest.product.version = "9.9.9"
	$json = $manifest | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -CaseName "tampered candidate manifest" -Arguments @("-NoProfile", "-File", $verifier, "-CandidatePath", $tamperedManifest)

	$injectedFile = Join-Path $tempRoot "injected-file"
	Copy-DirectoryContent -Source $candidateRoot -Destination $injectedFile
	[System.IO.File]::WriteAllText(
		(Join-Path $injectedFile "unexpected.txt"),
		"not declared",
		[System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -CaseName "unlisted candidate file" -Arguments @("-NoProfile", "-File", $verifier, "-CandidatePath", $injectedFile)

	$stableForgery = Join-Path $tempRoot "stable-with-test-key"
	Copy-DirectoryContent -Source $candidateRoot -Destination $stableForgery
	$manifestPath = Join-Path $stableForgery "release-candidate.json"
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	$manifest.channel = "STABLE"
	$manifest.product.releaseStage = "STABLE"
	$manifest.product.version = "1.0.0"
	$manifest.source.tag = "v1.0.0"
	$manifest.trust.signerClass = "TEST_EPHEMERAL"
	$manifest.trust.productionTrust = "UNVERIFIED"
	$manifest.publicationReadiness.status = "PASS"
	$json = $manifest | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	$hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
	[System.IO.File]::WriteAllText(
		(Join-Path $stableForgery "release-candidate.sha256"),
		"$hash  release-candidate.json$([Environment]::NewLine)",
		[System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -CaseName "stable candidate with test signing key" -Arguments @("-NoProfile", "-File", $verifier, "-CandidatePath", $stableForgery)

	Write-Host "Release pipeline failure qualification PASS"
} finally {
	if (Test-Path -LiteralPath $tempRoot) {
		Remove-Item -LiteralPath $tempRoot -Recurse -Force
	}
}
