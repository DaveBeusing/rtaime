# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$CandidatePath,
	[Parameter(Mandatory)]
	[string]$PublicationPath,
	[string]$GitHubReleaseMetadataPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
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

$candidateRoot = [System.IO.Path]::GetFullPath($CandidatePath)
$publicationRoot = [System.IO.Path]::GetFullPath($PublicationPath)
Assert-Condition (Test-Path -LiteralPath $candidateRoot -PathType Container) "Candidate directory was not found."
Assert-Condition (Test-Path -LiteralPath $publicationRoot -PathType Container) "Publication directory was not found."

$candidateVerifierCandidates = @(
	(Join-Path $PSScriptRoot 'Test-ReleaseCandidate.ps1'),
	(Join-Path $PSScriptRoot '../release/Test-ReleaseCandidate.ps1')
)
$candidateVerifier = $candidateVerifierCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($candidateVerifier)) "Release Candidate verifier is unavailable."
& $candidateVerifier -CandidatePath $candidateRoot

$candidateManifestPath = Join-Path $candidateRoot 'release-candidate.json'
$candidateSidecarPath = Join-Path $candidateRoot 'release-candidate.sha256'
$candidate = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$candidate.channel -in @('PREVIEW', 'STABLE')) "Downloaded update candidate must be PREVIEW or STABLE."
Assert-Condition ([string]$candidate.product.name -eq 'rtaime') "Unexpected product identity."
Assert-Condition ([string]$candidate.trust.signerClass -eq 'EXTERNAL_CONTROLLED') "Downloaded update candidate must use EXTERNAL_CONTROLLED signing."
Assert-Condition ([string]$candidate.trust.productionTrust -eq 'PASS') "Downloaded update candidate requires production trust PASS."
Assert-Condition ([string]$candidate.publicationReadiness.status -eq 'PASS') "Downloaded update candidate requires publication readiness PASS."
Assert-Condition ([string]$candidate.source.tag -eq "v$($candidate.product.version)") "Candidate tag/version mismatch."

$candidateManifestHash = Get-FileSha256Hex -Path $candidateManifestPath
Assert-Condition ([System.IO.File]::ReadAllText($candidateSidecarPath).Trim() -eq "$candidateManifestHash  release-candidate.json") "Candidate manifest sidecar mismatch."

$bundlePath = Join-Path $candidateRoot ([string]$candidate.bundle.fileName)
$bundleSidecarPath = Join-Path $candidateRoot ([string]$candidate.bundle.sidecarFileName)
$bundleHash = Get-FileSha256Hex -Path $bundlePath
Assert-Condition ($bundleHash -eq ([string]$candidate.bundle.sha256).ToLowerInvariant()) "Candidate bundle SHA-256 mismatch."
Assert-Condition ([System.IO.File]::ReadAllText($bundleSidecarPath).Trim() -eq "$bundleHash  $([System.IO.Path]::GetFileName($bundlePath))") "Candidate bundle sidecar mismatch."

$publicationManifestPath = Join-Path $publicationRoot 'release-publication.json'
$publicationSidecarPath = Join-Path $publicationRoot 'release-publication.sha256'
Assert-Condition (Test-Path -LiteralPath $publicationManifestPath -PathType Leaf) "release-publication.json is missing."
Assert-Condition (Test-Path -LiteralPath $publicationSidecarPath -PathType Leaf) "release-publication.sha256 is missing."
$publication = Get-Content -LiteralPath $publicationManifestPath -Raw | ConvertFrom-Json
$publicationHash = Get-FileSha256Hex -Path $publicationManifestPath
Assert-Condition ([System.IO.File]::ReadAllText($publicationSidecarPath).Trim() -eq "$publicationHash  release-publication.json") "Publication manifest sidecar mismatch."

Assert-Condition ([string]$publication.schemaVersion -eq '1.0') "Unsupported publication schema version."
Assert-Condition ([string]$publication.repository -eq 'DaveBeusing/rtaime') "Unexpected publication repository."
Assert-Condition ([string]$publication.channel -eq [string]$candidate.channel) "Publication/candidate channel mismatch."
Assert-Condition ([string]$publication.product.version -eq [string]$candidate.product.version) "Publication/candidate version mismatch."
Assert-Condition ([string]$publication.product.releaseStage -eq [string]$candidate.product.releaseStage) "Publication/candidate release-stage mismatch."
Assert-Condition ([string]$publication.source.tag -eq [string]$candidate.source.tag) "Publication/candidate tag mismatch."
Assert-Condition ([string]$publication.source.sourceCommit -eq [string]$candidate.source.sourceCommit) "Publication/candidate source mismatch."
Assert-Condition ([string]$publication.candidate.candidateId -eq [string]$candidate.candidateId) "Publication/candidate identity mismatch."
Assert-Condition ([string]$publication.candidate.manifestSha256 -eq $candidateManifestHash) "Publication candidate hash mismatch."
Assert-Condition ([string]$publication.bundle.sha256 -eq $bundleHash) "Publication bundle hash mismatch."
Assert-Condition ([string]$publication.trust.signerClass -eq 'EXTERNAL_CONTROLLED') "Publication signer class is invalid."
Assert-Condition ([string]$publication.trust.productionTrust -eq 'PASS') "Publication production trust is not PASS."
Assert-Condition ([string]$publication.trust.keyFingerprint -eq [string]$candidate.trust.keyFingerprint) "Publication/Candidate key fingerprint mismatch."
Assert-Condition ([string]$publication.trust.authoritativeTrustSource -eq 'RELEASE_CANDIDATE') "Publication trust source must remain RELEASE_CANDIDATE."
Assert-Condition ($publication.githubRelease.assetOverwriteAllowed -eq $false) "Publication allows asset overwrite."
Assert-Condition ($publication.githubRelease.rebuildAllowed -eq $false) "Publication allows rebuild."

