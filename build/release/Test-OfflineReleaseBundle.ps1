# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$BundlePath = "",
	[switch]$RequireTrustedProductionKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$allowedRoles = @(
	"PRODUCT_PAYLOAD",
	"RELEASE_EVIDENCE",
	"SCHEMA",
	"DOCUMENTATION",
	"OFFLINE_TOOL",
	"TRUST_METADATA",
	"RUNTIME_METADATA",
	"PACKAGE_METADATA"
)
$allowedSignerClasses = @("TEST_EPHEMERAL", "EXTERNAL_CONTROLLED")

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

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-RelativeUnixPath {
	param(
		[Parameter(Mandatory)][string]$BasePath,
		[Parameter(Mandatory)][string]$Path
	)
	return [System.IO.Path]::GetRelativePath($BasePath, $Path).Replace('\', '/')
}

function Resolve-BundlePayloadPath {
	param(
		[Parameter(Mandatory)][string]$Root,
		[Parameter(Mandatory)][string]$RelativePath
	)
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($RelativePath)) "Bundle payload path must not be empty."
	Assert-Condition (-not $RelativePath.Contains('\')) "Bundle payload path '$RelativePath' must use '/' separators."
	Assert-Condition (-not $RelativePath.StartsWith('/')) "Bundle payload path '$RelativePath' must be relative."
	Assert-Condition ($RelativePath -notmatch '^[A-Za-z]:') "Bundle payload path '$RelativePath' must not contain a drive root."
	$segments = @($RelativePath.Split('/'))
	Assert-Condition ($segments.Count -gt 0) "Bundle payload path '$RelativePath' is invalid."
	Assert-Condition (-not ($segments -contains '..')) "Bundle payload path '$RelativePath' contains traversal."
	Assert-Condition (-not ($segments -contains '.')) "Bundle payload path '$RelativePath' contains a relative segment."
	Assert-Condition (-not ($segments -contains '')) "Bundle payload path '$RelativePath' contains an empty segment."

	$fullPath = [System.IO.Path]::GetFullPath((Join-Path $Root ($RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
	$rootPrefix = $Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
	Assert-Condition ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "Bundle payload path '$RelativePath' escapes the bundle root."
	return $fullPath
}

function Test-EcdsaAttestation {
	param(
		[Parameter(Mandatory)]$Attestation,
		[Parameter(Mandatory)][byte[]]$SubjectBytes,
		[Parameter(Mandatory)][string]$ExpectedSubjectPath,
		[Parameter(Mandatory)][string]$ExpectedAttestationType
	)

	Assert-Condition ([string]$Attestation.schemaVersion -eq "1.0") "Unsupported attestation schema version."
	Assert-Condition ([string]$Attestation.attestationType -eq $ExpectedAttestationType) "Unexpected attestation type '$($Attestation.attestationType)'."
	Assert-Condition ([string]$Attestation.subject.path -eq $ExpectedSubjectPath) "Attestation subject path mismatch."
	Assert-Condition ([string]$Attestation.algorithm -eq "ECDSA_P256_SHA256") "Unsupported attestation algorithm."
	Assert-Condition ([string]$Attestation.signatureFormat -eq "IEEE_P1363_FIXED_64") "Unsupported attestation signature format."
	Assert-Condition ([string]$Attestation.key.fingerprintAlgorithm -eq "SHA256") "Unsupported key fingerprint algorithm."
	Assert-Condition ([string]$Attestation.key.publicKeyFormat -eq "SUBJECT_PUBLIC_KEY_INFO_DER_BASE64") "Unsupported public key format."
	Assert-Condition ([string]$Attestation.key.signerClass -in $allowedSignerClasses) "Unsupported signer class '$($Attestation.key.signerClass)'."

	$subjectHashBytes = [System.Security.Cryptography.SHA256]::HashData($SubjectBytes)
	$subjectHash = [Convert]::ToHexString($subjectHashBytes).ToLowerInvariant()
	Assert-Condition ([string]$Attestation.subject.sha256 -eq $subjectHash) "Attestation subject SHA-256 mismatch."

	try {
		$publicKey = [Convert]::FromBase64String([string]$Attestation.key.publicKey)
		$signature = [Convert]::FromBase64String([string]$Attestation.signature)
	} catch {
		throw "Attestation contains invalid Base64 cryptographic material."
	}
	Assert-Condition ($signature.Length -eq 64) "ECDSA P-256 P1363 signature must be exactly 64 bytes."

	$keyFingerprint = Get-Sha256Hex -Bytes $publicKey
	Assert-Condition ([string]$Attestation.key.fingerprint -eq $keyFingerprint) "Attestation public-key fingerprint mismatch."

	$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
	try {
		$bytesRead = 0
		$ecdsa.ImportSubjectPublicKeyInfo($publicKey, [ref]$bytesRead)
		Assert-Condition ($bytesRead -eq $publicKey.Length) "Attestation public key contains trailing data."
		$parameters = $ecdsa.ExportParameters($false)
		Assert-Condition ($parameters.Curve.Oid.Value -eq "1.2.840.10045.3.1.7") "Attestation public key must use NIST P-256."
		$verified = $ecdsa.VerifyHash(
			$subjectHashBytes,
			$signature,
			[System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
		Assert-Condition $verified "Attestation signature verification failed."
	} finally {
		$ecdsa.Dispose()
	}

	return [pscustomobject]@{
		Fingerprint = $keyFingerprint
		SignerClass = [string]$Attestation.key.signerClass
		SubjectHash = $subjectHash
	}
}

function Expand-BundleArchiveSecurely {
	param(
		[Parameter(Mandatory)][string]$ArchivePath,
		[Parameter(Mandatory)][string]$Destination
	)

	New-Item -ItemType Directory -Path $Destination -Force | Out-Null
	$destinationPrefix = $Destination.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
	$seenEntries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
	$archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
	try {
		foreach ($entry in $archive.Entries) {
			if ([string]::IsNullOrEmpty($entry.Name)) {
				continue
			}
			$entryPath = $entry.FullName.Replace('\', '/')
			Assert-Condition (-not $entryPath.StartsWith('/')) "Archive entry '$entryPath' is absolute."
			Assert-Condition ($entryPath -notmatch '^[A-Za-z]:') "Archive entry '$entryPath' contains a drive root."
			$segments = @($entryPath.Split('/'))
			Assert-Condition (-not ($segments -contains '..')) "Archive entry '$entryPath' contains path traversal."
			Assert-Condition (-not ($segments -contains '.')) "Archive entry '$entryPath' contains a relative segment."
			Assert-Condition (-not ($segments -contains '')) "Archive entry '$entryPath' contains an empty segment."
			Assert-Condition ($seenEntries.Add($entryPath)) "Archive contains duplicate file entry '$entryPath'."

			$unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
			Assert-Condition ($unixType -ne 0xA000) "Archive entry '$entryPath' is a symbolic link and is not permitted."

			$destinationPath = [System.IO.Path]::GetFullPath((Join-Path $Destination ($entryPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
			Assert-Condition ($destinationPath.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) "Archive entry '$entryPath' escapes the extraction root."
			$parent = Split-Path -Parent $destinationPath
			if (-not (Test-Path -LiteralPath $parent)) {
				New-Item -ItemType Directory -Path $parent -Force | Out-Null
			}
			[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destinationPath, $false)
		}
	} finally {
		$archive.Dispose()
	}
}

if ([string]::IsNullOrWhiteSpace($BundlePath)) {
	$candidate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
	if (Test-Path -LiteralPath (Join-Path $candidate "bundle-manifest.json") -PathType Leaf) {
		$BundlePath = $candidate
	} else {
		throw "BundlePath is required when the verifier is not running from an unpacked offline bundle."
	}
}

$bundleInput = [System.IO.Path]::GetFullPath($BundlePath)
$tempRoot = $null
try {
	if (Test-Path -LiteralPath $bundleInput -PathType Leaf) {
		Assert-Condition ([System.IO.Path]::GetExtension($bundleInput).Equals(".zip", [StringComparison]::OrdinalIgnoreCase)) "Offline bundle file must be a .zip archive."
		$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-offline-verify-{0}" -f [Guid]::NewGuid().ToString("N"))
		Expand-BundleArchiveSecurely -ArchivePath $bundleInput -Destination $tempRoot
		$bundleRoot = $tempRoot
	} else {
		Assert-Condition (Test-Path -LiteralPath $bundleInput -PathType Container) "Offline bundle was not found at '$bundleInput'."
		$bundleRoot = $bundleInput
	}

	$privateMaterial = @(Get-ChildItem -LiteralPath $bundleRoot -File -Recurse | Where-Object {
		$_.Extension.ToLowerInvariant() -in @(".pem", ".key", ".pfx", ".p12")
	})
	if ($privateMaterial.Count -gt 0) {
		throw "Offline bundle contains forbidden private-key-like material '$($privateMaterial[0].FullName)'."
	}

	$manifestPath = Join-Path $bundleRoot "bundle-manifest.json"
	$attestationPath = Join-Path $bundleRoot "bundle-attestation.json"
	$manifest = Read-JsonFile $manifestPath
	$bundleAttestation = Read-JsonFile $attestationPath

	Assert-Condition ([string]$manifest.schemaVersion -eq "1.0") "Unsupported offline bundle manifest schema version."
	Assert-Condition ([string]$manifest.bundleType -eq "SOFTWARE_RELEASE") "Offline bundle must be a SOFTWARE_RELEASE bundle."
	Assert-Condition ([string]$manifest.packageFormatVersion -eq "1.0") "Unsupported offline package format version."
	Assert-Condition ([string]$manifest.productName -eq "rtaime") "Unexpected offline bundle product identity."
	Assert-Condition ([string]$manifest.platform.osFamily -eq "Windows") "Offline V1 bundle must target Windows."
	Assert-Condition ([string]$manifest.platform.architecture -eq "x64") "Offline V1 bundle must target x64."
	Assert-Condition ([string]$manifest.platform.rid -eq "win-x64") "Offline V1 bundle must target win-x64."
	Assert-Condition ([string]$manifest.payloadHashAlgorithm -eq "SHA256") "Offline bundle payload hash algorithm must be SHA256."
	Assert-Condition ([string]$manifest.runtimeRequirementsPath -eq "metadata/runtime-requirements.json") "Unexpected runtime requirements path."

	$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
	$bundleTrust = Test-EcdsaAttestation `
		-Attestation $bundleAttestation `
		-SubjectBytes $manifestBytes `
		-ExpectedSubjectPath "bundle-manifest.json" `
		-ExpectedAttestationType "rtaime.offline-software-bundle.v1"

	$payloadIndex = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
	$payload = @($manifest.payload)
	Assert-Condition ($payload.Count -gt 0) "Offline bundle manifest must contain payload entries."
	foreach ($entry in $payload) {
		$relativePath = [string]$entry.path
		Assert-Condition ($payloadIndex.Add($relativePath)) "Offline bundle manifest contains duplicate payload path '$relativePath'."
		Assert-Condition ([string]$entry.role -in $allowedRoles) "Offline bundle payload '$relativePath' has unsupported role '$($entry.role)'."
		$filePath = Resolve-BundlePayloadPath -Root $bundleRoot -RelativePath $relativePath
		Assert-Condition (Test-Path -LiteralPath $filePath -PathType Leaf) "Offline bundle payload '$relativePath' is missing."
		$fileInfo = Get-Item -LiteralPath $filePath
		Assert-Condition ($fileInfo.Length -eq [long]$entry.size) "Offline bundle payload size mismatch for '$relativePath'."
		Assert-Condition ((Get-FileSha256Hex -Path $filePath) -eq ([string]$entry.sha256).ToLowerInvariant()) "Offline bundle payload SHA-256 mismatch for '$relativePath'."
	}

	foreach ($actualFile in @(Get-ChildItem -LiteralPath $bundleRoot -File -Recurse)) {
		$relativePath = Get-RelativeUnixPath -BasePath $bundleRoot -Path $actualFile.FullName
		if ($relativePath -in @("bundle-manifest.json", "bundle-attestation.json")) {
			continue
		}
		Assert-Condition ($payloadIndex.Contains($relativePath)) "Offline bundle contains unlisted payload file '$relativePath'."
	}

	$requiredBundleFiles = @(
		"schemas/release/v1/offline-bundle-manifest.schema.json",
		"schemas/release/v1/offline-bundle-attestation.schema.json",
		"schemas/release/v1/runtime-requirements.schema.json",
		"schemas/release/v1/qualification-evidence-manifest.schema.json",
		"docs/ReleasePackagingAndOfflineDeployment.md",
		"docs/QualificationEvidenceProvenance.md",
		"docs/InvestorDemoScenario.md",
		"Start-rtaime-Showcase.cmd",
		"tools/Invoke-InvestorDemo.ps1",
		"tools/Start-rtaime-Showcase.cmd",
		"tools/Test-OfflineReleaseBundle.ps1",
		"tools/Invoke-OfflinePreflight.ps1",
		"tools/Install-OfflineRelease.ps1",
		"trust/trusted-release-keys.json",
		"metadata/runtime-requirements.json",
		"release/qualification-evidence-manifest.json",
		"OFFLINE-README.md"
	)
	foreach ($relativePath in $requiredBundleFiles) {
		Assert-Condition (Test-Path -LiteralPath (Resolve-BundlePayloadPath -Root $bundleRoot -RelativePath $relativePath) -PathType Leaf) "Offline bundle is missing required file '$relativePath'."
	}

	$runtimeRequirements = Read-JsonFile (Join-Path $bundleRoot "metadata/runtime-requirements.json")
	Assert-Condition ([string]$runtimeRequirements.schemaVersion -eq "1.0") "Unsupported runtime requirements schema version."
	Assert-Condition ([string]$runtimeRequirements.productName -eq [string]$manifest.productName) "Runtime requirements product identity mismatch."
	Assert-Condition ([string]$runtimeRequirements.productVersion -eq [string]$manifest.productVersion) "Runtime requirements product version mismatch."
	Assert-Condition ([string]$runtimeRequirements.platform.osFamily -eq [string]$manifest.platform.osFamily) "Runtime requirements OS mismatch."
	Assert-Condition ([string]$runtimeRequirements.platform.architecture -eq [string]$manifest.platform.architecture) "Runtime requirements architecture mismatch."
	Assert-Condition ([string]$runtimeRequirements.platform.rid -eq [string]$manifest.platform.rid) "Runtime requirements RID mismatch."
	Assert-Condition ($runtimeRequirements.continuousInternetRequired -eq $false) "Offline bundle must not require continuous Internet connectivity."
	Assert-Condition (@($runtimeRequirements.dotnetRuntimes).Count -gt 0) "Offline bundle must declare required .NET runtimes."

	$releaseDirectory = Join-Path $bundleRoot "release"
	$releaseEvidencePath = Join-Path $releaseDirectory "release-evidence.json"
	$releaseAttestationPath = Join-Path $releaseDirectory "release-attestation.json"
	$releaseRecordPath = Join-Path $releaseDirectory "release-record.json"
	$artifactManifestPath = Join-Path $releaseDirectory "artifact-manifest.json"
	$sbomPath = Join-Path $releaseDirectory "sbom.cdx.json"
	$compatibilityPath = Join-Path $releaseDirectory "compatibility-manifest.json"
	$qualificationManifestPath = Join-Path $releaseDirectory "qualification-evidence-manifest.json"

	$releaseEvidence = Read-JsonFile $releaseEvidencePath
	$releaseAttestation = Read-JsonFile $releaseAttestationPath
	$releaseRecord = Read-JsonFile $releaseRecordPath
	$artifactManifest = Read-JsonFile $artifactManifestPath
	$compatibilityManifest = Read-JsonFile $compatibilityPath
	$qualificationManifest = Read-JsonFile $qualificationManifestPath

	$releaseEvidenceBytes = [System.IO.File]::ReadAllBytes($releaseEvidencePath)
	$releaseTrust = Test-EcdsaAttestation `
		-Attestation $releaseAttestation `
		-SubjectBytes $releaseEvidenceBytes `
		-ExpectedSubjectPath "release-evidence.json" `
		-ExpectedAttestationType "rtaime.software-release.v1"

	Assert-Condition ($releaseTrust.Fingerprint -eq $bundleTrust.Fingerprint) "Offline bundle and contained release evidence must be signed by the same key."
	Assert-Condition ($releaseTrust.SignerClass -eq $bundleTrust.SignerClass) "Offline bundle and contained release evidence signer classes differ."
	Assert-Condition ([string]$manifest.releaseTrust.releaseKeyFingerprint -eq $releaseTrust.Fingerprint) "Bundle manifest release key fingerprint mismatch."
	Assert-Condition ([string]$manifest.releaseTrust.releaseSignerClass -eq $releaseTrust.SignerClass) "Bundle manifest release signer class mismatch."

	Assert-Condition ([string]$releaseRecord.productName -eq [string]$manifest.productName) "Release record product identity mismatch."
	Assert-Condition ([string]$releaseRecord.productVersion -eq [string]$manifest.productVersion) "Release record product version mismatch."
	Assert-Condition ([string]$releaseRecord.releaseStage -eq [string]$manifest.releaseStage) "Release record stage mismatch."
	Assert-Condition ([string]$releaseRecord.sourceCommit -eq [string]$manifest.sourceCommit) "Release record source commit mismatch."
	Assert-Condition ([string]$releaseRecord.buildCommit -eq [string]$manifest.buildCommit) "Release record build commit mismatch."
	Assert-Condition ([string]$releaseRecord.buildId -eq [string]$manifest.buildId) "Release record build identity mismatch."
	Assert-Condition ([string]$releaseRecord.recordId -eq [string]$manifest.releaseTrust.releaseRecordId) "Bundle manifest release record identity mismatch."

	$releaseEvidenceHash = Get-FileSha256Hex -Path $releaseEvidencePath
	Assert-Condition ([string]$releaseRecord.releaseEvidence.path -eq "release-evidence.json") "Release record evidence path mismatch."
	Assert-Condition ([string]$releaseRecord.releaseEvidence.sha256 -eq $releaseEvidenceHash) "Release record evidence SHA-256 mismatch."
	$releaseAttestationHash = Get-FileSha256Hex -Path $releaseAttestationPath
	Assert-Condition ([string]$releaseRecord.attestation.path -eq "release-attestation.json") "Release record attestation path mismatch."
	Assert-Condition ([string]$releaseRecord.attestation.sha256 -eq $releaseAttestationHash) "Release record attestation SHA-256 mismatch."
	Assert-Condition ([string]$releaseRecord.attestation.keyFingerprint -eq $releaseTrust.Fingerprint) "Release record key fingerprint mismatch."

	$recordCanonical = @(
		"rtaime.release-record.v1",
		[string]$releaseEvidence.productName,
		[string]$releaseEvidence.productVersion,
		[string]$releaseEvidence.releaseStage,
		[string]$releaseEvidence.sourceCommit,
		[string]$releaseEvidence.buildCommit,
		[string]$releaseEvidence.buildId,
		$releaseEvidenceHash,
		$releaseAttestationHash,
		$releaseTrust.Fingerprint
	) -join "`n"
	$expectedRecordId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($recordCanonical))
	Assert-Condition ([string]$releaseRecord.recordId -eq $expectedRecordId) "Contained release record content id mismatch."

	$releaseRecordSidecarPath = Join-Path $releaseDirectory "release-record.sha256"
	Assert-Condition (Test-Path -LiteralPath $releaseRecordSidecarPath -PathType Leaf) "Contained release record SHA-256 sidecar is missing."
	$expectedRecordFileHash = Get-FileSha256Hex -Path $releaseRecordPath
	$sidecar = [System.IO.File]::ReadAllText($releaseRecordSidecarPath).Trim()
	Assert-Condition ($sidecar -eq "$expectedRecordFileHash  release-record.json") "Contained release record SHA-256 sidecar mismatch."

	$releaseReferences = @(
		@{ Name = "artifact manifest"; Reference = $releaseEvidence.artifactManifest; Path = $artifactManifestPath },
		@{ Name = "SBOM"; Reference = $releaseEvidence.sbom; Path = $sbomPath },
		@{ Name = "compatibility manifest"; Reference = $releaseEvidence.compatibilityManifest; Path = $compatibilityPath },
		@{ Name = "qualification evidence manifest"; Reference = $releaseEvidence.qualificationEvidenceManifest; Path = $qualificationManifestPath }
	)
	foreach ($reference in $releaseReferences) {
		Assert-Condition (Test-Path -LiteralPath $reference.Path -PathType Leaf) "Contained $($reference.Name) is missing."
		Assert-Condition ((Get-FileSha256Hex -Path $reference.Path) -eq ([string]$reference.Reference.sha256).ToLowerInvariant()) "Contained $($reference.Name) hash mismatch."
	}

	Assert-Condition ([string]$qualificationManifest.schemaVersion -eq "1.0") "Contained qualification evidence manifest schema mismatch."
	Assert-Condition ([string]$qualificationManifest.repository -eq "DaveBeusing/rtaime") "Contained qualification evidence repository identity mismatch."
	Assert-Condition ([string]$qualificationManifest.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Contained qualification evidence source commit mismatch."
	$qualificationRequirements = @($qualificationManifest.requirements)
	$compatibilityHardware = @($compatibilityManifest.hardwareQualification)
	Assert-Condition ($qualificationRequirements.Count -eq $compatibilityHardware.Count) "Contained qualification and compatibility hardware requirement counts differ."
	foreach ($qualification in $qualificationRequirements) {
		$name = [string]$qualification.requirement
		$status = [string]$qualification.status
		Assert-Condition ($status -in @("PASSED", "UNVERIFIED")) "Contained qualification '$name' has invalid status '$status'."
		$compatibility = @($compatibilityHardware | Where-Object { [string]$_.requirement -eq $name })
		Assert-Condition ($compatibility.Count -eq 1) "Contained compatibility evidence must contain exactly one '$name' requirement."
		if ($status -eq "UNVERIFIED") {
			Assert-Condition ([string]$compatibility[0].status -eq "UNVERIFIED") "Contained compatibility requirement '$name' must remain UNVERIFIED without physical evidence."
			continue
		}

		Assert-Condition ([string]$compatibility[0].status -eq "PASS") "Contained compatibility requirement '$name' must be PASS when physical evidence is PASSED."
		$bindingPath = Resolve-BundlePayloadPath -Root $releaseDirectory -RelativePath ([string]$qualification.binding.path)
		$payloadPath = Resolve-BundlePayloadPath -Root $releaseDirectory -RelativePath ([string]$qualification.payload.path)
		Assert-Condition (Test-Path -LiteralPath $bindingPath -PathType Leaf) "Contained qualification binding for '$name' is missing."
		Assert-Condition (Test-Path -LiteralPath $payloadPath -PathType Leaf) "Contained qualification payload for '$name' is missing."
		Assert-Condition ((Get-FileSha256Hex -Path $bindingPath) -eq [string]$qualification.binding.sha256) "Contained qualification binding hash mismatch for '$name'."
		Assert-Condition ((Get-FileSha256Hex -Path $payloadPath) -eq [string]$qualification.payload.sha256) "Contained qualification payload hash mismatch for '$name'."
		$binding = Read-JsonFile $bindingPath
		$physicalPayload = Read-JsonFile $payloadPath
		Assert-Condition ([string]$binding.schemaVersion -eq "1.0") "Contained qualification binding schema mismatch for '$name'."
		Assert-Condition ([string]$binding.status -eq "PASSED") "Contained qualification binding is not PASSED for '$name'."
		Assert-Condition ([string]$binding.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Contained qualification binding source commit mismatch for '$name'."
		Assert-Condition (@($binding.releaseRequirements) -contains $name) "Contained qualification binding does not authorize release requirement '$name'."
		Assert-Condition ([string]$binding.payload.sha256 -eq [string]$qualification.payload.sha256) "Contained qualification binding payload hash mismatch for '$name'."
		Assert-Condition ([string]$physicalPayload.status -eq "PASSED") "Contained physical qualification payload is not PASSED for '$name'."
		Assert-Condition ([string]$binding.workflow.runId -eq [string]$qualification.workflow.runId) "Contained qualification workflow runId mismatch for '$name'."
		Assert-Condition ([int]$binding.workflow.runAttempt -eq [int]$qualification.workflow.runAttempt) "Contained qualification workflow runAttempt mismatch for '$name'."
	}

	foreach ($artifact in @($artifactManifest.artifacts)) {
		$artifactPath = Resolve-BundlePayloadPath -Root $bundleRoot -RelativePath ([string]$artifact.path)
		Assert-Condition (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Signed product artifact '$($artifact.path)' is missing from bundle."
		$fileInfo = Get-Item -LiteralPath $artifactPath
		Assert-Condition ($fileInfo.Length -eq [long]$artifact.size) "Signed product artifact size mismatch for '$($artifact.path)'."
		Assert-Condition ((Get-FileSha256Hex -Path $artifactPath) -eq ([string]$artifact.sha256).ToLowerInvariant()) "Signed product artifact SHA-256 mismatch for '$($artifact.path)'."
	}

	Assert-Condition ([string]$releaseEvidence.productName -eq [string]$manifest.productName) "Contained release evidence product mismatch."
	Assert-Condition ([string]$releaseEvidence.productVersion -eq [string]$manifest.productVersion) "Contained release evidence version mismatch."
	Assert-Condition ([string]$releaseEvidence.releaseStage -eq [string]$manifest.releaseStage) "Contained release evidence stage mismatch."
	Assert-Condition ([string]$releaseEvidence.sourceCommit -eq [string]$manifest.sourceCommit) "Contained release evidence source commit mismatch."
	Assert-Condition ([string]$releaseEvidence.buildCommit -eq [string]$manifest.buildCommit) "Contained release evidence build commit mismatch."
	Assert-Condition ([string]$releaseEvidence.buildId -eq [string]$manifest.buildId) "Contained release evidence build identity mismatch."

	$trustStore = Read-JsonFile (Join-Path $bundleRoot "trust/trusted-release-keys.json")
	Assert-Condition ([string]$trustStore.schemaVersion -eq "1.0") "Unsupported trusted release key store schema version."
	$trustedKey = @($trustStore.keys | Where-Object {
		[string]$_.fingerprint -eq $bundleTrust.Fingerprint -and
		[string]$_.status -eq "ACTIVE" -and
		[string]$_.purpose -eq "SOFTWARE_RELEASE"
	})
	Assert-Condition ($trustedKey.Count -le 1) "Trusted release key store contains duplicate active entries for '$($bundleTrust.Fingerprint)'."
	if ($bundleTrust.SignerClass -eq "TEST_EPHEMERAL") {
		Assert-Condition ($trustedKey.Count -eq 0) "TEST_EPHEMERAL signing key must never be enrolled as active production trust."
	}
	$productionTrust = if ($trustedKey.Count -eq 1 -and $bundleTrust.SignerClass -eq "EXTERNAL_CONTROLLED") { "PASS" } else { "UNVERIFIED" }
	if ($RequireTrustedProductionKey) {
		Assert-Condition ($bundleTrust.SignerClass -eq "EXTERNAL_CONTROLLED") "Production-trust verification requires EXTERNAL_CONTROLLED signer class."
		Assert-Condition ($productionTrust -eq "PASS") "Offline bundle signature is valid but its key is not active trusted SOFTWARE_RELEASE production trust."
	}

	Write-Host "Offline release bundle verification PASS"
	Write-Host "Bundle: $($manifest.bundleName)"
	Write-Host "Product: $($manifest.productName) $($manifest.productVersion) ($($manifest.releaseStage))"
	Write-Host "Payload files verified: $($payload.Count)"
	Write-Host "Physical qualification requirements: $(@($qualificationRequirements | Where-Object { [string]$_.status -eq 'PASSED' }).Count) / $($qualificationRequirements.Count) PASSED"
	Write-Host "Bundle signature: PASS"
	Write-Host "Contained release signature: PASS"
	Write-Host "Production key trust: $productionTrust"
	Write-Host "Continuous Internet required: false"
} finally {
	if ($null -ne $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
		Remove-Item -LiteralPath $tempRoot -Recurse -Force
	}
}
