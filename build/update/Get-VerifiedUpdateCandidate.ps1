# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$DiscoveryPath,
	[string]$OutputPath = 'artifacts/update/downloaded-release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Assert-SafeAssetName {
	param([Parameter(Mandatory)][string]$Name)
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($Name)) "Release asset name must not be empty."
	Assert-Condition (-not [System.IO.Path]::IsPathRooted($Name)) "Release asset '$Name' must not be rooted."
	Assert-Condition ([System.IO.Path]::GetFileName($Name) -eq $Name) "Release asset '$Name' must be a basename."
	Assert-Condition ($Name -notmatch '[\\/]') "Release asset '$Name' must not contain path separators."
	Assert-Condition ($Name -notin @('.', '..')) "Release asset name '$Name' is invalid."
}

function Get-Asset {
	param([Parameter(Mandatory)]$Discovery, [Parameter(Mandatory)][string]$Name)
	Assert-SafeAssetName -Name $Name
	$matches = @($Discovery.assets | Where-Object { [string]$_.name -eq $Name })
	Assert-Condition ($matches.Count -eq 1) "Release discovery must contain exactly one asset named '$Name'."
	return $matches[0]
}

function Download-Asset {
	param([Parameter(Mandatory)]$Asset, [Parameter(Mandatory)][string]$Destination)
	$name = [string]$Asset.name
	Assert-SafeAssetName -Name $name
	$url = [string]$Asset.browserDownloadUrl
	Assert-Condition ($url.StartsWith('https://github.com/DaveBeusing/rtaime/releases/download/', [StringComparison]::OrdinalIgnoreCase)) "Asset '$name' does not use the expected GitHub release download origin."
	$parent = Split-Path -Parent $Destination
	if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
	Invoke-WebRequest -Uri $url -OutFile $Destination -Headers @{ 'User-Agent' = 'rtaime-update-download' } -MaximumRedirection 10
	Assert-Condition (Test-Path -LiteralPath $Destination -PathType Leaf) "Asset '$name' was not downloaded."
	if ([long]$Asset.size -gt 0) {
		Assert-Condition ((Get-Item -LiteralPath $Destination).Length -eq [long]$Asset.size) "Downloaded asset '$name' size mismatch."
	}
}

$discoveryFullPath = [System.IO.Path]::GetFullPath($DiscoveryPath)
Assert-Condition (Test-Path -LiteralPath $discoveryFullPath -PathType Leaf) "Discovery metadata was not found."
$discovery = Get-Content -LiteralPath $discoveryFullPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$discovery.schemaVersion -eq '1.0') "Unsupported discovery metadata schema version."
Assert-Condition ([string]$discovery.repository -eq 'DaveBeusing/rtaime') "Unexpected discovery repository."
Assert-Condition ([string]$discovery.channel -in @('PREVIEW', 'STABLE')) "Unexpected discovery channel."
Assert-Condition ($discovery.draft -eq $false) "Draft releases cannot be downloaded as updates."

$outputRoot = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
$candidateRoot = Join-Path $outputRoot 'candidate'
$publicationRoot = Join-Path $outputRoot 'publication'
New-Item -ItemType Directory -Path $candidateRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publicationRoot -Force | Out-Null

$descriptorName = [string]$discovery.descriptorAssetName
Assert-SafeAssetName -Name $descriptorName
$expectedDescriptorName = if ([string]$discovery.channel -eq 'PREVIEW') { 'rtaime-channel-preview.json' } else { 'rtaime-channel-stable.json' }
Assert-Condition ($descriptorName -eq $expectedDescriptorName) "Discovery descriptor asset name does not match channel."

$fixedAssets = @(
	@('release-candidate.json', $candidateRoot),
	@('release-candidate.sha256', $candidateRoot),
	@('release-publication.json', $publicationRoot),
	@('release-publication.sha256', $publicationRoot),
	@('release-notes.md', $publicationRoot),
	@($descriptorName, $publicationRoot),
	@("$descriptorName.sha256", $publicationRoot)
)
foreach ($entry in $fixedAssets) {
	$name = [string]$entry[0]
	$destinationRoot = [string]$entry[1]
	Download-Asset -Asset (Get-Asset -Discovery $discovery -Name $name) -Destination (Join-Path $destinationRoot $name)
}

$candidateManifestPath = Join-Path $candidateRoot 'release-candidate.json'
$candidateManifestHash = (Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$candidateSidecar = [System.IO.File]::ReadAllText((Join-Path $candidateRoot 'release-candidate.sha256')).Trim()
Assert-Condition ($candidateSidecar -eq "$candidateManifestHash  release-candidate.json") "Downloaded Candidate manifest sidecar mismatch."

$candidate = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$candidate.channel -eq [string]$discovery.channel) "Downloaded candidate channel differs from discovery metadata."
Assert-Condition ([string]$candidate.product.version -eq [string]$discovery.version) "Downloaded candidate version differs from discovery metadata."
Assert-Condition ([string]$candidate.source.tag -eq [string]$discovery.tag) "Downloaded candidate tag differs from discovery metadata."

$bundleFileName = [string]$candidate.bundle.fileName
$bundleSidecarFileName = [string]$candidate.bundle.sidecarFileName
Assert-SafeAssetName -Name $bundleFileName
Assert-SafeAssetName -Name $bundleSidecarFileName
Assert-Condition ([System.IO.Path]::GetExtension($bundleFileName).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) "Downloaded Candidate bundle must be a ZIP file."
Assert-Condition ($bundleSidecarFileName -eq "$bundleFileName.sha256") "Downloaded Candidate bundle sidecar name is invalid."

foreach ($name in @($bundleFileName, $bundleSidecarFileName)) {
	Download-Asset -Asset (Get-Asset -Discovery $discovery -Name $name) -Destination (Join-Path $candidateRoot $name)
}

$releaseMetadata = [ordered]@{
	copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
	schemaVersion = '1.0'
	id = [long]$discovery.releaseId
	tag_name = [string]$discovery.tag
	html_url = [string]$discovery.releaseUrl
	prerelease = [bool]$discovery.prerelease
	draft = [bool]$discovery.draft
	assets = @($discovery.assets | ForEach-Object { [ordered]@{ name = [string]$_.name; size = [long]$_.size; browser_download_url = [string]$_.browserDownloadUrl } })
}
$metadataPath = Join-Path $outputRoot 'github-release.json'
[System.IO.File]::WriteAllText($metadataPath, ($releaseMetadata | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$verifier = Join-Path $PSScriptRoot 'Test-DownloadedRelease.ps1'
Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Downloaded release verifier is unavailable."
$result = & $verifier -CandidatePath $candidateRoot -PublicationPath $publicationRoot -GitHubReleaseMetadataPath $metadataPath

Write-Host "Verified update download PASS"
Write-Host "Version: $($candidate.product.version)"
Write-Host "Bundle: $bundleFileName"
Write-Host "Output: $outputRoot"

return [ordered]@{
	root = $outputRoot
	candidatePath = $candidateRoot
	publicationPath = $publicationRoot
	metadataPath = $metadataPath
	bundlePath = [string]$result.bundlePath
}
