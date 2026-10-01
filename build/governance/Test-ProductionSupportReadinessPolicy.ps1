# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Repository-Path {
	param([Parameter(Mandatory)][string]$Path)
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Read-Text {
	param([Parameter(Mandatory)][string]$Path)
	$full = Repository-Path $Path
	Assert-Condition (Test-Path -LiteralPath $full -PathType Leaf) "Required production-support artifact is missing: '$Path'."
	return Get-Content -LiteralPath $full -Raw
}

function Clone-JsonObject {
	param([Parameter(Mandatory)]$Value)
	return ($Value | ConvertTo-Json -Depth 64 | ConvertFrom-Json)
}

function Get-SupportPolicyErrors {
	param([Parameter(Mandatory)]$Policy)
	$errors = [System.Collections.Generic.List[string]]::new()

	if ([string]$Policy.schemaVersion -ne "1.0") { $errors.Add("schemaVersion") }
	if ([string]$Policy.frameworkStatus -ne "PASS") { $errors.Add("frameworkStatus") }

	$stable = $Policy.lifecycle.stableRelease
	$stableStatus = [string]$stable.status
	if ($stableStatus -eq "PASS") {
		foreach ($property in @("maintenanceMonths", "securityMonths", "eolNotificationLeadDays")) {
			$value = $stable.$property
			if ($null -eq $value -or [int]$value -le 0) { $errors.Add("stable.$property") }
		}
	} elseif ($stableStatus -eq "UNVERIFIED") {
		foreach ($property in @("maintenanceMonths", "securityMonths", "eolNotificationLeadDays")) {
			if ($null -ne $stable.$property) { $errors.Add("unapproved-stable.$property") }
		}
	} elseif ($stableStatus -ne "FAIL") {
		$errors.Add("stable.status")
	}
	if (-not [bool]$stable.approvalRequired) { $errors.Add("stable.approvalRequired") }

	$preview = $Policy.lifecycle.preview

	$security = $Policy.securitySupport
	if ([string]$security.remediationTargetsStatus -eq "PASS") {
		foreach ($severity in @("CRITICAL", "HIGH", "MEDIUM", "LOW")) {
			$value = $security.severityTargetsDays.$severity
			if ($null -eq $value -or [int]$value -le 0) { $errors.Add("security.$severity") }
		}
	} elseif ([string]$security.remediationTargetsStatus -eq "UNVERIFIED") {
		foreach ($severity in @("CRITICAL", "HIGH", "MEDIUM", "LOW")) {
			if ($null -ne $security.severityTargetsDays.$severity) { $errors.Add("unapproved-security.$severity") }
		}
	}

	$deprecation = $Policy.upgradeDeprecation.contractDeprecation
	if ([string]$deprecation.status -eq "PASS") {
		if ($null -eq $deprecation.minimumNoticeDays -or [int]$deprecation.minimumNoticeDays -le 0) {
			$errors.Add("deprecation.minimumNoticeDays")
		}
	} elseif ([string]$deprecation.status -eq "UNVERIFIED" -and $null -ne $deprecation.minimumNoticeDays) {
		$errors.Add("unapproved-deprecation.minimumNoticeDays")
	}

	$stableLines = @($Policy.supportedVersions.stableLines)
	$versionLines = @($stableLines | ForEach-Object { [string]$_.versionLine })
	if (@($versionLines | Sort-Object -Unique).Count -ne $versionLines.Count) {
		$errors.Add("stableLine.duplicateVersionLine")
	}

	foreach ($line in $stableLines) {
		$status = [string]$line.status
		$dateProperties = @("supportStart", "maintenanceEnd", "securityEnd", "eolDate")
		if ($status -in @("SUPPORTED", "EOL")) {
			if ($status -eq "SUPPORTED" -and $stableStatus -ne "PASS") {
				$errors.Add("stableLine.supportedWithoutLifecycleCommitment")
			}
			$parsedDates = @{}
			foreach ($property in $dateProperties) {
				$value = [string]$line.$property
				if ([string]::IsNullOrWhiteSpace($value)) {
					$errors.Add("stableLine.$property")
					continue
				}
				$parsed = [DateTime]::MinValue
				if (-not [DateTime]::TryParseExact(
					$value,
					"yyyy-MM-dd",
					[Globalization.CultureInfo]::InvariantCulture,
					[Globalization.DateTimeStyles]::None,
					[ref]$parsed)) {
					$errors.Add("stableLine.$property.invalid")
				} else {
					$parsedDates[$property] = $parsed
				}
			}
			if ($parsedDates.Count -eq $dateProperties.Count) {
				if ($parsedDates["maintenanceEnd"] -lt $parsedDates["supportStart"]) { $errors.Add("stableLine.maintenanceOrder") }
				if ($parsedDates["securityEnd"] -lt $parsedDates["maintenanceEnd"]) { $errors.Add("stableLine.securityOrder") }
				if ($parsedDates["eolDate"] -lt $parsedDates["securityEnd"]) { $errors.Add("stableLine.eolOrder") }
			}
		} elseif ($status -eq "UNVERIFIED") {
			foreach ($property in $dateProperties) {
				if ($null -ne $line.$property) { $errors.Add("stableLine.unverified.$property") }
			}
		}
	}

	$previewVersion = $Policy.supportedVersions.preview
	if ([string]$previewVersion.status -eq "PASS") {
		if ([string]$preview.status -ne "PASS") { $errors.Add("preview.versionWithoutLifecycleCommitment") }
		if ([string]$previewVersion.supportMode -notin @("DEFINED", "BEST_EFFORT")) {
			$errors.Add("preview.supportMode")
		} elseif ([string]$previewVersion.supportMode -eq "DEFINED") {
			if (-not [bool]$preview.maintenanceCommitment -or -not [bool]$preview.securityCommitment) {
				$errors.Add("preview.definedCommitment")
			}
		} elseif ([bool]$preview.maintenanceCommitment -or [bool]$preview.securityCommitment) {
			$errors.Add("preview.bestEffortMustNotPromiseCommitments")
		}
	} elseif ([string]$previewVersion.status -eq "UNVERIFIED" -and [string]$previewVersion.supportMode -ne "UNVERIFIED") {
		$errors.Add("preview.unverifiedSupportMode")
	}
	if ([string]$preview.status -eq "PASS" -and [string]$previewVersion.status -ne "PASS") {
		$errors.Add("preview.lifecycleWithoutVersionPolicy")
	}
	if ([string]$preview.status -eq "UNVERIFIED" -and ([bool]$preview.maintenanceCommitment -or [bool]$preview.securityCommitment)) {
		$errors.Add("preview.unverifiedCommitment")
	}

	return @($errors)
}

