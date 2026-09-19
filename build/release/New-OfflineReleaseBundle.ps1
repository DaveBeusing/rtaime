# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$EvidencePath = "artifacts/release-evidence",
	[string]$OutputPath = "artifacts/offline-release",
	[Parameter(Mandatory)]
	[string]$PrivateKeyPath,
	[ValidateSet("TEST_EPHEMERAL", "EXTERNAL_CONTROLLED")]
	[string]$SignerClass = "TEST_EPHEMERAL",
	[string]$SignerId = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) {
		throw $Message
	}
}

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Path))
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeUnixPath {
	param(
		[Parameter(Mandatory)][string]$BasePath,
		[Parameter(Mandatory)][string]$Path
	)
	return [System.IO.Path]::GetRelativePath($BasePath, $Path).Replace('\', '/')
}

function Copy-DirectoryContent {
	param(
		[Parameter(Mandatory)][string]$Source,
		[Parameter(Mandatory)][string]$Destination
	)
	if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
		throw "Required bundle source directory was not found at '$Source'."
	}
	foreach ($file in @(Get-ChildItem -LiteralPath $Source -File -Recurse)) {
		$relative = [System.IO.Path]::GetRelativePath($Source, $file.FullName)
		$target = Join-Path $Destination $relative
		$targetDirectory = Split-Path -Parent $target
		if (-not (Test-Path -LiteralPath $targetDirectory)) {
			New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
		}
		Copy-Item -LiteralPath $file.FullName -Destination $target -Force
	}
}

function Get-PayloadRole {
	param([Parameter(Mandatory)][string]$RelativePath)
	if ($RelativePath.StartsWith("product/", [StringComparison]::OrdinalIgnoreCase) -or
		$RelativePath -in @("rtaime.exe", "rtaime.dll", "rtaime.deps.json", "rtaime.runtimeconfig.json", "host-lifecycle-policy.json")) {
		return "PRODUCT_PAYLOAD"
	}
	if ($RelativePath.StartsWith("release/", [StringComparison]::OrdinalIgnoreCase)) { return "RELEASE_EVIDENCE" }
	if ($RelativePath.StartsWith("schemas/", [StringComparison]::OrdinalIgnoreCase)) { return "SCHEMA" }
	if ($RelativePath.StartsWith("docs/", [StringComparison]::OrdinalIgnoreCase)) { return "DOCUMENTATION" }
	if ($RelativePath.StartsWith("tools/", [StringComparison]::OrdinalIgnoreCase)) { return "OFFLINE_TOOL" }
	if ($RelativePath.StartsWith("trust/", [StringComparison]::OrdinalIgnoreCase)) { return "TRUST_METADATA" }
	if ($RelativePath.StartsWith("metadata/", [StringComparison]::OrdinalIgnoreCase)) { return "RUNTIME_METADATA" }
	return "PACKAGE_METADATA"
}

$policyPath = Join-Path $PSScriptRoot "offline-bundle-policy.json"
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
	throw "Offline bundle policy was not found at '$policyPath'."
}
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

$evidenceRoot = Resolve-RepositoryPath $EvidencePath
$outputRoot = Resolve-RepositoryPath $OutputPath
$privateKeyFullPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$repositoryPrefix = $script:RepositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if ($privateKeyFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
	throw "Offline bundle private signing key material must not be stored inside the repository."
}
if (-not (Test-Path -LiteralPath $privateKeyFullPath -PathType Leaf)) {
	throw "Offline bundle private signing key was not found at '$privateKeyFullPath'."
}
if ($SignerClass -eq "EXTERNAL_CONTROLLED" -and [string]::IsNullOrWhiteSpace($SignerId)) {
	throw "EXTERNAL_CONTROLLED bundle signing requires an explicit SignerId."
}

& (Join-Path $PSScriptRoot "Test-ReleaseEvidence.ps1") -OutputPath $evidenceRoot
& (Join-Path $PSScriptRoot "Test-ReleaseAttestation.ps1") -OutputPath $evidenceRoot

$releaseEvidence = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-evidence.json") -Raw | ConvertFrom-Json
$releaseRecord = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-record.json") -Raw | ConvertFrom-Json
$releaseAttestation = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-attestation.json") -Raw | ConvertFrom-Json

