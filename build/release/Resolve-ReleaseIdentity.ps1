# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[ValidateSet("AUTO", "QUALIFICATION", "PREVIEW", "STABLE")]
	[string]$Channel = "QUALIFICATION",
	[string]$Tag = "",
	[string]$SourceCommit = $env:RTAIME_SOURCE_COMMIT,
	[string]$BuildCommit = $env:RTAIME_BUILD_COMMIT,
	[string]$BuildId = $env:RTAIME_BUILD_ID,
	[string]$OutputPath = "artifacts/release-pipeline/release-identity.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Resolve-HeadCommit {
	$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
	if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
		throw "Unable to resolve the current Git commit."
	}
	return $commit.ToLowerInvariant()
}

function Resolve-TagCommit {
	param([Parameter(Mandatory)][string]$TagName)
	$commit = (& git -C $repositoryRoot rev-parse "$TagName^{commit}" 2>$null).Trim()
	if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
		throw "Release tag '$TagName' does not resolve to a commit in the checked-out repository."
	}
	return $commit.ToLowerInvariant()
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

$policyPath = Join-Path $PSScriptRoot "release-channels.json"
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
	throw "Release channel policy was not found at '$policyPath'."
}
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

$buildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"
[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersionNode = $buildProps.SelectSingleNode("//RtaimeProductVersion")
$releaseStageNode = $buildProps.SelectSingleNode("//RtaimeReleaseStage")
if ($null -eq $productVersionNode -or [string]::IsNullOrWhiteSpace($productVersionNode.InnerText)) {
	throw "Directory.Build.props must declare RtaimeProductVersion."
}
if ($null -eq $releaseStageNode -or [string]::IsNullOrWhiteSpace($releaseStageNode.InnerText)) {
	throw "Directory.Build.props must declare RtaimeReleaseStage."
}
$productVersion = $productVersionNode.InnerText.Trim()
$releaseStage = $releaseStageNode.InnerText.Trim().ToUpperInvariant()

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
	$SourceCommit = Resolve-HeadCommit
}
if ([string]::IsNullOrWhiteSpace($BuildCommit)) {
	$BuildCommit = Resolve-HeadCommit
}
$SourceCommit = $SourceCommit.Trim().ToLowerInvariant()
$BuildCommit = $BuildCommit.Trim().ToLowerInvariant()
if ($SourceCommit -notmatch '^[0-9a-f]{40,64}$') {
	throw "Source commit '$SourceCommit' is not a supported Git object identity."
}
if ($BuildCommit -notmatch '^[0-9a-f]{40,64}$') {
	throw "Build commit '$BuildCommit' is not a supported Git object identity."
}
if ([string]::IsNullOrWhiteSpace($BuildId)) {
	$BuildId = "local-$($BuildCommit.Substring(0, [Math]::Min(12, $BuildCommit.Length)))"
}

if ($Channel -eq "AUTO") {
	if ([string]::IsNullOrWhiteSpace($Tag)) {
		$Channel = "QUALIFICATION"
	} elseif ($releaseStage -eq "PREVIEW") {
		$Channel = "PREVIEW"
	} elseif ($releaseStage -eq "STABLE") {
		$Channel = "STABLE"
	} else {
		throw "AUTO channel cannot map release stage '$releaseStage' with tag '$Tag'. Tagged releases must declare PREVIEW or STABLE in Directory.Build.props."
	}
}

$channelProperty = $policy.channels.PSObject.Properties[$Channel]
if ($null -eq $channelProperty) {
	throw "Release channel '$Channel' is not declared in release-channels.json."
}
$channelPolicy = $channelProperty.Value

if ($releaseStage -ne [string]$channelPolicy.releaseStage) {
	throw "Release channel '$Channel' requires stage '$($channelPolicy.releaseStage)', but source declares '$releaseStage'."
}
if ($productVersion -notmatch [string]$channelPolicy.versionPattern) {
	throw "Product version '$productVersion' is invalid for release channel '$Channel'."
}

$normalizedTag = if ([string]::IsNullOrWhiteSpace($Tag)) { $null } else { $Tag.Trim() }
if ($channelPolicy.tagRequired -eq $true) {
	if ($null -eq $normalizedTag) {
		throw "Release channel '$Channel' requires a Git tag."
	}
	$match = [Regex]::Match($normalizedTag, [string]$channelPolicy.tagPattern)
	if (-not $match.Success) {
		throw "Release tag '$normalizedTag' does not match the '$Channel' channel pattern."
	}
	$tagVersion = $match.Groups["version"].Value
	if ($tagVersion -ne $productVersion) {
		throw "Release tag version '$tagVersion' does not equal source product version '$productVersion'."
	}
	$tagCommit = Resolve-TagCommit -TagName $normalizedTag
	if ($tagCommit -ne $SourceCommit) {
		throw "Release tag '$normalizedTag' resolves to '$tagCommit', not source commit '$SourceCommit'."
	}
} elseif ($null -ne $normalizedTag) {
	throw "Release channel '$Channel' does not permit a release tag."
}

$headCommit = Resolve-HeadCommit
if ($headCommit -ne $SourceCommit) {
	throw "Checked-out HEAD '$headCommit' does not equal source commit '$SourceCommit'. Release identity must be built from the exact source commit."
}
if ($BuildCommit -ne $SourceCommit) {
	throw "Build commit '$BuildCommit' does not equal source commit '$SourceCommit'. The single release pipeline does not permit source/build commit divergence."
}

$identity = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	channel = $Channel
	productName = "rtaime"
	productVersion = $productVersion
	releaseStage = $releaseStage
	tag = $normalizedTag
	sourceCommit = $SourceCommit
	buildCommit = $BuildCommit
	buildId = $BuildId
	signingPolicy = [ordered]@{
		allowedSignerClasses = @($channelPolicy.allowedSignerClasses)
		requireTrustedProductionKey = [bool]$channelPolicy.requireTrustedProductionKey
		publicationEligible = [bool]$channelPolicy.publicationEligible
	}
}

$outputFullPath = Resolve-RepositoryPath $OutputPath
Write-JsonFile -Value $identity -Path $outputFullPath

Write-Host "Release identity PASS"
Write-Host "Channel: $Channel"
Write-Host "Product version: $productVersion"
Write-Host "Release stage: $releaseStage"
Write-Host "Tag: $(if ($null -eq $normalizedTag) { '<none>' } else { $normalizedTag })"
Write-Host "Source commit: $SourceCommit"
Write-Host "Build commit: $BuildCommit"
Write-Host "Publication eligible by channel: $([bool]$channelPolicy.publicationEligible)"

return $identity
