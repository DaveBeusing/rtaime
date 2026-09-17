# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

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

function Read-RepositoryText {
	param([Parameter(Mandatory)][string]$Path)
	$fullPath = Join-Path $repositoryRoot $Path
	Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) "Required product-security artifact '$Path' is missing."
	return Get-Content -LiteralPath $fullPath -Raw
}

$securityPolicyText = Read-RepositoryText "SECURITY.md"
$governanceText = Read-RepositoryText "governance/product-security-policy.json"
$schemaText = Read-RepositoryText "schemas/security/v1/vulnerability-record.schema.json"
$assessmentSchemaText = Read-RepositoryText "schemas/security/v1/product-security-assessment.schema.json"
$documentationText = Read-RepositoryText "docs/ProductSecurityCompliance.md"
$assessmentDocumentationText = Read-RepositoryText "docs/ProductSecurityAssessment.md"
$releaseEvidenceText = Read-RepositoryText "build/release/New-ReleaseEvidence.ps1"
$releaseVerifierText = Read-RepositoryText "build/release/Test-ReleaseEvidence.ps1"
$releasePipelineText = Read-RepositoryText "build/release/Invoke-ReleasePipeline.ps1"
$assessmentVerifierText = Read-RepositoryText "build/security/Test-ProductSecurityAssessment.ps1"
$assessmentBinderText = Read-RepositoryText "build/security/Bind-ProductSecurityAssessment.ps1"
$assessmentBindingVerifierText = Read-RepositoryText "build/security/Test-ProductSecurityAssessmentBinding.ps1"
$assessmentFailureCasesText = Read-RepositoryText "build/security/Test-ProductSecurityAssessmentFailureCases.ps1"
$releaseSchemaText = Read-RepositoryText "schemas/release/v1/release-evidence.schema.json"
$repositorySecurityText = Read-RepositoryText "build/governance/Test-RepositorySecurity.ps1"

$policy = $governanceText | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Product-security policy schemaVersion must be 1.0."
Assert-Condition ([string]$policy.vulnerabilityDisclosurePolicy -eq "SECURITY.md") "Product-security policy must bind coordinated disclosure to SECURITY.md."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$policy.vulnerabilityContact)) "Product-security policy requires a vulnerability contact."
Assert-Condition ([bool]$policy.evidenceRules.failClosed) "Product-security evidence must fail closed."
Assert-Condition ([bool]$policy.evidenceRules.regulatorySubmissionRequiresExternalEvidence) "Regulatory submission status must require external evidence."
Assert-Condition ([bool]$policy.evidenceRules.releaseSecurityPassRequiresCompletedAssessment) "Release SECURITY PASS must require completed assessment evidence."
Assert-Condition ([bool]$policy.evidenceRules.sbomRequired) "Product-security policy must require an SBOM."
Assert-Condition ([bool]$policy.evidenceRules.securityReviewRequired) "Product-security policy must require security review evidence."
Assert-Condition ([bool]$policy.evidenceRules.signedSecurityUpdatePathRequired) "Security updates must use the signed update path."
Assert-Condition ([string]$policy.cra.regulation -eq "EU 2024/2847") "CRA policy must identify Regulation (EU) 2024/2847."
Assert-Condition ([string]$policy.cra.article14ReportingAppliesFrom -eq "2026-09-11") "CRA Article 14 reporting application date is incorrect."
Assert-Condition ([string]$policy.cra.fullApplicationFrom -eq "2027-12-11") "CRA full-application date is incorrect."
Assert-Condition ([int]$policy.cra.activelyExploitedVulnerability.earlyWarningHours -eq 24) "CRA actively exploited vulnerability early-warning window must be 24 hours."
Assert-Condition ([int]$policy.cra.activelyExploitedVulnerability.notificationHours -eq 72) "CRA actively exploited vulnerability notification window must be 72 hours."
Assert-Condition ([int]$policy.cra.activelyExploitedVulnerability.finalReportAfterMitigationDays -eq 14) "CRA actively exploited vulnerability final-report window must be 14 days after mitigation is available."
Assert-Condition ([int]$policy.cra.severeSecurityIncident.earlyWarningHours -eq 24) "CRA severe-incident early-warning window must be 24 hours."
Assert-Condition ([int]$policy.cra.severeSecurityIncident.notificationHours -eq 72) "CRA severe-incident notification window must be 72 hours."
Assert-Condition ([string]$policy.supportPeriod.status -eq "UNVERIFIED") "Support-period evidence must remain UNVERIFIED until explicitly approved."
Assert-Condition ([string]$policy.conformityClaim.status -eq "UNVERIFIED") "CRA conformity must remain UNVERIFIED in the foundation package."

$requiredLifecycle = @("RECEIVED", "TRIAGED", "ACCEPTED", "REJECTED", "REMEDIATING", "REMEDIATED", "DISCLOSED")
foreach ($state in $requiredLifecycle) {
	Assert-Condition (@($policy.vulnerabilityLifecycle) -contains $state) "Vulnerability lifecycle is missing '$state'."
}

Assert-Condition ($securityPolicyText -match 'coordinated vulnerability disclosure') "SECURITY.md must define coordinated vulnerability disclosure."
Assert-Condition ($securityPolicyText -match 'Do not open a public GitHub issue') "SECURITY.md must direct undisclosed vulnerabilities away from public issues."
Assert-Condition ($securityPolicyText -match 'regulatory report') "SECURITY.md must separate regulatory reporting from public disclosure."
Assert-Condition ($securityPolicyText -match 'UNVERIFIED') "SECURITY.md must preserve fail-closed support-period status."

