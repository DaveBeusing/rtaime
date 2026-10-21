# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [string]$DescriptorPath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [string]$BaselineEvidencePath,
    [string]$ApprovedPolicyPath,
    [string]$ComparisonOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
if ([string]::IsNullOrWhiteSpace($DescriptorPath)) {
    $DescriptorPath = Join-Path $repositoryRoot "qualification/performance/managed-allocation-gc.json"
}
if ($ExpectedSourceCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw "Exact lowercase 40-character expected source commit is required."
}

function Require {
    param([bool]$Condition, [string]$Detail)
    if (-not $Condition) { throw $Detail }
}

function Read-BoundedJson {
    param([string]$Path, [long]$MaximumBytes = 16777216)
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    Require ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) "Missing, empty or oversized evidence: $Path"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 64 -ErrorAction Stop
}

function File-Sha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Finite {
    param($Value, [string]$Label)
    Require ($null -ne $Value) "Missing numeric observation: $Label"
    $converted = [double]$Value
    Require ([double]::IsFinite($converted) -and $converted -ge 0) "Nonfinite or negative observation: $Label"
    return $converted
}

function Read-BoundArtifact {
    param([string]$Root, $Reference, [string]$Suffix)
    $name = [string]$Reference.path
    Require ($name -cmatch '^run-[0-9]{2}/[a-zA-Z0-9._-]+$' -and $name.EndsWith($Suffix, [StringComparison]::Ordinal)) "Invalid evidence artifact reference."
    $full = [IO.Path]::GetFullPath((Join-Path $Root $name))
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Require ($full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) "Evidence artifact escapes its run root."
    Require ([string]$Reference.sha256 -cmatch '^[0-9a-f]{64}$') "Artifact SHA-256 is missing."
    Require ((File-Sha $full) -ceq [string]$Reference.sha256) "Evidence artifact hash mismatch: $name"
    return $full
}

function Verify-Trx {
    param([string]$Path, [int]$MinimumExecuted = 1)
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    Require ([int]$counts.executed -ge $MinimumExecuted -and
        [int]$counts.passed -eq [int]$counts.executed -and
        [int]$counts.failed -eq 0 -and [int]$counts.notExecuted -eq 0) "Test process did not pass all expected tests: $Path"
}

$descriptor = Read-BoundedJson $DescriptorPath
Require ([string]$descriptor.schemaVersion -ceq "1.0" -and
    [string]$descriptor.qualification -ceq "rtaime-managed-allocation-gc-software" -and
    [string]$descriptor.sdk -ceq "10.0.401" -and
    [int]$descriptor.warmupBoundaries -eq 3 -and
    [int]$descriptor.measuredBoundaries -eq 12 -and
    [int]$descriptor.casesPerFormat -eq 8) "Unknown or incompatible source producer measurement contract."
Require ((@($descriptor.formats) -join ",") -ceq "1080p50,1080p59.94") "The two required Program rates are missing."
Require ((@($descriptor.consumerDimensions) -join ",") -ceq
    "monitoringEnabled,recordingEnabled,outputCopyEnabled") "The source consumer dimensions changed."
$descriptorHash = File-Sha $DescriptorPath