$productVersion = [string]$releaseEvidence.productVersion
$safeVersion = $productVersion -replace '[^A-Za-z0-9._-]', '-'
$bundleName = "rtaime-$safeVersion-$([string]$policy.platform.rid)"

if (Test-Path -LiteralPath $outputRoot) {
	Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$bundleRoot = Join-Path $outputRoot $bundleName
New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null

Copy-DirectoryContent -Source (Join-Path $evidenceRoot "product") -Destination (Join-Path $bundleRoot "product")

$applicationEntryPoint = $policy.applicationEntryPoint
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$applicationEntryPoint.productDirectory)) "Offline bundle policy must define the application entry-point product directory."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$applicationEntryPoint.executable)) "Offline bundle policy must define the application entry-point executable."
$applicationHostSource = Join-Path (Join-Path $evidenceRoot "product") ([string]$applicationEntryPoint.productDirectory)
Assert-Condition (Test-Path -LiteralPath $applicationHostSource -PathType Container) "Application host product payload was not found at '$applicationHostSource'."
$applicationExecutable = Join-Path $applicationHostSource ([string]$applicationEntryPoint.executable)
Assert-Condition (Test-Path -LiteralPath $applicationExecutable -PathType Leaf) "Canonical application executable was not found at '$applicationExecutable'."
Copy-DirectoryContent -Source $applicationHostSource -Destination $bundleRoot

$releaseDirectory = Join-Path $bundleRoot "release"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
foreach ($releaseFile in @($policy.releaseEvidenceFiles)) {
	$source = Join-Path $evidenceRoot ([string]$releaseFile)
	if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
		throw "Required release evidence file '$releaseFile' is missing."
	}
	Copy-Item -LiteralPath $source -Destination (Join-Path $releaseDirectory ([string]$releaseFile)) -Force
}
if (@($releaseEvidence.PSObject.Properties.Name) -contains "securityAssessment") {
	$securityAssessmentRelativePath = [string]$releaseEvidence.securityAssessment.path
	if ($securityAssessmentRelativePath -ne "security-assessment.json") {
		throw "Unexpected product-security assessment release path '$securityAssessmentRelativePath'."
	}
	$securityAssessmentSource = Join-Path $evidenceRoot $securityAssessmentRelativePath
	if (-not (Test-Path -LiteralPath $securityAssessmentSource -PathType Leaf)) {
		throw "Bound product-security assessment '$securityAssessmentRelativePath' is missing."
	}
	Copy-Item -LiteralPath $securityAssessmentSource -Destination (Join-Path $releaseDirectory $securityAssessmentRelativePath) -Force
}
$qualificationSource = Join-Path $evidenceRoot "qualification"
if (Test-Path -LiteralPath $qualificationSource -PathType Container) {
	Copy-DirectoryContent -Source $qualificationSource -Destination (Join-Path $releaseDirectory "qualification")
}

$schemaSource = Resolve-RepositoryPath ([string]$policy.schemaRoot)
Copy-DirectoryContent -Source $schemaSource -Destination (Join-Path $bundleRoot "schemas")

$documentationSource = Resolve-RepositoryPath ([string]$policy.documentationRoot)
Copy-DirectoryContent -Source $documentationSource -Destination (Join-Path $bundleRoot "docs")
foreach ($rootDocumentation in @($policy.rootDocumentation)) {
	$source = Resolve-RepositoryPath ([string]$rootDocumentation)
	if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
		throw "Required root documentation '$rootDocumentation' is missing."
	}
	Copy-Item -LiteralPath $source -Destination (Join-Path (Join-Path $bundleRoot "docs") ([System.IO.Path]::GetFileName($source))) -Force
}

