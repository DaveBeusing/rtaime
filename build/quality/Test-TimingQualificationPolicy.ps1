# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$artifacts = @{
	Probe = "src/Runtime/rtaime.Runtime/TimingQualification.cs"
	UnitTests = "tests/rtaime.Tests.Unit/TimingQualificationTests.cs"
	RuntimeHost = "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
	RuntimeService = "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
	NativeAja = "native/Providers/AjaNtv2/rtaime_media_io_aja.cpp"
	MediaIoSlice = "src/Media/rtaime.Media/MediaIoVerticalSlice.cs"
	HardwareTest = "tests/rtaime.Tests.Integration/TimingReferenceHardwareQualificationTests.cs"
	Runner = "build/qualification/Invoke-TimingReferenceSoakQualification.ps1"
	Workflow = ".github/workflows/timing-reference-qualification.yml"
	RequiredGates = ".github/workflows/required-gates.yml"
	Documentation = "docs/TimingReferenceLatencySoakQualification.md"
}

$content = @{}
foreach ($entry in $artifacts.GetEnumerator()) {
	$path = Join-Path $repositoryRoot $entry.Value
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required AP-34 artifact is missing: '$($entry.Value)'."
	$content[$entry.Key] = Get-Content -LiteralPath $path -Raw
}

$probe = $content.Probe
$tests = $content.UnitTests
$runtimeHost = $content.RuntimeHost
$runtimeService = $content.RuntimeService
$nativeAja = $content.NativeAja
$mediaIoSlice = $content.MediaIoSlice
$hardwareTest = $content.HardwareTest
$runner = $content.Runner
$workflow = $content.Workflow
$requiredGates = $content.RequiredGates
$documentation = $content.Documentation

Assert-Condition ($probe -match 'class RuntimeTimingQualificationProbe') "AP-34 must retain a dedicated Runtime timing qualification probe."
Assert-Condition ($probe -match 'TimingQualificationState[\s\S]*Healthy[\s\S]*Degraded[\s\S]*Unstable[\s\S]*Lost') "Timing qualification state model is incomplete."
Assert-Condition ($probe -match 'RetainedSampleCapacity') "Timing evidence must retain an explicit bounded sample capacity."
Assert-Condition ($probe -match 'new TimingBoundaryObservation\[') "Timing evidence must use bounded fixed-capacity retention."
Assert-Condition ($probe -notmatch 'File\.Write|File\.Append|StreamWriter|HttpClient|Socket|NamedPipe') "Runtime timing probe must not perform synchronous disk or network I/O."
Assert-Condition ($probe -notmatch 'ControlHost|DesiredState|ProductionSpecification') "Runtime timing evidence must remain non-authoritative."
Assert-Condition ($probe -match 'SequenceDiscontinuities') "Timing evidence must expose sequence-continuity failures."
Assert-Condition ($probe -match 'MaximumObservedJitter') "Timing evidence must expose maximum observed jitter."
Assert-Condition ($probe -match 'MaximumObservedProcessingDuration') "Timing evidence must expose maximum processing duration."

Assert-Condition ($tests -match 'Perfect_cadence_remains_healthy') "Unit coverage must regress healthy cadence."
Assert-Condition ($tests -match 'repeated_violations_become_unstable') "Unit coverage must regress unstable timing behavior."
Assert-Condition ($tests -match 'Sequence_gap_is_immediately_unstable') "Unit coverage must regress sequence discontinuity."
Assert-Condition ($tests -match 'three_frame_periods_are_lost') "Unit coverage must regress timing loss detection."
Assert-Condition ($tests -match 'Retained_samples_are_bounded_and_chronological') "Unit coverage must regress bounded retention."

Assert-Condition ($runtimeHost -match 'RuntimeTimingQualificationProbe') "RuntimeHost must own the live timing qualification probe."
Assert-Condition ($runtimeHost -match 'Stopwatch\.StartNew\(\)') "RuntimeHost timing evidence must use a monotonic stopwatch."
Assert-Condition ($runtimeHost -match '_timingProbe\.RecordBoundary') "RuntimeHost must record committed Program boundaries."
Assert-Condition ($runtimeHost -match 'runtime\.SetTimingHealth\(MapTimingHealth') "Measured timing state must feed the RuntimeHost snapshot health."
Assert-Condition ($runtimeHost -match 'framePeriod\.Ticks / 4') "RuntimeHost must retain the explicit 25-percent jitter baseline."
Assert-Condition ($runtimeHost -match '2048') "RuntimeHost live timing retention must remain bounded."

Assert-Condition ($runtimeService -match '_timingHealth = V1TimingHealthState\.Recovering') "Runtime timing health must start Recovering rather than claiming Healthy before evidence."
Assert-Condition ($runtimeService -match '\s_timingHealth,') "Runtime snapshot must expose measured timing health."
Assert-Condition ($runtimeService -match 'void SetTimingHealth\(V1TimingHealthState state\)') "Runtime service must expose only an observational timing-health update seam."

