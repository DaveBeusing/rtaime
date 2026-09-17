# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[ValidateSet("AUTO", "QUALIFICATION", "PREVIEW", "STABLE")]
	[string]$Channel = "QUALIFICATION",
	[string]$Tag = "",
	[string]$SourceCommit = $env:RTAIME_SOURCE_COMMIT,
	[string]$BuildCommit = $env:RTAIME_BUILD_COMMIT,
	[string]$BuildId = $env:RTAIME_BUILD_ID,
	[ValidateSet("TEST_EPHEMERAL", "EXTERNAL_CONTROLLED")]
	[string]$SignerClass = "TEST_EPHEMERAL",
	[string]$PrivateKeyPath = "",
	[string]$SignerId = "",
	[string]$SecurityAssessmentPath = "",
	[string]$OutputRoot = "artifacts/release-pipeline"
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

function Get-FileSha256Hex {
	param([Parameter(Mandatory)][string]$Path)
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Hex {
	param([Parameter(Mandatory)][byte[]]$Bytes)
	return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
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

$outputFullRoot = Resolve-RepositoryPath $OutputRoot
if (Test-Path -LiteralPath $outputFullRoot) {
	Remove-Item -LiteralPath $outputFullRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputFullRoot -Force | Out-Null

$identityPath = Join-Path $outputFullRoot "release-identity.json"
$identity = & (Join-Path $PSScriptRoot "Resolve-ReleaseIdentity.ps1") `
	-Channel $Channel `
	-Tag $Tag `
	-SourceCommit $SourceCommit `
	-BuildCommit $BuildCommit `
	-BuildId $BuildId `
	-OutputPath $identityPath

$allowedSignerClasses = @($identity.signingPolicy.allowedSignerClasses)
if ($SignerClass -notin $allowedSignerClasses) {
	throw "Signer class '$SignerClass' is not allowed for release channel '$($identity.channel)'. Allowed: $($allowedSignerClasses -join ', ')."
}
if ($SignerClass -eq "EXTERNAL_CONTROLLED" -and [string]::IsNullOrWhiteSpace($SignerId)) {
	throw "EXTERNAL_CONTROLLED release signing requires SignerId."
}

Push-Location $repositoryRoot
$generatedTestKey = $false
$effectiveKeyPath = $PrivateKeyPath
$installPath = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-release-install-{0}" -f [Guid]::NewGuid().ToString("N"))
try {
	Write-Host "Release pipeline: restore"
	& dotnet restore rtaime.slnx
	if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

	Write-Host "Release pipeline: build"
	& dotnet build rtaime.slnx --configuration Release --no-restore
	if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

	Write-Host "Release pipeline: test exact build"
	& dotnet test rtaime.slnx --configuration Release --no-build -m:1
	if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }

	$evidenceRoot = Join-Path $repositoryRoot "artifacts/release-evidence"
	& (Join-Path $PSScriptRoot "New-ReleaseEvidence.ps1") `
		-OutputPath $evidenceRoot `
		-SourceCommit ([string]$identity.sourceCommit) `
		-BuildCommit ([string]$identity.buildCommit) `
		-BuildId ([string]$identity.buildId) `
		-ManagedValidationStatus PASS

	if (-not [string]::IsNullOrWhiteSpace($SecurityAssessmentPath)) {
		& (Join-Path $repositoryRoot "build/security/Bind-ProductSecurityAssessment.ps1") `
			-AssessmentPath $SecurityAssessmentPath `
			-OutputPath $evidenceRoot
	}
	& (Join-Path $PSScriptRoot "Test-ReleaseEvidence.ps1") -OutputPath $evidenceRoot

	if ([string]::IsNullOrWhiteSpace($effectiveKeyPath)) {
		if ($SignerClass -ne "TEST_EPHEMERAL") {
			throw "Release channel '$($identity.channel)' requires an externally supplied signing key for signer class '$SignerClass'."
		}
		$effectiveKeyPath = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-release-test-{0}.pem" -f [Guid]::NewGuid().ToString("N"))
		& (Join-Path $PSScriptRoot "New-TestReleaseSigningKey.ps1") -PrivateKeyPath $effectiveKeyPath
		$generatedTestKey = $true
	}

	& (Join-Path $PSScriptRoot "New-ReleaseAttestation.ps1") `
		-OutputPath $evidenceRoot `
		-PrivateKeyPath $effectiveKeyPath `
		-SignerClass $SignerClass `
		-SignerId $SignerId

	if ([bool]$identity.signingPolicy.requireTrustedProductionKey) {
		& (Join-Path $PSScriptRoot "Test-ReleaseAttestation.ps1") -OutputPath $evidenceRoot -RequireTrustedProductionKey
	} else {
		& (Join-Path $PSScriptRoot "Test-ReleaseAttestation.ps1") -OutputPath $evidenceRoot
	}
	& (Join-Path $PSScriptRoot "Test-ReleaseSigningFailureCases.ps1") -OutputPath $evidenceRoot

	$offlineRoot = Join-Path $repositoryRoot "artifacts/offline-release"
	& (Join-Path $PSScriptRoot "New-OfflineReleaseBundle.ps1") `
		-EvidencePath $evidenceRoot `
		-OutputPath $offlineRoot `
		-PrivateKeyPath $effectiveKeyPath `
		-SignerClass $SignerClass `
		-SignerId $SignerId

	$bundleDirectories = @(Get-ChildItem -LiteralPath $offlineRoot -Directory)
	$bundleArchives = @(Get-ChildItem -LiteralPath $offlineRoot -Filter *.zip -File)
	if ($bundleDirectories.Count -ne 1) {
		throw "Expected exactly one generated offline bundle directory, found $($bundleDirectories.Count)."
	}
	if ($bundleArchives.Count -ne 1) {
		throw "Expected exactly one generated offline bundle ZIP, found $($bundleArchives.Count)."
	}
	$bundleArchive = $bundleArchives[0]
	$bundleSidecar = Get-Item -LiteralPath "$($bundleArchive.FullName).sha256"

	if ([bool]$identity.signingPolicy.requireTrustedProductionKey) {
		& (Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1") -BundlePath $bundleArchive.FullName -RequireTrustedProductionKey
	} else {
		& (Join-Path $PSScriptRoot "Test-OfflineReleaseBundle.ps1") -BundlePath $bundleArchive.FullName
	}

	& (Join-Path $PSScriptRoot "Invoke-OfflinePreflight.ps1") `
		-BundlePath $bundleArchive.FullName `
		-InstallPath $installPath
	& (Join-Path $PSScriptRoot "Test-OfflineReleaseFailureCases.ps1") `
		-BundleDirectory $bundleDirectories[0].FullName `
		-ArchivePath $bundleArchive.FullName
	& (Join-Path $PSScriptRoot "Install-OfflineRelease.ps1") `
		-BundlePath $bundleArchive.FullName `
		-InstallPath $installPath
	& (Join-Path $installPath "tools/Test-OfflineReleaseBundle.ps1") -BundlePath $installPath

	$releaseEvidence = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-evidence.json") -Raw | ConvertFrom-Json
	$releaseRecord = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-record.json") -Raw | ConvertFrom-Json
	$releaseAttestation = Get-Content -LiteralPath (Join-Path $evidenceRoot "release-attestation.json") -Raw | ConvertFrom-Json
	$keyFingerprint = [string]$releaseAttestation.key.fingerprint

	$trustStore = Get-Content -LiteralPath (Join-Path $PSScriptRoot "trusted-release-keys.json") -Raw | ConvertFrom-Json
	$activeTrustedKey = @($trustStore.keys | Where-Object {
		[string]$_.fingerprint -eq $keyFingerprint -and
		[string]$_.status -eq "ACTIVE" -and
		[string]$_.purpose -eq "SOFTWARE_RELEASE"
	})
	$productionTrust = if ($SignerClass -eq "EXTERNAL_CONTROLLED" -and $activeTrustedKey.Count -eq 1) { "PASS" } else { "UNVERIFIED" }
	if ([bool]$identity.signingPolicy.requireTrustedProductionKey -and $productionTrust -ne "PASS") {
		throw "Release channel '$($identity.channel)' requires active trusted production signing-key evidence."
	}

	$candidateName = "rtaime-$($identity.productVersion)-$(([string]$identity.channel).ToLowerInvariant())"
	$candidateRoot = Join-Path $outputFullRoot $candidateName
	New-Item -ItemType Directory -Path $candidateRoot -Force | Out-Null
	$candidateBundlePath = Join-Path $candidateRoot $bundleArchive.Name
	$candidateSidecarPath = Join-Path $candidateRoot $bundleSidecar.Name
	Copy-Item -LiteralPath $bundleArchive.FullName -Destination $candidateBundlePath -Force
	Copy-Item -LiteralPath $bundleSidecar.FullName -Destination $candidateSidecarPath -Force

	$bundleHash = Get-FileSha256Hex -Path $candidateBundlePath
	$sidecarHash = Get-FileSha256Hex -Path $candidateSidecarPath
	$publicationStatus = if ([bool]$identity.signingPolicy.publicationEligible -and $productionTrust -eq "PASS") {
		"PASS"
	} elseif ([string]$identity.channel -eq "QUALIFICATION") {
		"NOT_APPLICABLE"
	} else {
		"UNVERIFIED"
	}

	$candidateCanonical = @(
		"rtaime.release-candidate.v1",
		[string]$identity.channel,
		[string]$identity.productVersion,
		$(if ($null -eq $identity.tag) { "" } else { [string]$identity.tag }),
		[string]$identity.sourceCommit,
		[string]$releaseRecord.recordId,
		$keyFingerprint,
		$bundleHash
	) -join "`n"
	$candidateId = Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($candidateCanonical))

	$candidateManifest = [ordered]@{
		copyright = $copyright
		schemaVersion = "1.0"
		candidateId = $candidateId
		channel = [string]$identity.channel
		product = [ordered]@{
			name = "rtaime"
			version = [string]$identity.productVersion
			releaseStage = [string]$identity.releaseStage
		}
		source = [ordered]@{
			tag = $identity.tag
			sourceCommit = [string]$identity.sourceCommit
			buildCommit = [string]$identity.buildCommit
			buildId = [string]$identity.buildId
		}
		trust = [ordered]@{
			signerClass = $SignerClass
			keyFingerprint = $keyFingerprint
			productionTrust = $productionTrust
		}
		releaseRecord = [ordered]@{
			recordId = [string]$releaseRecord.recordId
			releaseEvidenceSha256 = Get-FileSha256Hex -Path (Join-Path $evidenceRoot "release-evidence.json")
		}
		bundle = [ordered]@{
			fileName = $bundleArchive.Name
			size = (Get-Item -LiteralPath $candidateBundlePath).Length
			sha256 = $bundleHash
			sidecarFileName = $bundleSidecar.Name
			sidecarSha256 = $sidecarHash
		}
		publicationReadiness = [ordered]@{
			status = $publicationStatus
			details = if ($publicationStatus -eq "PASS") {
				"Channel policy, source/tag identity, package integrity and active production signing-key trust are satisfied."
			} elseif ([string]$identity.channel -eq "QUALIFICATION") {
				"Qualification candidates are CI evidence and are not publication artifacts."
			} else {
				"Preview candidate mechanism is qualified, but official publication trust is not established by this candidate."
			}
		}
		createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	}
	$candidateManifestPath = Join-Path $candidateRoot "release-candidate.json"
	Write-JsonFile -Value $candidateManifest -Path $candidateManifestPath
	$candidateManifestHash = Get-FileSha256Hex -Path $candidateManifestPath
	[System.IO.File]::WriteAllText(
		(Join-Path $candidateRoot "release-candidate.sha256"),
		"$candidateManifestHash  release-candidate.json$([Environment]::NewLine)",
		[System.Text.UTF8Encoding]::new($false))

	& (Join-Path $PSScriptRoot "Test-ReleaseCandidate.ps1") -CandidatePath $candidateRoot
	& (Join-Path $PSScriptRoot "Test-ReleasePipelineFailureCases.ps1") -CandidatePath $candidateRoot

	Write-Host "Single release pipeline PASS"
	Write-Host "Channel: $($identity.channel)"
	Write-Host "Candidate: $candidateRoot"
	Write-Host "Candidate id: $candidateId"
	Write-Host "Product security assessment: $(if ([string]::IsNullOrWhiteSpace($SecurityAssessmentPath)) { 'UNVERIFIED' } else { 'BOUND' })"
	Write-Host "Production signing trust: $productionTrust"
	Write-Host "Publication readiness: $publicationStatus"
} finally {
	Pop-Location
	if (Test-Path -LiteralPath $installPath) {
		Remove-Item -LiteralPath $installPath -Recurse -Force
	}
	if ($generatedTestKey -and -not [string]::IsNullOrWhiteSpace($effectiveKeyPath) -and (Test-Path -LiteralPath $effectiveKeyPath)) {
		Remove-Item -LiteralPath $effectiveKeyPath -Force
	}
}
