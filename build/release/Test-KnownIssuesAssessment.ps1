# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$AssessmentPath,
	[string]$ExpectedProductVersion = "",
	[string]$ExpectedReleaseStage = "",
	[string]$ExpectedSourceCommit = "",
	[string]$ExpectedBuildCommit = "",
	[string]$ExpectedBuildId = "",
	[switch]$RequirePass
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$fullPath = [System.IO.Path]::GetFullPath($AssessmentPath)
Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) "Known-issues assessment was not found at '$fullPath'."
$assessment = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json

Assert-Condition ([string]$assessment.schemaVersion -eq "1.0") "Known-issues assessment schemaVersion must be 1.0."
Assert-Condition ([string]$assessment.productName -eq "rtaime") "Known-issues assessment product identity is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.assessmentId)) "Known-issues assessment requires assessmentId."
Assert-Condition ([string]$assessment.sourceCommit -match '^[0-9a-fA-F]{40,64}$') "Known-issues assessment sourceCommit is invalid."
Assert-Condition ([string]$assessment.buildCommit -match '^[0-9a-fA-F]{40,64}$') "Known-issues assessment buildCommit is invalid."
Assert-Condition ([string]$assessment.status -in @("PASS", "FAIL", "UNVERIFIED")) "Known-issues assessment status is invalid."
Assert-Condition ([string]$assessment.releaseStage -in @("DEV", "PREVIEW", "RELEASE_CANDIDATE", "STABLE")) "Known-issues assessment releaseStage is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.assessorRole)) "Known-issues assessment requires assessorRole."
[void][DateTimeOffset]::Parse([string]$assessment.assessedAtUtc, [Globalization.CultureInfo]::InvariantCulture)

if (-not [string]::IsNullOrWhiteSpace($ExpectedProductVersion)) {
	Assert-Condition ([string]$assessment.productVersion -eq $ExpectedProductVersion) "Known-issues assessment product version mismatch."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedReleaseStage)) {
	Assert-Condition ([string]$assessment.releaseStage -eq $ExpectedReleaseStage) "Known-issues assessment release stage mismatch."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceCommit)) {
	Assert-Condition ([string]$assessment.sourceCommit -eq $ExpectedSourceCommit) "Known-issues assessment source commit mismatch."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildCommit)) {
	Assert-Condition ([string]$assessment.buildCommit -eq $ExpectedBuildCommit) "Known-issues assessment build commit mismatch."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildId)) {
	Assert-Condition ([string]$assessment.buildId -eq $ExpectedBuildId) "Known-issues assessment build identity mismatch."
}

$issues = @($assessment.issues)
$ids = @($issues | ForEach-Object { [string]$_.id })
Assert-Condition (($ids | Sort-Object -Unique).Count -eq $ids.Count) "Known-issues assessment contains duplicate issue ids."
foreach ($issue in $issues) {
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$issue.id)) "Known issue id is required."
	Assert-Condition ([string]$issue.severity -in @("BLOCKER", "CRITICAL", "MAJOR", "MINOR", "INFORMATIONAL")) "Known issue '$($issue.id)' severity is invalid."
	Assert-Condition ([string]$issue.disposition -in @("OPEN", "ACCEPTED", "CLOSED")) "Known issue '$($issue.id)' disposition is invalid."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$issue.summary)) "Known issue '$($issue.id)' summary is required."
	if ([string]$issue.disposition -ne "CLOSED") {
		Assert-Condition ($issue.releaseNoteRequired -eq $true) "Unresolved known issue '$($issue.id)' must require release-note disclosure."
	}
}

if ([string]$assessment.status -eq "PASS") {
	$blocking = @($issues | Where-Object {
		[string]$_.severity -in @("BLOCKER", "CRITICAL") -and [string]$_.disposition -ne "CLOSED"
	})
	Assert-Condition ($blocking.Count -eq 0) "Known-issues PASS cannot contain unresolved BLOCKER or CRITICAL issues."
}

if ($RequirePass) {
	Assert-Condition ([string]$assessment.status -eq "PASS") "Known-issues assessment must be PASS."
}

Write-Host "Known-issues assessment verification PASS"
Write-Host "Assessment id: $($assessment.assessmentId)"
Write-Host "Status: $($assessment.status)"
Write-Host "Open/accepted issues: $(@($issues | Where-Object { [string]$_.disposition -ne 'CLOSED' }).Count)"