$toolsDirectory = Join-Path $bundleRoot "tools"
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$toolOutputNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($tool in @($policy.offlineTools)) {
	$toolPath = [string]$tool
	if ([string]::IsNullOrWhiteSpace($toolPath)) {
		throw "Offline tool source path must not be empty."
	}
	if ([System.IO.Path]::IsPathRooted($toolPath)) {
		throw "Offline tool source path '$toolPath' must be repository-relative."
	}
	$normalizedToolPath = $toolPath.Replace('\', '/')
	$segments = @($normalizedToolPath.Split('/'))
	if ($segments -contains '..' -or $segments -contains '.' -or $segments -contains '') {
		throw "Offline tool source path '$toolPath' contains invalid relative path segments."
	}
	if (-not $normalizedToolPath.StartsWith("build/", [StringComparison]::OrdinalIgnoreCase)) {
		throw "Offline tool source path '$toolPath' must come from the repository build/ tree."
	}

	$source = Resolve-RepositoryPath $toolPath
	if (-not $source.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
		throw "Offline tool source path '$toolPath' escapes the repository root."
	}
	if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
		throw "Required offline tool '$toolPath' is missing."
	}

	$outputName = [System.IO.Path]::GetFileName($source)
	if (-not $toolOutputNames.Add($outputName)) {
		throw "Offline tool source '$toolPath' collides with another bundle tool named '$outputName'."
	}

	Copy-Item -LiteralPath $source -Destination (Join-Path $toolsDirectory $outputName) -Force
}

$showcaseEntryPoint = Join-Path $toolsDirectory "Start-rtaime-Showcase.cmd"
if (Test-Path -LiteralPath $showcaseEntryPoint -PathType Leaf) {
	Copy-Item -LiteralPath $showcaseEntryPoint -Destination (Join-Path $bundleRoot "Start-rtaime-Showcase.cmd") -Force
}

$trustDirectory = Join-Path $bundleRoot "trust"
New-Item -ItemType Directory -Path $trustDirectory -Force | Out-Null
$trustedKeysSource = Resolve-RepositoryPath ([string]$policy.trustedReleaseKeys)
Copy-Item -LiteralPath $trustedKeysSource -Destination (Join-Path $trustDirectory "trusted-release-keys.json") -Force

$runtimeRequirements = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	productName = [string]$releaseEvidence.productName
	productVersion = $productVersion
	platform = [ordered]@{
		osFamily = [string]$policy.platform.osFamily
		architecture = [string]$policy.platform.architecture
		rid = [string]$policy.platform.rid
	}
	dotnetRuntimes = @($policy.runtimeRequirements)
	continuousInternetRequired = $false
}
$metadataDirectory = Join-Path $bundleRoot "metadata"
New-Item -ItemType Directory -Path $metadataDirectory -Force | Out-Null
Write-JsonFile -Value $runtimeRequirements -Path (Join-Path $metadataDirectory "runtime-requirements.json")

$offlineReadme = @"
<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# rtaime Offline Release Bundle

This is a SOFTWARE_RELEASE deployment bundle, not a Production Package.

Canonical product startup:

```powershell
./rtaime.exe
```

The AppHost starts or adopts ControlHost, waits for qualified engine readiness and opens Operator for the default Interactive profile. ControlHost, RuntimeHost, AIHost and Operator remain separate processes.

Offline verification:

```powershell
./tools/Test-OfflineReleaseBundle.ps1 -BundlePath .
```

Offline preflight:

```powershell
./tools/Invoke-OfflinePreflight.ps1 -BundlePath . -InstallPath C:\rtaime
```

Clean installation into a new or empty directory:

```powershell
./tools/Install-OfflineRelease.ps1 -BundlePath . -InstallPath C:\rtaime
```

