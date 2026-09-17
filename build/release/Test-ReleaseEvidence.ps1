# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = "artifacts/release-evidence"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$allowedStatuses = @("PASS", "FAIL", "NOT_APPLICABLE", "UNVERIFIED")
$allowedSeverities = @("BLOCKER", "CRITICAL", "MAJOR", "MINOR", "INFORMATIONAL")
$allowedStages = @("DEV", "PREVIEW", "RELEASE_CANDIDATE", "STABLE", "VALIDATED", "CERTIFIED")
$requiredDomains = @(
	"ARCHITECTURE",
	"CONTRACTS",
	"BEHAVIOR",
	"FAILURE",
	"PERFORMANCE",
	"COMPATIBILITY",
	"SECURITY",
	"SUPPLY_CHAIN",
	"DOCUMENTATION",
	"COMPLIANCE",
	"KNOWN_ISSUES"
)

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)

	if (-not $Condition) {
		throw $Message
	}
}

function Read-JsonFile {
	param([Parameter(Mandatory)][string]$Path)

	Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Required JSON file was not found at '$Path'."
	return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-Sha256 {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
	[System.IO.Path]::GetFullPath($OutputPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $OutputPath))
}

function Resolve-EvidencePath {
	param([Parameter(Mandatory)][string]$RelativePath)

	Assert-Condition (-not [string]::IsNullOrWhiteSpace($RelativePath)) "Evidence paths must not be empty."
	Assert-Condition (-not [System.IO.Path]::IsPathRooted($RelativePath)) "Evidence path '$RelativePath' must be relative."
	$fullPath = [System.IO.Path]::GetFullPath((Join-Path $outputRoot $RelativePath))
	$rootPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
	Assert-Condition ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "Evidence path '$RelativePath' escapes the evidence bundle."
	return $fullPath
}

Assert-Condition (Test-Path -LiteralPath $outputRoot -PathType Container) "Release evidence directory was not found at '$outputRoot'."

$artifactManifestPath = Join-Path $outputRoot "artifact-manifest.json"
$sbomPath = Join-Path $outputRoot "sbom.cdx.json"
$compatibilityManifestPath = Join-Path $outputRoot "compatibility-manifest.json"
$qualificationManifestPath = Join-Path $outputRoot "qualification-evidence-manifest.json"
$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"

$artifactManifest = Read-JsonFile $artifactManifestPath
$sbom = Read-JsonFile $sbomPath
$compatibilityManifest = Read-JsonFile $compatibilityManifestPath
$qualificationManifest = Read-JsonFile $qualificationManifestPath
$releaseEvidence = Read-JsonFile $releaseEvidencePath
$policy = Read-JsonFile (Join-Path $PSScriptRoot "release-policy.json")

