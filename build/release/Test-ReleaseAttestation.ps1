# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = "artifacts/release-evidence",
	[switch]$RequireTrustedProductionKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
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

$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
	[System.IO.Path]::GetFullPath($OutputPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}

Assert-Condition (Test-Path -LiteralPath $outputRoot -PathType Container) "Release evidence directory was not found at '$outputRoot'."

$privateMaterial = @(Get-ChildItem -LiteralPath $outputRoot -File -Recurse | Where-Object {
	$_.Extension.ToLowerInvariant() -in @(".pem", ".key", ".pfx", ".p12")
})
if ($privateMaterial.Count -gt 0) {
	throw "Release evidence bundle contains forbidden key-material file '$($privateMaterial[0].FullName)'."
}

$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"
$attestationPath = Join-Path $outputRoot "release-attestation.json"
$releaseRecordPath = Join-Path $outputRoot "release-record.json"
$releaseRecordHashPath = Join-Path $outputRoot "release-record.sha256"
$trustStorePath = Join-Path $PSScriptRoot "trusted-release-keys.json"

$releaseEvidence = Read-JsonFile $releaseEvidencePath
$attestation = Read-JsonFile $attestationPath
$releaseRecord = Read-JsonFile $releaseRecordPath
$trustStore = Read-JsonFile $trustStorePath

$requiredRepositoryFiles = @(
	"schemas/release/v1/release-attestation.schema.json",
	"schemas/release/v1/release-record.schema.json",
	"schemas/release/v1/trusted-release-keys.schema.json",
	"docs/ReleaseSigningAndAttestation.md"
)
foreach ($relativePath in $requiredRepositoryFiles) {
	Assert-Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath) -PathType Leaf) "Required signing trust artifact '$relativePath' is missing."
}

Assert-Condition ([string]$attestation.schemaVersion -eq "1.0") "Unsupported release attestation schema version."
Assert-Condition ([string]$attestation.attestationType -eq "rtaime.software-release.v1") "Unsupported release attestation type."
Assert-Condition ([string]$attestation.algorithm -eq "ECDSA_P256_SHA256") "Unsupported release signing algorithm."
Assert-Condition ([string]$attestation.signatureFormat -eq "IEEE_P1363_FIXED_64") "Unsupported release signature format."
Assert-Condition ([string]$attestation.key.fingerprintAlgorithm -eq "SHA256") "Unsupported release key fingerprint algorithm."
Assert-Condition ([string]$attestation.key.publicKeyFormat -eq "SUBJECT_PUBLIC_KEY_INFO_DER_BASE64") "Unsupported public key encoding."
Assert-Condition ([string]$attestation.key.signerClass -in $allowedSignerClasses) "Unsupported signer class '$($attestation.key.signerClass)'."

$releaseEvidenceBytes = [System.IO.File]::ReadAllBytes($releaseEvidencePath)
$releaseEvidenceHashBytes = [System.Security.Cryptography.SHA256]::HashData($releaseEvidenceBytes)
$releaseEvidenceHash = [Convert]::ToHexString($releaseEvidenceHashBytes).ToLowerInvariant()
Assert-Condition ([string]$attestation.subject.path -eq "release-evidence.json") "Attestation subject path must be release-evidence.json."
Assert-Condition ([string]$attestation.subject.sha256 -eq $releaseEvidenceHash) "Attestation subject hash does not match release-evidence.json."

try {
	$publicKey = [Convert]::FromBase64String([string]$attestation.key.publicKey)
	$signature = [Convert]::FromBase64String([string]$attestation.signature)
} catch {
	throw "Release attestation contains invalid Base64 cryptographic material."
}
Assert-Condition ($signature.Length -eq 64) "ECDSA P-256 P1363 signature must be exactly 64 bytes."

