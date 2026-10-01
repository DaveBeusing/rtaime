# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$AssessmentPath,
	[string]$OutputPath = "artifacts/release-evidence"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}
function Get-Sha256 {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Write-JsonFile {
	param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
	[System.IO.Path]::GetFullPath($OutputPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}
$fullAssessmentPath = if ([System.IO.Path]::IsPathRooted($AssessmentPath)) {
	[System.IO.Path]::GetFullPath($AssessmentPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $AssessmentPath))
}

Assert-Condition (Test-Path -LiteralPath $outputRoot -PathType Container) "Release evidence directory was not found at '$outputRoot'."
Assert-Condition (Test-Path -LiteralPath $fullAssessmentPath -PathType Leaf) "Known-issues assessment was not found at '$fullAssessmentPath'."
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $outputRoot "release-attestation.json"))) "Known-issues assessment must be bound before release attestation is created."
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $outputRoot "release-record.json"))) "Known-issues assessment must be bound before release record creation."

$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"
Assert-Condition (Test-Path -LiteralPath $releaseEvidencePath -PathType Leaf) "Release evidence manifest is missing."
$releaseEvidence = Get-Content -LiteralPath $releaseEvidencePath -Raw | ConvertFrom-Json

$domains = @($releaseEvidence.evidenceDomains | Where-Object { [string]$_.domain -eq "KNOWN_ISSUES" })
Assert-Condition ($domains.Count -eq 1) "Release evidence must contain exactly one KNOWN_ISSUES domain."
$domain = $domains[0]
Assert-Condition ([string]$domain.status -eq "UNVERIFIED") "Known-issues binding requires an UNVERIFIED KNOWN_ISSUES domain before promotion."
Assert-Condition (-not (@($releaseEvidence.PSObject.Properties.Name) -contains "knownIssuesAssessment")) "Release evidence already contains known-issues assessment binding."

& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessment.ps1") -AssessmentPath $fullAssessmentPath -ExpectedProductVersion ([string]$releaseEvidence.productVersion) -ExpectedReleaseStage ([string]$releaseEvidence.releaseStage) -ExpectedSourceCommit ([string]$releaseEvidence.sourceCommit) -ExpectedBuildCommit ([string]$releaseEvidence.buildCommit) -ExpectedBuildId ([string]$releaseEvidence.buildId) -RequirePass

$assessment = Get-Content -LiteralPath $fullAssessmentPath -Raw | ConvertFrom-Json
$targetPath = Join-Path $outputRoot "known-issues-assessment.json"
if ([System.IO.Path]::GetFullPath($fullAssessmentPath) -ne [System.IO.Path]::GetFullPath($targetPath)) {
	Copy-Item -LiteralPath $fullAssessmentPath -Destination $targetPath -Force
}
$hash = Get-Sha256 $targetPath

$reference = [ordered]@{
	path = "known-issues-assessment.json"
	sha256 = $hash
	assessmentId = [string]$assessment.assessmentId
}
$releaseEvidence | Add-Member -MemberType NoteProperty -Name knownIssuesAssessment -Value $reference
$releaseEvidence.knownIssues = @(
	$assessment.issues |
		Where-Object { [string]$_.disposition -ne "CLOSED" } |
		ForEach-Object {
			[ordered]@{
				id = [string]$_.id
				severity = [string]$_.severity
				summary = [string]$_.summary
			}
		}
)
$domain.status = "PASS"
$domain.source = "known-issues-assessment.json"
$domain.details = "Exact-source known-issues assessment '$($assessment.assessmentId)' is PASS and all unresolved issues are release-note disclosures without unresolved BLOCKER/CRITICAL findings."
Write-JsonFile $releaseEvidence $releaseEvidencePath

& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessmentBinding.ps1") -OutputPath $outputRoot

Write-Host "Known-issues assessment binding PASS"
Write-Host "Assessment id: $($assessment.assessmentId)"
Write-Host "Assessment SHA-256: $hash"
Write-Host "Release KNOWN_ISSUES domain: PASS"
