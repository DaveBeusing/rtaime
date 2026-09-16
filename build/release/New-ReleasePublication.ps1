# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$CandidatePath,
	[string]$Repository = $env:GITHUB_REPOSITORY,
	[string]$OutputPath = "artifacts/release-publication"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) {
		throw $Message
	}
}

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

$candidateRoot = [System.IO.Path]::GetFullPath($CandidatePath)
Assert-Condition (Test-Path -LiteralPath $candidateRoot -PathType Container) "Release candidate directory was not found at '$candidateRoot'."
Assert-Condition (-not [string]::IsNullOrWhiteSpace($Repository)) "Repository identity is required."
Assert-Condition ($Repository -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') "Repository identity '$Repository' must use owner/name format."

& (Join-Path $PSScriptRoot "Test-ReleaseCandidate.ps1") -CandidatePath $candidateRoot

$candidateManifestPath = Join-Path $candidateRoot "release-candidate.json"
$candidate = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json
$channel = [string]$candidate.channel

$policyPath = Join-Path $PSScriptRoot "release-publication-policy.json"
Assert-Condition (Test-Path -LiteralPath $policyPath -PathType Leaf) "Release publication policy is missing."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Unsupported release publication policy schema version."
$channelProperty = $policy.channels.PSObject.Properties[$channel]
Assert-Condition ($null -ne $channelProperty) "Publication policy does not declare channel '$channel'."
$channelPolicy = $channelProperty.Value
Assert-Condition ($channelPolicy.publish -eq $true) "Release channel '$channel' is not publishable."
Assert-Condition ($channel -in @("PREVIEW", "STABLE")) "Only PREVIEW and STABLE release candidates may be published."

Assert-Condition ([string]$candidate.trust.signerClass -eq [string]$channelPolicy.requiredSignerClass) "Release publication for '$channel' requires signer class '$($channelPolicy.requiredSignerClass)'."
if ($channelPolicy.requireProductionTrust -eq $true) {
	Assert-Condition ([string]$candidate.trust.productionTrust -eq "PASS") "Release publication for '$channel' requires production signing trust PASS."
}
if ($channelPolicy.requireCandidatePublicationReadinessPass -eq $true) {
	Assert-Condition ([string]$candidate.publicationReadiness.status -eq "PASS") "Release publication for '$channel' requires candidate publication readiness PASS."
}

$tag = [string]$candidate.source.tag
Assert-Condition (-not [string]::IsNullOrWhiteSpace($tag)) "Publishable release candidate must carry a Git tag."
Assert-Condition ($tag -eq "v$($candidate.product.version)") "Release candidate tag/version mismatch."

$tagCommit = (& git -C $repositoryRoot rev-parse "$tag^{commit}" 2>$null).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tagCommit)) "Release tag '$tag' does not resolve to a commit."
Assert-Condition ($tagCommit.ToLowerInvariant() -eq [string]$candidate.source.sourceCommit) "Release tag '$tag' does not resolve to candidate source commit '$($candidate.source.sourceCommit)'."

