# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$Configuration = "Release",
	[string]$OutputPath = "artifacts/release-evidence",
	[string]$SourceCommit = $env:RTAIME_SOURCE_COMMIT,
	[string]$BuildCommit = $env:RTAIME_BUILD_COMMIT,
	[string]$BuildId = $env:RTAIME_BUILD_ID,
	[string]$QualificationBindingRoot = "artifacts/qualification/bindings",
	[ValidateSet("PASS", "UNVERIFIED")]
	[string]$ManagedValidationStatus = "UNVERIFIED"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."

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
	[System.IO.File]::WriteAllText(
		$Path,
		$json + [Environment]::NewLine,
		[System.Text.UTF8Encoding]::new($false))
}

function Get-Sha256 {
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

function Resolve-GitCommit {
	$commit = (& git -C $script:RepositoryRoot rev-parse HEAD).Trim()
	if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
		throw "Unable to resolve the current Git commit."
	}
	return $commit
}

$policyPath = Join-Path $PSScriptRoot "release-policy.json"
if (-not (Test-Path -LiteralPath $policyPath)) {
	throw "Release policy was not found at '$policyPath'."
}
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

$buildPropsPath = Join-Path $script:RepositoryRoot "Directory.Build.props"
[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersionNode = $buildProps.SelectSingleNode("//RtaimeProductVersion")
$releaseStageNode = $buildProps.SelectSingleNode("//RtaimeReleaseStage")
if ($null -eq $productVersionNode -or [string]::IsNullOrWhiteSpace($productVersionNode.InnerText)) {
	throw "Directory.Build.props must declare RtaimeProductVersion."
}
if ($null -eq $releaseStageNode -or [string]::IsNullOrWhiteSpace($releaseStageNode.InnerText)) {
	throw "Directory.Build.props must declare RtaimeReleaseStage."
}

$productVersion = $productVersionNode.InnerText.Trim()
$releaseStage = $releaseStageNode.InnerText.Trim().ToUpperInvariant()
$allowedReleaseStages = @("DEV", "PREVIEW", "RELEASE_CANDIDATE", "STABLE", "VALIDATED", "CERTIFIED")
if ($releaseStage -notin $allowedReleaseStages) {
	throw "Unsupported release stage '$releaseStage'."
}

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
	$SourceCommit = Resolve-GitCommit
}
if ([string]::IsNullOrWhiteSpace($BuildCommit)) {
	$BuildCommit = Resolve-GitCommit
}
if ($SourceCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
	throw "Source commit '$SourceCommit' is not a supported Git object identity."
}
if ($BuildCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
	throw "Build commit '$BuildCommit' is not a supported Git object identity."
}
if ([string]::IsNullOrWhiteSpace($BuildId)) {
	$BuildId = "local-$($BuildCommit.Substring(0, [Math]::Min(12, $BuildCommit.Length)))"
}

$outputRoot = Resolve-RepositoryPath $OutputPath
if (Test-Path -LiteralPath $outputRoot) {
	Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$productRoot = Join-Path $outputRoot "product"
New-Item -ItemType Directory -Path $productRoot -Force | Out-Null

foreach ($hostPolicy in $policy.hosts) {
	$relativeOutput = [string]$hostPolicy.outputPath
	$relativeOutput = $relativeOutput.Replace("{configuration}", $Configuration)
	$sourceDirectory = Resolve-RepositoryPath $relativeOutput
	if (-not (Test-Path -LiteralPath $sourceDirectory)) {
		throw "Built host output for '$($hostPolicy.name)' was not found at '$sourceDirectory'."
	}

	$destinationDirectory = Join-Path $productRoot ([string]$hostPolicy.name)
	New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

	$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse | Where-Object Extension -ne ".pdb")
	if ($sourceFiles.Count -eq 0) {
		throw "Built host output for '$($hostPolicy.name)' does not contain release artifacts."
	}

	foreach ($sourceFile in $sourceFiles) {
		$relativePath = [System.IO.Path]::GetRelativePath($sourceDirectory, $sourceFile.FullName)
		$targetPath = Join-Path $destinationDirectory $relativePath
		$targetDirectory = Split-Path -Parent $targetPath
		if (-not (Test-Path -LiteralPath $targetDirectory)) {
			New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
		}
		Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetPath -Force
	}
}

$productFiles = @(
	Get-ChildItem -LiteralPath $productRoot -File -Recurse |
		Sort-Object { Get-RelativeUnixPath -BasePath $outputRoot -Path $_.FullName }
)
$artifactRecords = @(
	foreach ($file in $productFiles) {
		[ordered]@{
			path = Get-RelativeUnixPath -BasePath $outputRoot -Path $file.FullName
			size = $file.Length
			sha256 = Get-Sha256 -Path $file.FullName
		}
	}
)

$generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
$artifactManifest = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	productName = [string]$policy.productName
	productVersion = $productVersion
	releaseStage = $releaseStage
	sourceCommit = $SourceCommit.ToLowerInvariant()
	buildCommit = $BuildCommit.ToLowerInvariant()
	buildId = $BuildId
	hashAlgorithm = "SHA256"
	artifacts = $artifactRecords
}
$artifactManifestPath = Join-Path $outputRoot "artifact-manifest.json"
Write-JsonFile -Value $artifactManifest -Path $artifactManifestPath

$packageIndex = @{}
$assetFiles = @(Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot "src") -Filter "project.assets.json" -File -Recurse)
foreach ($assetFile in $assetFiles) {
	$assets = Get-Content -LiteralPath $assetFile.FullName -Raw | ConvertFrom-Json
	foreach ($library in $assets.libraries.PSObject.Properties) {
		if ([string]$library.Value.type -ne "package") {
			continue
		}

		$separator = $library.Name.LastIndexOf('/')
		if ($separator -le 0 -or $separator -ge ($library.Name.Length - 1)) {
			continue
		}

		$name = $library.Name.Substring(0, $separator)
		$version = $library.Name.Substring($separator + 1)
		$key = "$name@$version"
		if (-not $packageIndex.ContainsKey($key)) {
			$packageIndex[$key] = [ordered]@{
				type = "library"
				name = $name
				version = $version
				purl = "pkg:nuget/$([Uri]::EscapeDataString($name))@$([Uri]::EscapeDataString($version))"
			}
		}
	}
}

$components = @(
	$packageIndex.Values | Sort-Object { $_.name }, { $_.version }
)
$sbom = [ordered]@{
	bomFormat = "CycloneDX"
	specVersion = "1.6"
	version = 1
	metadata = [ordered]@{
		timestamp = $generatedAtUtc
		component = [ordered]@{
			type = "application"
			'bom-ref' = "pkg:generic/rtaime@$productVersion"
			name = [string]$policy.productName
			version = $productVersion
		}
		properties = @(
			[ordered]@{ name = "rtaime:sourceCommit"; value = $SourceCommit.ToLowerInvariant() },
			[ordered]@{ name = "rtaime:buildCommit"; value = $BuildCommit.ToLowerInvariant() },
			[ordered]@{ name = "rtaime:buildId"; value = $BuildId }
		)
	}
	components = $components
}
$sbomPath = Join-Path $outputRoot "sbom.cdx.json"
Write-JsonFile -Value $sbom -Path $sbomPath

$contractEntries = @(
	foreach ($contract in ($policy.contracts.PSObject.Properties | Sort-Object Name)) {
		[ordered]@{
			name = $contract.Name
			compatibilityPolicy = "EXACT_DECLARED"
			supportedVersions = @($contract.Value)
		}
	}
)
$hardwareQualification = @(
	foreach ($requirement in $policy.hardwareQualification) {
		[ordered]@{
			requirement = [string]$requirement.requirement
			status = [string]$requirement.status
		}
	}
)
$compatibilityManifest = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	product = [ordered]@{
		name = [string]$policy.productName
		version = $productVersion
		releaseStage = $releaseStage
	}
	source = [ordered]@{
		sourceCommit = $SourceCommit.ToLowerInvariant()
		buildCommit = $BuildCommit.ToLowerInvariant()
		buildId = $BuildId
	}
	managedPlatform = [ordered]@{
		osFamily = "Windows"
		sdkVersion = [string]$policy.sdkVersion
		targetFrameworks = @($policy.targetFrameworks)
	}
	contracts = $contractEntries
	ipc = [ordered]@{
		compatibilityPolicy = "EXACT_DECLARED"
		protocolVersions = @($policy.ipc.protocolVersions)
		schemaSet = [string]$policy.ipc.schemaSet
	}
	hardwareQualification = $hardwareQualification
}
$compatibilityManifestPath = Join-Path $outputRoot "compatibility-manifest.json"
Write-JsonFile -Value $compatibilityManifest -Path $compatibilityManifestPath

$documentationPath = Join-Path $script:RepositoryRoot "docs/ReleaseEvidence.md"
if (-not (Test-Path -LiteralPath $documentationPath)) {
	throw "Release evidence documentation is required at '$documentationPath'."
}

