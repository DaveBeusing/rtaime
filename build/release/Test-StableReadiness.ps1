# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$ReleaseEvidencePath = "",
	[string]$ReleaseCandidatePath = "",
	[string]$OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Read-Json {
	param([Parameter(Mandatory)][string]$Path)
	$fullPath = Resolve-RepositoryPath $Path
	Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) "Required Stable-readiness input is missing: '$Path'."
	return Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
}

function Add-Domain {
	param(
		[Parameter(Mandatory)][System.Collections.Generic.List[object]]$Domains,
		[Parameter(Mandatory)][string]$Name,
		[Parameter(Mandatory)][ValidateSet("PASS", "FAIL", "UNVERIFIED", "NOT_APPLICABLE")][string]$Status,
		[Parameter(Mandatory)][string]$Source,
		[Parameter(Mandatory)][string]$Details
	)
	$Domains.Add([ordered]@{
		name = $Name
		status = $Status
		source = $Source
		details = $Details
	})
}

$buildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"
Assert-Condition (Test-Path -LiteralPath $buildPropsPath -PathType Leaf) "Directory.Build.props is missing."
[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersion = $buildProps.SelectSingleNode("//RtaimeProductVersion").InnerText.Trim()
$releaseStage = $buildProps.SelectSingleNode("//RtaimeReleaseStage").InnerText.Trim().ToUpperInvariant()
Assert-Condition ($releaseStage -in @("DEV", "PREVIEW", "STABLE")) "Unsupported source release stage '$releaseStage'."

$supportPolicy = Read-Json "docs/Governance/ProductSupportPolicy.json"
$platformMatrix = Read-Json "docs/Governance/PlatformSupportMatrix.json"
$securityPolicy = Read-Json "docs/Governance/ProductSecurityPolicy.json"
$deploymentBaseline = Read-Json "docs/Governance/ProductionDeploymentBaseline.json"
$releasePolicy = Read-Json "build/release/release-policy.json"
$updatePolicy = Read-Json "build/update/update-policy.json"

$domains = [System.Collections.Generic.List[object]]::new()

$stableLifecycle = $supportPolicy.lifecycle.stableRelease
$supportPeriodStatus = if ([string]$stableLifecycle.status -eq "PASS" -and
	$null -ne $stableLifecycle.maintenanceMonths -and
	$null -ne $stableLifecycle.securityMonths -and
	$null -ne $stableLifecycle.eolNotificationLeadDays) {
	"PASS"
} elseif ([string]$stableLifecycle.status -eq "FAIL") {
	"FAIL"
} else {
	"UNVERIFIED"
}
Add-Domain $domains "supportPeriodPolicy" $supportPeriodStatus "docs/Governance/ProductSupportPolicy.json" (
	if ($supportPeriodStatus -eq "PASS") {
		"Approved Stable maintenance, security and EOL-notification durations are declared."
	} else {
		"Stable support durations are not yet an approved commitment."
	}
)

$versionPolicyStatus = "PASS"
$stableLines = @($supportPolicy.supportedVersions.stableLines)
if ($stableLines | Where-Object { [string]$_.status -eq "SUPPORTED" -and $null -eq $_.eolDate }) {
	$versionPolicyStatus = "FAIL"
}
Add-Domain $domains "supportedVersionPolicy" $versionPolicyStatus "docs/Governance/ProductSupportPolicy.json" (
	if ($stableLines.Count -eq 0) {
		"No Stable line is currently declared supported; that absence is explicit and policy-backed."
	} else {
		"Stable support lines and EOL state are source-controlled."
	}
)

$supportedConfigurations = @($platformMatrix.configurations | Where-Object { [string]$_.supportStatus -eq "SUPPORTED" })
$platformStatus = if ([string]$platformMatrix.matrixStatus -eq "FAIL") {
	"FAIL"
} elseif ($supportedConfigurations.Count -gt 0 -and @($supportedConfigurations | Where-Object { @($_.qualificationEvidence).Count -eq 0 }).Count -eq 0) {
	"PASS"
} else {
	"UNVERIFIED"
}
Add-Domain $domains "platformCompatibilityPolicy" $platformStatus "docs/Governance/PlatformSupportMatrix.json" (
	if ($platformStatus -eq "PASS") {
		"At least one evidence-backed platform tuple is declared SUPPORTED."
	} else {
		"No evidence-backed platform tuple is currently declared SUPPORTED."
	}
)

$securityFrameworkStatus = if (
	[bool]$securityPolicy.evidenceRules.failClosed -and
	[bool]$securityPolicy.evidenceRules.signedSecurityUpdatePathRequired -and
	-not [string]::IsNullOrWhiteSpace([string]$securityPolicy.vulnerabilityContact)
) { "PASS" } else { "FAIL" }
Add-Domain $domains "securityPolicy" $securityFrameworkStatus "docs/Governance/ProductSecurityPolicy.json" "Product-security governance, disclosure and signed-update requirements are source-controlled."

$securityCommitmentStatus = [string]$supportPolicy.securitySupport.remediationTargetsStatus
if ($securityCommitmentStatus -notin @("PASS", "FAIL", "UNVERIFIED")) { $securityCommitmentStatus = "FAIL" }
Add-Domain $domains "securitySupportCommitment" $securityCommitmentStatus "docs/Governance/ProductSupportPolicy.json" (
	if ($securityCommitmentStatus -eq "PASS") {
		"Approved security remediation targets are declared."
	} else {
		"Security remediation/SLA durations are not yet an approved commitment."
	}
)

$deprecationStatus = [string]$supportPolicy.upgradeDeprecation.contractDeprecation.status
if ($deprecationStatus -notin @("PASS", "FAIL", "UNVERIFIED")) { $deprecationStatus = "FAIL" }
$updateSourcesExist =
	(Test-Path -LiteralPath (Resolve-RepositoryPath ([string]$supportPolicy.upgradeDeprecation.directUpgradeRulesSource)) -PathType Leaf) -and
	(Test-Path -LiteralPath (Resolve-RepositoryPath ([string]$supportPolicy.upgradeDeprecation.persistentStatePolicySource)) -PathType Leaf)
if (-not $updateSourcesExist) { $deprecationStatus = "FAIL" }
Add-Domain $domains "upgradeDeprecationPolicy" $deprecationStatus "docs/Governance/ProductSupportPolicy.json" (
	if ($deprecationStatus -eq "PASS") {
		"Upgrade and deprecation commitments are fully approved."
	} elseif ($deprecationStatus -eq "UNVERIFIED") {
		"Upgrade mechanics are governed, but contract deprecation notice duration is not yet approved."
	} else {
		"Upgrade/deprecation policy inputs are invalid or missing."
	}
)

$deploymentStatus = if (
	[string]$deploymentBaseline.policyStatus -eq "DEFINED" -and
	@($deploymentBaseline.requiredChecks).Count -ge 10
) { "PASS" } else { "FAIL" }
Add-Domain $domains "deploymentPolicy" $deploymentStatus "docs/Governance/ProductionDeploymentBaseline.json" "Production deployment prerequisites are explicitly source-controlled and evidence-aware."

$updateRollbackStatus = if (
	[bool]$updatePolicy.trust.requireProductionTrust -and
	[bool]$updatePolicy.replacement.retainRollbackAfterSuccess -and
	[string]$updatePolicy.replacement.persistentStateMigration -eq "COORDINATED_ONLY"
) { "PASS" } else { "FAIL" }
Add-Domain $domains "updateRollback" $updateRollbackStatus "build/update/update-policy.json" "Managed updates require production trust, retain rollback state and use coordinated persistent-state migration."

$releaseEvidence = $null
$evidenceRoot = $null
if (-not [string]::IsNullOrWhiteSpace($ReleaseEvidencePath)) {
	$evidenceRoot = Resolve-RepositoryPath $ReleaseEvidencePath
	if (Test-Path -LiteralPath $evidenceRoot -PathType Leaf) {
		$evidenceRoot = Split-Path -Parent $evidenceRoot
	}
	$releaseEvidenceFile = Join-Path $evidenceRoot "release-evidence.json"
	if (Test-Path -LiteralPath $releaseEvidenceFile -PathType Leaf) {
		& (Join-Path $repositoryRoot "build/release/Test-ReleaseEvidence.ps1") -OutputPath $evidenceRoot
		$releaseEvidence = Get-Content -LiteralPath $releaseEvidenceFile -Raw | ConvertFrom-Json
	}
}

$releaseEvidenceStatus = if ($null -eq $releaseEvidence) {
	"UNVERIFIED"
} elseif ([string]$releaseEvidence.productVersion -ne $productVersion -or [string]$releaseEvidence.releaseStage -ne $releaseStage) {
	"FAIL"
} else {
	"PASS"
}
Add-Domain $domains "releaseEvidence" $releaseEvidenceStatus "release-evidence.json" (
	if ($releaseEvidenceStatus -eq "PASS") { "Release evidence verifies and matches the current product identity." }
	elseif ($releaseEvidenceStatus -eq "FAIL") { "Release evidence does not match the current product identity." }
	else { "No exact current-source release-evidence bundle was supplied to the Stable-readiness verifier." }
)

$knownIssuesStatus = "UNVERIFIED"
if ($null -ne $releaseEvidence) {
	$knownIssuesDomain = @($releaseEvidence.evidenceDomains | Where-Object { [string]$_.domain -eq "KNOWN_ISSUES" })
	if ($knownIssuesDomain.Count -ne 1) {
		$knownIssuesStatus = "FAIL"
	} elseif ([string]$knownIssuesDomain[0].status -eq "PASS") {
		$knownIssuesStatus = "PASS"
	} elseif ([string]$knownIssuesDomain[0].status -eq "FAIL") {
		$knownIssuesStatus = "FAIL"
	}
}
Add-Domain $domains "knownIssues" $knownIssuesStatus "release-evidence.json:KNOWN_ISSUES" (
	if ($knownIssuesStatus -eq "PASS") {
		"Known-issues assessment is bound as PASS for the supplied release evidence."
	} else {
		"A source-bound release known-issues PASS assessment is not available."
	}
)

$hardwareStatus = "UNVERIFIED"
if ($null -ne $releaseEvidence -and $null -ne $evidenceRoot) {
	$compatibilityPath = Join-Path $evidenceRoot "compatibility-manifest.json"
	if (Test-Path -LiteralPath $compatibilityPath -PathType Leaf) {
		$compatibility = Get-Content -LiteralPath $compatibilityPath -Raw | ConvertFrom-Json
		$requiredHardware = @($releasePolicy.hardwareQualification | ForEach-Object { [string]$_.requirement })
		$hardwareEntries = @($compatibility.hardwareQualification)
		if ($hardwareEntries.Count -ne $requiredHardware.Count) {
			$hardwareStatus = "FAIL"
		} else {
			$missing = @($requiredHardware | Where-Object {
				$requirement = $_
				@($hardwareEntries | Where-Object {
					[string]$_.requirement -eq $requirement -and [string]$_.status -eq "PASS"
				}).Count -ne 1
			})
			if ($missing.Count -eq 0) { $hardwareStatus = "PASS" }
		}
	}
}
Add-Domain $domains "requiredHardwareEvidence" $hardwareStatus "compatibility-manifest.json:hardwareQualification" (
	if ($hardwareStatus -eq "PASS") {
		"All release-policy hardware requirements are PASS in exact release evidence."
	} elseif ($hardwareStatus -eq "FAIL") {
		"Hardware evidence is structurally incomplete or inconsistent."
	} else {
		"Required physical qualification evidence remains incomplete or was not supplied."
	}
)

$productionTrustStatus = "UNVERIFIED"
if (-not [string]::IsNullOrWhiteSpace($ReleaseCandidatePath)) {
	$candidateRoot = Resolve-RepositoryPath $ReleaseCandidatePath
	if (Test-Path -LiteralPath $candidateRoot -PathType Leaf) { $candidateRoot = Split-Path -Parent $candidateRoot }
	$candidateFile = Join-Path $candidateRoot "release-candidate.json"
	if (Test-Path -LiteralPath $candidateFile -PathType Leaf) {
		$candidate = Get-Content -LiteralPath $candidateFile -Raw | ConvertFrom-Json
		if ([string]$candidate.channel -ne "STABLE" -or [string]$candidate.product.releaseStage -ne "STABLE") {
			$productionTrustStatus = "FAIL"
		} elseif (
			[string]$candidate.trust.signerClass -eq "EXTERNAL_CONTROLLED" -and
			[string]$candidate.trust.productionTrust -eq "PASS" -and
			[string]$candidate.publicationReadiness.status -eq "PASS"
		) {
			$productionTrustStatus = "PASS"
		}
	}
}
Add-Domain $domains "productionSigningTrust" $productionTrustStatus "release-candidate.json" (
	if ($productionTrustStatus -eq "PASS") {
		"Stable candidate uses externally controlled active production signing trust and publication readiness PASS."
	} elseif ($productionTrustStatus -eq "FAIL") {
		"Supplied release candidate is not an acceptable Stable production-trust candidate."
	} else {
		"No qualifying Stable production-trust candidate was supplied."
	}
)

$sourceStageStatus = if ($releaseStage -eq "STABLE") { "PASS" } else { "UNVERIFIED" }
Add-Domain $domains "sourceReleaseStage" $sourceStageStatus "Directory.Build.props" (
	if ($releaseStage -eq "STABLE") {
		"Source declares STABLE and must still satisfy every other readiness domain."
	} else {
		"Current source remains '$releaseStage'; Stable readiness does not promote release stage."
	}
)

$domainArray = @($domains)
$overallStatus = if (@($domainArray | Where-Object { [string]$_.status -eq "FAIL" }).Count -gt 0) {
	"FAIL"
} elseif ($releaseStage -ne "STABLE" -or @($domainArray | Where-Object { [string]$_.status -eq "UNVERIFIED" }).Count -gt 0) {
	"UNVERIFIED"
} else {
	"PASS"
}

$result = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	product = [ordered]@{
		name = "rtaime"
		version = $productVersion
		releaseStage = $releaseStage
	}
	overallStatus = $overallStatus
	releasePromotionPerformed = $false
	domains = $domainArray
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
	$resolvedOutput = Resolve-RepositoryPath $OutputPath
	$directory = Split-Path -Parent $resolvedOutput
	if (-not (Test-Path -LiteralPath $directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	$json = $result | ConvertTo-Json -Depth 32
	[System.IO.File]::WriteAllText($resolvedOutput, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Stable readiness: $overallStatus"
Write-Host "Product: rtaime $productVersion ($releaseStage)"
Write-Host "Release promotion performed: false"
foreach ($domain in $domainArray) {
	Write-Host "$($domain.name): $($domain.status)"
}

return [pscustomobject]$result
