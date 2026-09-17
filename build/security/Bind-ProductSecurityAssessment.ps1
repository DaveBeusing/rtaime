# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$AssessmentPath,
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

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
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
Assert-Condition (Test-Path -LiteralPath $fullAssessmentPath -PathType Leaf) "Product-security assessment was not found at '$fullAssessmentPath'."
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $outputRoot "release-attestation.json"))) "Product-security assessment must be bound before release attestation is created."
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $outputRoot "release-record.json"))) "Product-security assessment must be bound before release record creation."

$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"
Assert-Condition (Test-Path -LiteralPath $releaseEvidencePath -PathType Leaf) "Release evidence manifest is missing."
$releaseEvidence = Get-Content -LiteralPath $releaseEvidencePath -Raw | ConvertFrom-Json

$securityDomains = @($releaseEvidence.evidenceDomains | Where-Object { [string]$_.domain -eq "SECURITY" })
Assert-Condition ($securityDomains.Count -eq 1) "Release evidence must contain exactly one SECURITY domain."
$securityDomain = $securityDomains[0]
Assert-Condition ([string]$securityDomain.status -eq "UNVERIFIED") "Product-security assessment binding requires an UNVERIFIED SECURITY domain before promotion."
Assert-Condition (-not (@($releaseEvidence.PSObject.Properties.Name) -contains "securityAssessment")) "Release evidence already contains product-security assessment binding."

& (Join-Path $PSScriptRoot "Test-ProductSecurityAssessment.ps1") `
	-AssessmentPath $fullAssessmentPath `
	-ExpectedProductVersion ([string]$releaseEvidence.productVersion) `
	-ExpectedSourceCommit ([string]$releaseEvidence.sourceCommit) `
	-ExpectedBuildCommit ([string]$releaseEvidence.buildCommit) `
	-ExpectedBuildId ([string]$releaseEvidence.buildId) `
	-RequirePass

$assessment = Get-Content -LiteralPath $fullAssessmentPath -Raw | ConvertFrom-Json
$targetAssessmentPath = Join-Path $outputRoot "security-assessment.json"
if ([System.IO.Path]::GetFullPath($fullAssessmentPath) -ne [System.IO.Path]::GetFullPath($targetAssessmentPath)) {
	Copy-Item -LiteralPath $fullAssessmentPath -Destination $targetAssessmentPath -Force
}
$assessmentHash = Get-Sha256 -Path $targetAssessmentPath

$assessmentReference = [ordered]@{
	path = "security-assessment.json"
	sha256 = $assessmentHash
	assessmentId = [string]$assessment.assessmentId
}
$releaseEvidence | Add-Member -MemberType NoteProperty -Name securityAssessment -Value $assessmentReference
$securityDomain.status = "PASS"
$securityDomain.source = "security-assessment.json"
$securityDomain.details = "Exact-source product-security assessment '$($assessment.assessmentId)' is PASS and is hash-bound to this release evidence subject."
Write-JsonFile -Value $releaseEvidence -Path $releaseEvidencePath

& (Join-Path $PSScriptRoot "Test-ProductSecurityAssessmentBinding.ps1") -OutputPath $outputRoot

Write-Host "Product security assessment binding PASS"
Write-Host "Assessment id: $($assessment.assessmentId)"
Write-Host "Assessment SHA-256: $assessmentHash"
Write-Host "Release SECURITY domain: PASS"