function Get-PlatformMatrixErrors {
	param([Parameter(Mandatory)]$Matrix)
	$errors = [System.Collections.Generic.List[string]]::new()

	if ([string]$Matrix.schemaVersion -ne "1.0") { $errors.Add("schemaVersion") }
	if (-not [bool]$Matrix.evidencePolicy.supportedRequiresEvidence) { $errors.Add("supportedRequiresEvidence") }
	if (-not [bool]$Matrix.evidencePolicy.materialChangeInvalidatesEvidence) { $errors.Add("materialChangeInvalidatesEvidence") }
	if ([string]$Matrix.compatibilityRules.materialChangePolicy -ne "REQUALIFY") { $errors.Add("materialChangePolicy") }

	$configurations = @($Matrix.configurations)
	$supportedConfigurations = @($configurations | Where-Object { [string]$_.supportStatus -eq "SUPPORTED" })
	if ($supportedConfigurations.Count -gt 0 -and [string]$Matrix.matrixStatus -ne "PASS") {
		$errors.Add("supportedRequiresMatrixPass")
	}
	if ([string]$Matrix.matrixStatus -eq "PASS" -and $supportedConfigurations.Count -eq 0) {
		$errors.Add("matrixPassWithoutSupportedConfiguration")
	}

	foreach ($configuration in $configurations) {
		$evidencePaths = @($configuration.qualificationEvidence)
		foreach ($evidence in $evidencePaths) {
			if (-not (Test-Path -LiteralPath (Repository-Path ([string]$evidence)) -PathType Leaf)) {
				$errors.Add("staleEvidence:$evidence")
			}
		}
		foreach ($runtime in @($configuration.runtimeRequirements)) {
			if (-not (Test-Path -LiteralPath (Repository-Path ([string]$runtime.source)) -PathType Leaf)) {
				$errors.Add("runtimeSource:$($runtime.source)")
			}
		}

		if ([string]$configuration.supportStatus -eq "SUPPORTED") {
			if ($evidencePaths.Count -eq 0) { $errors.Add("supportedWithoutEvidence") }
			foreach ($value in @(
				$configuration.platform.edition,
				$configuration.platform.versionFamily,
				$configuration.gpu.driverRange,
				$configuration.mediaIo.device,
				$configuration.mediaIo.driverRange
			)) {
				if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
					$errors.Add("supportedIncompleteTuple")
				}
			}
			if ([string]$configuration.gpu.qualificationStatus -ne "PASS") { $errors.Add("supportedGpuWithoutPassEvidence") }
			if ([string]$configuration.mediaIo.qualificationStatus -ne "PASS") { $errors.Add("supportedMediaIoWithoutPassEvidence") }

			foreach ($evidence in $evidencePaths) {
				$fullEvidencePath = Repository-Path ([string]$evidence)
				if ([System.IO.Path]::GetExtension($fullEvidencePath) -ne ".json") {
					$errors.Add("supportedEvidenceNotMachineReadable:$evidence")
					continue
				}
				try {
					$evidenceDocument = Get-Content -LiteralPath $fullEvidencePath -Raw | ConvertFrom-Json
					$passed = $false
					if (@($evidenceDocument.PSObject.Properties.Name) -contains "status") {
						$passed = [string]$evidenceDocument.status -in @("PASS", "PASSED")
					} elseif (@($evidenceDocument.PSObject.Properties.Name) -contains "requirements") {
						$requirements = @($evidenceDocument.requirements)
						$passed = $requirements.Count -gt 0 -and @($requirements | Where-Object { [string]$_.status -ne "PASSED" }).Count -eq 0
					}
					if (-not $passed) { $errors.Add("supportedEvidenceNotPassed:$evidence") }
					if (-not (@($evidenceDocument.PSObject.Properties.Name) -contains "sourceCommit") -or
						[string]$evidenceDocument.sourceCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
						$errors.Add("supportedEvidenceMissingSourceCommit:$evidence")
					}
				} catch {
					$errors.Add("supportedEvidenceInvalid:$evidence")
				}
			}
		}

		foreach ($networkProvider in @($configuration.networkProviders)) {
			if ([string]$networkProvider.supportStatus -ne "SUPPORTED") { continue }
			$networkEvidence = @($networkProvider.qualificationEvidence)
			if ($networkEvidence.Count -eq 0) {
				$errors.Add("supportedNetworkProviderWithoutEvidence:$($networkProvider.provider)")
				continue
			}
			foreach ($evidence in $networkEvidence) {
				$fullEvidencePath = Repository-Path ([string]$evidence)
				if (-not (Test-Path -LiteralPath $fullEvidencePath -PathType Leaf)) {
					$errors.Add("staleNetworkEvidence:$evidence")
					continue
				}
				if ([System.IO.Path]::GetExtension($fullEvidencePath) -ne ".json") {
					$errors.Add("supportedNetworkEvidenceNotMachineReadable:$evidence")
					continue
				}
				try {
					$evidenceDocument = Get-Content -LiteralPath $fullEvidencePath -Raw | ConvertFrom-Json
					$passed = @($evidenceDocument.PSObject.Properties.Name) -contains "status" -and
						[string]$evidenceDocument.status -in @("PASS", "PASSED")
					if (-not $passed) { $errors.Add("supportedNetworkEvidenceNotPassed:$evidence") }
					if (-not (@($evidenceDocument.PSObject.Properties.Name) -contains "sourceCommit") -or
						[string]$evidenceDocument.sourceCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
						$errors.Add("supportedNetworkEvidenceMissingSourceCommit:$evidence")
					}
				} catch {
					$errors.Add("supportedNetworkEvidenceInvalid:$evidence")
				}
			}
		}
	}

	return @($errors)
}