Persistent Windows production engine:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 -Action Install -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime
./tools/Invoke-WindowsServiceLifecycle.ps1 -Action Start -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime
./tools/Invoke-WindowsServiceLifecycle.ps1 -Action Qualify -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime
```

Service-managed verified upgrade:

```powershell
./tools/Invoke-ServiceManagedUpdate.ps1 -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime -Channel PREVIEW -AcknowledgeExternalProcessesStopped
```

Service-managed rollback:

```powershell
./tools/Invoke-ServiceManagedRollback.ps1 -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime -AcknowledgeExternalProcessesStopped
```

The service-managed maintenance paths stop the persistent engine, delegate to the existing verified update or rollback controls, restart the engine only after successful maintenance, and require qualified readiness before reporting PASS.

Investor funding showcase:

Double-click `Start-rtaime-Showcase.cmd` in the installed bundle root. The launcher uses the managed host lifecycle, opens the Operator with the qualified endpoints, and stops only the lifecycle it started when the Operator closes.

Clean installation does not automatically register or start the Windows service. Service registration is an explicit administrative action. This bundle does not schedule background updates or perform Production Package activation.
"@
[System.IO.File]::WriteAllText((Join-Path $bundleRoot "OFFLINE-README.md"), $offlineReadme + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$payloadFiles = @(
	Get-ChildItem -LiteralPath $bundleRoot -File -Recurse |
		Sort-Object { Get-RelativeUnixPath -BasePath $bundleRoot -Path $_.FullName }
)
$payload = @(
	foreach ($file in $payloadFiles) {
		$relativePath = Get-RelativeUnixPath -BasePath $bundleRoot -Path $file.FullName
		[ordered]@{
			path = $relativePath
			role = Get-PayloadRole -RelativePath $relativePath
			size = $file.Length
			sha256 = Get-FileSha256Hex -Path $file.FullName
		}
	}
)

$bundleManifest = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	bundleType = [string]$policy.bundleType
	packageFormatVersion = [string]$policy.packageFormatVersion
	bundleName = $bundleName
	productName = [string]$releaseEvidence.productName
	productVersion = $productVersion
	releaseStage = [string]$releaseEvidence.releaseStage
	platform = [ordered]@{
		osFamily = [string]$policy.platform.osFamily
		architecture = [string]$policy.platform.architecture
		rid = [string]$policy.platform.rid
	}
	sourceCommit = [string]$releaseEvidence.sourceCommit
	buildCommit = [string]$releaseEvidence.buildCommit
	buildId = [string]$releaseEvidence.buildId
	releaseTrust = [ordered]@{
		releaseRecordId = [string]$releaseRecord.recordId
		releaseKeyFingerprint = [string]$releaseAttestation.key.fingerprint
		releaseSignerClass = [string]$releaseAttestation.key.signerClass
	}
	runtimeRequirementsPath = "metadata/runtime-requirements.json"
	payloadHashAlgorithm = "SHA256"
	createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	payload = $payload
}
$bundleManifestPath = Join-Path $bundleRoot "bundle-manifest.json"
Write-JsonFile -Value $bundleManifest -Path $bundleManifestPath

$manifestBytes = [System.IO.File]::ReadAllBytes($bundleManifestPath)
$manifestHashBytes = [System.Security.Cryptography.SHA256]::HashData($manifestBytes)
$manifestHash = [Convert]::ToHexString($manifestHashBytes).ToLowerInvariant()

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
	$privatePem = [System.IO.File]::ReadAllText($privateKeyFullPath)
	$ecdsa.ImportFromPem($privatePem)
	$parameters = $ecdsa.ExportParameters($false)
	if ($parameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7") {
		throw "Offline bundle signing key must use NIST P-256."
	}
	$publicKey = $ecdsa.ExportSubjectPublicKeyInfo()
	$keyFingerprint = Get-Sha256Hex -Bytes $publicKey
	if ($keyFingerprint -ne [string]$releaseAttestation.key.fingerprint) {
		throw "Offline bundle must be signed by the same key that signed the contained release evidence."
	}
	$signature = $ecdsa.SignHash(
		$manifestHashBytes,
		[System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
	if ($signature.Length -ne 64) {
		throw "Unexpected ECDSA P-256 bundle signature length '$($signature.Length)'."
	}

	$bundleAttestation = [ordered]@{
		copyright = $copyright
		schemaVersion = "1.0"
		attestationType = "rtaime.offline-software-bundle.v1"
		subject = [ordered]@{
			path = "bundle-manifest.json"
			sha256 = $manifestHash
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
	Write-JsonFile -Value $bundleAttestation -Path (Join-Path $bundleRoot "bundle-attestation.json")
} finally {
	$ecdsa.Dispose()
}

& (Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1") -BundlePath $bundleRoot

$archivePath = Join-Path $outputRoot "$bundleName.zip"
if (Test-Path -LiteralPath $archivePath) {
	Remove-Item -LiteralPath $archivePath -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
	$bundleRoot,
	$archivePath,
	[System.IO.Compression.CompressionLevel]::Optimal,
	$false)
$archiveHash = Get-FileSha256Hex -Path $archivePath
[System.IO.File]::WriteAllText(
	"$archivePath.sha256",
	"$archiveHash  $([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)",
	[System.Text.UTF8Encoding]::new($false))

Write-Host "Offline release bundle generated."
Write-Host "Bundle directory: $bundleRoot"
Write-Host "Archive: $archivePath"
Write-Host "Archive SHA-256: $archiveHash"
Write-Host "Bundle signer class: $SignerClass"
Write-Host "Production package activation: NOT_APPLICABLE"