Assert-Condition ([string]$releaseEvidence.schemaVersion -eq "1.0") "Unsupported release evidence schema version."
Assert-Condition ([string]$artifactManifest.schemaVersion -eq "1.0") "Unsupported artifact manifest schema version."
Assert-Condition ([string]$compatibilityManifest.schemaVersion -eq "1.0") "Unsupported compatibility manifest schema version."
Assert-Condition ([string]$qualificationManifest.schemaVersion -eq "1.0") "Unsupported qualification evidence manifest schema version."
Assert-Condition ([string]$qualificationManifest.repository -eq "DaveBeusing/rtaime") "Qualification evidence repository identity is invalid."
Assert-Condition ([string]$releaseEvidence.productName -eq [string]$policy.productName) "Release evidence product identity does not match release policy."
Assert-Condition ([string]$releaseEvidence.releaseStage -in $allowedStages) "Release evidence contains an unsupported release stage."
Assert-Condition ([string]$releaseEvidence.sourceCommit -match '^[0-9a-fA-F]{40,64}$') "Release evidence source commit is invalid."
Assert-Condition ([string]$releaseEvidence.buildCommit -match '^[0-9a-fA-F]{40,64}$') "Release evidence build commit is invalid."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$releaseEvidence.buildId)) "Release evidence build identity is required."
Assert-Condition ([string]$qualificationManifest.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Qualification evidence source commit does not match release evidence."

$identityDocuments = @($artifactManifest, $releaseEvidence)
foreach ($document in $identityDocuments) {
	Assert-Condition ([string]$document.productName -eq [string]$releaseEvidence.productName) "Product name identity mismatch inside release evidence bundle."
	Assert-Condition ([string]$document.productVersion -eq [string]$releaseEvidence.productVersion) "Product version identity mismatch inside release evidence bundle."
	Assert-Condition ([string]$document.releaseStage -eq [string]$releaseEvidence.releaseStage) "Release stage identity mismatch inside release evidence bundle."
	Assert-Condition ([string]$document.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Source commit identity mismatch inside release evidence bundle."
	Assert-Condition ([string]$document.buildCommit -eq [string]$releaseEvidence.buildCommit) "Build commit identity mismatch inside release evidence bundle."
	Assert-Condition ([string]$document.buildId -eq [string]$releaseEvidence.buildId) "Build identity mismatch inside release evidence bundle."
}

Assert-Condition ([string]$compatibilityManifest.product.name -eq [string]$releaseEvidence.productName) "Compatibility manifest product name mismatch."
Assert-Condition ([string]$compatibilityManifest.product.version -eq [string]$releaseEvidence.productVersion) "Compatibility manifest product version mismatch."
Assert-Condition ([string]$compatibilityManifest.product.releaseStage -eq [string]$releaseEvidence.releaseStage) "Compatibility manifest release stage mismatch."
Assert-Condition ([string]$compatibilityManifest.source.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Compatibility manifest source commit mismatch."
Assert-Condition ([string]$compatibilityManifest.source.buildCommit -eq [string]$releaseEvidence.buildCommit) "Compatibility manifest build commit mismatch."
Assert-Condition ([string]$compatibilityManifest.source.buildId -eq [string]$releaseEvidence.buildId) "Compatibility manifest build identity mismatch."

$seenArtifactPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$artifacts = @($artifactManifest.artifacts)
Assert-Condition ($artifacts.Count -gt 0) "Artifact manifest must contain at least one product artifact."
foreach ($artifact in $artifacts) {
	$relativePath = [string]$artifact.path
	Assert-Condition ($seenArtifactPaths.Add($relativePath)) "Artifact manifest contains duplicate path '$relativePath'."
	$filePath = Resolve-EvidencePath $relativePath
	Assert-Condition (Test-Path -LiteralPath $filePath -PathType Leaf) "Artifact '$relativePath' is missing from the evidence bundle."
	$fileInfo = Get-Item -LiteralPath $filePath
	Assert-Condition ($fileInfo.Length -eq [long]$artifact.size) "Artifact size mismatch for '$relativePath'."
	Assert-Condition ((Get-Sha256 $filePath) -eq ([string]$artifact.sha256).ToLowerInvariant()) "Artifact SHA-256 mismatch for '$relativePath'."
}

$evidenceReferences = @(
	@{ Name = "artifact manifest"; Reference = $releaseEvidence.artifactManifest },
	@{ Name = "SBOM"; Reference = $releaseEvidence.sbom },
	@{ Name = "compatibility manifest"; Reference = $releaseEvidence.compatibilityManifest },
	@{ Name = "qualification evidence manifest"; Reference = $releaseEvidence.qualificationEvidenceManifest }
)
foreach ($entry in $evidenceReferences) {
	$referencePath = Resolve-EvidencePath ([string]$entry.Reference.path)
	Assert-Condition (Test-Path -LiteralPath $referencePath -PathType Leaf) "Referenced $($entry.Name) is missing."
	Assert-Condition ((Get-Sha256 $referencePath) -eq ([string]$entry.Reference.sha256).ToLowerInvariant()) "Referenced $($entry.Name) hash mismatch."
}

Assert-Condition ([string]$sbom.bomFormat -eq "CycloneDX") "SBOM must use CycloneDX."
Assert-Condition ([string]$sbom.specVersion -eq "1.6") "SBOM must use CycloneDX 1.6 for the AP-17 foundation."
Assert-Condition ([int]$sbom.version -ge 1) "SBOM version must be at least 1."
Assert-Condition ([string]$sbom.metadata.component.name -eq [string]$releaseEvidence.productName) "SBOM product identity mismatch."
Assert-Condition ([string]$sbom.metadata.component.version -eq [string]$releaseEvidence.productVersion) "SBOM product version mismatch."
$components = @($sbom.components)
Assert-Condition ($components.Count -gt 0) "SBOM must contain the resolved production dependency inventory."
$seenPurls = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($component in $components) {
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$component.name)) "SBOM component name is required."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$component.version)) "SBOM component version is required."
	Assert-Condition ([string]$component.purl -like "pkg:nuget/*") "SBOM component '$($component.name)' must carry a NuGet purl."
	Assert-Condition ($seenPurls.Add([string]$component.purl)) "SBOM contains duplicate component purl '$($component.purl)'."
}