$outputRoot = Resolve-RepositoryPath $OutputPath
if (Test-Path -LiteralPath $outputRoot) {
	Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$releaseUrl = "https://github.com/$Repository/releases/tag/$([Uri]::EscapeDataString($tag))"
$releaseTitle = if ($channel -eq "PREVIEW") {
	"rtaime $($candidate.product.version) Preview"
} else {
	"rtaime $($candidate.product.version)"
}

$notesPath = Join-Path $outputRoot "release-notes.md"
$notes = @"
<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# $releaseTitle

Channel: **$channel**  
Version: **$($candidate.product.version)**  
Source commit: **$($candidate.source.sourceCommit)**  
Release Candidate: **$($candidate.candidateId)**

This release publishes the already verified Release Candidate produced by the authoritative rtaime release pipeline. No rebuild occurs during publication.

Verification assets include the Release Candidate manifest and SHA-256 sidecar, the offline software bundle and its SHA-256 sidecar, publication metadata, and channel discovery metadata.

The channel descriptor is discovery metadata only. Authenticity remains anchored in the signed Release Candidate and contained release/bundle trust chain.
"@
[System.IO.File]::WriteAllText($notesPath, $notes.Trim() + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$candidateManifestHash = Get-FileSha256Hex -Path $candidateManifestPath
$candidateSidecarPath = Join-Path $candidateRoot "release-candidate.sha256"
$candidateBundlePath = Join-Path $candidateRoot ([string]$candidate.bundle.fileName)
$candidateBundleSidecarPath = Join-Path $candidateRoot ([string]$candidate.bundle.sidecarFileName)

$publicationCanonical = @(
	"rtaime.release-publication.v1",
	$Repository,
	$channel,
	[string]$candidate.product.version,
	$tag,
	[string]$candidate.source.sourceCommit,
	[string]$candidate.candidateId,
	$candidateManifestHash,
	[string]$candidate.bundle.sha256
) -join "`n"
$publicationId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($publicationCanonical))

$publicationManifest = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	publicationId = $publicationId
	repository = $Repository
	channel = $channel
	product = [ordered]@{
		name = "rtaime"
		version = [string]$candidate.product.version
		releaseStage = [string]$candidate.product.releaseStage
	}
	source = [ordered]@{
		tag = $tag
		sourceCommit = [string]$candidate.source.sourceCommit
	}
	candidate = [ordered]@{
		candidateId = [string]$candidate.candidateId
		manifestFileName = "release-candidate.json"
		manifestSha256 = $candidateManifestHash
		sidecarFileName = "release-candidate.sha256"
		sidecarSha256 = Get-FileSha256Hex -Path $candidateSidecarPath
	}
	bundle = [ordered]@{
		fileName = [string]$candidate.bundle.fileName
		sha256 = Get-FileSha256Hex -Path $candidateBundlePath
		sidecarFileName = [string]$candidate.bundle.sidecarFileName
		sidecarSha256 = Get-FileSha256Hex -Path $candidateBundleSidecarPath
	}
	trust = [ordered]@{
		signerClass = [string]$candidate.trust.signerClass
		keyFingerprint = [string]$candidate.trust.keyFingerprint
		productionTrust = [string]$candidate.trust.productionTrust
		authoritativeTrustSource = "RELEASE_CANDIDATE"
	}
	githubRelease = [ordered]@{
		tag = $tag
		title = $releaseTitle
		url = $releaseUrl
		prerelease = [bool]$channelPolicy.githubPrerelease
		makeLatest = [bool]$channelPolicy.makeLatest
		existingReleaseAction = [string]$policy.immutability.existingReleaseAction
		assetOverwriteAllowed = [bool]$policy.immutability.assetOverwriteAllowed
		rebuildAllowed = [bool]$policy.immutability.rebuildAllowed
	}
	releaseNotes = [ordered]@{
		fileName = "release-notes.md"
		sha256 = Get-FileSha256Hex -Path $notesPath
	}
	createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$publicationManifestPath = Join-Path $outputRoot "release-publication.json"
Write-JsonFile -Value $publicationManifest -Path $publicationManifestPath
$publicationManifestHash = Get-FileSha256Hex -Path $publicationManifestPath
[System.IO.File]::WriteAllText(
	(Join-Path $outputRoot "release-publication.sha256"),
	"$publicationManifestHash  release-publication.json$([Environment]::NewLine)",
	[System.Text.UTF8Encoding]::new($false))

$descriptorFileName = [string]$channelPolicy.channelDescriptorFileName
$channelDescriptor = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	channel = $channel
	repository = $Repository
	product = [ordered]@{
		name = "rtaime"
		version = [string]$candidate.product.version
	}
	release = [ordered]@{
		tag = $tag
		url = $releaseUrl
		prerelease = [bool]$channelPolicy.githubPrerelease
	}
	candidate = [ordered]@{
		candidateId = [string]$candidate.candidateId
		sourceCommit = [string]$candidate.source.sourceCommit
		manifestFileName = "release-candidate.json"
		manifestSha256 = $candidateManifestHash
	}
	publication = [ordered]@{
		publicationId = $publicationId
		manifestFileName = "release-publication.json"
		manifestSha256 = $publicationManifestHash
	}
	bundle = [ordered]@{
		fileName = [string]$candidate.bundle.fileName
		sha256 = [string]$candidate.bundle.sha256
	}
	discoveryTrust = [ordered]@{
		status = "POINTER_ONLY"
		details = "This channel descriptor aids discovery only. Verify the Release Candidate and contained signed release/bundle trust chain before trusting payloads."
	}
}
$descriptorPath = Join-Path $outputRoot $descriptorFileName
Write-JsonFile -Value $channelDescriptor -Path $descriptorPath
$descriptorHash = Get-FileSha256Hex -Path $descriptorPath
[System.IO.File]::WriteAllText(
	(Join-Path $outputRoot "$descriptorFileName.sha256"),
	"$descriptorHash  $descriptorFileName$([Environment]::NewLine)",
	[System.Text.UTF8Encoding]::new($false))

& (Join-Path $PSScriptRoot "Test-ReleasePublication.ps1") `
	-CandidatePath $candidateRoot `
	-PublicationPath $outputRoot

Write-Host "Release publication preparation PASS"
Write-Host "Publication id: $publicationId"
Write-Host "Channel: $channel"
Write-Host "Tag: $tag"
Write-Host "Release URL: $releaseUrl"
Write-Host "GitHub prerelease: $([bool]$channelPolicy.githubPrerelease)"
Write-Host "Make latest: $([bool]$channelPolicy.makeLatest)"
