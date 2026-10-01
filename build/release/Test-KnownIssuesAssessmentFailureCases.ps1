# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-known-issues-{0}" -f [Guid]::NewGuid().ToString("N"))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}
function Write-Json {
	param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
	$json = $Value | ConvertTo-Json -Depth 32
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}
function Expect-Failure {
	param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Case)
	$failed = $false
	try { & $Action } catch { $failed = $true }
	Assert-Condition $failed "Expected known-issues failure case '$Case' to fail."
}

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
	$source = "1111111111111111111111111111111111111111"
	$build = "2222222222222222222222222222222222222222"
	$assessmentPath = Join-Path $tempRoot "assessment.json"
	$assessment = [ordered]@{
		copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
		schemaVersion = "1.0"
		assessmentId = "known-issues-test"
		productName = "rtaime"
		productVersion = "1.0.0"
		releaseStage = "STABLE"
		sourceCommit = $source
		buildCommit = $build
		buildId = "known-issues-test-build"
		status = "PASS"
		assessedAtUtc = "2026-10-01T00:00:00Z"
		assessorRole = "Release Management"
		issues = @(
			[ordered]@{
				id = "KNOWN-1"
				severity = "MAJOR"
				summary = "Documented non-blocking limitation."
				disposition = "ACCEPTED"
				releaseNoteRequired = $true
				workaround = "Use the documented bounded workaround."
			}
		)
	}
	Write-Json $assessment $assessmentPath
	& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessment.ps1") -AssessmentPath $assessmentPath -ExpectedProductVersion "1.0.0" -ExpectedReleaseStage "STABLE" -ExpectedSourceCommit $source -ExpectedBuildCommit $build -ExpectedBuildId "known-issues-test-build" -RequirePass

	$critical = $assessment | ConvertTo-Json -Depth 32 | ConvertFrom-Json
	$critical.issues[0].severity = "CRITICAL"
	$criticalPath = Join-Path $tempRoot "critical.json"
	Write-Json $critical $criticalPath
	Expect-Failure -Case "unresolved critical issue" -Action {
		& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessment.ps1") -AssessmentPath $criticalPath -RequirePass
	}

	$undisclosed = $assessment | ConvertTo-Json -Depth 32 | ConvertFrom-Json
	$undisclosed.issues[0].releaseNoteRequired = $false
	$undisclosedPath = Join-Path $tempRoot "undisclosed.json"
	Write-Json $undisclosed $undisclosedPath
	Expect-Failure -Case "unresolved issue without release-note disclosure" -Action {
		& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessment.ps1") -AssessmentPath $undisclosedPath -RequirePass
	}

	$mismatch = $assessment | ConvertTo-Json -Depth 32 | ConvertFrom-Json
	$mismatch.sourceCommit = "3333333333333333333333333333333333333333"
	$mismatchPath = Join-Path $tempRoot "mismatch.json"
	Write-Json $mismatch $mismatchPath
	Expect-Failure -Case "source commit mismatch" -Action {
		& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessment.ps1") -AssessmentPath $mismatchPath -ExpectedSourceCommit $source -RequirePass
	}

	$evidenceRoot = Join-Path $tempRoot "evidence"
	New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
	$releaseEvidence = [ordered]@{
		productVersion = "1.0.0"
		releaseStage = "STABLE"
		sourceCommit = $source
		buildCommit = $build
		buildId = "known-issues-test-build"
		evidenceDomains = @(
			[ordered]@{
				domain = "KNOWN_ISSUES"
				status = "UNVERIFIED"
				severity = "MAJOR"
				source = "release-policy"
				details = "Known issues assessment not yet bound."
			}
		)
		knownIssues = @()
	}
	Write-Json $releaseEvidence (Join-Path $evidenceRoot "release-evidence.json")
	& (Join-Path $PSScriptRoot "Bind-KnownIssuesAssessment.ps1") -AssessmentPath $assessmentPath -OutputPath $evidenceRoot
	& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessmentBinding.ps1") -OutputPath $evidenceRoot

	$bound = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-evidence.json") -Raw | ConvertFrom-Json
	Assert-Condition ([string](@($bound.evidenceDomains)[0].status) -eq "PASS") "Known-issues binding did not promote the domain to PASS."
	Assert-Condition (@($bound.knownIssues).Count -eq 1) "Known-issues binding did not project unresolved disclosed issues."

	Add-Content -LiteralPath (Join-Path $evidenceRoot "known-issues-assessment.json") -Value " "
	Expect-Failure -Case "tampered bound assessment" -Action {
		& (Join-Path $PSScriptRoot "Test-KnownIssuesAssessmentBinding.ps1") -OutputPath $evidenceRoot
	}

	Write-Host "Known-issues assessment failure qualification PASS"
} finally {
	if (Test-Path -LiteralPath $tempRoot) {
		Remove-Item -LiteralPath $tempRoot -Recurse -Force
	}
}