$policyContracts = @{}
foreach ($property in $policy.contracts.PSObject.Properties) {
	$policyContracts[$property.Name] = (@($property.Value) | Sort-Object) -join '|'
}
$manifestContracts = @{}
foreach ($contract in @($compatibilityManifest.contracts)) {
	$name = [string]$contract.name
	Assert-Condition (-not $manifestContracts.ContainsKey($name)) "Compatibility manifest contains duplicate contract '$name'."
	Assert-Condition ([string]$contract.compatibilityPolicy -eq "EXACT_DECLARED") "Contract '$name' must use explicit compatibility declaration."
	$manifestContracts[$name] = (@($contract.supportedVersions) | Sort-Object) -join '|'
}
Assert-Condition ($manifestContracts.Count -eq $policyContracts.Count) "Compatibility manifest contract set does not match release policy."
foreach ($name in $policyContracts.Keys) {
	Assert-Condition ($manifestContracts.ContainsKey($name)) "Compatibility manifest is missing contract '$name'."
	Assert-Condition ($manifestContracts[$name] -eq $policyContracts[$name]) "Compatibility versions for '$name' do not match release policy."
}
Assert-Condition ([string]$compatibilityManifest.ipc.compatibilityPolicy -eq "EXACT_DECLARED") "IPC compatibility must be explicitly declared."
Assert-Condition (((@($compatibilityManifest.ipc.protocolVersions) | Sort-Object) -join '|') -eq ((@($policy.ipc.protocolVersions) | Sort-Object) -join '|')) "IPC compatibility versions do not match release policy."
Assert-Condition ([string]$compatibilityManifest.ipc.schemaSet -eq [string]$policy.ipc.schemaSet) "IPC schema-set identity does not match release policy."

