# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$matrixPath = Join-Path $repositoryRoot "qualification/security/r143-v3-control-matrix.json"

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

Assert-Condition (Test-Path -LiteralPath $matrixPath -PathType Leaf) "EBU R 143 v3 control matrix is missing."
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
Assert-Condition ($matrix.schemaVersion -eq "1.0" -and $matrix.standard -eq "EBU R 143" -and $matrix.standardVersion -eq "3.0") "EBU R 143 matrix identity/version is invalid."
Assert-Condition (@($matrix.controlRows).Count -gt 0) "EBU R 143 matrix has no controls."

$sourceCommit = ((& git -C $repositoryRoot rev-parse HEAD) | Out-String).Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $sourceCommit -match '^[a-f0-9]{40}$') "Unable to obtain exact source commit."
if (-not [string]::IsNullOrWhiteSpace($env:RTAIME_SOURCE_COMMIT)) {
	Assert-Condition ($sourceCommit -eq $env:RTAIME_SOURCE_COMMIT) "Broadcast security evidence source does not match the requested CI checkout."
}

$knownStatuses = @("VERIFIED", "PARTIAL", "MISSING", "NOT_APPLICABLE")
$knownPriorities = @("P1", "P2")
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$unresolvedP1 = [System.Collections.Generic.List[string]]::new()
$resolvedControls = [System.Collections.Generic.List[object]]::new()
foreach ($row in @($matrix.controlRows)) {
	$id = [string]$row.id
	$status = [string]$row.status
	$priority = [string]$row.priority
	Assert-Condition ($id -match '^M-[A-Z]{2}-[0-9]{2}$' -and $seen.Add($id)) "Matrix contains invalid or duplicate control identifier '$id'."
	Assert-Condition ($priority -in $knownPriorities) "Matrix control '$id' must be classified as P1 or P2."
	Assert-Condition ($status -in $knownStatuses) "Matrix control '$id' has an invalid evidence status."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$row.finding)) "Matrix control '$id' is missing its finding."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$row.remediation)) "Matrix control '$id' is missing remediation."
	if ($status -in @("PARTIAL", "MISSING")) {
		Assert-Condition ([string]$row.severity -in @("LOW", "MEDIUM", "HIGH", "CRITICAL")) "Matrix control '$id' must classify its unresolved risk."
	}
	if ($status -eq "VERIFIED") {
		Assert-Condition (@($row.testMethods).Count -gt 0) "A VERIFIED control requires named regression proof."
	}
	$paths = @($row.evidencePaths)
	Assert-Condition ($paths.Count -gt 0) "Matrix control '$id' has no source evidence paths."
	$testFiles = [System.Collections.Generic.List[string]]::new()
	foreach ($path in $paths) {
		$relative = [string]$path
		Assert-Condition ($relative -match '^[a-zA-Z0-9_./-]+$' -and $relative -notmatch '(^/|\.\./|//)') "Matrix control '$id' contains an unsafe evidence path."
		$absolute = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relative))
		Assert-Condition (Test-Path -LiteralPath $absolute -PathType Leaf) "Missing evidence '$relative' for control '$id'."
		if ($relative.StartsWith("tests/", [StringComparison]::Ordinal)) { $testFiles.Add($absolute) }
	}
	foreach ($testMethod in @($row.testMethods)) {
		$name = [string]$testMethod
		Assert-Condition ($name -match '^[a-zA-Z0-9_]+$') "Invalid named regression '$name' for '$id'."
		$found = $false
		foreach ($testFile in $testFiles) {
			if ((Get-Content -LiteralPath $testFile -Raw).Contains($name)) { $found = $true; break }
		}
		Assert-Condition ($found) "Matrix control '$id' references missing regression '$name'."
	}
	if ($priority -eq "P1" -and $status -in @("PARTIAL", "MISSING")) { $unresolvedP1.Add($id) }
	$resolvedControls.Add([ordered]@{ id = $id; priority = $priority; status = $status; severity = [string]$row.severity; evidencePaths = $paths; testMethods = @($row.testMethods) })
}

$evidence = [ordered]@{
	schemaVersion = "1.0"
	standard = "EBU R 143"
	standardVersion = "3.0"
	sourceCommit = $sourceCommit
	assessmentScope = [string]$matrix.assessmentScope
	status = if ($unresolvedP1.Count -gt 0) { "UNVERIFIED" } else { "PARTIAL" }
	softwareEvidence = "SOURCE_PATHS_AND_TEST_DECLARATIONS_VERIFIED"
	testExecutionStatus = "UNVERIFIED"
	operationalAndHardwareQualification = "UNVERIFIED"
	securityCertification = "UNVERIFIED"
	unresolvedP1 = $unresolvedP1.ToArray()
	controls = $resolvedControls.ToArray()
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
	$OutputPath = Join-Path $repositoryRoot "artifacts/qualification/broadcast-security/r143-v3-evidence.json"
}
$target = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
$json = $evidence | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($target, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "EBU R 143 v3 exact-source matrix verification PASS: $sourceCommit"
Write-Host "Controls assessed: $($resolvedControls.Count); unresolved P1: $($unresolvedP1.Count)"
Write-Host "Test execution, network/hardware qualification and certification: UNVERIFIED"
Write-Host "Evidence file: $target"
