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
	} else {
		$errors.Add("stable.status")
	}

	$preview = $Policy.lifecycle.preview
	if ([string]$preview.status -eq "PASS" -and
		(-not [bool]$preview.maintenanceCommitment -or -not [bool]$preview.securityCommitment)) {
		$errors.Add("preview.commitment")
	}

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

	foreach ($line in @($Policy.supportedVersions.stableLines)) {
		if ([string]$line.status -eq "SUPPORTED") {
			foreach ($property in @("supportStart", "maintenanceEnd", "securityEnd", "eolDate")) {
				if ([string]::IsNullOrWhiteSpace([string]$line.$property)) { $errors.Add("stableLine.$property") }
			}
			if (-not [string]::IsNullOrWhiteSpace([string]$line.supportStart) -and
				-not [string]::IsNullOrWhiteSpace([string]$line.maintenanceEnd) -and
				-not [string]::IsNullOrWhiteSpace([string]$line.securityEnd) -and
				-not [string]::IsNullOrWhiteSpace([string]$line.eolDate)) {
				$start = [DateTime]::ParseExact([string]$line.supportStart, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
				$maintenanceEnd = [DateTime]::ParseExact([string]$line.maintenanceEnd, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
				$securityEnd = [DateTime]::ParseExact([string]$line.securityEnd, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
				$eol = [DateTime]::ParseExact([string]$line.eolDate, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
				if ($maintenanceEnd -lt $start) { $errors.Add("stableLine.maintenanceOrder") }
				if ($securityEnd -lt $maintenanceEnd) { $errors.Add("stableLine.securityOrder") }
				if ($eol -lt $securityEnd) { $errors.Add("stableLine.eolOrder") }
			}
		}
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

	foreach ($configuration in @($Matrix.configurations)) {
		foreach ($evidence in @($configuration.qualificationEvidence)) {
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
			if (@($configuration.qualificationEvidence).Count -eq 0) { $errors.Add("supportedWithoutEvidence") }
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
$matrixGeneratorText = Read-Text "build/governance/New-SupportCompatibilityMatrix.ps1"
$supportMatrixText = Read-Text "docs/SupportCompatibilityMatrix.md"
$securityText = Read-Text "SECURITY.md"
$productSecurityText = Read-Text "docs/Governance/ProductSecurityPolicy.json"
$releaseChannelsText = Read-Text "build/release/release-channels.json"
$updatePolicyText = Read-Text "build/update/update-policy.json"
$coordinatedUpgradeText = Read-Text "build/update/coordinated-upgrade-policy.json"
$supportBundleText = Read-Text "src/Hosts/rtaime.Operator/SupportBundleExporter.cs"
$supportBundleTestsText = Read-Text "tests/rtaime.Tests.Operator/SupportBundleExporterTests.cs"
$observabilityPolicyText = Read-Text "build/quality/Test-ObservabilityPolicy.ps1"
$buildPropsText = Read-Text "Directory.Build.props"

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
& (Repository-Path "build/governance/New-SupportCompatibilityMatrix.ps1") -Verify

Assert-Condition ($stableVerifierText -match 'releasePromotionPerformed = \$false') "Stable readiness verifier must never promote release stage."
Assert-Condition ($stableVerifierText -match 'productionSigningTrust' -and $stableVerifierText -match 'requiredHardwareEvidence') "Stable readiness verifier must evaluate production trust and hardware evidence."
Assert-Condition ($stableVerifierText -match 'KNOWN_ISSUES') "Stable readiness verifier must evaluate known-issues evidence."
Assert-Condition ($stableVerifierText -match 'Test-ReleaseEvidence\.ps1') "Stable readiness verifier must validate supplied release evidence."

$tempReadiness = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-stable-readiness-{0}.json" -f [Guid]::NewGuid().ToString("N"))
try {
	$readiness = & (Repository-Path "build/release/Test-StableReadiness.ps1") -OutputPath $tempReadiness
	Assert-Condition ([string]$readiness.overallStatus -eq "UNVERIFIED") "Current DEV source must remain Stable-readiness UNVERIFIED."
	Assert-Condition ([string]$readiness.product.releaseStage -eq "DEV") "Stable readiness must not change the current DEV stage."
	Assert-Condition ($readiness.releasePromotionPerformed -eq $false) "Stable readiness must report no release promotion."
	$trust = @($readiness.domains | Where-Object { [string]$_.name -eq "productionSigningTrust" })
	$hardware = @($readiness.domains | Where-Object { [string]$_.name -eq "requiredHardwareEvidence" })
	Assert-Condition ($trust.Count -eq 1 -and [string]$trust[0].status -eq "UNVERIFIED") "Missing production trust must keep readiness UNVERIFIED."
	Assert-Condition ($hardware.Count -eq 1 -and [string]$hardware[0].status -eq "UNVERIFIED") "Missing physical evidence must keep readiness UNVERIFIED."
} finally {
	if (Test-Path -LiteralPath $tempReadiness) { Remove-Item -LiteralPath $tempReadiness -Force }
}

$invalidCommitment = Clone-JsonObject $supportPolicy
$invalidCommitment.lifecycle.stableRelease.status = "PASS"
Assert-Condition (@(Get-SupportPolicyErrors $invalidCommitment).Count -gt 0) "Support PASS without required durations must fail validation."

$invalidPreview = Clone-JsonObject $supportPolicy
$invalidPreview.lifecycle.preview.status = "PASS"
Assert-Condition (@(Get-SupportPolicyErrors $invalidPreview).Count -gt 0) "Preview PASS without explicit commitments must fail validation."

$invalidDates = Clone-JsonObject $supportPolicy
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

$unsupportedWithoutEvidence = Clone-JsonObject $platformMatrix
$unsupportedWithoutEvidence.configurations[0].supportStatus = "SUPPORTED"
$unsupportedWithoutEvidence.configurations[0].qualificationEvidence = @()
Assert-Condition (@(Get-PlatformMatrixErrors $unsupportedWithoutEvidence).Count -gt 0) "SUPPORTED platform without evidence must fail validation."

$staleEvidence = Clone-JsonObject $platformMatrix
$staleEvidence.configurations[0].qualificationEvidence = @("docs/qualification/does-not-exist.json")
Assert-Condition (@(Get-PlatformMatrixErrors $staleEvidence).Count -gt 0) "Stale platform evidence references must fail validation."

Assert-Condition ($buildPropsText -match '<RtaimeProductVersion>0\.1\.0-dev</RtaimeProductVersion>') "Current source version must remain 0.1.0-dev."
Assert-Condition ($buildPropsText -match '<RtaimeReleaseStage>DEV</RtaimeReleaseStage>') "Current source release stage must remain DEV."

Write-Host "Production support and Stable-readiness policy PASS"
Write-Host "Support lifecycle commitment: UNVERIFIED"
Write-Host "Platform support matrix: UNVERIFIED"
Write-Host "Production signing trust: UNVERIFIED"
Write-Host "Physical support evidence: UNVERIFIED"
Write-Host "Current release stage: DEV"
