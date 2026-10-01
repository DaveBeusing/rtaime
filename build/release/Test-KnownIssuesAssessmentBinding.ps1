# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
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

$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
	[System.IO.Path]::GetFullPath($OutputPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}
Assert-Condition (Test-Path -LiteralPath $outputRoot -PathType Container) "Release evidence directory was not found at '$outputRoot'."

$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"
Assert-Condition (Test-Path -LiteralPath $releaseEvidencePath -PathType Leaf) "Release evidence manifest is missing."
$releaseEvidence = Get-Content -LiteralPath $releaseEvidencePath -Raw | ConvertFrom-Json

$domains = @($releaseEvidence.evidenceDomains | Where-Object { [string]$_.domain -eq "KNOWN_ISSUES" })
Assert-Condition ($domains.Count -eq 1) "Release evidence must contain exactly one KNOWN_ISSUES domain."
$domain = $domains[0]
$status = [string]$domain.status
Assert-Condition ($status -in @("PASS", "UNVERIFIED")) "Release KNOWN_ISSUES domain must be PASS or UNVERIFIED."

$hasReference = @($releaseEvidence.PSObject.Properties.Name) -contains "knownIssuesAssessment"
if ($status -eq "UNVERIFIED") {
	Assert-Condition (-not $hasReference) "UNVERIFIED KNOWN_ISSUES domain must not carry known-issues assessment evidence."
	Write-Host "Known-issues assessment binding verification PASS"
	Write-Host "Release KNOWN_ISSUES domain: UNVERIFIED"
	Write-Host "Assessment binding: none"
	return
}

Assert-Condition ($hasReference) "PASS KNOWN_ISSUES domain requires knownIssuesAssessment evidence."
$reference = $releaseEvidence.knownIssuesAssessment
Assert-Condition ([string]$reference.path -eq "known-issues-assessment.json") "Known-issues assessment must use canonical path 'known-issues-assessment.json'."
Assert-Condition ([string]$reference.sha256 -match '^[0-9a-f]{64}$') "Known-issues assessment reference SHA-256 is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$reference.assessmentId)) "Known-issues assessment reference assessmentId is required."

$assessmentPath = [System.IO.Path]::GetFullPath((Join-Path $outputRoot ([string]$reference.path)))
$rootPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
Assert-Condition ($assessmentPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "Known-issues assessment reference escapes the release evidence directory."
Assert-Condition (Test-Path -LiteralPath $assessmentPath -PathType Leaf) "Referenced known-issues assessment is missing."
Assert-Condition ((Get-Sha256 $assessmentPath) -eq [string]$reference.sha256) "Known-issues assessment SHA-256 mismatch."

$assessment = Get-Content -LiteralPath $assessmentPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$assessment.assessmentId -eq [string]$reference.assessmentId) "Known-issues assessment id mismatch."
& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessment.ps1") -AssessmentPath $assessmentPath -ExpectedProductVersion ([string]$releaseEvidence.productVersion) -ExpectedReleaseStage ([string]$releaseEvidence.releaseStage) -ExpectedSourceCommit ([string]$releaseEvidence.sourceCommit) -ExpectedBuildCommit ([string]$releaseEvidence.buildCommit) -ExpectedBuildId ([string]$releaseEvidence.buildId) -RequirePass

Assert-Condition ([string]$domain.source -eq "known-issues-assessment.json") "PASS KNOWN_ISSUES domain must cite known-issues-assessment.json."
$expectedIssues = @($assessment.issues | Where-Object { [string]$_.disposition -ne "CLOSED" })
$boundIssues = @($releaseEvidence.knownIssues)
Assert-Condition ($boundIssues.Count -eq $expectedIssues.Count) "Release knownIssues count differs from unresolved assessment issues."
foreach ($issue in $expectedIssues) {
	$bound = @($boundIssues | Where-Object { [string]$_.id -eq [string]$issue.id })
	Assert-Condition ($bound.Count -eq 1) "Release knownIssues is missing assessment issue '$($issue.id)'."
	Assert-Condition ([string]$bound[0].severity -eq [string]$issue.severity) "Release known issue '$($issue.id)' severity mismatch."
	Assert-Condition ([string]$bound[0].summary -eq [string]$issue.summary) "Release known issue '$($issue.id)' summary mismatch."
}

Write-Host "Known-issues assessment binding verification PASS"
Write-Host "Release KNOWN_ISSUES domain: PASS"
Write-Host "Assessment id: $($assessment.assessmentId)"
Write-Host "Unresolved disclosed issues: $($boundIssues.Count)"