$supportPolicyText = Read-Text "docs/Governance/ProductSupportPolicy.json"
$platformMatrixText = Read-Text "docs/Governance/PlatformSupportMatrix.json"
$deploymentBaselineText = Read-Text "docs/Governance/ProductionDeploymentBaseline.json"
$supportSchemaText = Read-Text "schemas/support/v1/product-support-policy.schema.json"
$platformSchemaText = Read-Text "schemas/support/v1/platform-support-matrix.schema.json"
$readinessSchemaText = Read-Text "schemas/support/v1/stable-readiness.schema.json"
$stableVerifierText = Read-Text "build/release/Test-StableReadiness.ps1"
$knownIssuesVerifierText = Read-Text "build/release/Test-KnownIssuesAssessment.ps1"
$knownIssuesBinderText = Read-Text "build/release/Bind-KnownIssuesAssessment.ps1"
$knownIssuesBindingVerifierText = Read-Text "build/release/Test-KnownIssuesAssessmentBinding.ps1"
$knownIssuesFailureCasesText = Read-Text "build/release/Test-KnownIssuesAssessmentFailureCases.ps1"
$matrixGeneratorText = Read-Text "build/governance/New-SupportCompatibilityMatrix.ps1"
$supportMatrixText = Read-Text "docs/SupportCompatibilityMatrix.md"
$productionSupportText = Read-Text "docs/ProductionSupport.md"
$deploymentDocumentationText = Read-Text "docs/ProductionDeploymentBaseline.md"
$securityText = Read-Text "SECURITY.md"
$productSecurityText = Read-Text "docs/Governance/ProductSecurityPolicy.json"
$releaseChannelsText = Read-Text "build/release/release-channels.json"
$updatePolicyText = Read-Text "build/update/update-policy.json"
$coordinatedUpgradeText = Read-Text "build/update/coordinated-upgrade-policy.json"
$supportBundleText = Read-Text "src/Hosts/rtaime.Operator/SupportBundleExporter.cs"
$supportBundleTestsText = Read-Text "tests/rtaime.Tests.Operator/SupportBundleExporterTests.cs"
$observabilityPolicyText = Read-Text "build/quality/Test-ObservabilityPolicy.ps1"
$buildPropsText = Read-Text "Directory.Build.props"

