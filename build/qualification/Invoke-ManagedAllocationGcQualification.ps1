# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [ValidateSet("smoke", "repeat")][string]$Profile = "smoke",
    [string]$OutputRoot = "artifacts/qualification/managed-allocation-gc",
    [string]$BaselineEvidencePath,
    [string]$ApprovedPolicyPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows -or -not [Environment]::Is64BitProcess) {
    throw "Managed allocation/GC qualification requires Windows x64. Other platforms remain UNVERIFIED."
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$descriptorPath = Join-Path $repositoryRoot "qualification/performance/managed-allocation-gc.json"
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
$selected = @($descriptor.profiles | Where-Object name -EQ $Profile)
if ([string]$descriptor.schemaVersion -ne "1.0" -or $selected.Count -ne 1 -or
    [int]$selected[0].repetitions -lt 1 -or [int]$selected[0].repetitions -gt 3) {
    throw "Unknown or unbounded managed allocation/GC workload."
}
$source = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$') {
    throw "A complete source SHA is required."
}
& git -C $repositoryRoot diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw "Source checkout has tracked changes. Commit before qualification." }
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -ne "10.0.401") { throw "Pinned .NET SDK 10.0.401 is required." }

$startedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
$output = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$runRoot = Join-Path $output ([DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmss") +
    "-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$performanceProject = "tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj"
$integrationProject = "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj"

function Artifact {
    param([string]$File)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "Missing expected qualification artifact: $File"
    }
    $relative = [IO.Path]::GetRelativePath($runRoot, $File).Replace('\', '/')
    return [ordered]@{
        path = $relative
        sha256 = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Assert-PassedTrx {
    param([string]$File, [int]$MinimumExecuted = 1)
    [xml]$trx = Get-Content -LiteralPath $File -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int]$counts.executed -lt $MinimumExecuted -or
        [int]$counts.failed -ne 0 -or [int]$counts.passed -ne [int]$counts.executed) {
        throw "Incomplete or failed Windows test result: $File"
    }
}

# Exact-source Release compilation is mandatory. Skipping it would claim that
# a previous binary belongs to the current repository SHA without proof.
Push-Location $repositoryRoot
$envNames = @(
    "RTAIME_SOURCE_COMMIT", "RTAIME_BUILD_COMMIT", "RTAIME_BASELINE_SDK",
    "RTAIME_PIPELINE_BASELINE_EVIDENCE", "RTAIME_MANAGED_GC_PRODUCER_DIR",
    "RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS"
)
$original = @{}
foreach ($name in $envNames) { $original[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
try {
    & ./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "The canonical Windows Release build failed." }
    & git -C $repositoryRoot diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw "Build modified tracked source. Evidence cannot be attributed to the original commit." }

    $env:RTAIME_SOURCE_COMMIT = $source
    $env:RTAIME_BUILD_COMMIT = $source
    $env:RTAIME_BASELINE_SDK = $sdk
    $descriptorHash = (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runs = [Collections.Generic.List[object]]::new()
    $runner = $null

    for ($i = 1; $i -le [int]$selected[0].repetitions; $i++) {
        $iterationDir = Join-Path $runRoot ("run-{0:D2}" -f $i)
        New-Item -ItemType Directory -Path $iterationDir | Out-Null
        $env:RTAIME_PIPELINE_BASELINE_EVIDENCE = $iterationDir
        $env:RTAIME_MANAGED_GC_PRODUCER_DIR = $iterationDir

        & dotnet test $performanceProject --configuration Release --no-build -m:1 --filter "FullyQualifiedName~ManagedAllocationGcEvidenceTests.Capture_existing_Program_producer_for_exact_source_profile" --logger "trx;LogFileName=program.trx" --results-directory $iterationDir
        if ($LASTEXITCODE -ne 0) { throw "Actual Program producer failed iteration $i." }
        $trx = Join-Path $iterationDir "program.trx"
        Assert-PassedTrx $trx

        $producerPath = Join-Path $iterationDir "producer-process.json"
        $process = Get-Content -LiteralPath $producerPath -Raw | ConvertFrom-Json
        if ([string]$process.sourceCommit -cne $source -or
            [int]$process.testhostProcessId -le 0 -or
            [string]$process.processArchitecture -ne "X64") {
            throw "Testhost identity differs from the actual source or platform."
        }
        $reportedRunnerId = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_NAME)) {
            $env:RUNNER_NAME
        } else { [string]$process.machineName }
        if ($null -eq $runner) {
            $runner = [ordered]@{
                runnerId = $reportedRunnerId
                machineName = [string]$process.machineName
                os = [string]$process.os
                cpu = [string]$process.cpu
                architecture = [string]$process.processArchitecture
                logicalProcessors = [int]$process.logicalProcessors
            }
        } elseif ($runner.machineName -cne [string]$process.machineName -or
            $runner.cpu -cne [string]$process.cpu -or
            $runner.os -cne [string]$process.os -or
            $runner.architecture -cne [string]$process.processArchitecture) {
            throw "Different producer machine detected inside one qualification."
        }

        $runs.Add([ordered]@{
            iteration = $i
            result = "PASSED"
            producer = Artifact $producerPath
            trx = Artifact $trx
            rateReports = @(
                ([ordered]@{ format = "1080p50"; path = (Artifact (Join-Path $iterationDir "program-load-False.json")).path; sha256 = (Artifact (Join-Path $iterationDir "program-load-False.json")).sha256 }),
                ([ordered]@{ format = "1080p59.94"; path = (Artifact (Join-Path $iterationDir "program-load-True.json")).path; sha256 = (Artifact (Join-Path $iterationDir "program-load-True.json")).sha256 })
            )
        })
    }

    $guardDir = Join-Path $runRoot "run-01"
    & dotnet test $performanceProject --configuration Release --no-build -m:1 --filter "FullyQualifiedName~RuntimePerformanceStatusBarPerformanceTests" --logger "trx;LogFileName=scalar-guard.trx" --results-directory $guardDir
    if ($LASTEXITCODE -ne 0) { throw "Scalar Program/Operator telemetry guard failed." }
    $scalarPath = Join-Path $guardDir "scalar-guard.trx"
    Assert-PassedTrx $scalarPath

    & dotnet test $performanceProject --configuration Release --no-build -m:1 --filter "FullyQualifiedName~GpuCompositorPerformanceProfileTests|FullyQualifiedName~OutputMappingSoftwarePerformanceTests|FullyQualifiedName~V1CombinedReferencePerformanceTests.Combined_reference_workload_remains_bounded_and_reports_tail_metrics" --logger "trx;LogFileName=compositor-guard.trx" --results-directory $guardDir
    if ($LASTEXITCODE -ne 0) { throw "Factored GPU-layer/transition correctness suite failed." }
    $compositorPath = Join-Path $guardDir "compositor-guard.trx"
    Assert-PassedTrx $compositorPath

    $native = [ordered]@{ status = "UNVERIFIED" }
    if ([bool]$selected[0].includeNativeDiagnostic) {
        $nativeDir = Join-Path $runRoot "run-01"
        $env:RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS = "1"
        $nativeLog = Join-Path $nativeDir "native-diagnostic.txt"
        & dotnet test $integrationProject --configuration Release --no-build -m:1 --filter "FullyQualifiedName~Native_decoder_copy_stages_report_source_bound_allocations_and_runtime_input_copy|FullyQualifiedName~Native_decode_worker_attributes_slot_copies_without_releasing_retained_frames" --logger "console;verbosity=detailed" --logger "trx;LogFileName=native.trx" --results-directory $nativeDir *>&1 |
            Tee-Object -FilePath $nativeLog
        if ($LASTEXITCODE -ne 0) { throw "Isolated native decoder/worker diagnostics failed." }
        $nativeTrx = Join-Path $nativeDir "native.trx"
        Assert-PassedTrx $nativeTrx 2
        $native = [ordered]@{
            status = "PASSED_ASSERTION_ONLY"
            trx = Artifact $nativeTrx
            log = Artifact $nativeLog
            producerScope = "ISOLATED_NATIVE_DECODER_AND_WORKER_NOT_LIVE_PROGRAM"
            numericalDecoderAndGcResults = "UNVERIFIED_UNTIL_LOG_REVIEW"
        }
    }

    $manifest = [ordered]@{
        schemaVersion = "1.0"
        qualification = "rtaime-managed-allocation-gc-software"
        completion = "SOFTWARE_PRODUCER_PASS"
        scope = [string]$descriptor.sourceScope
        sourceCommit = $source
        buildCommit = $source
        sdk = $sdk
        descriptorSha256 = $descriptorHash
        profile = $Profile
        startedAtUtc = $startedAtUtc
        runner = $runner
        harnessProcessId = $PID
        githubRunId = $env:GITHUB_RUN_ID
        githubRunAttempt = $env:GITHUB_RUN_ATTEMPT
        runs = $runs.ToArray()
        scalarGuard = [ordered]@{ status = "PASSED"; scope = "SCALAR_TELEMETRY_ASSERTION_ONLY"; trx = Artifact $scalarPath }
        compositorGuard = [ordered]@{ status = "PASSED"; scope = "FACTORED_SOFTWARE_PARITY_ONLY"; trx = Artifact $compositorPath }
        nativeDecoder = $native
        eventSampleCompleteness = "UNVERIFIED"
        physicalOutput = "UNVERIFIED"
        hardwareGpu = "UNVERIFIED"
        operatorUiLatency = "UNVERIFIED"
        replayAndNetwork = "UNVERIFIED"
        crossProductLayersAndTransitions = "UNVERIFIED"
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $manifestPath = Join-Path $runRoot "manifest.json"
    $manifest | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8

    $comparisonPath = Join-Path $runRoot "comparison.json"
    $verify = @{
        EvidencePath = $manifestPath
        DescriptorPath = $descriptorPath
        ExpectedSourceCommit = $source
        ComparisonOutputPath = $comparisonPath
    }
    if (-not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
        $verify.BaselineEvidencePath = [IO.Path]::GetFullPath($BaselineEvidencePath)
    }
    if (-not [string]::IsNullOrWhiteSpace($ApprovedPolicyPath)) {
        $verify.ApprovedPolicyPath = [IO.Path]::GetFullPath($ApprovedPolicyPath)
    }
    & ./build/qualification/Test-ManagedAllocationGcEvidence.ps1 @verify
    if (-not $?) { throw "Managed allocation/GC source-bound verification failed." }
    if ($Profile -eq "smoke") {
        & ./build/qualification/Test-ManagedAllocationGcVerifierRegressions.ps1 -EvidencePath $manifestPath -ExpectedSourceCommit $source -DescriptorPath $descriptorPath
        if (-not $?) { throw "Source-bound verifier negative regression qualification failed." }
    }
    # Presentation-only projection. Every numeric value is read back from a
    # verified producer artifact; manifest hashes remain the evidence authority.
    $observations = [Collections.Generic.List[object]]::new()
    foreach ($run in $runs) {
        foreach ($reference in $run.rateReports) {
            $rawPath = Join-Path $runRoot $reference.path
            $raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
            foreach ($case in @($raw.profiles)) {
                $observations.Add([ordered]@{
                    iteration = [int]$run.iteration
                    format = [string]$case.format
                    monitoringEnabled = [bool]$case.monitoringEnabled
                    recordingEnabled = [bool]$case.recordingEnabled
                    syntheticOutputCopyEnabled = [bool]$case.outputCopyEnabled
                    measuredBoundaries = [int]$case.samples
                    p50CpuWallMs = [double]$case.p50Milliseconds
                    p95CpuWallMs = [double]$case.p95Milliseconds
                    p99CpuWallMs = [double]$case.p99Milliseconds
                    maxCpuWallMs = [double]$case.maximumMilliseconds
                    producerThreadAllocatedBytesPerFrame = [double]$case.allocation.measuredThreadBytesPerBoundary
                    approximateProcessAllocatedBytesPerFrame = [double]$case.allocation.approximateProcessBytesPerBoundary
                    gen0 = [int]$case.garbageCollection.gen0Collections
                    gen1 = [int]$case.garbageCollection.gen1Collections
                    gen2 = [int]$case.garbageCollection.gen2Collections
                    gcPauseMs = [double]$case.garbageCollection.pauseMilliseconds
                    lohBytesAtLastGc = $case.garbageCollection.largeObjectHeapBytesAtLastGc
                    pohBytesAtLastGc = $case.garbageCollection.pinnedObjectHeapBytesAtLastGc
                    logicalReadbacks = [ulong]$case.transfers.ReadbackOperations
                    logicalReadbackBytes = [ulong]$case.transfers.ReadbackBytes
                    deviceToHostBytes = [ulong]$case.transfers.DeviceToHostBytes
                    activeReadbackBuffersAfterBoundary = [int]$case.transfers.ReadbackPool.ActiveBuffers
                })
            }
        }
    }
    $comparison = Get-Content -LiteralPath $comparisonPath -Raw | ConvertFrom-Json
    $summary = [ordered]@{
        schemaVersion = "1.0"
        qualification = "rtaime-managed-allocation-gc-derived-summary"
        numericalAuthority = "MANIFEST_HASHED_SOURCE_PRODUCER_REPORTS"
        sourceCommit = $source
        descriptorSha256 = $descriptorHash
        runner = $runner
        profile = $Profile
        caseCount = $observations.Count
        cases = $observations.ToArray()
        comparison = $comparison
        subsystems = @(
            [ordered]@{ name = "Program"; status = "MEASURED_SOFTWARE"; nextAction = "Compare a baseline from the same runner using approved profile-bound thresholds." },
            [ordered]@{ name = "GC_LOH_POH"; status = "COUNTERS_AND_LAST_GC_SNAPSHOTS"; nextAction = "Collect correlated complete ETW events on a qualified runner before claiming all object allocations." },
            [ordered]@{ name = "NativeDecoder"; status = [string]$native.status; nextAction = "Qualify real simultaneous A/B and SSD source playback separately." },
            [ordered]@{ name = "GpuReadback"; status = "LOGICAL_MANAGED_READBACK"; nextAction = "Run CUDA reference GPU transfer and fence evidence on identified hardware." },
            [ordered]@{ name = "OperatorUI"; status = "SCALAR_ASSERTION_ONLY"; nextAction = "Qualify actual WPF render and Dispatcher P99 with source-linked UI evidence." },
            [ordered]@{ name = "ReplayAndNetwork"; status = "UNVERIFIED"; nextAction = "Bind their actual consuming producers to a future independent workload profile." }
        )
        physicalOutput = "UNVERIFIED"
    }
    $summary | ConvertTo-Json -Depth 32 |
        Set-Content -LiteralPath (Join-Path $runRoot "subsystem-summary.json") -Encoding utf8
    Write-Host "Source-bound Program / GC software qualification complete: $runRoot"
    Write-Host "Physical hardware, complete ETW object allocations and full Operator UI latency remain UNVERIFIED."
}
finally {
    foreach ($name in $envNames) {
        [Environment]::SetEnvironmentVariable($name, $original[$name], "Process")
    }
    Pop-Location
}
