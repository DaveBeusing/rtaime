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

function Get-Asset {
	param([Parameter(Mandatory)]$Discovery, [Parameter(Mandatory)][string]$Name)
	$matches = @($Discovery.assets | Where-Object { [string]$_.name -eq $Name })
	Assert-Condition ($matches.Count -eq 1) "Release discovery must contain exactly one asset named '$Name'."
	return $matches[0]
}

function Download-Asset {
	param([Parameter(Mandatory)]$Asset, [Parameter(Mandatory)][string]$Destination)
	$url = [string]$Asset.browserDownloadUrl
	Assert-Condition ($url.StartsWith('https://github.com/DaveBeusing/rtaime/releases/download/', [StringComparison]::OrdinalIgnoreCase)) "Asset '$($Asset.name)' does not use the expected GitHub release download origin."
	$parent = Split-Path -Parent $Destination
	if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
	Invoke-WebRequest -Uri $url -OutFile $Destination -Headers @{ 'User-Agent' = 'rtaime-update-download' } -MaximumRedirection 10
	Assert-Condition (Test-Path -LiteralPath $Destination -PathType Leaf) "Asset '$($Asset.name)' was not downloaded."
	if ([long]$Asset.size -gt 0) {
		Assert-Condition ((Get-Item -LiteralPath $Destination).Length -eq [long]$Asset.size) "Downloaded asset '$($Asset.name)' size mismatch."
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

$candidate = Get-Content -LiteralPath (Join-Path $candidateRoot 'release-candidate.json') -Raw | ConvertFrom-Json
Assert-Condition ([string]$candidate.channel -eq [string]$discovery.channel) "Downloaded candidate channel differs from discovery metadata."
Assert-Condition ([string]$candidate.product.version -eq [string]$discovery.version) "Downloaded candidate version differs from discovery metadata."
Assert-Condition ([string]$candidate.source.tag -eq [string]$discovery.tag) "Downloaded candidate tag differs from discovery metadata."

foreach ($name in @([string]$candidate.bundle.fileName, [string]$candidate.bundle.sidecarFileName)) {
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

$verifierCandidates = @(
	(Join-Path $PSScriptRoot 'Test-DownloadedRelease.ps1'),
	(Join-Path $PSScriptRoot '../update/Test-DownloadedRelease.ps1')
)
$verifier = $verifierCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($verifier)) "Downloaded release verifier is unavailable."
$result = & $verifier -CandidatePath $candidateRoot -PublicationPath $publicationRoot -GitHubReleaseMetadataPath $metadataPath

Write-Host "Verified update download PASS"
Write-Host "Version: $($candidate.product.version)"
Write-Host "Bundle: $($candidate.bundle.fileName)"
Write-Host "Output: $outputRoot"

return [ordered]@{
	root = $outputRoot
	candidatePath = $candidateRoot
	publicationPath = $publicationRoot
	metadataPath = $metadataPath
	bundlePath = [string]$result.bundlePath
}