foreach ($scriptPath in @(
	"build/governance/New-SupportCompatibilityMatrix.ps1",
	"build/release/Test-StableReadiness.ps1",
	"build/release/Test-KnownIssuesAssessment.ps1",
	"build/release/Bind-KnownIssuesAssessment.ps1",
	"build/release/Test-KnownIssuesAssessmentBinding.ps1",
	"build/release/Test-KnownIssuesAssessmentFailureCases.ps1"
)) {
	$parseTokens = $null
	$parseErrors = $null
	[void][System.Management.Automation.Language.Parser]::ParseFile(
		(Repository-Path $scriptPath),
		[ref]$parseTokens,
		[ref]$parseErrors)
	Assert-Condition (@($parseErrors).Count -eq 0) "Production-support script '$scriptPath' must parse as valid PowerShell."
}

$supportPolicy = $supportPolicyText | ConvertFrom-Json
$platformMatrix = $platformMatrixText | ConvertFrom-Json
$deploymentBaseline = $deploymentBaselineText | ConvertFrom-Json
$productSecurity = $productSecurityText | ConvertFrom-Json
$releaseChannels = $releaseChannelsText | ConvertFrom-Json
$updatePolicy = $updatePolicyText | ConvertFrom-Json
$coordinatedUpgrade = $coordinatedUpgradeText | ConvertFrom-Json

Assert-Condition (@(Get-SupportPolicyErrors $supportPolicy).Count -eq 0) "Production support policy failed validation."
Assert-Condition (@(Get-PlatformMatrixErrors $platformMatrix).Count -eq 0) "Platform support matrix failed validation."

Assert-Condition ($supportSchemaText -match '"title": "rtaime Product Support Policy"') "Support policy schema is missing or unexpected."
Assert-Condition ($platformSchemaText -match '"title": "rtaime Platform Support Matrix"') "Platform support matrix schema is missing or unexpected."
Assert-Condition ($readinessSchemaText -match '"title": "rtaime Stable Readiness Result"') "Stable-readiness schema is missing or unexpected."
Assert-Condition ($readinessSchemaText -match '"sourceCommit"') "Stable-readiness schema must carry exact source identity."

Assert-Condition ([string]$supportPolicy.releaseChannelsSource -eq "build/release/release-channels.json") "Support policy must reuse release-channel governance."
Assert-Condition ([string]$supportPolicy.upgradeDeprecation.directUpgradeRulesSource -eq "build/update/update-policy.json") "Support policy must reuse the governed update policy."
Assert-Condition ([string]$supportPolicy.upgradeDeprecation.persistentStatePolicySource -eq "build/update/coordinated-upgrade-policy.json") "Support policy must reuse coordinated state-upgrade policy."
Assert-Condition (@($releaseChannels.channels.PSObject.Properties.Name) -contains "STABLE") "Release-channel policy must retain STABLE."
Assert-Condition ([bool]$releaseChannels.channels.STABLE.requireTrustedProductionKey) "STABLE release channel must continue requiring production signing trust."
Assert-Condition ([bool]$updatePolicy.trust.requireProductionTrust) "Managed updates must continue requiring production trust."
Assert-Condition ([string]$coordinatedUpgrade.directSoftwareRollbackWhenStateChanged -eq "BLOCKED") "State-changing upgrades must retain fail-closed rollback semantics."