Assert-Condition ($nativeAja -match 'external_reference_locked') "AJA adapter must retain explicit external-reference lock evaluation."
Assert-Condition ($nativeAja -match 'GetReference\(reference_source\)') "AJA reference state must verify the selected clock source."
Assert-Condition ($nativeAja -match 'GetReferenceVideoFormat\(\)') "AJA reference state must verify a detected reference format."
Assert-Condition ($nativeAja -match 'RTAIME_MEDIA_IO_SIGNAL_LOST') "AJA output status must surface reference loss."
Assert-Condition ($nativeAja -match 'external_reference_required[\s\S]*RTAIME_MEDIA_IO_WOULD_BLOCK') "Reference loss must become bounded output backpressure rather than silent fallback."
Assert-Condition ($nativeAja -notmatch 'SetReference\(NTV2_REFERENCE_FREERUN') "AP-34 must never silently fall back from external reference to free-run."
Assert-Condition ($mediaIoSlice -match 'ProgramOutputStatus => _output\.Status') "AP-34 qualification must be able to observe Program-output reference state."

Assert-Condition ($hardwareTest -match 'RTAIME_TIMING_REFERENCE_QUALIFICATION') "Physical AP-34 test must be explicit opt-in."
Assert-Condition ($hardwareTest -match 'requireExternalReference: true') "Physical AP-34 qualification must require external reference."
Assert-Condition ($hardwareTest -match 'referenceLossObserved') "Physical qualification must retain a reference-loss observation."
Assert-Condition ($hardwareTest -match 'referenceRelockObserved') "Physical qualification must retain a reference re-lock observation."
Assert-Condition ($hardwareTest -match 'expectedFrames \* 0\.85') "Soak qualification must enforce the continuity floor."
Assert-Condition ($hardwareTest -match 'OutputRejected == 0') "Soak qualification must reject hard Program-output failures."
Assert-Condition ($hardwareTest -match 'hostCycleP95') "Physical qualification must retain host-cycle p95 evidence."

Assert-Condition ($runner -match 'SoakSeconds -lt 1800') "Full AP-34 qualification must enforce at least a 30-minute soak."
Assert-Condition ($runner -match 'sampleCount -lt 30') "Independent physical E2E evidence must require at least 30 samples."
Assert-Condition ($runner -match 'measurementMethod') "Independent physical E2E evidence must identify its measurement method."
Assert-Condition ($runner -match 'physicalEndToEndLatency') "Final AP-34 evidence must keep physical E2E latency explicitly separated."
Assert-Condition ($runner -match 'referenceLossObserved') "Runner must fail closed without reference-loss evidence."
Assert-Condition ($runner -match 'referenceRelockObserved') "Runner must fail closed without re-lock evidence."

Assert-Condition ($workflow -match 'workflow_dispatch:') "AP-34 physical workflow must be manual-only."
Assert-Condition ($workflow -notmatch '(?m)^\s*(pull_request|push):') "AP-34 physical qualification must not run as generic CI."
Assert-Condition ($workflow -match 'self-hosted') "AP-34 workflow must run on self-hosted reference hardware."
Assert-Condition ($workflow -match 'rtaime-media-io-reference') "AP-34 workflow must use the declared reference-hardware runner label."
Assert-Condition ($workflow -match 'Invoke-TimingReferenceSoakQualification\.ps1') "AP-34 workflow must invoke the fail-closed qualification runner."
Assert-Condition ($workflow -match 'external_latency_evidence_path') "AP-34 workflow must require independent physical-latency evidence."
Assert-Condition ($workflow -match 'actions/upload-artifact@v4') "AP-34 workflow must retain immutable evidence as an artifact."

Assert-Condition ($requiredGates -match 'Test-TimingQualificationPolicy\.ps1') "Quality gate must execute the AP-34 timing qualification policy."
Assert-Condition ($requiredGates -notmatch 'Invoke-TimingReferenceSoakQualification\.ps1') "Generic Required Gates must never claim physical AP-34 qualification."

Assert-Condition ($documentation -match 'Host pipeline / host-cycle latency') "AP-34 documentation must distinguish host-side latency."
Assert-Condition ($documentation -match 'Physical end-to-end latency') "AP-34 documentation must distinguish physical end-to-end latency."
Assert-Condition ($documentation -match 'reference-loss') "AP-34 documentation must retain reference-loss qualification scope."
Assert-Condition ($documentation -match 'RuntimeHost live timing-health wiring:\s*\*\*IMPLEMENTED\*\*') "Documentation must reflect live RuntimeHost timing wiring."
Assert-Condition ($documentation -match 'physical end-to-end latency qualification:\s*\*\*UNVERIFIED\*\*') "AP-34 must not overstate retained physical latency evidence."
Assert-Condition ($documentation -match 'long soak qualification:\s*\*\*UNVERIFIED\*\*') "AP-34 must not overstate retained soak evidence."

Write-Host "Timing/reference/latency/soak qualification policy verification PASS"
Write-Host "Implementation mechanism: complete; retained physical AP-34 evidence: UNVERIFIED"
