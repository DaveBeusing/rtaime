# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$SoftwareEvidencePath,
    [Parameter(Mandatory)][string]$BaselineComparisonPath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidateSet("smoke", "stress", "soak", "soak8h", "soak24h", "soak72h")][string]$ExpectedProfile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-File {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Required renderer qualification evidence is missing: '$resolved'."
    }
    return $resolved
}

if ($ExpectedSourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "ExpectedSourceCommit must be an exact lowercase 40-character SHA."
}

function Assert-TimingSummary {
    param([Parameter(Mandatory)]$Summary, [Parameter(Mandatory)][string]$Context)
    $countProperty = if ($null -ne $Summary.PSObject.Properties["retainedSamples"]) { "retainedSamples" } else { "samples" }
    if ([int]$Summary.$countProperty -le 0) { throw "Timing sample count is invalid for '$Context'." }
    $values = @([double]$Summary.p50Milliseconds, [double]$Summary.p95Milliseconds,
        [double]$Summary.p99Milliseconds, [double]$Summary.maximumMilliseconds)
    foreach ($value in $values) {
        if (-not [double]::IsFinite($value) -or $value -lt 0) { throw "Timing evidence is negative or non-finite for '$Context'." }
    }
    if ($values[0] -gt $values[1] -or $values[1] -gt $values[2] -or $values[2] -gt $values[3]) {
        throw "Timing percentile ordering is invalid for '$Context'."
    }
    if ([string]$Summary.measurementKind -notin @("CPU_WALL", "GPU_EVENT", "SCHEDULER_LATENESS", "PRESENTATION_LATENESS")) {
        throw "Timing measurement kind is invalid for '$Context'."
    }
}

function Assert-RendererEvidenceIdentity {
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)]$Evidence, [Parameter(Mandatory)][string]$ExpectedProfileHash)
    if ([string]$Manifest.repository -ne "DaveBeusing/rtaime" -or
        [string]$Manifest.profileSha256 -ne $ExpectedProfileHash) { throw "Renderer repository/workload hash mismatch." }
    if ([int]$Manifest.gpu.ordinal -ne [int]$Evidence.deviceOrdinal -or [int]$Manifest.gpu.ordinal -lt 0 -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.gpu.pciBusId) -or [long]$Manifest.gpu.totalMemoryMiB -le 0) {
        throw "Renderer device ordinal/PCI/memory identity is inconsistent or missing."
    }
    if ([string]$Manifest.operatingSystem.architecture -ne "X64" -or [string]$Manifest.dotnet.processArchitecture -ne "X64" -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.operatingSystem.version) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.operatingSystem.buildNumber)) { throw "Renderer reference Windows x64 identity is incomplete." }
    $local = [string]$Manifest.workflow.runId -eq "local" -and [string]$Manifest.workflow.runAttempt -eq "local"
    $remote = [string]$Manifest.workflow.runId -match '^[1-9][0-9]*$' -and [string]$Manifest.workflow.runAttempt -match '^[1-9][0-9]*$'
    if (-not ($local -or $remote) -or [string]::IsNullOrWhiteSpace([string]$Manifest.workflow.runnerName)) {
        throw "Renderer workflow run/attempt identity is invalid."
    }
    $start = [DateTimeOffset]::Parse([string]$Manifest.capturedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
    $end = [DateTimeOffset]::Parse([string]$Evidence.capturedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
    if ($end -lt $start -or $end -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { throw "Renderer capture time provenance is invalid." }
}

$evidence = Get-Content -LiteralPath (Require-File $EvidencePath) -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Require-File $ManifestPath) -Raw | ConvertFrom-Json
$software = Get-Content -LiteralPath (Require-File $SoftwareEvidencePath) -Raw | ConvertFrom-Json
$baseline = Get-Content -LiteralPath (Require-File $BaselineComparisonPath) -Raw | ConvertFrom-Json

if ([string]$evidence.schemaVersion -ne "1.0") { throw "Renderer evidence schema must be 1.0." }
if ([string]$evidence.qualification -ne "rtaime-renderer-reference") { throw "Unexpected renderer evidence identity." }
if ([string]$evidence.status -ne "PASSED") { throw "Renderer evidence must be PASSED." }
if ([string]$evidence.sourceCommit -ne $ExpectedSourceCommit) { throw "Renderer evidence source SHA does not match the expected source." }
if ([string]$evidence.profile -ne $ExpectedProfile) { throw "Renderer evidence profile does not match '$ExpectedProfile'." }
if ([string]::IsNullOrWhiteSpace([string]$evidence.detectedDeviceName)) { throw "Renderer evidence is missing the detected GPU identity." }

if ([string]$manifest.schemaVersion -ne "1.0" -or [string]$manifest.qualification -ne "rtaime-renderer-reference") {
    throw "Renderer manifest identity or schema is invalid."
}
if ([string]$manifest.sourceCommit -ne $ExpectedSourceCommit) { throw "Renderer manifest source SHA does not match the expected source." }
if ([string]$manifest.profile -ne $ExpectedProfile) { throw "Renderer manifest profile does not match '$ExpectedProfile'." }
if ([string]$manifest.gpu.detectedName -ne [string]$evidence.detectedDeviceName) {
    throw "Renderer manifest and execution evidence GPU identities differ."
}
if ([string]$manifest.dotnet.sdk -ne "10.0.401") { throw "Renderer manifest must retain the pinned .NET SDK." }
if ([string]::IsNullOrWhiteSpace([string]$manifest.gpu.driverVersion)) { throw "Renderer manifest is missing the NVIDIA driver version." }

$profilePath = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))) "qualification/renderer/renderer-qualification.json"
$expectedProfileHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-RendererEvidenceIdentity -Manifest $manifest -Evidence $evidence -ExpectedProfileHash $expectedProfileHash