Assert-Condition ([string]$supportPolicy.lifecycle.stableRelease.status -eq "UNVERIFIED") "Stable support duration must remain UNVERIFIED until explicitly approved."
Assert-Condition ([string]$supportPolicy.securitySupport.remediationTargetsStatus -eq "UNVERIFIED") "Security remediation targets must remain UNVERIFIED until explicitly approved."
Assert-Condition ([string]$supportPolicy.upgradeDeprecation.contractDeprecation.status -eq "UNVERIFIED") "Contract deprecation notice must remain UNVERIFIED until explicitly approved."
Assert-Condition (@($supportPolicy.supportedVersions.stableLines).Count -eq 0) "No Stable version line may be invented before a Stable support commitment exists."

Assert-Condition ([string]$supportPolicy.knownIssues.assessmentRequiredFromStage -eq "RELEASE_CANDIDATE") "Known-issues assessment must be required from release-candidate stage."
Assert-Condition ($supportPolicy.knownIssues.releaseNotesRequired -eq $true) "Release notes must carry known-issues disclosure requirements."
Assert-Condition ([string]$supportPolicy.knownIssues.currentStatus -eq "UNVERIFIED") "Current known-issues support status must remain UNVERIFIED without release-candidate evidence."

Assert-Condition ([string]$platformMatrix.matrixStatus -eq "UNVERIFIED") "Platform matrix must remain UNVERIFIED until evidence-backed supported tuples exist."
Assert-Condition (@($platformMatrix.configurations | Where-Object { [string]$_.supportStatus -eq "SUPPORTED" }).Count -eq 0) "No platform tuple may be marked SUPPORTED without approved evidence."
Assert-Condition ([string]$platformMatrix.compatibilityRules.driverRangePolicy -eq "EVIDENCE_BOUND") "Driver ranges must remain evidence-bound."
Assert-Condition ([string]$platformMatrix.compatibilityRules.providerSdkPolicy -eq "PINNED_WHERE_REQUIRED") "Provider SDK policy must retain pinning where required."

Assert-Condition ([string]$deploymentBaseline.schemaVersion -eq "1.0") "Deployment baseline schemaVersion must be 1.0."
Assert-Condition ([string]$deploymentBaseline.policyStatus -eq "DEFINED") "Deployment baseline policy must be defined."
$requiredDeploymentIds = @(
	"WINDOWS_PLATFORM",
	"SERVICE_ACCOUNT",
	"FILESYSTEM_ACLS",
	"STORAGE",
	"GPU_DRIVER",
	"MEDIA_IO",
	"NETWORK_FIREWALL",
	"CERTIFICATE_TRUST",
	"DIAGNOSTICS_EVENT_LOG",
	"UPDATE_SOURCE",
	"ROLLBACK_STATE",
	"TIME_REFERENCE"
)
$deploymentIds = @($deploymentBaseline.requiredChecks | ForEach-Object { [string]$_.id })
foreach ($id in $requiredDeploymentIds) {
	Assert-Condition ($deploymentIds -contains $id) "Deployment baseline is missing '$id'."
}
Assert-Condition ([string]$deploymentBaseline.evidenceSemantics -match "does not create hardware qualification") "Deployment baseline must not infer qualification from checklist completion."

Assert-Condition ([int64]$supportPolicy.supportBundle.maximumSourceBytes -eq 268435456) "Support-bundle policy must retain the 256 MiB bounded-source limit."
Assert-Condition ($supportBundleText -match 'DefaultMaximumSourceBytes\s*=\s*256L \* 1024L \* 1024L') "Support-bundle implementation drifted from the policy limit."
Assert-Condition ($supportBundleText -match 'DiagnosticRedactor\.SanitizeValue' -and $supportBundleText -match 'DiagnosticRedactor\.RedactText') "Support-bundle metadata must use the shared redaction boundary."
Assert-Condition ($supportBundleText -notmatch 'PreviewImage|ProgramImage|MonitoringFrame|MediaFrame|GpuSurface') "Support bundle must not collect media/GPU payloads."
foreach ($testName in @($supportPolicy.supportBundle.regressionTests)) {
	Assert-Condition ($supportBundleTestsText -match [Regex]::Escape([string]$testName)) "Support-bundle regression test '$testName' is missing."
}
Assert-Condition ($observabilityPolicyText -match 'Support bundle collection must keep a bounded source-size limit') "Existing observability policy must continue enforcing support-bundle boundedness."