function Verify-Evidence {
    param([string]$Path, [string]$ExpectedSha)
    $manifest = Read-BoundedJson $Path 4194304
    $root = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    Require ([string]$manifest.schemaVersion -ceq "1.0" -and
        [string]$manifest.qualification -ceq "rtaime-managed-allocation-gc-software" -and
        [string]$manifest.completion -ceq "SOFTWARE_PRODUCER_PASS" -and
        [string]$manifest.scope -ceq [string]$descriptor.sourceScope) "Unrecognized or incomplete software evidence manifest."
    Require ([string]$manifest.sourceCommit -ceq $ExpectedSha -and
        [string]$manifest.buildCommit -ceq $ExpectedSha -and
        [string]$manifest.sdk -ceq "10.0.401" -and
        [string]$manifest.descriptorSha256 -ceq $descriptorHash) "Evidence source, build, SDK or descriptor fingerprint differs."
    $selected = @($descriptor.profiles | Where-Object name -EQ ([string]$manifest.profile))
    Require ($selected.Count -eq 1) "Unknown or duplicated workload profile."
    Require (@($manifest.runs).Count -eq [int]$selected[0].repetitions) "Missing or extra independent producer runs."
    Require ([string]$manifest.runner.machineName -ne "" -and
        [string]$manifest.runner.architecture -ceq "X64" -and
        [string]$manifest.runner.os -match "Windows" -and
        [string]$manifest.runner.cpu -ne "" -and
        [int]$manifest.runner.logicalProcessors -gt 0 -and
        [string]$manifest.runner.runnerId -ne "") "Runner platform identity is missing or unsupported."
    Require ([string]$manifest.eventSampleCompleteness -ceq "UNVERIFIED" -and
        [string]$manifest.physicalOutput -ceq "UNVERIFIED" -and
        [string]$manifest.hardwareGpu -ceq "UNVERIFIED" -and
        [string]$manifest.operatorUiLatency -ceq "UNVERIFIED") "Software evidence falsely claims complete ETW, physical or UI output."
    Require ([string]$manifest.replayAndNetwork -ceq "UNVERIFIED" -and
        [string]$manifest.crossProductLayersAndTransitions -ceq "UNVERIFIED") "Unmeasured workload combinations were incorrectly qualified."

    $scenarioResults = [Collections.Generic.List[object]]::new()
    $producerInstances = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $iteration = 0
    foreach ($run in @($manifest.runs)) {
        $iteration++
        Require ([int]$run.iteration -eq $iteration -and [string]$run.result -ceq "PASSED") "Producer iteration missing or not successful."
        $producerPath = Read-BoundArtifact $root $run.producer ".json"
        $producer = Read-BoundedJson $producerPath 1048576
        Require ([string]$producer.schemaVersion -ceq "1.0" -and
            [string]$producer.qualification -ceq [string]$manifest.qualification -and
            [string]$producer.sourceCommit -ceq $ExpectedSha -and
            [string]$producer.buildCommit -ceq $ExpectedSha -and
            [string]$producer.sdk -ceq "10.0.401" -and
            [string]$producer.producer -ceq "ProgramPipelineMeasurementBaselineTests" -and
            [int]$producer.testhostProcessId -gt 0 -and
            [int]$producer.currentThreadId -gt 0 -and
            [string]$producer.gcEventSamples -ceq "UNVERIFIED" -and
            [string]$producer.observer -ceq "NONINVASIVE_GC_COUNTER_SNAPSHOTS") "Invalid producer PID/identity or unsupported GC event claim."
        Require ([string]$producer.os -ceq [string]$manifest.runner.os -and
            [string]$producer.cpu -ceq [string]$manifest.runner.cpu -and
            [string]$producer.machineName -ceq [string]$manifest.runner.machineName -and
            [string]$producer.processArchitecture -ceq [string]$manifest.runner.architecture -and
            [int]$producer.logicalProcessors -eq [int]$manifest.runner.logicalProcessors) "Producer executed on an inconsistent runner."
        $null = Finite $producer.processPrivateBytesAfterSessions "process private bytes"
        $null = Finite $producer.processHandlesAfterSessions "process handles"
        $observedStart = [DateTimeOffset]::Parse([string]$producer.startedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture)
        $observedEnd = [DateTimeOffset]::Parse([string]$producer.completedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture)
        Require ($observedStart -le $observedEnd -and
            $producerInstances.Add(("$($producer.testhostProcessId)/$($observedStart.ToUnixTimeMilliseconds())"))) "Producer testhost instances were duplicated or have invalid time provenance."
        $null = Finite $producer.lastGcHeapBytes "last GC heap snapshot"
        $null = Finite $producer.lastGcFragmentedBytes "last GC fragmentation"

        $trxPath = Read-BoundArtifact $root $run.trx ".trx"
        Verify-Trx $trxPath
        Require (@($run.rateReports).Count -eq 2) "Exactly two format reports are required."
        for ($formatIndex = 0; $formatIndex -lt 2; $formatIndex++) {
            $expectedFormat = @("1080p50", "1080p59.94")[$formatIndex]
            $ref = @($run.rateReports)[$formatIndex]
            Require ([string]$ref.format -ceq $expectedFormat) "Format report order or identity differs."
            $reportPath = Read-BoundArtifact $root $ref ".json"
            $report = Read-BoundedJson $reportPath
            Require ([string]$report.schemaVersion -ceq "1.0" -and
                [string]$report.qualification -ceq "program-pipeline-software-baseline" -and
                [string]$report.sourceCommit -ceq $ExpectedSha -and
                [string]$report.buildCommit -ceq $ExpectedSha -and
                [string]$report.sdk -ceq "10.0.401" -and
                [string]$report.gpu -ceq "ManagedReferenceGpuBackend" -and
                [string]$report.physicalGpuAndOutput -ceq "UNVERIFIED") "Producer report source or runtime identity is incompatible."
            Require (@($report.profiles).Count -eq 8) "A Program workload is missing a consumer-mask case."
            $masks = [Collections.Generic.HashSet[int]]::new()
            foreach ($case in @($report.profiles)) {
                Require ([string]$case.format -ceq $expectedFormat -and
                    [string]$case.measurementKind -ceq "CPU_WALL" -and
                    [string]$case.boundaryScope -ceq
                    "V1_SERVICE_AND_OPTIONAL_SYNTHETIC_OUTPUT_COPY" -and
                    [int]$case.warmupBoundaries -eq 3 -and [int]$case.samples -eq 12) "Wrong Program sample scope, format, warmup or sample count."
                $mask = ([int][bool]$case.monitoringEnabled) +
                    (2 * [int][bool]$case.recordingEnabled) +
                    (4 * [int][bool]$case.outputCopyEnabled)
                Require ($masks.Add($mask)) "A Program consumer combination was reported twice."
                $p50 = Finite $case.p50Milliseconds "Program P50"
                $p95 = Finite $case.p95Milliseconds "Program P95"
                $p99 = Finite $case.p99Milliseconds "Program P99"
                $maximum = Finite $case.maximumMilliseconds "Program maximum latency"
                Require ($p50 -le $p95 -and $p95 -le $p99 -and $p99 -le $maximum) "Nonmonotonic Program latency quantiles."
                $threadBytes = Finite $case.allocation.measuredThreadBytes "Producer-thread allocated bytes"
                $processBytes = Finite $case.allocation.approximateProcessBytes "Approximate process allocated bytes"
                $threadPerBoundary = Finite $case.allocation.measuredThreadBytesPerBoundary "Producer-thread allocated bytes/frame"
                $processPerBoundary = Finite $case.allocation.approximateProcessBytesPerBoundary "Approximate process allocated bytes/frame"
                Require ([int]$case.allocation.measuredThread -gt 0 -and
                    [bool]$case.allocation.processTotalIncludesAsynchronousConsumers -and
                    [Math]::Abs($threadPerBoundary - $threadBytes / 12) -le 0.01 -and
                    [Math]::Abs($processPerBoundary - $processBytes / 12) -le 0.01) "Allocated bytes/frame do not match their own observed sample counts."
                $null = Finite $case.garbageCollection.gen0Collections "Gen0 collection count"
                $null = Finite $case.garbageCollection.gen1Collections "Gen1 collection count"
                $null = Finite $case.garbageCollection.gen2Collections "Gen2 collection count"
                $null = Finite $case.garbageCollection.pauseMilliseconds "GC pause"
                $null = Finite $case.garbageCollection.heapSizeBytesAtLastGc "Last-GC heap bytes"
                $null = Finite $case.garbageCollection.fragmentedBytesAtLastGc "Last-GC fragmented bytes"
                if ($null -ne $case.garbageCollection.largeObjectHeapBytesAtLastGc) {
                    $null = Finite $case.garbageCollection.largeObjectHeapBytesAtLastGc "LOH at last GC"
                }
                if ($null -ne $case.garbageCollection.pinnedObjectHeapBytesAtLastGc) {
                    $null = Finite $case.garbageCollection.pinnedObjectHeapBytesAtLastGc "POH at last GC"
                }
                Require ([int]$case.queues.recording.MaximumQueueDepth -le
                    [int]$case.queues.recording.QueueCapacity -and
                    [int]$case.queues.monitoringQueues.QueueDepth -le
                    [int]$case.queues.monitoringQueues.QueueCapacity -and
                    [int]$case.transfers.ReadbackPool.ActiveBuffers -eq 0 -and
                    [int]$case.transfers.MonitoringResources.ActiveResources -eq 0 -and
                    [string]$case.physicalOutput -ceq "UNVERIFIED" -and
                    [string]$case.gpuEventTiming -ceq "UNVERIFIED") "Readback, monitoring or recording leases were retained or hardware status overstated."
                Require ([ulong]$case.transfers.HostToDeviceBytes -eq 0 -and
                    [ulong]$case.transfers.DeviceToHostBytes -eq 0 -and
                    [ulong]$case.transfers.ReadbackOperations -gt 0) "Managed-reference readback transfer domain is inconsistent."

                $scenarioResults.Add([pscustomobject]@{
                    key = "$iteration/$expectedFormat/$mask"
                    p99Milliseconds = $p99
                    threadBytesPerFrame = $threadPerBoundary
                    processBytesPerFrame = $processPerBoundary
                    gcPauseMilliseconds = [double]$case.garbageCollection.pauseMilliseconds
                    processId = [int]$producer.testhostProcessId
                })
            }
            Require ($masks.Count -eq 8 -and (@(0..7 | Where-Object { -not $masks.Contains($_) })).Count -eq 0) "Some Program consumer masks were not exercised."
        }
    }
    Require ($scenarioResults.Count -eq (16 * [int]$selected[0].repetitions)) "The complete rate/consumer workload matrix was not observed."
    if ([string]$manifest.scalarGuard.status -ceq "PASSED") {
        Verify-Trx (Read-BoundArtifact $root $manifest.scalarGuard.trx ".trx") 1
    } else { throw "The scalar UI telemetry guard is not qualified." }
    if ([string]$manifest.compositorGuard.status -ceq "PASSED") {
        Verify-Trx (Read-BoundArtifact $root $manifest.compositorGuard.trx ".trx") 1
    } else { throw "Compositor factor regressions are missing." }
    if ([bool]$selected[0].includeNativeDiagnostic) {
        Require ([string]$manifest.nativeDecoder.status -ceq "PASSED_ASSERTION_ONLY") "Native diagnostic test was required but missing."
        Verify-Trx (Read-BoundArtifact $root $manifest.nativeDecoder.trx ".trx") 2
    } else {
        Require ([string]$manifest.nativeDecoder.status -ceq "UNVERIFIED") "Optional native diagnostics were fabricated."
    }
    return [pscustomobject]@{ manifest = $manifest; scenarios = $scenarioResults.ToArray() }
}