$keyFingerprint = Get-Sha256Hex -Bytes $publicKey
Assert-Condition ([string]$attestation.key.fingerprint -eq $keyFingerprint) "Release attestation public-key fingerprint mismatch."

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
	$bytesRead = 0
	$ecdsa.ImportSubjectPublicKeyInfo($publicKey, [ref]$bytesRead)
	Assert-Condition ($bytesRead -eq $publicKey.Length) "Release attestation public key contains trailing data."
	$parameters = $ecdsa.ExportParameters($false)
	Assert-Condition ($parameters.Curve.Oid.Value -eq "1.2.840.10045.3.1.7") "Release attestation public key must use NIST P-256."
	$verified = $ecdsa.VerifyHash(
		$releaseEvidenceHashBytes,
		$signature,
		[System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
	Assert-Condition $verified "Release attestation signature verification failed."
} finally {
	$ecdsa.Dispose()
}

Assert-Condition ([string]$releaseRecord.schemaVersion -eq "1.0") "Unsupported release record schema version."
Assert-Condition ([string]$releaseRecord.recordType -eq "rtaime.release-record.v1") "Unsupported release record type."
Assert-Condition ([string]$releaseRecord.recordIdAlgorithm -eq "SHA256") "Unsupported release record id algorithm."
Assert-Condition ([string]$releaseRecord.productName -eq [string]$releaseEvidence.productName) "Release record product identity mismatch."
Assert-Condition ([string]$releaseRecord.productVersion -eq [string]$releaseEvidence.productVersion) "Release record product version mismatch."
Assert-Condition ([string]$releaseRecord.releaseStage -eq [string]$releaseEvidence.releaseStage) "Release record stage mismatch."
Assert-Condition ([string]$releaseRecord.sourceCommit -eq [string]$releaseEvidence.sourceCommit) "Release record source commit mismatch."
Assert-Condition ([string]$releaseRecord.buildCommit -eq [string]$releaseEvidence.buildCommit) "Release record build commit mismatch."
Assert-Condition ([string]$releaseRecord.buildId -eq [string]$releaseEvidence.buildId) "Release record build identity mismatch."
Assert-Condition ([string]$releaseRecord.releaseEvidence.path -eq "release-evidence.json") "Release record evidence path mismatch."
Assert-Condition ([string]$releaseRecord.releaseEvidence.sha256 -eq $releaseEvidenceHash) "Release record evidence hash mismatch."

$attestationHash = Get-FileSha256Hex -Path $attestationPath
Assert-Condition ([string]$releaseRecord.attestation.path -eq "release-attestation.json") "Release record attestation path mismatch."
Assert-Condition ([string]$releaseRecord.attestation.sha256 -eq $attestationHash) "Release record attestation hash mismatch."
Assert-Condition ([string]$releaseRecord.attestation.keyFingerprint -eq $keyFingerprint) "Release record key fingerprint mismatch."
Assert-Condition ([string]$releaseRecord.attestation.signerClass -eq [string]$attestation.key.signerClass) "Release record signer class mismatch."

$recordCanonical = @(
	"rtaime.release-record.v1",
	[string]$releaseEvidence.productName,
	[string]$releaseEvidence.productVersion,
	[string]$releaseEvidence.releaseStage,
	[string]$releaseEvidence.sourceCommit,
	[string]$releaseEvidence.buildCommit,
	[string]$releaseEvidence.buildId,
	$releaseEvidenceHash,
	$attestationHash,
	$keyFingerprint
) -join "`n"
$expectedRecordId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($recordCanonical))
Assert-Condition ([string]$releaseRecord.recordId -eq $expectedRecordId) "Content-addressed release record id mismatch."

Assert-Condition (Test-Path -LiteralPath $releaseRecordHashPath -PathType Leaf) "Release record SHA-256 sidecar is missing."
$expectedRecordFileHash = Get-FileSha256Hex -Path $releaseRecordPath
$sidecar = [System.IO.File]::ReadAllText($releaseRecordHashPath).Trim()
Assert-Condition ($sidecar -eq "$expectedRecordFileHash  release-record.json") "Release record SHA-256 sidecar mismatch."

Assert-Condition ([string]$trustStore.schemaVersion -eq "1.0") "Unsupported trusted release key store schema version."
$trustedKey = @($trustStore.keys | Where-Object {
	[string]$_.fingerprint -eq $keyFingerprint -and
	[string]$_.status -eq "ACTIVE" -and
	[string]$_.purpose -eq "SOFTWARE_RELEASE"
})
Assert-Condition ($trustedKey.Count -le 1) "Trusted release key store contains duplicate active entries for '$keyFingerprint'."

$productionTrust = if ($trustedKey.Count -eq 1 -and [string]$attestation.key.signerClass -eq "EXTERNAL_CONTROLLED") { "PASS" } else { "UNVERIFIED" }
if ([string]$attestation.key.signerClass -eq "TEST_EPHEMERAL") {
	Assert-Condition ($trustedKey.Count -eq 0) "TEST_EPHEMERAL signing key must never be enrolled as an active production release key."
}
if ($RequireTrustedProductionKey) {
	Assert-Condition ([string]$attestation.key.signerClass -eq "EXTERNAL_CONTROLLED") "Production-trust verification requires EXTERNAL_CONTROLLED signer class."
	Assert-Condition ($productionTrust -eq "PASS") "Release signature is cryptographically valid but its key is not an active trusted SOFTWARE_RELEASE key."
}

Write-Host "Release signing verification PASS"
Write-Host "Cryptographic signature: PASS"
Write-Host "Content-addressed release record: PASS"
Write-Host "Key fingerprint: $keyFingerprint"
Write-Host "Signer class: $($attestation.key.signerClass)"
Write-Host "Production key trust: $productionTrust"
Write-Host "Release record id: $($releaseRecord.recordId)"
