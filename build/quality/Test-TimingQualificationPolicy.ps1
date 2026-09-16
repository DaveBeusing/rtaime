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

$probePath = Join-Path $repositoryRoot "src/Runtime/rtaime.Runtime/TimingQualification.cs"
$testPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/TimingQualificationTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/TimingReferenceLatencySoakQualification.md"

foreach ($path in @($probePath, $testPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required AP-34 timing qualification artifact is missing: '$path'."
}

$probe = Get-Content -LiteralPath $probePath -Raw
$tests = Get-Content -LiteralPath $testPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

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

Assert-Condition ($documentation -match 'Host pipeline latency') "AP-34 documentation must distinguish host pipeline latency."
Assert-Condition ($documentation -match 'Physical end-to-end latency') "AP-34 documentation must distinguish physical end-to-end latency."
Assert-Condition ($documentation -match 'reference-loss') "AP-34 documentation must retain reference-loss qualification scope."
Assert-Condition ($documentation -match 'long soak qualification:\s*\*\*UNVERIFIED\*\*') "AP-34 must not overstate soak evidence."
Assert-Condition ($documentation -match 'physical end-to-end latency qualification:\s*\*\*UNVERIFIED\*\*') "AP-34 must not overstate physical latency evidence."

Write-Host "Timing/reference/latency/soak qualification policy verification PASS"
Write-Host "Current evidence: deterministic timing probe implemented; physical reference/latency/soak remains UNVERIFIED"