$candidate = Verify-Evidence $EvidencePath $ExpectedSourceCommit
$comparison = [ordered]@{
    schemaVersion = "1.0"
    qualification = "rtaime-managed-allocation-gc-software-comparison"
    sourceCommit = $ExpectedSourceCommit
    descriptorSha256 = $descriptorHash
    status = "UNVERIFIED"
    reason = "NO_APPROVED_BASELINE"
    baselineCommit = $null
    approvedPolicySha256 = $null
    deltas = @()
}
if (-not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
    $baselineDoc = Read-BoundedJson $BaselineEvidencePath 4194304
    $baselineSource = [string]$baselineDoc.sourceCommit
    Require ($baselineSource -cmatch '^[0-9a-f]{40}$') "Baseline must contain an exact source SHA."
    $baseline = Verify-Evidence $BaselineEvidencePath $baselineSource
    Require ([string]$baseline.manifest.profile -ceq [string]$candidate.manifest.profile -and
        [string]$baseline.manifest.runner.runnerId -ceq [string]$candidate.manifest.runner.runnerId -and
        [string]$baseline.manifest.runner.machineName -ceq [string]$candidate.manifest.runner.machineName -and
        [string]$baseline.manifest.runner.os -ceq [string]$candidate.manifest.runner.os -and
        [string]$baseline.manifest.runner.cpu -ceq [string]$candidate.manifest.runner.cpu -and
        [int]$baseline.manifest.runner.logicalProcessors -eq [int]$candidate.manifest.runner.logicalProcessors -and
        [string]$baseline.manifest.runner.architecture -ceq [string]$candidate.manifest.runner.architecture) "Baseline is not directly comparable: profile or physical runner differs."

    $comparison.baselineCommit = $baselineSource
    $comparison.reason = "NO_APPROVED_MANAGED_REGRESSION_THRESHOLD"
    $deltas = [Collections.Generic.List[object]]::new()
    $before = @{}
    foreach ($entry in $baseline.scenarios) { $before[$entry.key] = $entry }
    foreach ($entry in $candidate.scenarios) {
        Require ($before.ContainsKey($entry.key)) "The baseline scenario matrix is incomplete."
        $prior = $before[$entry.key]
        $deltas.Add([pscustomobject]@{
            key = $entry.key
            baselineP99Ms = $prior.p99Milliseconds
            currentP99Ms = $entry.p99Milliseconds
            p99DeltaPercent = if ($prior.p99Milliseconds -eq 0) { $null } else {
                100 * ($entry.p99Milliseconds - $prior.p99Milliseconds) / $prior.p99Milliseconds
            }
            baselineThreadBytesPerFrame = $prior.threadBytesPerFrame
            currentThreadBytesPerFrame = $entry.threadBytesPerFrame
            threadByteDeltaPercent = if ($prior.threadBytesPerFrame -eq 0) { $null } else {
                100 * ($entry.threadBytesPerFrame - $prior.threadBytesPerFrame) / $prior.threadBytesPerFrame
            }
            baselineProcessBytesPerFrame = $prior.processBytesPerFrame
            currentProcessBytesPerFrame = $entry.processBytesPerFrame
        })
    }
    $comparison.deltas = $deltas.ToArray()

    if (-not [string]::IsNullOrWhiteSpace($ApprovedPolicyPath)) {
        $policy = Read-BoundedJson $ApprovedPolicyPath 65536
        Require ([string]$policy.schemaVersion -ceq "1.0" -and
            [string]$policy.qualification -ceq "rtaime-managed-allocation-gc-regression-policy" -and
            [string]$policy.descriptorSha256 -ceq $descriptorHash -and
            [string]$policy.runnerId -ceq [string]$candidate.manifest.runner.runnerId -and
            [string]$policy.status -ceq "APPROVED") "Regression thresholds require a separately approved exact-profile and runner policy."
        $p99Percent = Finite $policy.maximumP99IncreasePercent "Approved P99 regression threshold"
        $allocationPercent = Finite $policy.maximumThreadAllocationIncreasePercent "Approved allocation regression threshold"
        Require ($p99Percent -le 100 -and $allocationPercent -le 100) "Approved regression percentages are out of bounds."
        $comparison.approvedPolicySha256 = File-Sha $ApprovedPolicyPath
        $comparison.status = "PASS"
        $comparison.reason = "APPROVED_COMPARABLE_THRESHOLD_MET"
        foreach ($delta in $comparison.deltas) {
            $prior = $before[$delta.key]
            if ($delta.currentP99Ms -gt $prior.p99Milliseconds * (1 + $p99Percent / 100) -or
                $delta.currentThreadBytesPerFrame -gt $prior.threadBytesPerFrame * (1 + $allocationPercent / 100)) {
                $comparison.status = "FAIL"
                $comparison.reason = "APPROVED_P99_OR_MANAGED_ALLOCATION_REGRESSION"
                break
            }
        }
    }
} elseif (-not [string]::IsNullOrWhiteSpace($ApprovedPolicyPath)) {
    throw "An approved numerical regression policy requires a verified comparable baseline."
}

if (-not [string]::IsNullOrWhiteSpace($ComparisonOutputPath)) {
    $comparison | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $ComparisonOutputPath -Encoding utf8
}
if ($comparison.status -eq "FAIL") { throw "Approved source-bound P99 or allocation regression exceeded its budget." }
Write-Host "Managed allocation/GC producer evidence PASS; numerical baseline status: $($comparison.status)."
Write-Host "GPU hardware, native device transfers, sampled ETW completeness and physical output: UNVERIFIED."
