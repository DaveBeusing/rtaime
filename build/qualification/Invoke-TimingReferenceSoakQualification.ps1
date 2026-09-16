# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ExpectedAdapterName,
	[Parameter(Mandatory)][string]$AjaSdkRevision,
	[Parameter(Mandatory)][ValidateSet("1080p50", "1080p59.94")][string]$Format,
	[Parameter(Mandatory)][int]$SoakSeconds,
	[Parameter(Mandatory)][double]$MaximumHostCycleP95Milliseconds,
	[Parameter(Mandatory)][string]$ExternalLatencyEvidencePath,
	[Parameter(Mandatory)][double]$MaximumEndToEndP95Milliseconds,
	[switch]$RequireReferenceRelock,
	[string]$EvidencePath = "artifacts/qualification/timing-reference-soak.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "AP-34 physical qualification requires Windows reference hardware." }
if ([Environment]::Is64BitProcess -ne $true) { throw "AP-34 physical qualification requires an x64 process." }
if ([string]::IsNullOrWhiteSpace($ExpectedAdapterName)) { throw "ExpectedAdapterName is required." }
if ($AjaSdkRevision -notmatch '^[0-9a-f]{40}$') { throw "AjaSdkRevision must be an exact 40-character commit SHA." }
if ($SoakSeconds -lt 1800) { throw "AP-34 long-soak qualification requires at least 1800 seconds (30 minutes)." }
if (-not [double]::IsFinite($MaximumHostCycleP95Milliseconds) -or $MaximumHostCycleP95Milliseconds -le 0) { throw "MaximumHostCycleP95Milliseconds must be positive and finite." }
if (-not [double]::IsFinite($MaximumEndToEndP95Milliseconds) -or $MaximumEndToEndP95Milliseconds -le 0) { throw "MaximumEndToEndP95Milliseconds must be positive and finite." }
if (-not $RequireReferenceRelock) { throw "Full AP-34 qualification requires an explicit external-reference loss/re-lock exercise." }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$evidenceFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $EvidencePath))
$externalFullPath = [System.IO.Path]::GetFullPath($ExternalLatencyEvidencePath)
if (-not (Test-Path -LiteralPath $externalFullPath -PathType Leaf)) {
	throw "Independent physical end-to-end latency evidence is missing: '$externalFullPath'."
}

$external = Get-Content -LiteralPath $externalFullPath -Raw | ConvertFrom-Json
if ([string]$external.schemaVersion -ne "1.0") { throw "External latency evidence schemaVersion must be 1.0." }
if ([string]::IsNullOrWhiteSpace([string]$external.measurementMethod)) { throw "External latency evidence must name its independent measurement method." }
if ([int]$external.sampleCount -lt 30) { throw "External latency evidence requires at least 30 samples." }
$externalP95 = [double]$external.p95Milliseconds
$externalMaximum = [double]$external.maximumMilliseconds
if (-not [double]::IsFinite($externalP95) -or $externalP95 -lt 0) { throw "External latency p95 is invalid." }
if (-not [double]::IsFinite($externalMaximum) -or $externalMaximum -lt $externalP95) { throw "External latency maximum is invalid." }
if ($externalP95 -gt $MaximumEndToEndP95Milliseconds) {
	throw "Physical end-to-end latency p95 $externalP95 ms exceeds the allowed $MaximumEndToEndP95Milliseconds ms."
}

$previous = @{
	RTAIME_TIMING_REFERENCE_QUALIFICATION = $env:RTAIME_TIMING_REFERENCE_QUALIFICATION
	RTAIME_TIMING_EXPECTED_ADAPTER = $env:RTAIME_TIMING_EXPECTED_ADAPTER
	RTAIME_TIMING_REFERENCE_EVIDENCE = $env:RTAIME_TIMING_REFERENCE_EVIDENCE
	RTAIME_TIMING_SOAK_SECONDS = $env:RTAIME_TIMING_SOAK_SECONDS
	RTAIME_TIMING_REQUIRE_REFERENCE_RELOCK = $env:RTAIME_TIMING_REQUIRE_REFERENCE_RELOCK
	RTAIME_TIMING_MAX_HOST_CYCLE_P95_MS = $env:RTAIME_TIMING_MAX_HOST_CYCLE_P95_MS
	RTAIME_TIMING_REFERENCE_FORMAT = $env:RTAIME_TIMING_REFERENCE_FORMAT
	RTAIME_AJA_SDK_REVISION = $env:RTAIME_AJA_SDK_REVISION
}

