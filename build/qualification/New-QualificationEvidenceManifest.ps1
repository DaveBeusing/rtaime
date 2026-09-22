# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$SourceCommit,
	[string]$BindingRoot = "artifacts/qualification/bindings",
	[string]$OutputPath = "artifacts/qualification/qualification-evidence-manifest.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$qualificationPolicyPath = Join-Path $PSScriptRoot "qualification-evidence-policy.json"
$releasePolicyPath = Join-Path $repositoryRoot "build/release/release-policy.json"
$bindingVerifier = Join-Path $PSScriptRoot "Test-QualificationEvidenceBinding.ps1"
$performanceGenerator = Join-Path $PSScriptRoot "New-SupportedPerformanceEvidence.ps1"

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Get-RepositoryRelativePath {
	param([Parameter(Mandatory)][string]$Path)
	$relative = [System.IO.Path]::GetRelativePath($repositoryRoot, $Path).Replace('\', '/')
	if ($relative -eq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
		throw "Qualification evidence manifest inputs must remain inside the repository workspace."
	}
	return $relative
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
	$json = $Value | ConvertTo-Json -Depth 32
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw "SourceCommit must be an exact 40-character Git SHA." }
$SourceCommit = $SourceCommit.ToLowerInvariant()
foreach ($required in @($qualificationPolicyPath, $releasePolicyPath, $bindingVerifier, $performanceGenerator)) {
	if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required qualification evidence artifact is missing: '$required'." }
}

$qualificationPolicy = Get-Content -LiteralPath $qualificationPolicyPath -Raw | ConvertFrom-Json
$releasePolicy = Get-Content -LiteralPath $releasePolicyPath -Raw | ConvertFrom-Json
$requirementOrder = @($releasePolicy.hardwareQualification | ForEach-Object { [string]$_.requirement })
if ($requirementOrder.Count -eq 0) { throw "Release policy declares no hardware qualification requirements." }
if (@($requirementOrder | Sort-Object -Unique).Count -ne $requirementOrder.Count) { throw "Release policy contains duplicate hardware qualification requirements." }

$entries = [ordered]@{}
foreach ($item in $releasePolicy.hardwareQualification) {
	if ([string]$item.status -ne "UNVERIFIED") {
		throw "Static release-policy hardware status for '$($item.requirement)' must remain UNVERIFIED; only source-bound qualification evidence may upgrade it."
	}
	$entries[[string]$item.requirement] = [ordered]@{
		requirement = [string]$item.requirement
		status = "UNVERIFIED"
	}
}

$bindingRootFull = Resolve-RepositoryPath -Path $BindingRoot
[void](Get-RepositoryRelativePath -Path $bindingRootFull)
$seenTypes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if (Test-Path -LiteralPath $bindingRootFull -PathType Container) {
	foreach ($bindingFile in @(Get-ChildItem -LiteralPath $bindingRootFull -Filter "*.binding.json" -File | Sort-Object Name)) {
		& $bindingVerifier -BindingPath $bindingFile.FullName -ExpectedSourceCommit $SourceCommit
		$binding = Get-Content -LiteralPath $bindingFile.FullName -Raw | ConvertFrom-Json
		$type = [string]$binding.qualificationType
		if (-not $seenTypes.Add($type)) { throw "Duplicate qualification binding type '$type'." }

		$bindingRelative = Get-RepositoryRelativePath -Path $bindingFile.FullName
		$bindingHash = (Get-FileHash -LiteralPath $bindingFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
		foreach ($requirement in @($binding.releaseRequirements)) {
			$name = [string]$requirement
			if (-not $entries.Contains($name)) { throw "Qualification binding '$type' targets undeclared release requirement '$name'." }
			if ([string]$entries[$name].status -eq "PASSED") { throw "Release requirement '$name' is satisfied by more than one qualification binding." }
			$entries[$name] = [ordered]@{
				requirement = $name
				status = "PASSED"
				qualificationType = $type
				binding = [ordered]@{
					path = $bindingRelative
					sha256 = $bindingHash
				}
				payload = [ordered]@{
					path = [string]$binding.payload.path
					sha256 = [string]$binding.payload.sha256
				}
				workflow = [ordered]@{
					runId = [string]$binding.workflow.runId
					runAttempt = [int]$binding.workflow.runAttempt
				}
			}
		}
	}
}

$orderedRequirements = @(
	foreach ($requirement in $requirementOrder) { $entries[$requirement] }
)

$supportedPerformancePath = Join-Path $repositoryRoot "artifacts/qualification/supported-performance.json"
$supportedPerformanceMarkdownPath = Join-Path $repositoryRoot "artifacts/qualification/supported-performance.md"
& $performanceGenerator `
	-SourceCommit $SourceCommit `
	-BindingRoot $BindingRoot `
	-OutputPath (Get-RepositoryRelativePath -Path $supportedPerformancePath) `
	-MarkdownPath (Get-RepositoryRelativePath -Path $supportedPerformanceMarkdownPath)
$supportedPerformance = Get-Content -LiteralPath $supportedPerformancePath -Raw | ConvertFrom-Json
$supportedPerformanceHash = (Get-FileHash -LiteralPath $supportedPerformancePath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	repository = [string]$qualificationPolicy.repository
	sourceCommit = $SourceCommit
	generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	supportedPerformance = [ordered]@{
		status = [string]$supportedPerformance.status
		path = Get-RepositoryRelativePath -Path $supportedPerformancePath
		sha256 = $supportedPerformanceHash
	}
	requirements = $orderedRequirements
}

$outputFull = Resolve-RepositoryPath -Path $OutputPath
[void](Get-RepositoryRelativePath -Path $outputFull)
Write-JsonFile -Value $manifest -Path $outputFull

$passedCount = @($orderedRequirements | Where-Object { [string]$_.status -eq "PASSED" }).Count
Write-Host "Qualification evidence manifest PASS"
Write-Host "Source commit: $SourceCommit"
Write-Host "Bound requirements: $passedCount / $($orderedRequirements.Count)"
Write-Host "Manifest: $outputFull"