$policyHardware = @($policy.hardwareQualification)
$qualificationRequirements = @($qualificationManifest.requirements)
$compatibilityHardware = @($compatibilityManifest.hardwareQualification)
Assert-Condition ($qualificationRequirements.Count -eq $policyHardware.Count) "Qualification evidence requirement count does not match release policy."
Assert-Condition ($compatibilityHardware.Count -eq $policyHardware.Count) "Compatibility hardware qualification count does not match release policy."
$seenHardware = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($policyRequirement in $policyHardware) {
	$name = [string]$policyRequirement.requirement
	Assert-Condition ([string]$policyRequirement.status -eq "UNVERIFIED") "Static hardware qualification policy '$name' must remain UNVERIFIED."
	Assert-Condition ($seenHardware.Add($name)) "Release policy contains duplicate hardware requirement '$name'."
	$qualification = @($qualificationRequirements | Where-Object { [string]$_.requirement -eq $name })
	$compatibility = @($compatibilityHardware | Where-Object { [string]$_.requirement -eq $name })
	Assert-Condition ($qualification.Count -eq 1) "Qualification evidence manifest must contain exactly one '$name' requirement."
	Assert-Condition ($compatibility.Count -eq 1) "Compatibility manifest must contain exactly one '$name' requirement."

	$q = $qualification[0]
	$c = $compatibility[0]
	$qStatus = [string]$q.status
	Assert-Condition ($qStatus -in @("PASSED", "UNVERIFIED")) "Qualification evidence '$name' has invalid status '$qStatus'."
	$propertyNames = @($q.PSObject.Properties.Name)
	if ($qStatus -eq "UNVERIFIED") {
		Assert-Condition ([string]$c.status -eq "UNVERIFIED") "Compatibility hardware status '$name' must remain UNVERIFIED without bound physical evidence."
		Assert-Condition (-not ($propertyNames -contains "binding")) "UNVERIFIED qualification '$name' must not carry binding evidence."
		Assert-Condition (-not ($propertyNames -contains "payload")) "UNVERIFIED qualification '$name' must not carry payload evidence."
		continue
	}

	Assert-Condition ([string]$c.status -eq "PASS") "Compatibility hardware status '$name' must be PASS when source-bound qualification evidence is PASSED."
	foreach ($requiredProperty in @("qualificationType", "binding", "payload", "workflow")) {
		Assert-Condition ($propertyNames -contains $requiredProperty) "PASSED qualification '$name' is missing '$requiredProperty'."
	}
	$bindingPath = Resolve-EvidencePath ([string]$q.binding.path)
	$payloadPath = Resolve-EvidencePath ([string]$q.payload.path)
	Assert-Condition (Test-Path -LiteralPath $bindingPath -PathType Leaf) "Qualification binding for '$name' is missing from the release bundle."
	Assert-Condition (Test-Path -LiteralPath $payloadPath -PathType Leaf) "Qualification payload for '$name' is missing from the release bundle."
	Assert-Condition ((Get-Sha256 $bindingPath) -eq [string]$q.binding.sha256) "Qualification binding hash mismatch for '$name'."
	Assert-Condition ((Get-Sha256 $payloadPath) -eq [string]$q.payload.sha256) "Qualification payload hash mismatch for '$name'."

	$binding = Read-JsonFile $bindingPath
	$payload = Read-JsonFile $payloadPath
	Assert-Condition ([string]$binding.schemaVersion -eq "1.0") "Qualification binding schema mismatch for '$name'."
	Assert-Condition ([string]$binding.status -eq "PASSED") "Qualification binding is not PASSED for '$name'."
	Assert-Condition ([string]$binding.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Qualification binding source commit mismatch for '$name'."
	Assert-Condition ([string]$binding.qualificationType -eq [string]$q.qualificationType) "Qualification binding type mismatch for '$name'."
	Assert-Condition ([string]$binding.payload.sha256 -eq [string]$q.payload.sha256) "Qualification binding payload hash mismatch for '$name'."
	Assert-Condition ([string]$payload.schemaVersion -eq [string]$binding.payload.schemaVersion) "Qualification payload schema mismatch for '$name'."
	Assert-Condition ([string]$payload.status -eq "PASSED") "Qualification payload is not PASSED for '$name'."
	Assert-Condition ([string]$binding.workflow.runId -eq [string]$q.workflow.runId) "Qualification workflow runId mismatch for '$name'."
	Assert-Condition ([int]$binding.workflow.runAttempt -eq [int]$q.workflow.runAttempt) "Qualification workflow runAttempt mismatch for '$name'."
}

foreach ($hardware in $compatibilityHardware) {
	Assert-Condition ([string]$hardware.status -in $allowedStatuses) "Hardware qualification contains invalid status '$($hardware.status)'."
}

$domainIndex = @{}
foreach ($evidence in @($releaseEvidence.evidenceDomains)) {
	$domain = [string]$evidence.domain
	$status = [string]$evidence.status
	$severity = [string]$evidence.severity
	Assert-Condition (-not $domainIndex.ContainsKey($domain)) "Release evidence contains duplicate domain '$domain'."
	Assert-Condition ($status -in $allowedStatuses) "Release evidence domain '$domain' has invalid status '$status'."
	Assert-Condition ($severity -in $allowedSeverities) "Release evidence domain '$domain' has invalid severity '$severity'."
	Assert-Condition ($status -ne "FAIL") "Release evidence domain '$domain' is FAIL."
	$domainIndex[$domain] = $status
}
foreach ($requiredDomain in $requiredDomains) {
	Assert-Condition ($domainIndex.ContainsKey($requiredDomain)) "Release evidence is missing required domain '$requiredDomain'."
}

& (Join-Path $script:RepositoryRoot "build/security/Test-ProductSecurityAssessmentBinding.ps1") -OutputPath $outputRoot

Assert-Condition ([string]$releaseEvidence.signingAttestation.status -in $allowedStatuses) "Signing/attestation status is invalid."
if ([string]$releaseEvidence.signingAttestation.status -eq "PASS") {
	Assert-Condition ($releaseEvidence.signingAttestation.PSObject.Properties.Name -contains "signaturePath") "Signing/attestation PASS requires signaturePath evidence."
	$signaturePath = Resolve-EvidencePath ([string]$releaseEvidence.signingAttestation.signaturePath)
	Assert-Condition (Test-Path -LiteralPath $signaturePath -PathType Leaf) "Signing/attestation signature evidence is missing."
}

Assert-Condition ([string]$releaseEvidence.releaseReadiness.status -in $allowedStatuses) "Release-readiness status is invalid."
Assert-Condition ([string]$releaseEvidence.releaseReadiness.status -ne "FAIL") "Release-readiness gate is FAIL."
$promotedStages = @("RELEASE_CANDIDATE", "STABLE", "VALIDATED", "CERTIFIED")
if ([string]$releaseEvidence.releaseStage -in $promotedStages) {
	Assert-Condition ([string]$releaseEvidence.releaseReadiness.status -eq "PASS") "Release stage '$($releaseEvidence.releaseStage)' requires an explicit PASS release-readiness gate."
}

$schemaFiles = @(
	"schemas/release/v1/artifact-manifest.schema.json",
	"schemas/release/v1/compatibility-manifest.schema.json",
	"schemas/release/v1/qualification-evidence-manifest.schema.json",
	"schemas/release/v1/release-evidence.schema.json",
	"schemas/security/v1/product-security-assessment.schema.json"
)
foreach ($relativeSchema in $schemaFiles) {
	Assert-Condition (Test-Path -LiteralPath (Join-Path $script:RepositoryRoot $relativeSchema) -PathType Leaf) "Required release schema '$relativeSchema' is missing."
}
Assert-Condition (Test-Path -LiteralPath (Join-Path $script:RepositoryRoot "docs/ReleaseEvidence.md") -PathType Leaf) "Release evidence documentation is missing."

Write-Host "Release evidence verification PASS"
Write-Host "Product: $($releaseEvidence.productName) $($releaseEvidence.productVersion) ($($releaseEvidence.releaseStage))"
Write-Host "Source commit: $($releaseEvidence.sourceCommit)"
Write-Host "Build commit:  $($releaseEvidence.buildCommit)"
Write-Host "Artifacts verified: $($artifacts.Count)"
Write-Host "CycloneDX components verified: $($components.Count)"
Write-Host "Qualification requirements verified: $(@($qualificationRequirements | Where-Object { [string]$_.status -eq 'PASSED' }).Count) / $($qualificationRequirements.Count) PASSED"
Write-Host "Release SECURITY domain: $($domainIndex['SECURITY'])"
Write-Host "Release readiness: $($releaseEvidence.releaseReadiness.status)"
