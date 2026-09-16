# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$CandidatePath,
	[Parameter(Mandatory)]
	[string]$PublicationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

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
$publicationRoot = [System.IO.Path]::GetFullPath($PublicationPath)
Assert-Condition (Test-Path -LiteralPath $candidateRoot -PathType Container) "Release candidate directory was not found at '$candidateRoot'."
Assert-Condition (Test-Path -LiteralPath $publicationRoot -PathType Container) "Release publication directory was not found at '$publicationRoot'."

& (Join-Path $PSScriptRoot "Test-ReleaseCandidate.ps1") -CandidatePath $candidateRoot

$candidatePath = Join-Path $candidateRoot "release-candidate.json"
$candidate = Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json
$publicationPath = Join-Path $publicationRoot "release-publication.json"
$publicationSidecarPath = Join-Path $publicationRoot "release-publication.sha256"
Assert-Condition (Test-Path -LiteralPath $publicationPath -PathType Leaf) "Release publication manifest is missing."
Assert-Condition (Test-Path -LiteralPath $publicationSidecarPath -PathType Leaf) "Release publication SHA-256 sidecar is missing."
$publication = Get-Content -LiteralPath $publicationPath -Raw | ConvertFrom-Json

Assert-Condition ([string]$publication.schemaVersion -eq "1.0") "Unsupported release publication schema version."
Assert-Condition ([string]$publication.channel -eq [string]$candidate.channel) "Publication/candidate channel mismatch."
Assert-Condition ([string]$publication.channel -in @("PREVIEW", "STABLE")) "Only PREVIEW or STABLE may have publication metadata."
Assert-Condition ([string]$publication.product.name -eq "rtaime") "Unexpected publication product identity."
Assert-Condition ([string]$publication.product.version -eq [string]$candidate.product.version) "Publication/candidate version mismatch."
Assert-Condition ([string]$publication.product.releaseStage -eq [string]$candidate.product.releaseStage) "Publication/candidate release-stage mismatch."
Assert-Condition ([string]$publication.source.tag -eq [string]$candidate.source.tag) "Publication/candidate tag mismatch."
Assert-Condition ([string]$publication.source.sourceCommit -eq [string]$candidate.source.sourceCommit) "Publication/candidate source commit mismatch."
Assert-Condition ([string]$publication.candidate.candidateId -eq [string]$candidate.candidateId) "Publication/candidate id mismatch."
Assert-Condition ([string]$publication.trust.signerClass -eq "EXTERNAL_CONTROLLED") "Published releases require EXTERNAL_CONTROLLED signing."
Assert-Condition ([string]$publication.trust.productionTrust -eq "PASS") "Published releases require production trust PASS."
Assert-Condition ([string]$publication.trust.keyFingerprint -eq [string]$candidate.trust.keyFingerprint) "Publication/candidate signing-key fingerprint mismatch."
Assert-Condition ([string]$publication.trust.authoritativeTrustSource -eq "RELEASE_CANDIDATE") "Publication must preserve Release Candidate as the authoritative trust source."
Assert-Condition ($publication.githubRelease.assetOverwriteAllowed -eq $false) "Published release assets must be immutable/no-overwrite."
Assert-Condition ($publication.githubRelease.rebuildAllowed -eq $false) "Publication must not allow rebuilds."
Assert-Condition ([string]$publication.githubRelease.existingReleaseAction -eq "FAIL") "Publication must fail if the GitHub Release already exists."

if ([string]$publication.channel -eq "PREVIEW") {
	Assert-Condition ($publication.githubRelease.prerelease -eq $true) "PREVIEW publication must be a GitHub prerelease."
	Assert-Condition ($publication.githubRelease.makeLatest -eq $false) "PREVIEW publication must not become latest Stable."
} else {
	Assert-Condition ($publication.githubRelease.prerelease -eq $false) "STABLE publication must not be a GitHub prerelease."
	Assert-Condition ($publication.githubRelease.makeLatest -eq $true) "STABLE publication must become latest."
	Assert-Condition ([string]$candidate.publicationReadiness.status -eq "PASS") "STABLE publication requires candidate publication readiness PASS."
}

$publicationHash = Get-FileSha256Hex -Path $publicationPath
$publicationSidecar = [System.IO.File]::ReadAllText($publicationSidecarPath).Trim()
Assert-Condition ($publicationSidecar -eq "$publicationHash  release-publication.json") "Release publication SHA-256 sidecar mismatch."

$candidateManifestHash = Get-FileSha256Hex -Path $candidatePath
Assert-Condition ([string]$publication.candidate.manifestSha256 -eq $candidateManifestHash) "Publication candidate-manifest SHA-256 mismatch."
Assert-Condition ([string]$publication.candidate.sidecarSha256 -eq (Get-FileSha256Hex -Path (Join-Path $candidateRoot "release-candidate.sha256"))) "Publication candidate-sidecar SHA-256 mismatch."
Assert-Condition ([string]$publication.bundle.sha256 -eq (Get-FileSha256Hex -Path (Join-Path $candidateRoot ([string]$candidate.bundle.fileName)))) "Publication bundle SHA-256 mismatch."
Assert-Condition ([string]$publication.bundle.sidecarSha256 -eq (Get-FileSha256Hex -Path (Join-Path $candidateRoot ([string]$candidate.bundle.sidecarFileName)))) "Publication bundle-sidecar SHA-256 mismatch."