Assert-Condition ([string]$productSecurity.supportPeriod.status -eq "UNVERIFIED") "Product-security support-period status must remain UNVERIFIED until commitments are approved."
Assert-Condition ([bool]$productSecurity.evidenceRules.signedSecurityUpdatePathRequired) "Security updates must continue using the signed update path."
Assert-Condition ($securityText -match 'ProductSupportPolicy\.json') "SECURITY.md supported-version section must be backed by ProductSupportPolicy.json."
Assert-Condition ($securityText -match 'SupportCompatibilityMatrix\.md') "SECURITY.md must point to the generated support/version projection."

Assert-Condition ($matrixGeneratorText -match 'PlatformSupportMatrix\.json' -and $matrixGeneratorText -match 'ProductSupportPolicy\.json') "Support matrix generator must use both source-controlled policies."
Assert-Condition ($supportMatrixText -match 'Generated from') "Human-readable support matrix must identify its generated sources."
foreach ($term in @("policy commitment", "implementation capability", "qualification evidence", "release readiness", "legal/regulatory conformity")) {
	Assert-Condition ($productionSupportText -match [Regex]::Escape($term)) "Production support documentation must distinguish '$term'."
}
Assert-Condition ($productionSupportText -match 'Release notes and known issues' -and $productionSupportText -match 'KNOWN_ISSUES') "Production support documentation must define release-note/known-issues requirements."
Assert-Condition ($deploymentDocumentationText -match 'does not create hardware qualification' -and $deploymentDocumentationText -match 'test certificates or ephemeral release keys cannot satisfy production trust') "Deployment documentation must preserve qualification/trust evidence boundaries."
& (Repository-Path "build/governance/New-SupportCompatibilityMatrix.ps1") -Verify

Assert-Condition ($stableVerifierText -match 'releasePromotionPerformed = \$false') "Stable readiness verifier must never promote release stage."
Assert-Condition ($stableVerifierText -match 'productionSigningTrust' -and $stableVerifierText -match 'requiredHardwareEvidence') "Stable readiness verifier must evaluate production trust and hardware evidence."
Assert-Condition ($stableVerifierText -match 'KNOWN_ISSUES') "Stable readiness verifier must evaluate known-issues evidence."
Assert-Condition ($stableVerifierText -match 'Test-ReleaseEvidence\.ps1') "Stable readiness verifier must validate supplied release evidence."
Assert-Condition ($stableVerifierText -match 'ExpectedSourceCommit' -and $stableVerifierText -match 'sourceIdentity' -and $stableVerifierText -match 'git -C \$repositoryRoot rev-parse HEAD') "Stable readiness must bind to the exact checked-out Git source identity."
Assert-Condition ($stableVerifierText -match 'candidateEvidenceCorrelation' -and $stableVerifierText -match 'releaseEvidenceSha256') "Stable readiness must bind candidate trust to the exact supplied release evidence bytes."
Assert-Condition ($knownIssuesVerifierText -match 'unresolved BLOCKER or CRITICAL' -and $knownIssuesVerifierText -match 'release-note disclosure') "Known-issues assessment must reject unresolved critical blockers and undisclosed unresolved issues."
Assert-Condition ($knownIssuesBinderText -match 'known-issues-assessment\.json' -and $knownIssuesBinderText -match 'KNOWN_ISSUES') "Known-issues binder must use the canonical assessment artifact and release evidence domain."
Assert-Condition ($knownIssuesBindingVerifierText -match 'SHA-256 mismatch' -and $knownIssuesBindingVerifierText -match 'ExpectedSourceCommit') "Known-issues binding must be hash- and exact-source-verified."
Assert-Condition ($knownIssuesFailureCasesText -match 'source commit mismatch' -and $knownIssuesFailureCasesText -match 'tampered bound assessment') "Known-issues failure qualification must cover source mismatch and tampering."
& (Repository-Path "build/release/Test-KnownIssuesAssessmentFailureCases.ps1")

