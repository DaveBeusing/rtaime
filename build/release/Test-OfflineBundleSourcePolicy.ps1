# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$policyPath = Join-Path $PSScriptRoot "offline-bundle-policy.json"
$builderPath = Join-Path $PSScriptRoot "New-OfflineReleaseBundle.ps1"
$repositoryToolsPath = Join-Path $repositoryRoot "tools"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)

	if (-not $Condition) {
		throw $Message
	}
}

function Resolve-RepositoryRelativePath {
	param([Parameter(Mandatory)][string]$RelativePath)

	Assert-Condition (-not [string]::IsNullOrWhiteSpace($RelativePath)) "Repository source path must not be empty."
	Assert-Condition (-not [System.IO.Path]::IsPathRooted($RelativePath)) "Repository source path '$RelativePath' must be relative."

	$normalized = $RelativePath.Replace('\', '/')
	$segments = @($normalized.Split('/'))
	Assert-Condition (-not ($segments -contains '..')) "Repository source path '$RelativePath' contains parent traversal."
	Assert-Condition (-not ($segments -contains '.')) "Repository source path '$RelativePath' contains a relative segment."
	Assert-Condition (-not ($segments -contains '')) "Repository source path '$RelativePath' contains an empty segment."

	$fullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $RelativePath))
	$rootPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
	Assert-Condition ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "Repository source path '$RelativePath' escapes the repository root."
	return $fullPath
}

Assert-Condition (Test-Path -LiteralPath $policyPath -PathType Leaf) "Offline bundle policy is missing."
Assert-Condition (Test-Path -LiteralPath $builderPath -PathType Leaf) "Offline bundle builder is missing."
Assert-Condition (-not (Test-Path -LiteralPath $repositoryToolsPath)) "Repository root tools/ must not exist; canonical automation sources belong under build/."

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Unsupported offline bundle policy schema version."

foreach ($directoryProperty in @("schemaRoot", "documentationRoot")) {
	$relativePath = [string]$policy.$directoryProperty
	$fullPath = Resolve-RepositoryRelativePath $relativePath
	Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Container) "Offline bundle source directory '$relativePath' is missing."
}

$trustedKeys = [string]$policy.trustedReleaseKeys
$trustedKeysPath = Resolve-RepositoryRelativePath $trustedKeys
Assert-Condition (Test-Path -LiteralPath $trustedKeysPath -PathType Leaf) "Trusted release-key source '$trustedKeys' is missing."

foreach ($rootDocument in @($policy.rootDocumentation)) {
	$relativePath = [string]$rootDocument
	$fullPath = Resolve-RepositoryRelativePath $relativePath
	Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) "Root documentation source '$relativePath' is missing."
}

$offlineTools = @($policy.offlineTools)
Assert-Condition ($offlineTools.Count -gt 0) "Offline bundle policy must define at least one offline tool source."

$outputNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($tool in $offlineTools) {
	$relativePath = [string]$tool
	$normalized = $relativePath.Replace('\', '/')
	Assert-Condition ($normalized.StartsWith("build/", [StringComparison]::OrdinalIgnoreCase)) "Offline tool source '$relativePath' must come from build/."

	$fullPath = Resolve-RepositoryRelativePath $relativePath
	Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) "Offline tool source '$relativePath' is missing."

	$outputName = [System.IO.Path]::GetFileName($fullPath)
	Assert-Condition ($outputNames.Add($outputName)) "Offline tool source '$relativePath' collides with another bundle tool named '$outputName'."
}

$builder = Get-Content -LiteralPath $builderPath -Raw
Assert-Condition ($builder -match 'policy\.offlineTools') "Offline bundle builder must consume offlineTools from policy."
Assert-Condition ($builder -match 'Join-Path \$bundleRoot "tools"') "Offline bundle builder must materialize the generated bundle tools/ directory."
Assert-Condition ($builder -match 'toolOutputNames') "Offline bundle builder must reject duplicate flattened tool names."
Assert-Condition ($builder -match 'StartsWith\("build/"') "Offline bundle builder must reject non-build offline-tool source paths."

Write-Host "Offline bundle source policy PASS"
Write-Host "Repository root tools/: absent"
Write-Host "Offline tool source count: $($offlineTools.Count)"
Write-Host "Offline tool source root: build/"
Write-Host "Generated bundle tool target: tools/"
