# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$OutputPath,
	[Parameter(Mandatory)][string]$SourceCommit,
	[string]$BindingRoot = "artifacts/qualification/bindings"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$qualificationRoot = Join-Path $repositoryRoot "build/qualification"
$manifestGenerator = Join-Path $qualificationRoot "New-QualificationEvidenceManifest.ps1"
$manifestVerifier = Join-Path $qualificationRoot "Test-QualificationEvidenceManifest.ps1"

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-Sha256 {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-EvidenceRelativePath {
	param(
		[Parameter(Mandatory)][string]$EvidenceRoot,
		[Parameter(Mandatory)][string]$Path
	)
	$relative = [System.IO.Path]::GetRelativePath($EvidenceRoot, $Path).Replace('\', '/')
	if ($relative -eq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
		throw "Qualification evidence copy escaped the release evidence bundle."
	}
	return $relative
}

Assert-Condition ($SourceCommit -match '^[0-9a-fA-F]{40}$') "SourceCommit must be an exact 40-character Git SHA."
$SourceCommit = $SourceCommit.ToLowerInvariant()
foreach ($required in @($manifestGenerator, $manifestVerifier)) {
	Assert-Condition (Test-Path -LiteralPath $required -PathType Leaf) "Required qualification evidence tool is missing: '$required'."
}

$evidenceRoot = Resolve-RepositoryPath -Path $OutputPath
Assert-Condition (Test-Path -LiteralPath $evidenceRoot -PathType Container) "Release evidence directory is missing: '$evidenceRoot'."
$releaseEvidencePath = Join-Path $evidenceRoot "release-evidence.json"
$compatibilityManifestPath = Join-Path $evidenceRoot "compatibility-manifest.json"
Assert-Condition (Test-Path -LiteralPath $releaseEvidencePath -PathType Leaf) "release-evidence.json is missing."
Assert-Condition (Test-Path -LiteralPath $compatibilityManifestPath -PathType Leaf) "compatibility-manifest.json is missing."

$releaseEvidence = Get-Content -LiteralPath $releaseEvidencePath -Raw | ConvertFrom-Json
$compatibilityManifest = Get-Content -LiteralPath $compatibilityManifestPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$releaseEvidence.sourceCommit -eq $SourceCommit) "Release evidence source commit differs from qualification source commit."
Assert-Condition ([string]$compatibilityManifest.source.sourceCommit -eq $SourceCommit) "Compatibility manifest source commit differs from qualification source commit."

$stagingManifest = Join-Path $repositoryRoot "artifacts/qualification/qualification-evidence-manifest.json"
& $manifestGenerator `
	-SourceCommit $SourceCommit `
	-BindingRoot $BindingRoot `
	-OutputPath $stagingManifest
& $manifestVerifier -ManifestPath $stagingManifest -ExpectedSourceCommit $SourceCommit
$sourceManifest = Get-Content -LiteralPath $stagingManifest -Raw | ConvertFrom-Json

$qualificationBundleRoot = Join-Path $evidenceRoot "qualification"
if (Test-Path -LiteralPath $qualificationBundleRoot) { Remove-Item -LiteralPath $qualificationBundleRoot -Recurse -Force }
New-Item -ItemType Directory -Path $qualificationBundleRoot -Force | Out-Null
$bindingBundleRoot = Join-Path $qualificationBundleRoot "bindings"
$payloadBundleRoot = Join-Path $qualificationBundleRoot "payloads"
New-Item -ItemType Directory -Path $bindingBundleRoot -Force | Out-Null
New-Item -ItemType Directory -Path $payloadBundleRoot -Force | Out-Null

$sourceSupportedPerformance = Resolve-RepositoryPath -Path ([string]$sourceManifest.supportedPerformance.path)
Assert-Condition (Test-Path -LiteralPath $sourceSupportedPerformance -PathType Leaf) "Supported-performance evidence referenced by qualification manifest is missing."
Assert-Condition ((Get-Sha256 $sourceSupportedPerformance) -eq [string]$sourceManifest.supportedPerformance.sha256) "Supported-performance evidence hash changed after manifest verification."
$supportedPerformanceTarget = Join-Path $qualificationBundleRoot "supported-performance.json"
Copy-Item -LiteralPath $sourceSupportedPerformance -Destination $supportedPerformanceTarget -Force
Assert-Condition ((Get-Sha256 $supportedPerformanceTarget) -eq [string]$sourceManifest.supportedPerformance.sha256) "Copied supported-performance evidence hash mismatch."
$sourceSupportedPerformanceMarkdown = [System.IO.Path]::ChangeExtension($sourceSupportedPerformance, ".md")
if (Test-Path -LiteralPath $sourceSupportedPerformanceMarkdown -PathType Leaf) {
	Copy-Item -LiteralPath $sourceSupportedPerformanceMarkdown -Destination (Join-Path $qualificationBundleRoot "supported-performance.md") -Force
}
$releaseSupportedPerformance = [ordered]@{
	status = [string]$sourceManifest.supportedPerformance.status
	path = Get-EvidenceRelativePath -EvidenceRoot $evidenceRoot -Path $supportedPerformanceTarget
	sha256 = [string]$sourceManifest.supportedPerformance.sha256
}

$copiedTypes = @{}
$releaseRequirements = @(
	foreach ($entry in @($sourceManifest.requirements)) {
		$status = [string]$entry.status
		if ($status -eq "UNVERIFIED") {
			[ordered]@{
				requirement = [string]$entry.requirement
				status = "UNVERIFIED"
			}
			continue
		}

		Assert-Condition ($status -eq "PASSED") "Qualification manifest contains unsupported status '$status'."
		$type = [string]$entry.qualificationType
		if (-not $copiedTypes.ContainsKey($type)) {
			$sourceBinding = Resolve-RepositoryPath -Path ([string]$entry.binding.path)
			$sourcePayload = Resolve-RepositoryPath -Path ([string]$entry.payload.path)
			Assert-Condition (Test-Path -LiteralPath $sourceBinding -PathType Leaf) "Qualification binding '$sourceBinding' is missing."
			Assert-Condition (Test-Path -LiteralPath $sourcePayload -PathType Leaf) "Qualification payload '$sourcePayload' is missing."
			Assert-Condition ((Get-Sha256 $sourceBinding) -eq [string]$entry.binding.sha256) "Qualification binding hash changed after verification."
			Assert-Condition ((Get-Sha256 $sourcePayload) -eq [string]$entry.payload.sha256) "Qualification payload hash changed after verification."

			$stem = $type.ToLowerInvariant().Replace('_', '-')
			$bindingTarget = Join-Path $bindingBundleRoot "$stem.binding.json"
			$payloadTarget = Join-Path $payloadBundleRoot "$stem.json"
			Copy-Item -LiteralPath $sourceBinding -Destination $bindingTarget -Force
			Copy-Item -LiteralPath $sourcePayload -Destination $payloadTarget -Force
			Assert-Condition ((Get-Sha256 $bindingTarget) -eq [string]$entry.binding.sha256) "Copied qualification binding hash mismatch."
			Assert-Condition ((Get-Sha256 $payloadTarget) -eq [string]$entry.payload.sha256) "Copied qualification payload hash mismatch."

			$copiedTypes[$type] = [ordered]@{
				bindingPath = Get-EvidenceRelativePath -EvidenceRoot $evidenceRoot -Path $bindingTarget
				bindingSha256 = [string]$entry.binding.sha256
				payloadPath = Get-EvidenceRelativePath -EvidenceRoot $evidenceRoot -Path $payloadTarget
				payloadSha256 = [string]$entry.payload.sha256
			}
		}

		$copy = $copiedTypes[$type]
		[ordered]@{
			requirement = [string]$entry.requirement
			status = "PASSED"
			qualificationType = $type
			binding = [ordered]@{
				path = $copy.bindingPath
				sha256 = $copy.bindingSha256
			}
			payload = [ordered]@{
				path = $copy.payloadPath
				sha256 = $copy.payloadSha256
			}
			workflow = [ordered]@{
				runId = [string]$entry.workflow.runId
				runAttempt = [int]$entry.workflow.runAttempt
			}
		}
	}
)

$releaseQualificationManifest = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	repository = [string]$sourceManifest.repository
	sourceCommit = $SourceCommit
	generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	supportedPerformance = $releaseSupportedPerformance
	requirements = $releaseRequirements
}
$releaseQualificationManifestPath = Join-Path $evidenceRoot "qualification-evidence-manifest.json"
Write-JsonFile -Value $releaseQualificationManifest -Path $releaseQualificationManifestPath

$compatibilityManifest.hardwareQualification = @(
	foreach ($entry in $releaseRequirements) {
		[ordered]@{
			requirement = [string]$entry.requirement
			status = if ([string]$entry.status -eq "PASSED") { "PASS" } else { "UNVERIFIED" }
		}
	}
)
Write-JsonFile -Value $compatibilityManifest -Path $compatibilityManifestPath

$qualificationReference = [pscustomobject]@{
	path = "qualification-evidence-manifest.json"
	sha256 = Get-Sha256 -Path $releaseQualificationManifestPath
}
$releaseEvidence | Add-Member -NotePropertyName qualificationEvidenceManifest -NotePropertyValue $qualificationReference -Force
$releaseEvidence.compatibilityManifest.sha256 = Get-Sha256 -Path $compatibilityManifestPath
Write-JsonFile -Value $releaseEvidence -Path $releaseEvidencePath

$passed = @($releaseRequirements | Where-Object { [string]$_.status -eq "PASSED" }).Count
Write-Host "Qualification evidence applied to release evidence PASS"
Write-Host "Source commit: $SourceCommit"
Write-Host "Bound release requirements: $passed / $($releaseRequirements.Count)"
Write-Host "Release qualification manifest: $releaseQualificationManifestPath"
