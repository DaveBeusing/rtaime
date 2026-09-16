# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = "artifacts/release-evidence",
	[Parameter(Mandatory)]
	[string]$PrivateKeyPath,
	[ValidateSet("TEST_EPHEMERAL", "EXTERNAL_CONTROLLED")]
	[string]$SignerClass = "TEST_EPHEMERAL",
	[string]$SignerId = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-Sha256Bytes {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [System.Security.Cryptography.SHA256]::HashData($Bytes)
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString((Get-Sha256Bytes -Bytes $Bytes)).ToLowerInvariant()
}

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$outputRoot = Resolve-RepositoryPath $OutputPath
$privateKeyFullPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$outputPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if ($privateKeyFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
	throw "Release private key material must not be stored inside the repository."
}
if ($privateKeyFullPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
	throw "Release private key material must not be stored inside the release evidence bundle."
}
if (-not (Test-Path -LiteralPath $privateKeyFullPath -PathType Leaf)) {
	throw "Release private key was not found at '$privateKeyFullPath'."
}
if ($SignerClass -eq "EXTERNAL_CONTROLLED" -and [string]::IsNullOrWhiteSpace($SignerId)) {
	throw "EXTERNAL_CONTROLLED signing requires an explicit SignerId."
}

$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"
if (-not (Test-Path -LiteralPath $releaseEvidencePath -PathType Leaf)) {
	throw "Release evidence manifest was not found at '$releaseEvidencePath'."
}
$releaseEvidence = Get-Content -LiteralPath $releaseEvidencePath -Raw | ConvertFrom-Json
$releaseEvidenceBytes = [System.IO.File]::ReadAllBytes($releaseEvidencePath)
$releaseEvidenceHashBytes = Get-Sha256Bytes -Bytes $releaseEvidenceBytes
$releaseEvidenceHash = [Convert]::ToHexString($releaseEvidenceHashBytes).ToLowerInvariant()

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
	$privatePem = [System.IO.File]::ReadAllText($privateKeyFullPath)
	$ecdsa.ImportFromPem($privatePem)
	$parameters = $ecdsa.ExportParameters($false)
	if ($parameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7") {
		throw "Release signing key must use NIST P-256."
	}

	$publicKey = $ecdsa.ExportSubjectPublicKeyInfo()
	$keyFingerprint = Get-Sha256Hex -Bytes $publicKey
	$signature = $ecdsa.SignHash(
		$releaseEvidenceHashBytes,
		[System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
	if ($signature.Length -ne 64) {
		throw "Unexpected ECDSA P-256 signature length '$($signature.Length)'."
	}

	$attestation = [ordered]@{
		copyright = $copyright
		schemaVersion = "1.0"
		attestationType = "rtaime.software-release.v1"
		subject = [ordered]@{
			path = "release-evidence.json"
			sha256 = $releaseEvidenceHash
		}
		algorithm = "ECDSA_P256_SHA256"
		signatureFormat = "IEEE_P1363_FIXED_64"
		key = [ordered]@{
			fingerprintAlgorithm = "SHA256"
			fingerprint = $keyFingerprint
			publicKeyFormat = "SUBJECT_PUBLIC_KEY_INFO_DER_BASE64"
			publicKey = [Convert]::ToBase64String($publicKey)
			signerClass = $SignerClass
			signerId = if ([string]::IsNullOrWhiteSpace($SignerId)) { $null } else { $SignerId.Trim() }
		}
		signature = [Convert]::ToBase64String($signature)
		signedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	}

	$attestationPath = Join-Path $outputRoot "release-attestation.json"
	Write-JsonFile -Value $attestation -Path $attestationPath
	$attestationHash = Get-FileSha256Hex -Path $attestationPath

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
	$recordId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($recordCanonical))

	$releaseRecord = [ordered]@{
		copyright = $copyright
		schemaVersion = "1.0"
		recordType = "rtaime.release-record.v1"
		recordIdAlgorithm = "SHA256"
		recordId = $recordId
		productName = [string]$releaseEvidence.productName
		productVersion = [string]$releaseEvidence.productVersion
		releaseStage = [string]$releaseEvidence.releaseStage
		sourceCommit = [string]$releaseEvidence.sourceCommit
		buildCommit = [string]$releaseEvidence.buildCommit
		buildId = [string]$releaseEvidence.buildId
		releaseEvidence = [ordered]@{
			path = "release-evidence.json"
			sha256 = $releaseEvidenceHash
		}
		attestation = [ordered]@{
			path = "release-attestation.json"
			sha256 = $attestationHash
			keyFingerprint = $keyFingerprint
			signerClass = $SignerClass
		}
	}

	$releaseRecordPath = Join-Path $outputRoot "release-record.json"
	Write-JsonFile -Value $releaseRecord -Path $releaseRecordPath
	$releaseRecordHash = Get-FileSha256Hex -Path $releaseRecordPath
	[System.IO.File]::WriteAllText(
		(Join-Path $outputRoot "release-record.sha256"),
		"$releaseRecordHash  release-record.json$([Environment]::NewLine)",
		[System.Text.UTF8Encoding]::new($false))

	Write-Host "Release attestation created."
	Write-Host "Signer class: $SignerClass"
	Write-Host "Key fingerprint: $keyFingerprint"
	Write-Host "Release record id: $recordId"
} finally {
	$ecdsa.Dispose()
}