$schema = $schemaText | ConvertFrom-Json
Assert-Condition ([string]$schema.title -eq "rtaime Vulnerability Record") "Vulnerability record schema title is unexpected."
Assert-Condition (@($schema.required) -contains "regulatoryReporting") "Vulnerability record must require regulatory reporting assessment."
Assert-Condition ($schemaText -match 'ACTIVELY_EXPLOITED') "Vulnerability schema must represent actively exploited state explicitly."
Assert-Condition ($schemaText -match 'EXTERNALLY_VERIFIED') "Vulnerability schema must represent externally verified regulatory evidence explicitly."
Assert-Condition ($schemaText -match 'sourceCommits') "Vulnerability schema must bind affected source commits."

$assessmentSchema = $assessmentSchemaText | ConvertFrom-Json
Assert-Condition ([string]$assessmentSchema.title -eq "rtaime Product Security Assessment") "Product-security assessment schema title is unexpected."
Assert-Condition (@($assessmentSchema.required) -contains "assessmentId") "Product-security assessment schema must require assessmentId."
Assert-Condition ($assessmentSchemaText -match 'sourceCommit') "Product-security assessment schema must bind sourceCommit."
Assert-Condition ($assessmentSchemaText -match 'buildCommit') "Product-security assessment schema must bind buildCommit."
Assert-Condition ($assessmentSchemaText -match 'buildId') "Product-security assessment schema must bind buildId."
Assert-Condition ($assessmentSchemaText -match 'vulnerabilityTriage') "Product-security assessment schema must require vulnerability triage."
Assert-Condition ($assessmentSchemaText -match 'securityUpdatePath') "Product-security assessment schema must require security-update-path assessment."

Assert-Condition ($documentationText -match 'does not assert legal conformity') "Product-security documentation must reject unsupported conformity claims."
Assert-Condition ($documentationText -match '2026-09-11') "Product-security documentation must record the Article 14 application date."
Assert-Condition ($documentationText -match '2027-12-11') "Product-security documentation must record the CRA full-application date."
Assert-Condition ($documentationText -match 'Missing evidence remains `UNVERIFIED`') "Product-security documentation must preserve fail-closed evidence semantics."
Assert-Condition ($assessmentDocumentationText -match 'exact source') "Product-security assessment documentation must define exact-source binding."
Assert-Condition ($assessmentDocumentationText -match 'before release attestation') "Product-security assessment documentation must define pre-signing binding order."
Assert-Condition ($assessmentDocumentationText -match 'does not assert CRA conformity') "Product-security assessment documentation must not convert engineering evidence into a legal conformity claim."

Assert-Condition ($releaseEvidenceText -match 'domain = "SECURITY"; status = "UNVERIFIED"') "Release evidence must default SECURITY to UNVERIFIED when no assessment is bound."
Assert-Condition ($releasePipelineText -match 'SecurityAssessmentPath') "Release pipeline must expose an explicit SecurityAssessmentPath input."
Assert-Condition ($releasePipelineText -match 'Bind-ProductSecurityAssessment\.ps1') "Release pipeline must bind product-security assessment before signing."
Assert-Condition ($releasePipelineText -match 'New-ReleaseAttestation\.ps1') "Release pipeline must retain release attestation after evidence binding."
Assert-Condition ($releaseVerifierText -match 'Test-ProductSecurityAssessmentBinding\.ps1') "Release evidence verifier must verify product-security assessment binding."
Assert-Condition ($assessmentVerifierText -match 'ExpectedSourceCommit') "Product-security assessment verifier must support exact source matching."
Assert-Condition ($assessmentVerifierText -match 'ExpectedBuildCommit') "Product-security assessment verifier must support exact build matching."
Assert-Condition ($assessmentVerifierText -match 'ExpectedBuildId') "Product-security assessment verifier must support exact build identity matching."
Assert-Condition ($assessmentBinderText -match 'release-attestation\.json') "Product-security assessment binder must reject post-attestation mutation."
Assert-Condition ($assessmentBindingVerifierText -match 'security-assessment\.json') "Product-security assessment binding verifier must verify the canonical assessment artifact."
Assert-Condition ($assessmentFailureCasesText -match 'source commit mismatch') "Product-security assessment failure qualification must cover source mismatch."
Assert-Condition ($assessmentFailureCasesText -match 'tampered bound assessment') "Product-security assessment failure qualification must cover tampering."
Assert-Condition ($assessmentFailureCasesText -match 'unresolved HIGH finding') "Product-security assessment failure qualification must cover unresolved high-severity findings."
Assert-Condition ($releaseSchemaText -match 'securityAssessment') "Release evidence schema must allow the product-security assessment hash reference."
Assert-Condition ($repositorySecurityText -match 'Test-ProductSecurityPolicy\.ps1') "Repository Security gate must execute the product-security policy verification."

& (Join-Path $repositoryRoot "build/security/Test-ProductSecurityAssessmentFailureCases.ps1")

Write-Host "Product security policy verification PASS"
Write-Host "CRA Article 14 reporting baseline: 2026-09-11"
Write-Host "CRA full application baseline: 2027-12-11"
Write-Host "Release SECURITY default: UNVERIFIED"
Write-Host "Exact-source security assessment binding: ENFORCED"
Write-Host "Support period: UNVERIFIED"
Write-Host "CRA conformity claim: UNVERIFIED"
