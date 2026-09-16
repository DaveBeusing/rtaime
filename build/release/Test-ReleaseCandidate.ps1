# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$CandidatePath
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

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

$candidateRoot = [System.IO.Path]::GetFullPath($CandidatePath)
Assert-Condition (Test-Path -LiteralPath $candidateRoot -PathType Container) "Release candidate directory was not found at '$candidateRoot'."

$manifestPath = Join-Path $candidateRoot "release-candidate.json"
$sidecarPath = Join-Path $candidateRoot "release-candidate.sha256"
Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Release candidate manifest is missing."
Assert-Condition (Test-Path -LiteralPath $sidecarPath -PathType Leaf) "Release candidate manifest SHA-256 sidecar is missing."

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$manifest.schemaVersion -eq "1.0") "Unsupported release candidate manifest schema version."
Assert-Condition ([string]$manifest.product.name -eq "rtaime") "Unexpected release candidate product identity."
Assert-Condition ([string]$manifest.channel -in @("QUALIFICATION", "PREVIEW", "STABLE")) "Unexpected release channel '$($manifest.channel)'."
Assert-Condition ([string]$manifest.trust.signerClass -in @("TEST_EPHEMERAL", "EXTERNAL_CONTROLLED")) "Unexpected release candidate signer class."
Assert-Condition ([string]$manifest.trust.productionTrust -in @("PASS", "UNVERIFIED")) "Unexpected production trust status."
Assert-Condition ([string]$manifest.publicationReadiness.status -in @("PASS", "NOT_APPLICABLE", "UNVERIFIED")) "Unexpected publication-readiness status."

$manifestHash = Get-FileSha256Hex -Path $manifestPath
$sidecar = [System.IO.File]::ReadAllText($sidecarPath).Trim()
Assert-Condition ($sidecar -eq "$manifestHash  release-candidate.json") "Release candidate manifest SHA-256 sidecar mismatch."

$bundlePath = Join-Path $candidateRoot ([string]$manifest.bundle.fileName)
$bundleSidecarPath = Join-Path $candidateRoot ([string]$manifest.bundle.sidecarFileName)
Assert-Condition (Test-Path -LiteralPath $bundlePath -PathType Leaf) "Release candidate bundle is missing."
Assert-Condition (Test-Path -LiteralPath $bundleSidecarPath -PathType Leaf) "Release candidate bundle SHA-256 sidecar is missing."
Assert-Condition ((Get-Item -LiteralPath $bundlePath).Length -eq [long]$manifest.bundle.size) "Release candidate bundle size mismatch."
$bundleHash = Get-FileSha256Hex -Path $bundlePath
Assert-Condition ($bundleHash -eq ([string]$manifest.bundle.sha256).ToLowerInvariant()) "Release candidate bundle SHA-256 mismatch."
Assert-Condition ((Get-FileSha256Hex -Path $bundleSidecarPath) -eq ([string]$manifest.bundle.sidecarSha256).ToLowerInvariant()) "Release candidate bundle sidecar SHA-256 mismatch."
$bundleSidecar = [System.IO.File]::ReadAllText($bundleSidecarPath).Trim()
Assert-Condition ($bundleSidecar -eq "$bundleHash  $([System.IO.Path]::GetFileName($bundlePath))") "Release candidate bundle sidecar content mismatch."