$compositorCases = @($evidence.compositor)
if ($compositorCases.Count -ne 20) {
    throw "Renderer evidence must contain 20 compositor cases: two formats, CUT/DISSOLVE and 0/1/2/4/8 layers."
}
foreach ($format in @("1080p50", "1080p59.94")) {
    foreach ($transition in @("CUT", "DISSOLVE")) {
        foreach ($layerCount in @(0, 1, 2, 4, 8)) {
            $matches = @($compositorCases | Where-Object {
                [string]$_.format -eq $format -and
                [string]$_.transition -eq $transition -and
                [int]$_.layerCount -eq $layerCount
            })
            if ($matches.Count -ne 1) {
                throw "Renderer evidence is missing exactly one '$format/$transition/$layerCount-layer' compositor case."
            }
            $case = $matches[0]
            if (-not [bool]$case.pixelIntegrity) { throw "Renderer compositor pixel integrity failed for '$format/$transition/$layerCount'." }
            if (-not [bool]$case.surfaceLifetimeCorrect) { throw "Renderer compositor surface lifetime failed for '$format/$transition/$layerCount'." }
            if (-not [bool]$case.monitoringExported) { throw "Renderer D3D11 monitoring export failed for '$format/$transition/$layerCount'." }
            Assert-TimingSummary $case "$format/$transition/$layerCount"
            if ([int]$case.warmupBoundaries -le 0) { throw "Compositor warmup is missing." }
            foreach ($timing in @($case.gpuTimings)) {
                Assert-TimingSummary $timing "$format/$transition/$layerCount/$($timing.operation)"
                $expectedKind = if ([string]$timing.operation -eq "KernelGpuElapsed") { "GPU_EVENT" } else { "CPU_WALL" }
                if ([string]$timing.measurementKind -ne $expectedKind) { throw "CUDA/CPU measurement kind mismatch." }
            }
            $identityCase = $transition -eq "CUT" -and $layerCount -eq 0
            $expectedPath = if ($identityCase) { "IDENTITY_ALIAS" } else { "COMPOSITE_KERNEL" }
            if ([string]$case.executionPath -ne $expectedPath) { throw "Renderer matrix execution-path evidence is invalid." }
            $operations = @($case.gpuTimings | ForEach-Object { [string]$_.operation })
            foreach ($kernelOperation in @("KernelLaunch", "ContextSynchronize", "KernelGpuElapsed")) {
                if ($identityCase -and $operations -contains $kernelOperation) { throw "Identity renderer CUT must not execute a kernel." }
                if (-not $identityCase -and $operations -notcontains $kernelOperation) { throw "Renderer matrix kernel timing is missing." }
            }
            $p50 = [double]$case.p50Milliseconds
            $p95 = [double]$case.p95Milliseconds
            $p99 = [double]$case.p99Milliseconds
            $maximum = [double]$case.maximumMilliseconds
            if (-not [double]::IsFinite($p50) -or -not [double]::IsFinite($p95) -or -not [double]::IsFinite($p99) -or -not [double]::IsFinite($maximum)) {
                throw "Renderer compositor latency evidence is non-finite for '$format/$transition/$layerCount'."
            }
            if ($p50 -gt $p95 -or $p95 -gt $p99 -or $p99 -gt $maximum) {
                throw "Renderer compositor percentile ordering is invalid for '$format/$transition/$layerCount'."
            }
        }
    }
}

