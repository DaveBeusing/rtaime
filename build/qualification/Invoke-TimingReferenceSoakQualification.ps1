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
	[Parameter(Mandatory)][string]$ExternalAvSyncEvidencePath,
	[Parameter(Mandatory)][double]$MaximumAvSyncP95Milliseconds,
	[switch]$RequireReferenceRelock,
	[string]$EvidencePath = "artifacts/qualification/timing-reference-soak.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "Physical timing qualification requires Windows reference hardware." }
if ([Environment]::Is64BitProcess -ne $true) { throw "Physical timing qualification requires an x64 process." }
if ([string]::IsNullOrWhiteSpace($ExpectedAdapterName)) { throw "ExpectedAdapterName is required." }
if ($AjaSdkRevision -notmatch '^[0-9a-f]{40}$') { throw "AjaSdkRevision must be an exact 40-character commit SHA." }
if ($SoakSeconds -lt 1800) { throw "Long-soak qualification requires at least 1800 seconds (30 minutes)." }
if (-not [double]::IsFinite($MaximumHostCycleP95Milliseconds) -or $MaximumHostCycleP95Milliseconds -le 0) { throw "MaximumHostCycleP95Milliseconds must be positive and finite." }
if (-not [double]::IsFinite($MaximumEndToEndP95Milliseconds) -or $MaximumEndToEndP95Milliseconds -le 0) { throw "MaximumEndToEndP95Milliseconds must be positive and finite." }
if (-not [double]::IsFinite($MaximumAvSyncP95Milliseconds) -or $MaximumAvSyncP95Milliseconds -le 0) { throw "MaximumAvSyncP95Milliseconds must be positive and finite." }
if (-not $RequireReferenceRelock) { throw "Full physical timing qualification requires an explicit external-reference loss/re-lock exercise." }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$evidenceFullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $EvidencePath))
$externalLatencyFullPath = [System.IO.Path]::GetFullPath($ExternalLatencyEvidencePath)
$externalAvSyncFullPath = [System.IO.Path]::GetFullPath($ExternalAvSyncEvidencePath)

if (-not (Test-Path -LiteralPath $externalLatencyFullPath -PathType Leaf)) {
	throw "Independent physical end-to-end latency evidence is missing: '$externalLatencyFullPath'."
}
if (-not (Test-Path -LiteralPath $externalAvSyncFullPath -PathType Leaf)) {
	throw "Independent physical A/V synchronization evidence is missing: '$externalAvSyncFullPath'."
}

$externalLatency = Get-Content -LiteralPath $externalLatencyFullPath -Raw | ConvertFrom-Json
if ([string]$externalLatency.schemaVersion -ne "1.0") { throw "External latency evidence schemaVersion must be 1.0." }
if ([string]::IsNullOrWhiteSpace([string]$externalLatency.measurementMethod)) { throw "External latency evidence must name its independent measurement method." }
if ([int]$externalLatency.sampleCount -lt 30) { throw "External latency evidence requires at least 30 samples." }
$externalLatencyP95 = [double]$externalLatency.p95Milliseconds
$externalLatencyMaximum = [double]$externalLatency.maximumMilliseconds
if (-not [double]::IsFinite($externalLatencyP95) -or $externalLatencyP95 -lt 0) { throw "External latency p95 is invalid." }
if (-not [double]::IsFinite($externalLatencyMaximum) -or $externalLatencyMaximum -lt $externalLatencyP95) { throw "External latency maximum is invalid." }
if ($externalLatencyP95 -gt $MaximumEndToEndP95Milliseconds) {
	throw "Physical end-to-end latency p95 $externalLatencyP95 ms exceeds the allowed $MaximumEndToEndP95Milliseconds ms."
}