$testSource = "dotnet test rtaime.slnx --configuration Release --no-build"
$evidenceDomains = @(
	[ordered]@{ domain = "ARCHITECTURE"; status = $ManagedValidationStatus; severity = "CRITICAL"; source = $testSource; details = "Architecture test project completed within the managed validation run." },
	[ordered]@{ domain = "CONTRACTS"; status = $ManagedValidationStatus; severity = "CRITICAL"; source = $testSource; details = "Contract test project completed within the managed validation run." },
	[ordered]@{ domain = "BEHAVIOR"; status = $ManagedValidationStatus; severity = "CRITICAL"; source = $testSource; details = "Behavioral test project completed within the managed validation run." },
	[ordered]@{ domain = "FAILURE"; status = $ManagedValidationStatus; severity = "CRITICAL"; source = $testSource; details = "Failure and process-recovery evidence completed within the managed validation run." },
	[ordered]@{ domain = "PERFORMANCE"; status = $ManagedValidationStatus; severity = "MAJOR"; source = $testSource; details = "The managed performance test project completed; this does not qualify unavailable reference hardware." },
	[ordered]@{ domain = "COMPATIBILITY"; status = "PASS"; severity = "CRITICAL"; source = "compatibility-manifest.json"; details = "Current supported contract and IPC versions are explicitly declared." },
	[ordered]@{ domain = "SECURITY"; status = "UNVERIFIED"; severity = "CRITICAL"; source = "release-policy"; details = "A complete product security release gate is outside this release-trust package." },
	[ordered]@{ domain = "SUPPLY_CHAIN"; status = "PASS"; severity = "CRITICAL"; source = "artifact-manifest.json + sbom.cdx.json"; details = "SHA-256 artifact inventory and resolved production NuGet component inventory were generated." },
	[ordered]@{ domain = "DOCUMENTATION"; status = "PASS"; severity = "MAJOR"; source = "docs/ReleaseEvidence.md"; details = "Offline release-evidence generation and verification boundaries are documented." },
	[ordered]@{ domain = "COMPLIANCE"; status = "UNVERIFIED"; severity = "MAJOR"; source = "release-policy"; details = "This evidence bundle does not claim formal CRA conformity or another legal compliance determination." },
	[ordered]@{ domain = "KNOWN_ISSUES"; status = "UNVERIFIED"; severity = "MAJOR"; source = "release-policy"; details = "A formal release-candidate known-issues assessment has not been performed by this release-trust package." }
)

$releaseEvidence = [ordered]@{
	copyright = $copyright
	schemaVersion = "1.0"
	productName = [string]$policy.productName
	productVersion = $productVersion
	releaseStage = $releaseStage
	sourceCommit = $SourceCommit.ToLowerInvariant()
	buildCommit = $BuildCommit.ToLowerInvariant()
	buildId = $BuildId
	generatedAtUtc = $generatedAtUtc
	artifactManifest = [ordered]@{
		path = "artifact-manifest.json"
		sha256 = Get-Sha256 -Path $artifactManifestPath
	}
	sbom = [ordered]@{
		path = "sbom.cdx.json"
		sha256 = Get-Sha256 -Path $sbomPath
		format = "CycloneDX"
		specVersion = "1.6"
	}
	compatibilityManifest = [ordered]@{
		path = "compatibility-manifest.json"
		sha256 = Get-Sha256 -Path $compatibilityManifestPath
	}
	signingAttestation = [ordered]@{
		status = "UNVERIFIED"
		details = "Production signing trust is not established by the unsigned release-evidence subject itself. A downstream attestation may cryptographically bind these exact bytes without rewriting this subject; production trust still requires an externally controlled enrolled release key."
	}
	releaseReadiness = [ordered]@{
		status = "UNVERIFIED"
		details = "The release evidence bundle is integrity-verifiable for stage '$releaseStage', but publication or higher qualification gates remain unsatisfied until their required evidence exists."
	}
	evidenceDomains = $evidenceDomains
	knownIssues = @()
}
$releaseEvidencePath = Join-Path $outputRoot "release-evidence.json"
Write-JsonFile -Value $releaseEvidence -Path $releaseEvidencePath

& (Join-Path $PSScriptRoot "Apply-QualificationEvidence.ps1") `
	-OutputPath $outputRoot `
	-SourceCommit $SourceCommit `
	-BindingRoot $QualificationBindingRoot

Write-Host "Release evidence generated: $outputRoot"
Write-Host "Product: $($policy.productName) $productVersion ($releaseStage)"
Write-Host "Source commit: $SourceCommit"
Write-Host "Build commit:  $BuildCommit"
Write-Host "Artifacts: $($artifactRecords.Count)"
Write-Host "CycloneDX components: $($components.Count)"
Write-Host "Release readiness: UNVERIFIED (release stage: $releaseStage)"