$runtimeResults = @($evidence.runtime)
if ($runtimeResults.Count -ne 2) { throw "Renderer evidence must contain exactly two runtime-format results." }
foreach ($format in @("1080p50", "1080p59.94")) {
    $matches = @($runtimeResults | Where-Object { [string]$_.format -eq $format })
    if ($matches.Count -ne 1) { throw "Renderer runtime evidence is missing '$format'." }
    $result = $matches[0]
    if (-not [bool]$result.hardwareBackend -or
        [string]$result.boundaryScope -ne "V1_SERVICE_WITH_CONSUMERS_EXCLUDES_EXTERNAL_MEDIA_IO") {
        throw "Software workload cannot be renderer hardware evidence."
    }
    $minimumSeconds = switch ($ExpectedProfile) {
        "smoke" { 30 } "stress" { 300 } "soak" { 3600 }
        "soak8h" { 14400 } "soak24h" { 43200 } "soak72h" { 129600 }
    }
    if ([int]$result.durationSeconds -ne $minimumSeconds -or
        -not [double]::IsFinite([double]$result.elapsedSeconds) -or
        [double]$result.elapsedSeconds -lt $minimumSeconds) { throw "Actual renderer duration is incomplete." }
    if ([int]$result.cpuBoundary.histogram.capacity -ne 10001 -or
        [string]$result.cpuBoundary.histogram.percentileMethod -ne "WHOLE_RUN_NEAREST_RANK_UPPER_BIN_BOUND" -or
        [ulong]$result.cpuBoundary.histogram.count -ne [ulong]$result.boundaries -or
        @($result.telemetry.resourceWindows).Count -gt 96 -or @($result.telemetry.vramWindows).Count -gt 96) { throw "Renderer whole-run evidence is unbounded or incomplete." }
    if ($minimumSeconds -ge 14400 -and ([string]$result.telemetry.resourcePlateau -ne "PASS" -or [string]$result.telemetry.vramPlateau -ne "PASS")) {
        throw "Long renderer profile requires measured resource plateau evidence."
    }
    if ([int]$result.recording.effectivePressureReleaseBoundary -le [int]$result.recording.queueCapacity + 1) {
        throw "Controlled pressure must exceed the real queue plus in-flight sample."
    }
    if ([int]$result.boundaries -le 0) { throw "Renderer runtime evidence has no boundaries for '$format'." }
    if ([ulong]$result.continuity.sequenceDiscontinuities -ne 0 -or
        [ulong]$result.continuity.duplicateSequenceFrames -ne 0 -or
        [ulong]$result.continuity.missingSequenceFrames -ne 0) {
        throw "Renderer runtime duplicate/missing sequence evidence detected for '$format'."
    }
    if ($null -eq $result.deviceFaults -or [int]$result.deviceFaults.observed -lt 0) {
        throw "Renderer runtime device-fault evidence is missing for '$format'."
    }
    if (-not [bool]$result.recording.recoveredAfterPressure) { throw "Renderer recording did not recover after controlled backpressure for '$format'." }
    if ([ulong]$result.recording.rejectedDuringPressure -eq 0) { throw "Renderer recording pressure was not observed for '$format'." }
    if (-not [bool]$result.monitoring.disconnected -or -not [bool]$result.monitoring.reconnected) {
        throw "Renderer monitoring disconnect/reconnect did not complete for '$format'."
    }
    if ([ulong]$result.monitoring.totalSharedExports -eq 0) { throw "Renderer monitoring shared-resource export was not exercised for '$format'." }
    if ([int]$result.cleanup.activeGpuSurfaces -ne 0 -or
        [int]$result.cleanup.activeReadbackBuffers -ne 0 -or [int]$result.cleanup.allocatedReadbackBuffers -ne 0 -or
        [int]$result.cleanup.activeSharedMonitoringResources -ne 0) {
        throw "Renderer cleanup retained GPU/readback/monitoring resources for '$format'."
    }
    $p50 = [double]$result.cpuBoundary.p50Milliseconds
    $p95 = [double]$result.cpuBoundary.p95Milliseconds
    $p99 = [double]$result.cpuBoundary.p99Milliseconds
    $maximum = [double]$result.cpuBoundary.maximumMilliseconds
    if (-not [double]::IsFinite($p50) -or -not [double]::IsFinite($p95) -or -not [double]::IsFinite($p99) -or -not [double]::IsFinite($maximum)) {
        throw "Renderer runtime latency evidence is non-finite for '$format'."
    }
    if ($p50 -gt $p95 -or $p95 -gt $p99 -or $p99 -gt $maximum) {
        throw "Renderer runtime percentile ordering is invalid for '$format'."
    }
    if ([double]$result.frameBudgetMilliseconds -le 0) { throw "Renderer frame budget is invalid for '$format'." }

    if ([int]$result.warmupBoundaries -le 0) { throw "Runtime warmup is missing." }
    $pipeline = $result.pipelineTiming
    if ([int]$pipeline.capacity -le 0 -or @($pipeline.samples).Count -gt [int]$pipeline.capacity) { throw "Pipeline timing window is unbounded." }
    if ([ulong]$pipeline.recordedSamples -ne ([ulong]$pipeline.overwrittenSamples + [ulong]@($pipeline.samples).Count)) {
        throw "Pipeline retained/overwritten counters do not reconcile."
    }
    foreach ($stage in @($pipeline.stages)) { Assert-TimingSummary $stage "$format/$($stage.stage)" }
    $stages = @($pipeline.stages | ForEach-Object { [string]$_.stage })
    foreach ($required in @("ServiceBoundaryAttempt", "ExecutionLockWait", "ExecutionLockHold", "CaptureLockWait",
        "CaptureLockHold", "MetadataLockWait", "MetadataLockHold", "Composite", "Readback", "MonitoringExport",
        "ConsumerSubmission", "SchedulerStartLateness", "PresentationLateness")) {
        if ($stages -notcontains $required) { throw "Pipeline stage '$required' is missing for '$format'." }
    }
    foreach ($timing in @($result.gpuTimings)) {
        Assert-TimingSummary $timing "$format/$($timing.operation)"
        $expectedKind = if ([string]$timing.operation -eq "KernelGpuElapsed") { "GPU_EVENT" } else { "CPU_WALL" }
        if ([string]$timing.measurementKind -ne $expectedKind) { throw "CUDA/CPU measurement kind mismatch." }
    }
    $timings = @($result.gpuTimings | ForEach-Object { [string]$_.operation })
    foreach ($required in @("UploadHostToDevice", "KernelLaunch", "ContextSynchronize", "KernelGpuElapsed", "ReadbackDeviceToHost", "MonitoringExport", "BackendLockWait", "BackendLockHold")) {
        if ($timings -notcontains $required) {
            throw "Renderer runtime evidence is missing CUDA timing '$required' for '$format'."
        }
    }
}

