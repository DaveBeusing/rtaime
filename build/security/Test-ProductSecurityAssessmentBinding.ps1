# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = "artifacts/release-evidence"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
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

$securityDomains = @($releaseEvidence.evidenceDomains | Where-Object { [string]$_.domain -eq "SECURITY" })
Assert-Condition ($securityDomains.Count -eq 1) "Release evidence must contain exactly one SECURITY domain."
$securityDomain = $securityDomains[0]
$securityStatus = [string]$securityDomain.status
Assert-Condition ($securityStatus -in @("PASS", "UNVERIFIED")) "Release SECURITY domain must be PASS or UNVERIFIED."

$hasAssessmentReference = @($releaseEvidence.PSObject.Properties.Name) -contains "securityAssessment"
if ($securityStatus -eq "UNVERIFIED") {
	Assert-Condition (-not $hasAssessmentReference) "UNVERIFIED release SECURITY domain must not carry product-security assessment evidence."
	Write-Host "Product security assessment binding verification PASS"
	Write-Host "Release SECURITY domain: UNVERIFIED"
	Write-Host "Assessment binding: none"
	return
}

Assert-Condition ($hasAssessmentReference) "PASS release SECURITY domain requires securityAssessment evidence."
$reference = $releaseEvidence.securityAssessment
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$reference.path)) "Product-security assessment reference path is required."
Assert-Condition ([string]$reference.path -eq "security-assessment.json") "Product-security assessment must use canonical release evidence path 'security-assessment.json'."
Assert-Condition ([string]$reference.sha256 -match '^[0-9a-f]{64}$') "Product-security assessment reference SHA-256 is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$reference.assessmentId)) "Product-security assessment reference assessmentId is required."

$assessmentPath = [System.IO.Path]::GetFullPath((Join-Path $outputRoot ([string]$reference.path)))
$rootPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
Assert-Condition ($assessmentPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "Product-security assessment reference escapes the release evidence directory."
Assert-Condition (Test-Path -LiteralPath $assessmentPath -PathType Leaf) "Referenced product-security assessment is missing."
Assert-Condition ((Get-Sha256 -Path $assessmentPath) -eq [string]$reference.sha256) "Product-security assessment SHA-256 does not match release evidence reference."

$assessment = Get-Content -LiteralPath $assessmentPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$assessment.assessmentId -eq [string]$reference.assessmentId) "Product-security assessment id does not match release evidence reference."

& (Join-Path $PSScriptRoot "Test-ProductSecurityAssessment.ps1") `
	-AssessmentPath $assessmentPath `
	-ExpectedProductVersion ([string]$releaseEvidence.productVersion) `
	-ExpectedSourceCommit ([string]$releaseEvidence.sourceCommit) `
	-ExpectedBuildCommit ([string]$releaseEvidence.buildCommit) `
	-ExpectedBuildId ([string]$releaseEvidence.buildId) `
	-RequirePass

Assert-Condition ([string]$securityDomain.source -eq "security-assessment.json") "PASS release SECURITY domain must cite security-assessment.json."

Write-Host "Product security assessment binding verification PASS"
Write-Host "Release SECURITY domain: PASS"
Write-Host "Assessment id: $($assessment.assessmentId)"
Write-Host "Assessment SHA-256: $($reference.sha256)"