$descriptorName = if ([string]$candidate.channel -eq 'PREVIEW') { 'rtaime-channel-preview.json' } else { 'rtaime-channel-stable.json' }
$descriptorPath = Join-Path $publicationRoot $descriptorName
$descriptorSidecarPath = Join-Path $publicationRoot "$descriptorName.sha256"
$notesPath = Join-Path $publicationRoot 'release-notes.md'
Assert-Condition (Test-Path -LiteralPath $descriptorPath -PathType Leaf) "Channel descriptor is missing."
Assert-Condition (Test-Path -LiteralPath $descriptorSidecarPath -PathType Leaf) "Channel descriptor sidecar is missing."
Assert-Condition (Test-Path -LiteralPath $notesPath -PathType Leaf) "Release notes are missing."
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
$descriptorHash = Get-FileSha256Hex -Path $descriptorPath
Assert-Condition ([System.IO.File]::ReadAllText($descriptorSidecarPath).Trim() -eq "$descriptorHash  $descriptorName") "Channel descriptor sidecar mismatch."
Assert-Condition ([string]$descriptor.discoveryTrust.status -eq 'POINTER_ONLY') "Channel descriptor must remain discovery-only."
Assert-Condition ([string]$descriptor.repository -eq [string]$publication.repository) "Descriptor repository mismatch."
Assert-Condition ([string]$descriptor.channel -eq [string]$candidate.channel) "Descriptor channel mismatch."
Assert-Condition ([string]$descriptor.release.tag -eq [string]$candidate.source.tag) "Descriptor tag mismatch."
Assert-Condition ([string]$descriptor.candidate.candidateId -eq [string]$candidate.candidateId) "Descriptor candidate identity mismatch."
Assert-Condition ([string]$descriptor.candidate.manifestSha256 -eq $candidateManifestHash) "Descriptor candidate hash mismatch."
Assert-Condition ([string]$descriptor.publication.publicationId -eq [string]$publication.publicationId) "Descriptor publication identity mismatch."
Assert-Condition ([string]$descriptor.publication.manifestSha256 -eq $publicationHash) "Descriptor publication hash mismatch."
Assert-Condition ([string]$descriptor.bundle.sha256 -eq $bundleHash) "Descriptor bundle hash mismatch."
Assert-Condition ([string]$publication.releaseNotes.sha256 -eq (Get-FileSha256Hex $notesPath)) "Release notes hash mismatch."

$expectedPublicationId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes((@(
	'rtaime.release-publication.v1',
	[string]$publication.repository,
	[string]$publication.channel,
	[string]$publication.product.version,
	[string]$publication.source.tag,
	[string]$publication.source.sourceCommit,
	[string]$publication.candidate.candidateId,
	$candidateManifestHash,
	$bundleHash
) -join "`n")))
Assert-Condition ([string]$publication.publicationId -eq $expectedPublicationId) "Publication content id mismatch."

if (-not [string]::IsNullOrWhiteSpace($GitHubReleaseMetadataPath)) {
	$metadataPath = [System.IO.Path]::GetFullPath($GitHubReleaseMetadataPath)
	Assert-Condition (Test-Path -LiteralPath $metadataPath -PathType Leaf) "GitHub release metadata is missing."
	$release = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
	Assert-Condition ($release.draft -eq $false) "Draft releases are not update sources."
	Assert-Condition ([string]$release.tag_name -eq [string]$candidate.source.tag) "GitHub release tag mismatch."
	Assert-Condition ([string]$release.html_url -eq [string]$publication.githubRelease.url) "GitHub release URL mismatch."
	if ([string]$candidate.channel -eq 'PREVIEW') {
		Assert-Condition ($release.prerelease -eq $true) "Preview update source must be a prerelease."
	} else {
		Assert-Condition ($release.prerelease -eq $false) "Stable update source must not be a prerelease."
	}
	$assetNames = @($release.assets | ForEach-Object { [string]$_.name })
	foreach ($requiredAsset in @('release-candidate.json', 'release-candidate.sha256', [string]$candidate.bundle.fileName, [string]$candidate.bundle.sidecarFileName, 'release-publication.json', 'release-publication.sha256', 'release-notes.md', $descriptorName, "$descriptorName.sha256")) {
		Assert-Condition ($assetNames -contains $requiredAsset) "GitHub release is missing required asset '$requiredAsset'."
	}
}

Write-Host "Downloaded release verification PASS"
Write-Host "Channel: $($candidate.channel)"
Write-Host "Version: $($candidate.product.version)"
Write-Host "Candidate id: $($candidate.candidateId)"
Write-Host "Publication id: $($publication.publicationId)"
Write-Host "Signed bundle identity binding: PASS"
Write-Host "Production trust: PASS"

return [ordered]@{
	candidate = $candidate
	publication = $publication
	descriptor = $descriptor
	bundlePath = $bundlePath
}
