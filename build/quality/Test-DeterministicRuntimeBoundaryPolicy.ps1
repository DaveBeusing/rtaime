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

$runtimeProcessPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$monitoringPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeMonitoring.cs"
$gpuPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$boundaryTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/DeterministicRuntimeBoundaryTests.cs"
$monitoringTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/OperatorMonitoringPlaneTests.cs"
$soakTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ProgramFrameMemoryOwnershipTests.cs"
$gpuTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/GpuProcessingTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/DeterministicRuntimeBoundaryExecution.md"
$timingDocumentationPath = Join-Path $repositoryRoot "docs/TimingReferenceLatencySoakQualification.md"
$qualificationDocumentationPath = Join-Path $repositoryRoot "docs/qualification/ReferencePlatformQualification.md"

foreach ($path in @(
	$runtimeProcessPath,
	$runtimePath,
	$monitoringPath,
	$gpuPath,
	$boundaryTestsPath,
	$monitoringTestsPath,
	$soakTestsPath,
	$gpuTestsPath,
	$documentationPath,
	$timingDocumentationPath,
	$qualificationDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required deterministic Runtime boundary artifact is missing: '$path'."
}

$runtimeProcess = Get-Content -LiteralPath $runtimeProcessPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$monitoring = Get-Content -LiteralPath $monitoringPath -Raw
$gpu = Get-Content -LiteralPath $gpuPath -Raw
$boundaryTests = Get-Content -LiteralPath $boundaryTestsPath -Raw
$monitoringTests = Get-Content -LiteralPath $monitoringTestsPath -Raw
$soakTests = Get-Content -LiteralPath $soakTestsPath -Raw
$gpuTests = Get-Content -LiteralPath $gpuTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$timingDocumentation = Get-Content -LiteralPath $timingDocumentationPath -Raw
$qualificationDocumentation = Get-Content -LiteralPath $qualificationDocumentationPath -Raw

Assert-Condition (-not ($runtimeProcess -match "RunMediaDeckLoopAsync")) "RuntimeHost must not retain an independent media-deck production loop."
Assert-Condition ([regex]::Matches($runtimeProcess, "new PeriodicTimer").Count -eq 1) "RuntimeHost process must have exactly one PeriodicTimer production cadence."
Assert-Condition ($runtimeProcess -match "AdmitMediaDeckBoundary\(runtime, mediaDeck\)") "Media-deck admission must execute from the Program cadence."
Assert-Condition ($runtimeProcess -match "RunMediaLoopAsync\(_runtime, _mediaDeck, _mediaIo, _aiShowcase") "The single Program loop must own media-deck admission."

Assert-Condition ($runtime -match "private readonly object _boundaryExecutionGate") "RuntimeHost must retain explicit single-writer boundary serialization."
Assert-Condition ($runtime -match "lock \(_boundaryExecutionGate\)") "Boundary execution and committed execution replacement must share the boundary serializer."
Assert-Condition ($runtime -match "programEvidence\.Frame\.Timing\.SequenceNumber >= _nextSequenceNumber") "Program health must hide in-flight output evidence until boundary publication."
Assert-Condition ($runtime -match "auxEvidence\.Frame\.Timing\.SequenceNumber >= _nextSequenceNumber") "Aux health must hide in-flight output evidence until boundary publication."

Assert-Condition ($monitoring -match "RuntimeMonitoringSourceSnapshot") "Monitoring must carry explicit immutable source snapshots."
Assert-Condition ($monitoring -match "CaptureSources" -and $monitoring -match "DownscaleRgbaNearest") "Mutable source pixels must be snapshotted synchronously at boundary capture."
Assert-Condition ($monitoring -match "program\.Retain\(\)") "Program monitoring must retain leased Program memory for asynchronous use."

Assert-Condition ($gpu -match "_unreleasedBackendSurfaces") "GPU provider must retain failed backend releases as explicit resource evidence."
Assert-Condition ($gpu -match "Volatile\.Read\(ref _observableActiveSurfaceCount\)") "GPU resource diagnostics must remain readable without taking the execution lock."
Assert-Condition ($gpu -match "foreach \(var surfaceId in _unreleasedBackendSurfaces\.ToArray\(\)\)") "GPU provider stop must retry failed surface releases."

Assert-Condition ($boundaryTests -match "Snapshot_remains_available_while_gpu_composite_is_blocked") "Boundary regression must prove snapshot responsiveness during heavy GPU work."
Assert-Condition ($boundaryTests -match "Execution_commit_arriving_during_boundary_is_serialized_between_frames") "Boundary regression must prove execution commits cannot partially affect an in-flight frame."
Assert-Condition ($monitoringTests -match "Monitoring_source_snapshot_does_not_observe_later_mutation") "Monitoring regression must prove source temporal-aliasing protection."
Assert-Condition ($soakTests -match "Sustained_runtime_boundary_workload_keeps_resources_and_retention_bounded") "CI-sized sustained Runtime boundary qualification is required."
Assert-Condition ($soakTests -match "RTAIME_RUNTIME_BOUNDARY_SOAK_LONG") "Sustained Runtime qualification must expose an explicit longer mode."
Assert-Condition ($gpuTests -match "Failed_surface_release_remains_visible_until_stop_retries_cleanup") "GPU release-failure resource accounting regression is required."

Assert-Condition ($documentation -match "one production cadence authority" -and $documentation -match "There is no independent media-deck production timer") "Runtime boundary documentation must define one production cadence authority."
Assert-Condition ($documentation -match "Heavy data-plane work then executes without holding `_gate`") "Runtime documentation must describe state capture/publication versus heavy execution."
Assert-Condition ($documentation -match "UNVERIFIED") "Runtime boundary documentation must preserve the physical qualification boundary."
Assert-Condition ($timingDocumentation -match "single V1 production cadence boundary") "Timing qualification documentation must describe the single Runtime cadence."
Assert-Condition ($qualificationDocumentation -match "green deterministic-boundary or software-soak tests do not change") "Reference Platform Qualification must keep software and physical evidence separate."

Write-Host "Deterministic Runtime boundary policy verification PASS"
Write-Host "Cadence: one Program production timer"
Write-Host "Boundary state: capture/heavy execution/atomic publication"
Write-Host "Physical reference qualification: UNVERIFIED unless exact-source evidence passes"
