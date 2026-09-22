# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ResultPath,
	[string]$ExpectedProfile = "rtaime-v1-reference-platform",
	[string]$ExpectedSourceCommit = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$validStatuses = @("PASS", "FAIL", "NOT_APPLICABLE", "UNVERIFIED")

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Get-AggregatedStatus {
	param([Parameter(Mandatory)][object[]]$Items)
	$mandatory = @($Items | Where-Object { [bool]$_.mandatory })
	if (@($mandatory | Where-Object { [string]$_.status -eq "FAIL" }).Count -gt 0) { return "FAIL" }
	if (@($mandatory | Where-Object { [string]$_.status -eq "UNVERIFIED" }).Count -gt 0) { return "UNVERIFIED" }
	$invalid = @($mandatory | Where-Object { [string]$_.status -notin @("PASS", "NOT_APPLICABLE") })
	if ($invalid.Count -gt 0) { return "FAIL" }
	return "PASS"
}

$resolvedResult = Resolve-RepositoryPath -Path $ResultPath
Assert-Condition (Test-Path -LiteralPath $resolvedResult -PathType Leaf) "Reference-platform qualification result is missing: '$resolvedResult'."
$result = Get-Content -LiteralPath $resolvedResult -Raw | ConvertFrom-Json

Assert-Condition ([string]$result.schemaVersion -eq "1.0") "Reference-platform qualification result schema must be 1.0."
Assert-Condition ([string]$result.profile -eq $ExpectedProfile) "Reference-platform qualification profile mismatch."
Assert-Condition ($validStatuses -contains [string]$result.status) "Reference-platform qualification result contains an invalid overall status."
Assert-Condition ($validStatuses -contains [string]$result.softwareStatus) "Reference-platform qualification result contains an invalid software status."
Assert-Condition ($validStatuses -contains [string]$result.hardwareStatus) "Reference-platform qualification result contains an invalid hardware status."
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceCommit)) {
	Assert-Condition ([string]$result.sourceCommit -eq $ExpectedSourceCommit) "Reference-platform qualification source commit mismatch."
}

$scenarios = @($result.scenarios)
$expectedScenarioIds = @(1..10 | ForEach-Object { "Q{0:D2}" -f $_ })
Assert-Condition ($scenarios.Count -eq 10) "Reference-platform qualification must contain exactly ten software scenarios."
Assert-Condition (@($scenarios.id | Sort-Object -Unique).Count -eq 10) "Reference-platform qualification scenario IDs must be unique."
foreach ($id in $expectedScenarioIds) {
	Assert-Condition (@($scenarios | Where-Object { [string]$_.id -eq $id }).Count -eq 1) "Reference-platform qualification is missing scenario '$id'."
}
foreach ($scenario in $scenarios) {
	Assert-Condition ($validStatuses -contains [string]$scenario.status) "Scenario '$($scenario.id)' contains an invalid status."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$scenario.evidenceGroup)) "Scenario '$($scenario.id)' is missing an evidence group."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$scenario.testProject)) "Scenario '$($scenario.id)' is missing a test project."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$scenario.filter)) "Scenario '$($scenario.id)' is missing a test filter."
	if ([string]$scenario.status -in @("PASS", "FAIL")) {
		Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$scenario.evidencePath)) "Scenario '$($scenario.id)' must reference execution evidence."
		Assert-Condition (Test-Path -LiteralPath (Resolve-RepositoryPath -Path ([string]$scenario.evidencePath)) -PathType Leaf) "Scenario '$($scenario.id)' evidence file is missing."
	}
}

$hardware = @($result.hardwareRequirements)
$expectedHardware = @(
	"CUDA_GPU_EXECUTION",
	"GPU_FRAME_LATENCY",
	"GPU_SURFACE_LIFETIME",
	"PROFESSIONAL_MEDIA_IO",
	"SUSTAINED_FRAME_CADENCE",
	"DROPPED_FRAME_BEHAVIOR",
	"SYSTEM_TELEMETRY_CONTINUITY",
	"TIMING_REFERENCE_LOCK",
	"AUDIO_VIDEO_SYNCHRONIZATION",
	"RECOVERY_UNDER_LOAD",
	"PHYSICAL_END_TO_END_LATENCY",
	"LONG_SOAK_STABILITY"
)
Assert-Condition ($hardware.Count -eq $expectedHardware.Count) "Reference-platform qualification must contain every explicit physical evidence requirement."
Assert-Condition (@($hardware.requirement | Sort-Object -Unique).Count -eq $expectedHardware.Count) "Physical requirement names must be unique."
foreach ($requirement in $expectedHardware) {
	Assert-Condition (@($hardware | Where-Object { [string]$_.requirement -eq $requirement }).Count -eq 1) "Reference-platform qualification is missing physical requirement '$requirement'."
}
foreach ($item in $hardware) {
	Assert-Condition ($validStatuses -contains [string]$item.status) "Physical requirement '$($item.requirement)' contains an invalid status."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$item.qualificationType)) "Physical requirement '$($item.requirement)' is missing a qualification type."
	if ([string]$item.status -eq "PASS") {
		Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$item.evidencePath)) "Passed physical requirement '$($item.requirement)' must reference source-bound evidence."
		Assert-Condition (Test-Path -LiteralPath (Resolve-RepositoryPath -Path ([string]$item.evidencePath)) -PathType Leaf) "Passed physical requirement '$($item.requirement)' evidence is missing."
	}
}