$notesPath = Join-Path $publicationRoot ([string]$publication.releaseNotes.fileName)
Assert-Condition (Test-Path -LiteralPath $notesPath -PathType Leaf) "Release notes file is missing."
Assert-Condition ([string]$publication.releaseNotes.sha256 -eq (Get-FileSha256Hex -Path $notesPath)) "Release notes SHA-256 mismatch."

$publicationCanonical = @(
	"rtaime.release-publication.v1",
	[string]$publication.repository,
	[string]$publication.channel,
	[string]$publication.product.version,
	[string]$publication.source.tag,
	[string]$publication.source.sourceCommit,
	[string]$publication.candidate.candidateId,
	$candidateManifestHash,
	[string]$publication.bundle.sha256
) -join "`n"
$expectedPublicationId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($publicationCanonical))
Assert-Condition ([string]$publication.publicationId -eq $expectedPublicationId) "Release publication content id mismatch."

$descriptorName = if ([string]$publication.channel -eq "PREVIEW") { "rtaime-channel-preview.json" } else { "rtaime-channel-stable.json" }
$descriptorPath = Join-Path $publicationRoot $descriptorName
$descriptorSidecarPath = Join-Path $publicationRoot "$descriptorName.sha256"
Assert-Condition (Test-Path -LiteralPath $descriptorPath -PathType Leaf) "Channel descriptor '$descriptorName' is missing."
Assert-Condition (Test-Path -LiteralPath $descriptorSidecarPath -PathType Leaf) "Channel descriptor sidecar is missing."
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$descriptor.schemaVersion -eq "1.0") "Unsupported channel descriptor schema version."
Assert-Condition ([string]$descriptor.channel -eq [string]$publication.channel) "Channel descriptor channel mismatch."
Assert-Condition ([string]$descriptor.repository -eq [string]$publication.repository) "Channel descriptor repository mismatch."
Assert-Condition ([string]$descriptor.product.version -eq [string]$publication.product.version) "Channel descriptor product version mismatch."
Assert-Condition ([string]$descriptor.release.tag -eq [string]$publication.source.tag) "Channel descriptor tag mismatch."
Assert-Condition ([string]$descriptor.release.url -eq [string]$publication.githubRelease.url) "Channel descriptor release URL mismatch."
Assert-Condition ([string]$descriptor.candidate.candidateId -eq [string]$candidate.candidateId) "Channel descriptor candidate id mismatch."
Assert-Condition ([string]$descriptor.candidate.manifestSha256 -eq $candidateManifestHash) "Channel descriptor candidate hash mismatch."
Assert-Condition ([string]$descriptor.publication.publicationId -eq [string]$publication.publicationId) "Channel descriptor publication id mismatch."
Assert-Condition ([string]$descriptor.publication.manifestSha256 -eq $publicationHash) "Channel descriptor publication hash mismatch."
Assert-Condition ([string]$descriptor.bundle.sha256 -eq [string]$candidate.bundle.sha256) "Channel descriptor bundle hash mismatch."
Assert-Condition ([string]$descriptor.discoveryTrust.status -eq "POINTER_ONLY") "Channel descriptor must not represent itself as a trust anchor."
$descriptorHash = Get-FileSha256Hex -Path $descriptorPath
$descriptorSidecar = [System.IO.File]::ReadAllText($descriptorSidecarPath).Trim()
Assert-Condition ($descriptorSidecar -eq "$descriptorHash  $descriptorName") "Channel descriptor SHA-256 sidecar mismatch."

$tagCommit = (& git -C $repositoryRoot rev-parse "$($publication.source.tag)^{commit}" 2>$null).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $tagCommit.ToLowerInvariant() -eq [string]$publication.source.sourceCommit) "Publication tag/source commit binding failed."

$allowed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @("release-notes.md", "release-publication.json", "release-publication.sha256", $descriptorName, "$descriptorName.sha256")) {
	$allowed.Add($name) | Out-Null
}
foreach ($file in @(Get-ChildItem -LiteralPath $publicationRoot -File)) {
	Assert-Condition ($allowed.Contains($file.Name)) "Release publication contains unlisted file '$($file.Name)'."
}
Assert-Condition (@(Get-ChildItem -LiteralPath $publicationRoot -Directory).Count -eq 0) "Release publication directory must not contain nested directories."

Write-Host "Release publication verification PASS"
Write-Host "Publication id: $($publication.publicationId)"
Write-Host "Channel: $($publication.channel)"
Write-Host "Version: $($publication.product.version)"
Write-Host "Tag: $($publication.source.tag)"
Write-Host "Production trust: $($publication.trust.productionTrust)"
Write-Host "Discovery trust anchor: false"