if ([string]$manifest.channel -eq "QUALIFICATION") {
	Assert-Condition ($null -eq $manifest.source.tag) "Qualification release candidate must not carry a Git tag."
	Assert-Condition ([string]$manifest.product.releaseStage -eq "DEV") "Qualification release candidate must use DEV stage."
	Assert-Condition ([string]$manifest.trust.signerClass -eq "TEST_EPHEMERAL") "Qualification release candidate must use TEST_EPHEMERAL signing."
	Assert-Condition ([string]$manifest.publicationReadiness.status -eq "NOT_APPLICABLE") "Qualification release candidate must not claim publication readiness."
} elseif ([string]$manifest.channel -eq "PREVIEW") {
	Assert-Condition ([string]$manifest.product.releaseStage -eq "PREVIEW") "Preview release candidate must use PREVIEW stage."
	Assert-Condition ([string]$manifest.source.tag -match '^v[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+$') "Preview release candidate has invalid tag."
	Assert-Condition ([string]$manifest.source.tag -eq "v$($manifest.product.version)") "Preview tag and product version differ."
	if ([string]$manifest.publicationReadiness.status -eq "PASS") {
		Assert-Condition ([string]$manifest.trust.signerClass -eq "EXTERNAL_CONTROLLED") "Preview publication readiness PASS requires EXTERNAL_CONTROLLED signing."
		Assert-Condition ([string]$manifest.trust.productionTrust -eq "PASS") "Preview publication readiness PASS requires production trust PASS."
	} else {
		Assert-Condition ([string]$manifest.publicationReadiness.status -eq "UNVERIFIED") "Preview candidate must be PASS or UNVERIFIED for publication readiness."
	}
} elseif ([string]$manifest.channel -eq "STABLE") {
	Assert-Condition ([string]$manifest.product.releaseStage -eq "STABLE") "Stable release candidate must use STABLE stage."
	Assert-Condition ([string]$manifest.source.tag -match '^v[0-9]+\.[0-9]+\.[0-9]+$') "Stable release candidate has invalid tag."
	Assert-Condition ([string]$manifest.source.tag -eq "v$($manifest.product.version)") "Stable tag and product version differ."
	Assert-Condition ([string]$manifest.trust.signerClass -eq "EXTERNAL_CONTROLLED") "Stable release candidate must use EXTERNAL_CONTROLLED signing."
	Assert-Condition ([string]$manifest.trust.productionTrust -eq "PASS") "Stable release candidate requires active production signing-key trust."
	Assert-Condition ([string]$manifest.publicationReadiness.status -eq "PASS") "Stable release candidate requires publication-readiness PASS."
}

Assert-Condition ([string]$manifest.source.sourceCommit -eq [string]$manifest.source.buildCommit) "Release candidate source/build commit identities differ."
Assert-Condition ([string]$manifest.source.sourceCommit -match '^[0-9a-f]{40,64}$') "Release candidate source commit identity is invalid."
Assert-Condition ([string]$manifest.releaseRecord.recordId -match '^[0-9a-f]{64}$') "Release candidate Release Record id is invalid."
Assert-Condition ([string]$manifest.releaseRecord.releaseEvidenceSha256 -match '^[0-9a-f]{64}$') "Release candidate Release Evidence hash is invalid."
Assert-Condition ([string]$manifest.trust.keyFingerprint -match '^[0-9a-f]{64}$') "Release candidate signing-key fingerprint is invalid."

$candidateCanonical = @(
	"rtaime.release-candidate.v1",
	[string]$manifest.channel,
	[string]$manifest.product.version,
	$(if ($null -eq $manifest.source.tag) { "" } else { [string]$manifest.source.tag }),
	[string]$manifest.source.sourceCommit,
	[string]$manifest.releaseRecord.recordId,
	[string]$manifest.trust.keyFingerprint,
	$bundleHash
) -join "`n"
$expectedCandidateId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($candidateCanonical))
Assert-Condition ([string]$manifest.candidateId -eq $expectedCandidateId) "Release candidate content id mismatch."

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$bundleVerifier = Join-Path $repositoryRoot "build/release/Test-OfflineReleaseBundle.ps1"
if ([string]$manifest.trust.productionTrust -eq "PASS" -and [string]$manifest.trust.signerClass -eq "EXTERNAL_CONTROLLED") {
	& $bundleVerifier -BundlePath $bundlePath -RequireTrustedProductionKey
} else {
	& $bundleVerifier -BundlePath $bundlePath
}

$allowedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$allowedFiles.Add("release-candidate.json") | Out-Null
$allowedFiles.Add("release-candidate.sha256") | Out-Null
$allowedFiles.Add([string]$manifest.bundle.fileName) | Out-Null
$allowedFiles.Add([string]$manifest.bundle.sidecarFileName) | Out-Null
foreach ($file in @(Get-ChildItem -LiteralPath $candidateRoot -File)) {
	Assert-Condition ($allowedFiles.Contains($file.Name)) "Release candidate contains unlisted file '$($file.Name)'."
}
Assert-Condition (@(Get-ChildItem -LiteralPath $candidateRoot -Directory).Count -eq 0) "Release candidate must not contain nested directories."

Write-Host "Release candidate verification PASS"
Write-Host "Candidate id: $($manifest.candidateId)"
Write-Host "Channel: $($manifest.channel)"
Write-Host "Product version: $($manifest.product.version)"
Write-Host "Production signing trust: $($manifest.trust.productionTrust)"
Write-Host "Publication readiness: $($manifest.publicationReadiness.status)"
