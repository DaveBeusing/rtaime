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
	if (-not $Condition) { throw $Message }
}

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Assert-SafeAssetName {
	param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Description)
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($Name)) "$Description must not be empty."
	Assert-Condition (-not [System.IO.Path]::IsPathRooted($Name)) "$Description '$Name' must be a file name, not a rooted path."
	Assert-Condition ([System.IO.Path]::GetFileName($Name) -eq $Name) "$Description '$Name' must not contain path separators."
	Assert-Condition ($Name -notmatch '[\\/]') "$Description '$Name' must not contain path separators."
	Assert-Condition ($Name -notin @('.', '..')) "$Description '$Name' is invalid."
}

function Read-ZipEntryBytes {
	param([Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)][string]$EntryName)
	$archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
	try {
		$matches = @($archive.Entries | Where-Object { $_.FullName.Replace('\\', '/') -eq $EntryName })
		Assert-Condition ($matches.Count -eq 1) "Release bundle must contain exactly one '$EntryName' entry."
		$stream = $matches[0].Open()
		try {
			$memory = [System.IO.MemoryStream]::new()
			try {
				$stream.CopyTo($memory)
				return $memory.ToArray()
			} finally { $memory.Dispose() }
		} finally { $stream.Dispose() }
	} finally { $archive.Dispose() }
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

$bundleFileName = [string]$manifest.bundle.fileName
$bundleSidecarFileName = [string]$manifest.bundle.sidecarFileName
Assert-SafeAssetName -Name $bundleFileName -Description "Release candidate bundle file name"
Assert-SafeAssetName -Name $bundleSidecarFileName -Description "Release candidate bundle sidecar file name"
Assert-Condition ([System.IO.Path]::GetExtension($bundleFileName).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) "Release candidate bundle must be a ZIP file."
Assert-Condition ($bundleSidecarFileName -eq "$bundleFileName.sha256") "Release candidate bundle sidecar name must equal '<bundle>.sha256'."

$bundlePath = Join-Path $candidateRoot $bundleFileName
$bundleSidecarPath = Join-Path $candidateRoot $bundleSidecarFileName
Assert-Condition (Test-Path -LiteralPath $bundlePath -PathType Leaf) "Release candidate bundle is missing."
Assert-Condition (Test-Path -LiteralPath $bundleSidecarPath -PathType Leaf) "Release candidate bundle SHA-256 sidecar is missing."
Assert-Condition ((Get-Item -LiteralPath $bundlePath).Length -eq [long]$manifest.bundle.size) "Release candidate bundle size mismatch."
$bundleHash = Get-FileSha256Hex -Path $bundlePath
Assert-Condition ($bundleHash -eq ([string]$manifest.bundle.sha256).ToLowerInvariant()) "Release candidate bundle SHA-256 mismatch."
Assert-Condition ((Get-FileSha256Hex -Path $bundleSidecarPath) -eq ([string]$manifest.bundle.sidecarSha256).ToLowerInvariant()) "Release candidate bundle sidecar SHA-256 mismatch."
$bundleSidecar = [System.IO.File]::ReadAllText($bundleSidecarPath).Trim()
Assert-Condition ($bundleSidecar -eq "$bundleHash  $bundleFileName") "Release candidate bundle sidecar content mismatch."

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
$bundleVerifierCandidates = @(
	(Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1"),
	(Join-Path $repositoryRoot "build/release/Test-OfflineReleaseBundle.ps1")
)
$bundleVerifier = $bundleVerifierCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($bundleVerifier)) "Offline release bundle verifier is unavailable."
if ([string]$manifest.trust.productionTrust -eq "PASS" -and [string]$manifest.trust.signerClass -eq "EXTERNAL_CONTROLLED") {
	& $bundleVerifier -BundlePath $bundlePath -RequireTrustedProductionKey
} else {
	& $bundleVerifier -BundlePath $bundlePath
}

$bundleManifestBytes = Read-ZipEntryBytes -ArchivePath $bundlePath -EntryName "bundle-manifest.json"
$bundleManifest = [System.Text.Encoding]::UTF8.GetString($bundleManifestBytes) | ConvertFrom-Json
$releaseEvidenceBytes = Read-ZipEntryBytes -ArchivePath $bundlePath -EntryName "release/release-evidence.json"
$releaseEvidenceHash = Get-Sha256Hex -Bytes $releaseEvidenceBytes
Assert-Condition ([string]$bundleManifest.productName -eq [string]$manifest.product.name) "Candidate/bundle product identity mismatch."
Assert-Condition ([string]$bundleManifest.productVersion -eq [string]$manifest.product.version) "Candidate/bundle product version mismatch."
Assert-Condition ([string]$bundleManifest.releaseStage -eq [string]$manifest.product.releaseStage) "Candidate/bundle release-stage mismatch."
Assert-Condition ([string]$bundleManifest.sourceCommit -eq [string]$manifest.source.sourceCommit) "Candidate/bundle source commit mismatch."
Assert-Condition ([string]$bundleManifest.buildCommit -eq [string]$manifest.source.buildCommit) "Candidate/bundle build commit mismatch."
Assert-Condition ([string]$bundleManifest.buildId -eq [string]$manifest.source.buildId) "Candidate/bundle build identity mismatch."
Assert-Condition ([string]$bundleManifest.releaseTrust.releaseRecordId -eq [string]$manifest.releaseRecord.recordId) "Candidate/bundle Release Record id mismatch."
Assert-Condition ([string]$bundleManifest.releaseTrust.releaseKeyFingerprint -eq [string]$manifest.trust.keyFingerprint) "Candidate/bundle signing-key fingerprint mismatch."
Assert-Condition ([string]$bundleManifest.releaseTrust.releaseSignerClass -eq [string]$manifest.trust.signerClass) "Candidate/bundle signer-class mismatch."
Assert-Condition ($releaseEvidenceHash -eq ([string]$manifest.releaseRecord.releaseEvidenceSha256).ToLowerInvariant()) "Candidate/bundle Release Evidence SHA-256 mismatch."

$allowedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$allowedFiles.Add("release-candidate.json") | Out-Null
$allowedFiles.Add("release-candidate.sha256") | Out-Null
$allowedFiles.Add($bundleFileName) | Out-Null
$allowedFiles.Add($bundleSidecarFileName) | Out-Null
foreach ($file in @(Get-ChildItem -LiteralPath $candidateRoot -File)) {
	Assert-Condition ($allowedFiles.Contains($file.Name)) "Release candidate contains unlisted file '$($file.Name)'."
}
Assert-Condition (@(Get-ChildItem -LiteralPath $candidateRoot -Directory).Count -eq 0) "Release candidate must not contain nested directories."

Write-Host "Release candidate verification PASS"
Write-Host "Candidate id: $($manifest.candidateId)"
Write-Host "Channel: $($manifest.channel)"
Write-Host "Product version: $($manifest.product.version)"
Write-Host "Signed bundle identity binding: PASS"
Write-Host "Production signing trust: $($manifest.trust.productionTrust)"
Write-Host "Publication readiness: $($manifest.publicationReadiness.status)"