$tempReadiness = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-stable-readiness-{0}.json" -f [Guid]::NewGuid().ToString("N"))
try {
	$readiness = & (Repository-Path "build/release/Test-StableReadiness.ps1") -OutputPath $tempReadiness
	Assert-Condition ([string]$readiness.overallStatus -eq "UNVERIFIED") "Current DEV source must remain Stable-readiness UNVERIFIED."
	Assert-Condition ([string]$readiness.product.releaseStage -eq "DEV") "Stable readiness must not change the current DEV stage."
	Assert-Condition ($readiness.releasePromotionPerformed -eq $false) "Stable readiness must report no release promotion."

	$currentHead = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
	Assert-Condition ([string]$readiness.sourceCommit -eq $currentHead) "Stable readiness must report the exact checked-out Git commit."

	$sourceIdentity = @($readiness.domains | Where-Object { [string]$_.name -eq "sourceIdentity" })
	Assert-Condition ($sourceIdentity.Count -eq 1 -and [string]$sourceIdentity[0].status -eq "PASS") "Current repository source identity must verify as PASS."
	$correlation = @($readiness.domains | Where-Object { [string]$_.name -eq "candidateEvidenceCorrelation" })
	Assert-Condition ($correlation.Count -eq 1 -and [string]$correlation[0].status -eq "UNVERIFIED") "Candidate/evidence correlation must remain UNVERIFIED when no release artifacts are supplied."
	$trust = @($readiness.domains | Where-Object { [string]$_.name -eq "productionSigningTrust" })
	$hardware = @($readiness.domains | Where-Object { [string]$_.name -eq "requiredHardwareEvidence" })
	Assert-Condition ($trust.Count -eq 1 -and [string]$trust[0].status -eq "UNVERIFIED") "Missing production trust must keep readiness UNVERIFIED."
	Assert-Condition ($hardware.Count -eq 1 -and [string]$hardware[0].status -eq "UNVERIFIED") "Missing physical evidence must keep readiness UNVERIFIED."
} finally {
	if (Test-Path -LiteralPath $tempReadiness) { Remove-Item -LiteralPath $tempReadiness -Force }
}

$wrongSource = "1111111111111111111111111111111111111111"
if ($wrongSource -eq $expectedSource) { $wrongSource = "2222222222222222222222222222222222222222" }
$sourceMismatchReadiness = & (Repository-Path "build/release/Test-StableReadiness.ps1") -ExpectedSourceCommit $wrongSource
$sourceMismatchDomain = @($sourceMismatchReadiness.domains | Where-Object { [string]$_.name -eq "sourceIdentity" })
Assert-Condition ([string]$sourceMismatchReadiness.overallStatus -eq "FAIL") "Mismatched expected source commit must fail Stable readiness."
Assert-Condition ($sourceMismatchDomain.Count -eq 1 -and [string]$sourceMismatchDomain[0].status -eq "FAIL") "Source-identity domain must fail on an expected-source mismatch."

$invalidCommitment = Clone-JsonObject $supportPolicy
$invalidCommitment.lifecycle.stableRelease.status = "PASS"
Assert-Condition (@(Get-SupportPolicyErrors $invalidCommitment).Count -gt 0) "Support PASS without required durations must fail validation."

$invalidPreview = Clone-JsonObject $supportPolicy
$invalidPreview.lifecycle.preview.status = "PASS"
Assert-Condition (@(Get-SupportPolicyErrors $invalidPreview).Count -gt 0) "Preview PASS without explicit commitments must fail validation."

$invalidDates = Clone-JsonObject $supportPolicy
$invalidDates.lifecycle.stableRelease.status = "PASS"
$invalidDates.lifecycle.stableRelease.maintenanceMonths = 12
$invalidDates.lifecycle.stableRelease.securityMonths = 18
$invalidDates.lifecycle.stableRelease.eolNotificationLeadDays = 90
$invalidDates.supportedVersions.stableLines = @(
	[pscustomobject]@{
		versionLine = "1.0"
		status = "SUPPORTED"
		supportStart = "2027-06-01"
		maintenanceEnd = "2027-05-01"
		securityEnd = "2027-07-01"
		eolDate = "2027-08-01"
	}
)
Assert-Condition (@(Get-SupportPolicyErrors $invalidDates).Count -gt 0) "Invalid Stable support date ordering must fail validation."

