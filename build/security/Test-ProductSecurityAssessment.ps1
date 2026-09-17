# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$AssessmentPath,
	[string]$ExpectedProductVersion = "",
	[string]$ExpectedSourceCommit = "",
	[string]$ExpectedBuildCommit = "",
	[string]$ExpectedBuildId = "",
	[switch]$RequirePass
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$schemaPath = Join-Path $repositoryRoot "schemas/security/v1/product-security-assessment.schema.json"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Has-Property {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Name
	)
	return @($Value.PSObject.Properties.Name) -contains $Name
}

Assert-Condition (Test-Path -LiteralPath $schemaPath -PathType Leaf) "Product-security assessment schema is missing."
$schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$schema.title -eq "rtaime Product Security Assessment") "Unexpected product-security assessment schema."

$fullAssessmentPath = [System.IO.Path]::GetFullPath($AssessmentPath)
Assert-Condition (Test-Path -LiteralPath $fullAssessmentPath -PathType Leaf) "Product-security assessment was not found at '$fullAssessmentPath'."
$assessment = Get-Content -LiteralPath $fullAssessmentPath -Raw | ConvertFrom-Json

foreach ($required in @("copyright", "schemaVersion", "assessmentId", "product", "source", "status", "checks", "findings", "reviewer", "assessedAtUtc")) {
	Assert-Condition (Has-Property -Value $assessment -Name $required) "Product-security assessment is missing '$required'."
}

Assert-Condition ([string]$assessment.schemaVersion -eq "1.0") "Unsupported product-security assessment schemaVersion."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.copyright)) "Product-security assessment copyright is required."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.assessmentId)) "Product-security assessment assessmentId is required."
Assert-Condition ([string]$assessment.product.name -eq "rtaime") "Product-security assessment product name must be 'rtaime'."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.product.version)) "Product-security assessment product version is required."
Assert-Condition ([string]$assessment.source.sourceCommit -match '^[0-9a-fA-F]{40,64}$') "Product-security assessment sourceCommit is invalid."
Assert-Condition ([string]$assessment.source.buildCommit -match '^[0-9a-fA-F]{40,64}$') "Product-security assessment buildCommit is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.source.buildId)) "Product-security assessment buildId is required."
Assert-Condition ([string]$assessment.status -in @("PASS", "FAIL", "UNVERIFIED")) "Product-security assessment status is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.reviewer.identity)) "Product-security assessment reviewer identity is required."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$assessment.reviewer.role)) "Product-security assessment reviewer role is required."

$assessedAt = [DateTimeOffset]::MinValue
Assert-Condition ([DateTimeOffset]::TryParse([string]$assessment.assessedAtUtc, [ref]$assessedAt)) "Product-security assessment assessedAtUtc is invalid."

$checkNames = @("sourceReview", "dependencyReview", "vulnerabilityTriage", "securityUpdatePath")
foreach ($checkName in $checkNames) {
	Assert-Condition (Has-Property -Value $assessment.checks -Name $checkName) "Product-security assessment is missing check '$checkName'."
	$checkStatus = [string]$assessment.checks.$checkName
	Assert-Condition ($checkStatus -in @("PASS", "FAIL", "UNVERIFIED")) "Product-security assessment check '$checkName' has invalid status '$checkStatus'."
}

$seenFindingIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$blockingFindings = @()
foreach ($finding in @($assessment.findings)) {
	foreach ($required in @("id", "severity", "status", "summary")) {
		Assert-Condition (Has-Property -Value $finding -Name $required) "Product-security finding is missing '$required'."
	}
	$id = [string]$finding.id
	$severity = [string]$finding.severity
	$findingStatus = [string]$finding.status
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($id)) "Product-security finding id is required."
	Assert-Condition ($seenFindingIds.Add($id)) "Product-security assessment contains duplicate finding id '$id'."
	Assert-Condition ($severity -in @("BLOCKER", "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFORMATIONAL")) "Product-security finding '$id' has invalid severity '$severity'."
	Assert-Condition ($findingStatus -in @("OPEN", "MITIGATED", "ACCEPTED_RISK", "CLOSED")) "Product-security finding '$id' has invalid status '$findingStatus'."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$finding.summary)) "Product-security finding '$id' summary is required."
	if ($severity -in @("BLOCKER", "CRITICAL", "HIGH") -and $findingStatus -ne "CLOSED") {
		$blockingFindings += $finding
	}
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedProductVersion)) {
	Assert-Condition ([string]$assessment.product.version -eq $ExpectedProductVersion) "Product-security assessment product version does not match the release evidence identity."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceCommit)) {
	Assert-Condition ([string]$assessment.source.sourceCommit -ieq $ExpectedSourceCommit) "Product-security assessment sourceCommit does not match the release evidence identity."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildCommit)) {
	Assert-Condition ([string]$assessment.source.buildCommit -ieq $ExpectedBuildCommit) "Product-security assessment buildCommit does not match the release evidence identity."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedBuildId)) {
	Assert-Condition ([string]$assessment.source.buildId -eq $ExpectedBuildId) "Product-security assessment buildId does not match the release evidence identity."
}

$failedChecks = @($checkNames | Where-Object { [string]$assessment.checks.$_ -eq "FAIL" })
$unverifiedChecks = @($checkNames | Where-Object { [string]$assessment.checks.$_ -eq "UNVERIFIED" })
if ([string]$assessment.status -eq "PASS") {
	Assert-Condition ($failedChecks.Count -eq 0) "PASS product-security assessment contains failed checks."
	Assert-Condition ($unverifiedChecks.Count -eq 0) "PASS product-security assessment contains unverified checks."
	Assert-Condition ($blockingFindings.Count -eq 0) "PASS product-security assessment contains unresolved BLOCKER, CRITICAL or HIGH findings."
}
if ([string]$assessment.status -eq "FAIL") {
	Assert-Condition ($failedChecks.Count -gt 0 -or $blockingFindings.Count -gt 0) "FAIL product-security assessment requires a failed check or unresolved high-severity finding."
}
if ($RequirePass) {
	Assert-Condition ([string]$assessment.status -eq "PASS") "Product-security assessment must be PASS for release-evidence binding."
}

Write-Host "Product security assessment verification PASS"
Write-Host "Assessment id: $($assessment.assessmentId)"
Write-Host "Status: $($assessment.status)"
Write-Host "Source commit: $($assessment.source.sourceCommit)"
Write-Host "Build commit: $($assessment.source.buildCommit)"
Write-Host "Build id: $($assessment.source.buildId)"
Write-Host "Findings: $(@($assessment.findings).Count)"