$externalAvSync = Get-Content -LiteralPath $externalAvSyncFullPath -Raw | ConvertFrom-Json
if ([string]$externalAvSync.schemaVersion -ne "1.0") { throw "External A/V sync evidence schemaVersion must be 1.0." }
if ([string]::IsNullOrWhiteSpace([string]$externalAvSync.measurementMethod)) { throw "External A/V sync evidence must name its independent measurement method." }
if ([int]$externalAvSync.sampleCount -lt 30) { throw "External A/V sync evidence requires at least 30 samples." }
$externalAvSyncP95 = [double]$externalAvSync.p95AbsoluteOffsetMilliseconds
$externalAvSyncMaximum = [double]$externalAvSync.maximumAbsoluteOffsetMilliseconds
if (-not [double]::IsFinite($externalAvSyncP95) -or $externalAvSyncP95 -lt 0) { throw "External A/V sync p95 absolute offset is invalid." }
if (-not [double]::IsFinite($externalAvSyncMaximum) -or $externalAvSyncMaximum -lt $externalAvSyncP95) { throw "External A/V sync maximum absolute offset is invalid." }
if ($externalAvSyncP95 -gt $MaximumAvSyncP95Milliseconds) {
	throw "Physical A/V sync p95 absolute offset $externalAvSyncP95 ms exceeds the allowed $MaximumAvSyncP95Milliseconds ms."
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
		throw "Physical timing hardware test did not emit qualification evidence. Test exit code: $testExitCode."
	}

	$evidence = Get-Content -LiteralPath $evidenceFullPath -Raw | ConvertFrom-Json
	if ([string]$evidence.schemaVersion -ne "1.0") { throw "Timing evidence schemaVersion must be 1.0." }
	if ([string]$evidence.status -ne "PASSED") { throw "Timing hardware evidence is '$($evidence.status)', not PASSED." }
	if ([int]$evidence.soakSeconds -lt 1800) { throw "Timing evidence does not contain a qualifying long soak." }
	if (-not [bool]$evidence.reference.seenInitialReferenceLock) { throw "Initial external-reference lock was not evidenced." }
	if (-not [bool]$evidence.reference.referenceLossObserved) { throw "External-reference loss was not evidenced." }
	if (-not [bool]$evidence.reference.referenceRelockObserved) { throw "External-reference re-lock was not evidenced." }
	if ([string]$evidence.reference.finalOutput -ne "Locked") { throw "Program output did not finish reference-locked." }
	if ([double]$evidence.hostCycle.p95Milliseconds -gt $MaximumHostCycleP95Milliseconds) { throw "Host-cycle p95 exceeds the configured threshold." }
	if ([string]$evidence.telemetry.status -ne "PASSED") { throw "CPU/RAM/GPU/VRAM telemetry continuity did not pass." }
	if ([int]$evidence.telemetry.sampleCount -lt 30) { throw "Telemetry continuity requires at least 30 samples." }
	if ([int]$evidence.telemetry.completeSampleCount -ne [int]$evidence.telemetry.sampleCount) { throw "Telemetry continuity contains incomplete samples." }
	if ($testExitCode -ne 0) { throw "Physical timing hardware test failed with exit code $testExitCode despite emitting evidence." }

	$evidence | Add-Member -NotePropertyName physicalEndToEndLatency -NotePropertyValue ([pscustomobject]@{
		status = "PASSED"
		measurementMethod = [string]$externalLatency.measurementMethod
		sampleCount = [int]$externalLatency.sampleCount
		p95Milliseconds = $externalLatencyP95
		maximumMilliseconds = $externalLatencyMaximum
		maximumAllowedP95Milliseconds = $MaximumEndToEndP95Milliseconds
		sourceEvidenceSha256 = (Get-FileHash -LiteralPath $externalLatencyFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
	}) -Force

	$evidence | Add-Member -NotePropertyName audioVideoSynchronization -NotePropertyValue ([pscustomobject]@{
		status = "PASSED"
		measurementMethod = [string]$externalAvSync.measurementMethod
		sampleCount = [int]$externalAvSync.sampleCount
		p95AbsoluteOffsetMilliseconds = $externalAvSyncP95
		maximumAbsoluteOffsetMilliseconds = $externalAvSyncMaximum
		maximumAllowedP95Milliseconds = $MaximumAvSyncP95Milliseconds
		sourceEvidenceSha256 = (Get-FileHash -LiteralPath $externalAvSyncFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
	}) -Force

	$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidenceFullPath -Encoding utf8
	Write-Host "Physical timing/reference/latency/A-V-sync/telemetry/soak qualification PASS"
	Write-Host "Evidence: $evidenceFullPath"
}
finally {
	foreach ($entry in $previous.GetEnumerator()) {
		[Environment]::SetEnvironmentVariable([string]$entry.Key, $entry.Value, "Process")
	}
}