$invalidEol = Clone-JsonObject $supportPolicy
$invalidEol.supportedVersions.stableLines = @(
	[pscustomobject]@{
		versionLine = "0.9"
		status = "EOL"
		supportStart = "2026-01-01"
		maintenanceEnd = "2026-06-01"
		securityEnd = "2026-09-01"
		eolDate = $null
	}
)
Assert-Condition (@(Get-SupportPolicyErrors $invalidEol).Count -gt 0) "EOL line without a complete date record must fail validation."

$unverifiedWithDates = Clone-JsonObject $supportPolicy
$unverifiedWithDates.supportedVersions.stableLines = @(
	[pscustomobject]@{
		versionLine = "1.0"
		status = "UNVERIFIED"
		supportStart = "2027-01-01"
		maintenanceEnd = $null
		securityEnd = $null
		eolDate = $null
	}
)
Assert-Condition (@(Get-SupportPolicyErrors $unverifiedWithDates).Count -gt 0) "UNVERIFIED support line must not carry implied support dates."

$unsupportedWithoutEvidence = Clone-JsonObject $platformMatrix
$unsupportedWithoutEvidence.configurations[0].supportStatus = "SUPPORTED"
$unsupportedWithoutEvidence.configurations[0].qualificationEvidence = @()
Assert-Condition (@(Get-PlatformMatrixErrors $unsupportedWithoutEvidence).Count -gt 0) "SUPPORTED platform without evidence must fail validation."

$staleEvidence = Clone-JsonObject $platformMatrix
$staleEvidence.configurations[0].qualificationEvidence = @("docs/qualification/does-not-exist.json")
Assert-Condition (@(Get-PlatformMatrixErrors $staleEvidence).Count -gt 0) "Stale platform evidence references must fail validation."

$matrixPassWithoutSupport = Clone-JsonObject $platformMatrix
$matrixPassWithoutSupport.matrixStatus = "PASS"
Assert-Condition (@(Get-PlatformMatrixErrors $matrixPassWithoutSupport).Count -gt 0) "Matrix PASS without a SUPPORTED tuple must fail validation."

$nonMachineReadableEvidence = Clone-JsonObject $platformMatrix
$nonMachineReadableEvidence.matrixStatus = "PASS"
$nonMachineReadableEvidence.configurations[0].supportStatus = "SUPPORTED"
$nonMachineReadableEvidence.configurations[0].platform.edition = "Example"
$nonMachineReadableEvidence.configurations[0].platform.versionFamily = "Example"
$nonMachineReadableEvidence.configurations[0].gpu.driverRange = "example"
$nonMachineReadableEvidence.configurations[0].gpu.qualificationStatus = "PASS"
$nonMachineReadableEvidence.configurations[0].mediaIo.device = "example"
$nonMachineReadableEvidence.configurations[0].mediaIo.driverRange = "example"
$nonMachineReadableEvidence.configurations[0].mediaIo.qualificationStatus = "PASS"
$nonMachineReadableEvidence.configurations[0].qualificationEvidence = @("docs/QualificationEvidenceProvenance.md")
Assert-Condition (@(Get-PlatformMatrixErrors $nonMachineReadableEvidence).Count -gt 0) "SUPPORTED platform must reject non-machine-readable qualification evidence."

$networkWithoutEvidence = Clone-JsonObject $platformMatrix
$networkWithoutEvidence.configurations[0].networkProviders[0].supportStatus = "SUPPORTED"
$networkWithoutEvidence.configurations[0].networkProviders[0].qualificationEvidence = @()
Assert-Condition (@(Get-PlatformMatrixErrors $networkWithoutEvidence).Count -gt 0) "SUPPORTED network provider without qualification evidence must fail validation."

Assert-Condition ($buildPropsText -match '<RtaimeProductVersion>0\.1\.0-dev</RtaimeProductVersion>') "Current source version must remain 0.1.0-dev."
Assert-Condition ($buildPropsText -match '<RtaimeReleaseStage>DEV</RtaimeReleaseStage>') "Current source release stage must remain DEV."

Write-Host "Production support and Stable-readiness policy PASS"
Write-Host "Support lifecycle commitment: UNVERIFIED"
Write-Host "Platform support matrix: UNVERIFIED"
Write-Host "Production signing trust: UNVERIFIED"
Write-Host "Physical support evidence: UNVERIFIED"
Write-Host "Current release stage: DEV"