try {
	$env:RTAIME_TIMING_REFERENCE_QUALIFICATION = "1"
	$env:RTAIME_TIMING_EXPECTED_ADAPTER = $ExpectedAdapterName
	$env:RTAIME_TIMING_REFERENCE_EVIDENCE = $evidenceFullPath
	$env:RTAIME_TIMING_SOAK_SECONDS = $SoakSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
	$env:RTAIME_TIMING_REQUIRE_REFERENCE_RELOCK = "true"
	$env:RTAIME_TIMING_MAX_HOST_CYCLE_P95_MS = $MaximumHostCycleP95Milliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
	$env:RTAIME_TIMING_REFERENCE_FORMAT = $Format
	$env:RTAIME_AJA_SDK_REVISION = $AjaSdkRevision

	dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj `
		--configuration Release `
		--filter "FullyQualifiedName~TimingReferenceHardwareQualificationTests.Reference_loss_relock_host_cycle_and_soak_must_pass_when_explicitly_enabled"
	$testExitCode = $LASTEXITCODE

	if (-not (Test-Path -LiteralPath $evidenceFullPath -PathType Leaf)) {
		throw "AP-34 hardware test did not emit qualification evidence. Test exit code: $testExitCode."
	}

	$evidence = Get-Content -LiteralPath $evidenceFullPath -Raw | ConvertFrom-Json
	if ([string]$evidence.schemaVersion -ne "1.0") { throw "AP-34 evidence schemaVersion must be 1.0." }
	if ([string]$evidence.status -ne "PASSED") { throw "AP-34 hardware evidence is '$($evidence.status)', not PASSED." }
	if ([int]$evidence.soakSeconds -lt 1800) { throw "AP-34 evidence does not contain a qualifying long soak." }
	if (-not [bool]$evidence.reference.seenInitialReferenceLock) { throw "Initial external-reference lock was not evidenced." }
	if (-not [bool]$evidence.reference.referenceLossObserved) { throw "External-reference loss was not evidenced." }
	if (-not [bool]$evidence.reference.referenceRelockObserved) { throw "External-reference re-lock was not evidenced." }
	if ([string]$evidence.reference.finalOutput -ne "Locked") { throw "Program output did not finish reference-locked." }
	if ([double]$evidence.hostCycle.p95Milliseconds -gt $MaximumHostCycleP95Milliseconds) { throw "Host-cycle p95 exceeds the configured threshold." }
	if ($testExitCode -ne 0) { throw "AP-34 hardware test failed with exit code $testExitCode despite emitting evidence." }

	$evidence | Add-Member -NotePropertyName physicalEndToEndLatency -NotePropertyValue ([pscustomobject]@{
		status = "PASSED"
		measurementMethod = [string]$external.measurementMethod
		sampleCount = [int]$external.sampleCount
		p95Milliseconds = $externalP95
		maximumMilliseconds = $externalMaximum
		maximumAllowedP95Milliseconds = $MaximumEndToEndP95Milliseconds
		sourceEvidence = $externalFullPath
	}) -Force

	$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidenceFullPath -Encoding utf8
	Write-Host "AP-34 timing/reference/latency/soak qualification PASS"
	Write-Host "Evidence: $evidenceFullPath"
}
finally {
	foreach ($entry in $previous.GetEnumerator()) {
		[Environment]::SetEnvironmentVariable([string]$entry.Key, $entry.Value, "Process")
	}
}
