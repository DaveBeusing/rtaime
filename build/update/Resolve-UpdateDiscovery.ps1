# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[ValidateSet('PREVIEW', 'STABLE')]
	[string]$Channel,
	[string]$Repository = 'DaveBeusing/rtaime',
	[string]$CurrentVersion = '',
	[string]$PinnedVersion = '',
	[string]$OutputPath = 'artifacts/update/discovery.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Parse-VersionRecord {
	param([Parameter(Mandatory)][string]$Version)
	$match = [Regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-preview\.(?<preview>\d+))?$')
	if (-not $match.Success) { return $null }
	return [pscustomobject]@{
		major = [int]$match.Groups['major'].Value
		minor = [int]$match.Groups['minor'].Value
		patch = [int]$match.Groups['patch'].Value
		isPreview = $match.Groups['preview'].Success
		preview = if ($match.Groups['preview'].Success) { [int]$match.Groups['preview'].Value } else { -1 }
	}
}

function Get-VersionSortKey {
	param([Parameter(Mandatory)][string]$Version)
	$v = Parse-VersionRecord $Version
	if ($null -eq $v) { return $null }
	$channelWeight = if ($v.isPreview) { 0 } else { 1 }
	return ('{0:D10}.{1:D10}.{2:D10}.{3:D1}.{4:D10}' -f $v.major, $v.minor, $v.patch, $channelWeight, ($v.preview + 1))
}

function Compare-Version {
	param([string]$Left, [string]$Right)
	$l = Get-VersionSortKey $Left
	$r = Get-VersionSortKey $Right
	Assert-Condition ($null -ne $l -and $null -ne $r) "Unsupported version comparison '$Left' / '$Right'."
	return [string]::CompareOrdinal($l, $r)
}

Assert-Condition ($Repository -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') "Repository must use owner/name format."
$descriptorName = if ($Channel -eq 'PREVIEW') { 'rtaime-channel-preview.json' } else { 'rtaime-channel-stable.json' }
$tagPattern = if ($Channel -eq 'PREVIEW') { '^v(?<version>\d+\.\d+\.\d+-preview\.\d+)$' } else { '^v(?<version>\d+\.\d+\.\d+)$' }

$headers = @{
	'Accept' = 'application/vnd.github+json'
	'User-Agent' = 'rtaime-update-discovery'
	'X-GitHub-Api-Version' = '2022-11-28'
}
$apiUrl = "https://api.github.com/repos/$Repository/releases?per_page=100"
try {
	$releases = @(Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get)
} catch {
	throw "Unable to query GitHub release discovery endpoint '$apiUrl': $($_.Exception.Message)"
}

$candidates = @()
foreach ($release in $releases) {
	if ($release.draft -eq $true) { continue }
	if ($Channel -eq 'PREVIEW' -and $release.prerelease -ne $true) { continue }
	if ($Channel -eq 'STABLE' -and $release.prerelease -ne $false) { continue }
	$tagMatch = [Regex]::Match([string]$release.tag_name, $tagPattern)
	if (-not $tagMatch.Success) { continue }
	$version = $tagMatch.Groups['version'].Value
	if (-not [string]::IsNullOrWhiteSpace($PinnedVersion) -and $version -ne $PinnedVersion) { continue }
	if (-not [string]::IsNullOrWhiteSpace($CurrentVersion) -and (Compare-Version -Left $CurrentVersion -Right $version) -ge 0) { continue }
	$assetNames = @($release.assets | ForEach-Object { [string]$_.name })
	if ($assetNames -notcontains $descriptorName) { continue }
	if ($assetNames -notcontains "$descriptorName.sha256") { continue }
	if ($assetNames -notcontains 'release-candidate.json') { continue }
	if ($assetNames -notcontains 'release-publication.json') { continue }
	$candidates += [pscustomobject]@{
		version = $version
		sortKey = Get-VersionSortKey $version
		release = $release
	}
}

Assert-Condition ($candidates.Count -gt 0) "No eligible published $Channel update was discovered."
$selected = $candidates | Sort-Object sortKey -Descending | Select-Object -First 1
$release = $selected.release

$discovery = [ordered]@{
	copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
	schemaVersion = '1.0'
	repository = $Repository
	channel = $Channel
	version = [string]$selected.version
	tag = [string]$release.tag_name
	releaseId = [long]$release.id
	releaseUrl = [string]$release.html_url
	apiUrl = [string]$release.url
	prerelease = [bool]$release.prerelease
	draft = [bool]$release.draft
	descriptorAssetName = $descriptorName
	assets = @($release.assets | ForEach-Object {
		[ordered]@{
			name = [string]$_.name
			size = [long]$_.size
			browserDownloadUrl = [string]$_.browser_download_url
		}
	})
	discoveredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
[System.IO.File]::WriteAllText($outputFullPath, ($discovery | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "Update discovery PASS"
Write-Host "Repository: $Repository"
Write-Host "Channel: $Channel"
Write-Host "Version: $($discovery.version)"
Write-Host "Tag: $($discovery.tag)"

return $discovery
