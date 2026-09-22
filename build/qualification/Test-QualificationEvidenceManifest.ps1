# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ManifestPath,
	[Parameter(Mandatory)][string]$ExpectedSourceCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$qualificationPolicyPath = Join-Path $PSScriptRoot "qualification-evidence-policy.json"
$releasePolicyPath = Join-Path $repositoryRoot "build/release/release-policy.json"
$bindingVerifier = Join-Path $PSScriptRoot "Test-QualificationEvidenceBinding.ps1"

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

Assert-Condition ($ExpectedSourceCommit -match '^[0-9a-fA-F]{40}$') "ExpectedSourceCommit must be an exact Git SHA."
$ExpectedSourceCommit = $ExpectedSourceCommit.ToLowerInvariant()
$resolvedManifest = Resolve-RepositoryPath -Path $ManifestPath
Assert-Condition (Test-Path -LiteralPath $resolvedManifest -PathType Leaf) "Qualification evidence manifest is missing."
Assert-Condition (Test-Path -LiteralPath $qualificationPolicyPath -PathType Leaf) "Qualification evidence policy is missing."
Assert-Condition (Test-Path -LiteralPath $releasePolicyPath -PathType Leaf) "Release policy is missing."

$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
$qualificationPolicy = Get-Content -LiteralPath $qualificationPolicyPath -Raw | ConvertFrom-Json
$releasePolicy = Get-Content -LiteralPath $releasePolicyPath -Raw | ConvertFrom-Json

Assert-Condition ([string]$manifest.schemaVersion -eq "1.0") "Qualification evidence manifest schemaVersion must be 1.0."
Assert-Condition ([string]$manifest.repository -eq [string]$qualificationPolicy.repository) "Qualification evidence manifest repository identity differs from policy."
Assert-Condition ([string]$manifest.sourceCommit -eq $ExpectedSourceCommit) "Qualification evidence manifest source commit differs from the release source commit."

Assert-Condition ([string]$manifest.supportedPerformance.status -in @("PASS", "UNVERIFIED")) "Qualification manifest supported-performance status is invalid."
$supportedPerformancePath = Resolve-RepositoryPath -Path ([string]$manifest.supportedPerformance.path)
Assert-Condition (Test-Path -LiteralPath $supportedPerformancePath -PathType Leaf) "Qualification manifest supported-performance evidence is missing."
$supportedPerformanceHash = (Get-FileHash -LiteralPath $supportedPerformancePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Condition ($supportedPerformanceHash -eq [string]$manifest.supportedPerformance.sha256) "Qualification manifest supported-performance evidence hash mismatch."
$supportedPerformance = Get-Content -LiteralPath $supportedPerformancePath -Raw | ConvertFrom-Json
Assert-Condition ([string]$supportedPerformance.sourceCommit -eq $ExpectedSourceCommit) "Supported-performance evidence source commit differs from the release source commit."
Assert-Condition ([string]$supportedPerformance.status -eq [string]$manifest.supportedPerformance.status) "Supported-performance status differs from qualification manifest."
if ([string]$supportedPerformance.status -eq "PASS") {
	Assert-Condition (@($supportedPerformance.measurements).Count -gt 0) "Supported-performance PASS requires measured values."
}

$expectedRequirements = @($releasePolicy.hardwareQualification | ForEach-Object { [string]$_.requirement })
$entries = @($manifest.requirements)
Assert-Condition ($entries.Count -eq $expectedRequirements.Count) "Qualification evidence manifest requirement count differs from release policy."
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $entries) {
	$name = [string]$entry.requirement
	Assert-Condition ($expectedRequirements -contains $name) "Qualification evidence manifest contains undeclared requirement '$name'."
	Assert-Condition ($seen.Add($name)) "Qualification evidence manifest contains duplicate requirement '$name'."
	$status = [string]$entry.status
	Assert-Condition ($status -in @("PASSED", "UNVERIFIED")) "Qualification evidence requirement '$name' has invalid status '$status'."
	$propertyNames = @($entry.PSObject.Properties.Name)

	if ($status -eq "UNVERIFIED") {
		Assert-Condition (-not ($propertyNames -contains "qualificationType")) "UNVERIFIED requirement '$name' must not carry a qualification type."
		Assert-Condition (-not ($propertyNames -contains "binding")) "UNVERIFIED requirement '$name' must not carry binding evidence."
		Assert-Condition (-not ($propertyNames -contains "payload")) "UNVERIFIED requirement '$name' must not carry payload evidence."
		Assert-Condition (-not ($propertyNames -contains "workflow")) "UNVERIFIED requirement '$name' must not carry workflow evidence."
		continue
	}

	Assert-Condition ($propertyNames -contains "qualificationType") "PASSED requirement '$name' must identify a qualification type."
	Assert-Condition ($propertyNames -contains "binding") "PASSED requirement '$name' must identify binding evidence."
	Assert-Condition ($propertyNames -contains "payload") "PASSED requirement '$name' must identify payload evidence."
	Assert-Condition ($propertyNames -contains "workflow") "PASSED requirement '$name' must identify workflow evidence."
	$type = [string]$entry.qualificationType
	Assert-Condition (-not [string]::IsNullOrWhiteSpace($type)) "PASSED requirement '$name' must identify a qualification type."
	$bindingPolicy = @($qualificationPolicy.bindings | Where-Object { [string]$_.qualificationType -eq $type })
	Assert-Condition ($bindingPolicy.Count -eq 1) "Qualification type '$type' is not uniquely declared by policy."
	Assert-Condition (@($bindingPolicy[0].releaseRequirements) -contains $name) "Qualification type '$type' is not authorized to satisfy release requirement '$name'."

	$bindingPath = Resolve-RepositoryPath -Path ([string]$entry.binding.path)
	Assert-Condition (Test-Path -LiteralPath $bindingPath -PathType Leaf) "Qualification binding for '$name' is missing."
	$bindingHash = (Get-FileHash -LiteralPath $bindingPath -Algorithm SHA256).Hash.ToLowerInvariant()
	Assert-Condition ($bindingHash -eq [string]$entry.binding.sha256) "Qualification binding hash for '$name' does not match the manifest."
	& $bindingVerifier -BindingPath $bindingPath -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedQualificationType $type

	$binding = Get-Content -LiteralPath $bindingPath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$entry.payload.path -eq [string]$binding.payload.path) "Qualification payload path for '$name' differs between manifest and binding."
	Assert-Condition ([string]$entry.payload.sha256 -eq [string]$binding.payload.sha256) "Qualification payload hash for '$name' differs between manifest and binding."
	Assert-Condition ([string]$entry.workflow.runId -eq [string]$binding.workflow.runId) "Qualification workflow runId for '$name' differs between manifest and binding."
	Assert-Condition ([int]$entry.workflow.runAttempt -eq [int]$binding.workflow.runAttempt) "Qualification workflow runAttempt for '$name' differs between manifest and binding."
}

foreach ($requirement in $expectedRequirements) {
	Assert-Condition ($seen.Contains($requirement)) "Qualification evidence manifest is missing release requirement '$requirement'."
}

Write-Host "Qualification evidence manifest verification PASS"
Write-Host "Source commit: $ExpectedSourceCommit"
Write-Host "PASSED requirements: $(@($entries | Where-Object { [string]$_.status -eq 'PASSED' }).Count) / $($entries.Count)"
