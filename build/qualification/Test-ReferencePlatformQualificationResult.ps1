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
$expectedHardware = @("REFERENCE_GPU", "PROFESSIONAL_MEDIA_IO", "GENLOCK", "PHYSICAL_END_TO_END_LATENCY", "LONG_SOAK")
Assert-Condition ($hardware.Count -eq 5) "Reference-platform qualification must contain exactly five physical requirements."
Assert-Condition (@($hardware.requirement | Sort-Object -Unique).Count -eq 5) "Physical requirement names must be unique."
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

Write-Host "Reference-platform qualification result verification PASS"
Write-Host "Overall: $($result.status)"
Write-Host "Software: $($result.softwareStatus)"
Write-Host "Hardware: $($result.hardwareStatus)"