$softwareStatus = Get-AggregatedStatus -Items $scenarios
$hardwareStatus = Get-AggregatedStatus -Items $hardware
$combined = @($scenarios) + @($hardware)
$overallStatus = Get-AggregatedStatus -Items $combined
Assert-Condition ([string]$result.softwareStatus -eq $softwareStatus) "Software qualification aggregation is inconsistent."
Assert-Condition ([string]$result.hardwareStatus -eq $hardwareStatus) "Hardware qualification aggregation is inconsistent."
Assert-Condition ([string]$result.status -eq $overallStatus) "Overall qualification aggregation is inconsistent; UNVERIFIED must never become PASS."

$expectedCounts = [ordered]@{
	PASS = @($combined | Where-Object { [string]$_.status -eq "PASS" }).Count
	FAIL = @($combined | Where-Object { [string]$_.status -eq "FAIL" }).Count
	NOT_APPLICABLE = @($combined | Where-Object { [string]$_.status -eq "NOT_APPLICABLE" }).Count
	UNVERIFIED = @($combined | Where-Object { [string]$_.status -eq "UNVERIFIED" }).Count
}
foreach ($status in $validStatuses) {
	Assert-Condition ([int]$result.counts.$status -eq [int]$expectedCounts[$status]) "Qualification count '$status' is inconsistent."
}

Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$result.environmentPath)) "Qualification result must reference environment evidence."
Assert-Condition (Test-Path -LiteralPath (Resolve-RepositoryPath -Path ([string]$result.environmentPath)) -PathType Leaf) "Qualification environment evidence is missing."

Assert-Condition ([string]$result.supportedPerformance.status -in @("PASS", "UNVERIFIED")) "Supported-performance evidence has an invalid status."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$result.supportedPerformance.path)) "Qualification result must reference supported-performance JSON evidence."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$result.supportedPerformance.markdownPath)) "Qualification result must reference the supported-performance matrix."
$performancePath = Resolve-RepositoryPath -Path ([string]$result.supportedPerformance.path)
$performanceMarkdownPath = Resolve-RepositoryPath -Path ([string]$result.supportedPerformance.markdownPath)
Assert-Condition (Test-Path -LiteralPath $performancePath -PathType Leaf) "Supported-performance JSON evidence is missing."
Assert-Condition (Test-Path -LiteralPath $performanceMarkdownPath -PathType Leaf) "Supported-performance Markdown matrix is missing."
$performance = Get-Content -LiteralPath $performancePath -Raw | ConvertFrom-Json
Assert-Condition ([string]$performance.schemaVersion -eq "1.0") "Supported-performance evidence schemaVersion must be 1.0."
Assert-Condition ([string]$performance.profile -eq $ExpectedProfile) "Supported-performance profile mismatch."
Assert-Condition ([string]$performance.sourceCommit -eq [string]$result.sourceCommit) "Supported-performance evidence source commit mismatch."
Assert-Condition ([string]$performance.status -eq [string]$result.supportedPerformance.status) "Supported-performance status differs from qualification result."
if ([string]$performance.status -eq "PASS") {
	Assert-Condition ([string]$result.hardwareStatus -eq "PASS") "Supported-performance PASS requires complete physical hardware qualification."
	Assert-Condition (@($performance.measurements).Count -gt 0) "Supported-performance PASS requires measured values."
}
foreach ($measurement in @($performance.measurements)) {
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$measurement.metric)) "Supported-performance measurement metric is required."
	Assert-Condition ([double]::IsFinite([double]$measurement.value)) "Supported-performance measurement value must be finite."
	Assert-Condition ([string]$measurement.payloadSha256 -match '^[0-9a-f]{64}

Write-Host "Reference-platform qualification result verification PASS"
Write-Host "Overall: $($result.status)"
Write-Host "Software: $($result.softwareStatus)"
Write-Host "Hardware: $($result.hardwareStatus)"
) "Supported-performance measurement must identify its verified payload hash."
}

Write-Host "Reference-platform qualification result verification PASS"
Write-Host "Overall: $($result.status)"
Write-Host "Software: $($result.softwareStatus)"
Write-Host "Hardware: $($result.hardwareStatus)"