if ([string]$evidence.physicalExternalOutput.status -ne "UNVERIFIED") {
    throw "Renderer qualification must not claim professional Media I/O/genlock/physical output evidence."
}
if ([string]$evidence.physicalDeviceFaultInjection.status -ne "UNVERIFIED") {
    throw "Renderer qualification must not claim destructive device-fault injection that was not performed."
}

if ([string]$software.schemaVersion -ne "1.0" -or [string]$software.sourceCommit -ne $ExpectedSourceCommit) {
    throw "Renderer software-scenario evidence schema/source is invalid."
}
if ([string]$software.status -ne "PASS") { throw "Renderer software-scenario evidence must be PASS." }
$softwareIds = @($software.scenarios | ForEach-Object { [string]$_.id })
$expectedSoftwareIds = @("CLIP_SEEK", "RENDERER_BACKEND_RECOVERY", "RESOURCE_PRESSURE", "INTEROP_FAILURE_RECOVERY", "RESIZE_DPI_CHURN", "MONITORING_TRANSPORT_RECONNECT", "PROCESS_RESTART")
if (($softwareIds -join ",") -ne ($expectedSoftwareIds -join ",")) {
    throw "Renderer software evidence must contain exactly the ordered qualified scenario matrix without duplicates or unknown scenarios."
}
foreach ($scenario in @($software.scenarios)) {
    if ([string]$scenario.status -ne "PASS" -or [int]$scenario.exitCode -ne 0) {
        throw "Renderer software scenario '$($scenario.id)' did not complete successfully."
    }
    if ([string]::IsNullOrWhiteSpace([string]$scenario.log)) {
        throw "Renderer software scenario '$($scenario.id)' lacks its retained log reference."
    }
}
if (@($software.scenarios | Where-Object { [string]$_.status -ne "PASS" }).Count -ne 0) {
    throw "Renderer software evidence contains a failed scenario."
}

if ([string]$baseline.schemaVersion -ne "1.0" -or [string]$baseline.sourceCommit -ne $ExpectedSourceCommit) {
    throw "Renderer baseline-comparison evidence schema/source is invalid."
}
if ([string]$baseline.status -notin @("PASS", "UNVERIFIED")) {
    throw "Renderer baseline comparison must be PASS or UNVERIFIED."
}
if ([string]$baseline.status -eq "UNVERIFIED" -and @($baseline.comparison).Count -ne 0) {
    throw "UNVERIFIED renderer baseline comparison must not contain fabricated comparison results."
}

Write-Host "Renderer reference qualification verification PASS"
Write-Host "Source: $ExpectedSourceCommit"
Write-Host "Profile: $ExpectedProfile"
Write-Host "Baseline comparison: $($baseline.status)"
Write-Host "External professional output/genlock evidence remains UNVERIFIED in this qualification."
