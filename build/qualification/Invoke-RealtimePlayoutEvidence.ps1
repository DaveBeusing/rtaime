# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [ValidateSet("smoke", "reference")][string]$Profile = "smoke",
    [string]$OutputRoot = "artifacts/qualification/realtime-playout"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows -or -not [Environment]::Is64BitProcess) {
    throw "Real-time playout software qualification requires Windows x64."
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$descriptorPath = Join-Path $repo "qualification/performance/realtime-playout-matrix.json"
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
$selected = @($descriptor.executionProfiles | Where-Object name -CEQ $Profile)
if ($descriptor.schemaVersion -cne "1.0" -or
    $descriptor.qualification -cne "rtaime-realtime-playout-software-matrix" -or
    $descriptor.sdk -cne "10.0.401" -or $selected.Count -ne 1 -or
    $selected[0].requireLongSoak -eq $true -or
    (@($descriptor.cadence | ForEach-Object format) -join ",") -cne "1080p25,1080p50,1080p59.94") {
    throw "Unknown or unbounded real-time playout profile."
}
$source = (& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$') {
    throw "Complete source commit identity is required."
}
& git -C $repo diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw "Tracked source is not clean." }
$untracked = @(& git -C $repo ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0 -or $untracked.Count -ne 0) {
    throw "Untracked source prevents exact-source qualification."
}
if ((& dotnet --version).Trim() -cne "10.0.401" -or $LASTEXITCODE -ne 0) {
    throw "Pinned .NET SDK 10.0.401 is required."
}
$base = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else { [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot)) }
$root = Join-Path $base ([DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmss") +
    "-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($root)
$startedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
$stage = "INITIAL"
$previousReference = $env:RTAIME_REFERENCE_MEDIA_REGRESSION
$previousEncoder = $env:RTAIME_FFMPEG_PATH
$previousPath = $env:PATH

function Single-File {
    param([string]$Folder, [string]$Name)
    $matches = @(Get-ChildItem -LiteralPath $Folder -Filter $Name -File -Recurse -ErrorAction Stop)
    if ($matches.Count -ne 1) { throw "Expected exactly one $Name under $Folder." }
    return $matches[0].FullName
}
function Artifact {
    param([string]$Path)
    $actual = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $actual.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence reference escapes the run directory."
    }
    $file = Get-Item -LiteralPath $actual -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt 32MB) { throw "Missing or unbounded raw evidence." }
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($root, $actual).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
function Require-Trx {
    param([string]$Path, [string[]]$Required)
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $count = $trx.TestRun.ResultSummary.Counters
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ([int]$count.executed -lt $Required.Count -or
        [int]$count.executed -ne [int]$count.passed -or [int]$count.failed -ne 0 -or
        [int]$count.notExecuted -ne 0 -or $results.Count -ne [int]$count.executed) {
        throw "A required native/software guard was not executed successfully: $Path"
    }
    foreach ($name in $Required) {
        if (@($results | Where-Object { [string]$_.testName -like "*$name*" -and
                [string]$_.outcome -ceq "Passed" }).Count -lt 1) {
            throw "Required test $name is not evidenced by the Windows TRX."
        }
    }
}
function Read-Metric {
    param($Value, [string]$Name)
    if ($null -eq $Value -or -not [double]::IsFinite([double]$Value) -or
        [double]$Value -lt 0) { throw "Invalid producer metric $Name." }
    return [double]$Value
}

Push-Location $repo
try {
    $stage = "MANAGED_GC_SOURCE_PRODUCER"
    & ./build/qualification/Invoke-ManagedAllocationGcQualification.ps1 -Profile $selected[0].managedGc -OutputRoot (Join-Path $root "managed")
    $managedPath = Single-File (Join-Path $root "managed") "manifest.json"
    & ./build/qualification/Test-ManagedAllocationGcEvidence.ps1 -EvidencePath $managedPath -ExpectedSourceCommit $source
    $managed = Get-Content -LiteralPath $managedPath -Raw | ConvertFrom-Json
    $program = @()
    foreach ($format in @("1080p50", "1080p59.94")) {
        $cases = [Collections.Generic.List[object]]::new()
        foreach ($iteration in @($managed.runs)) {
            foreach ($report in @($iteration.rateReports)) {
                $raw = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $managedPath) $report.path) -Raw | ConvertFrom-Json
                foreach ($case in @($raw.profiles | Where-Object { $_.format -ceq $format })) {
                    $cases.Add($case)
                }
            }
        }
        if ($cases.Count -ne 8 * @($managed.runs).Count) {
            throw "Actual managed Program consumer cases are missing for $format."
        }
        $p99 = @($cases | ForEach-Object { Read-Metric $_.p99Milliseconds "CPU P99" })
        $alloc = @($cases | ForEach-Object {
            Read-Metric $_.allocation.measuredThreadBytesPerBoundary "thread allocations"
        })
        $budget = if ($format -eq "1080p50") { 20.0 } else { 1001.0 / 60.0 }
        $program += [ordered]@{
            format = $format; consumerCases = $cases.Count
            samplesPerCase = [int]$descriptor.sourceScenarios.Count * 0 + 12
            frameBudgetMilliseconds = $budget
            maximumObservedCpuP99Milliseconds = ($p99 | Measure-Object -Maximum).Maximum
            maximumObservedCallerThreadBytesPerBoundary = ($alloc | Measure-Object -Maximum).Maximum
            sampledCpuP99ExceedsNominalFramePeriod = (($p99 | Measure-Object -Maximum).Maximum -gt $budget)
            measurementScope = "MANAGED_REFERENCE_CPU_WALL_NOT_PHYSICAL_SCANOUT"
            numericalRegressionApproval = "UNVERIFIED_NO_APPROVED_MANAGED_THRESHOLD"
        }
    }

    $stage = "PINNED_DISTINCT_SOURCE_ENCODER"
    & ./build/testmedia/Install-ReferenceMediaEncoder.ps1
    if ($LASTEXITCODE -ne 0) { throw "Reference media encoder installer failed." }
    $env:RTAIME_REFERENCE_MEDIA_REGRESSION = "1"
    $sequenceDir = Join-Path $root "distinct-sequence"
    [void][IO.Directory]::CreateDirectory($sequenceDir)
    $filter = "FullyQualifiedName~ReferenceSequenceDistinctSourcesOutputTests|FullyQualifiedName~SequenceNativeProgramOutputIntegrationTests|FullyQualifiedName~RuntimeMemoryBoundednessTests|FullyQualifiedName~RuntimeResourceCapacityAdmissionTests"
    $stage = "DISTINCT_NATIVE_PREVIEW_PROGRAM_AND_RESOURCE_REGRESSIONS"
    & dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build -m:1 --filter $filter --logger "trx;LogFileName=sequence.trx" --results-directory $sequenceDir
    if ($LASTEXITCODE -ne 0) { throw "Real native sequence and resource tests failed." }
    $seqTrx = Join-Path $sequenceDir "sequence.trx"
    Require-Trx $seqTrx @("Two_distinct_native_colors_and_tones_reach_monitoring_Preview_and_committed_Program_PCM",
        "Two_native_video_and_two_audio_tracks_reach_committed_program_pixel_and_pcm_sink",
        "RuntimeHost_observations_and_recent_snapshot_cost_remain_bounded",
        "Uhd_and_physical_output_are_not_inferred_from_current_HD_or_software_evidence")

    $stage = "MONITORING_VISUAL_RESOURCE_GUARDS"
    $operatorDir = Join-Path $root "monitoring"
    [void][IO.Directory]::CreateDirectory($operatorDir)
    & dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build -m:1 --filter "FullyQualifiedName~MonitoringVisualPerformanceQualificationTests" --logger "trx;LogFileName=monitoring.trx" --results-directory $operatorDir
    if ($LASTEXITCODE -ne 0) { throw "Monitoring resource tests failed." }
    $monitorTrx = Join-Path $operatorDir "monitoring.trx"
    Require-Trx $monitorTrx @("Steady_state_geometry_calculation_has_a_bounded_managed_allocation_budget",
        "Sustained_resize_and_dpi_churn_does_not_create_unbounded_target_revisions")

    $decks = @()
    foreach ($format in @("1080p50", "1080p59.94")) {
        $stage = "TWO_DECK_" + $format
        $deckRoot = Join-Path $root ("deck-" + $format)
        & ./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile $selected[0].ssd -Format $format -NoBuild -OutputRoot $deckRoot
        $outcome = Single-File $deckRoot "ssd-playback.outcome.json"
        $inventory = Single-File $deckRoot "environment.json"
        $manifest = Single-File $deckRoot "manifest.json"
        & ./build/qualification/Test-SsdPlaybackEvidence.ps1 -EvidencePath $outcome -ExpectedSourceCommit $source -ExpectedProfile $selected[0].ssd -ExpectedFormat $format
        & ./build/qualification/Test-SsdPlaybackEnvironment.ps1 -EnvironmentPath $inventory -ExpectedSourceCommit $source -OutcomePath $outcome
        $native = Get-Content -LiteralPath $outcome -Raw | ConvertFrom-Json
        $decks += [ordered]@{
            format = $format
            status = if ([ulong]$native.result.missedDelta -gt 0) { "OBSERVED_PROGRAM_MISSES" } else { "SOFTWARE_CAPTURED_TIMING_NOT_PHYSICALLY_QUALIFIED" }
            media = @($native.result.media)
            elapsedSeconds = Read-Metric $native.result.elapsedSeconds "elapsed seconds"
            missedProgramSlots = [ulong]$native.result.missedDelta
            decodedFramesA = [ulong]$native.result.firstPlayback.DecodedFrames
            decodedFramesB = [ulong]$native.result.secondPlayback.DecodedFrames
            privateByteGrowth = [long]$native.result.privateGrowth
            handleGrowth = [int]$native.result.handleGrowth
            cleanup = [string]$native.cleanup
            outcome = Artifact $outcome
            environment = Artifact $inventory
            manifest = Artifact $manifest
        }
    }

    $stage = "STAGED_MIXED_RECORDING_RECOVERY"
    $mixedRoot = Join-Path $root "mixed"
    & ./build/qualification/Invoke-SustainedMixedLoadQualification.ps1 -Profile $selected[0].mixed -NoBuild -OutputRoot $mixedRoot
    $mixedEvidence = Single-File $mixedRoot "summary.json"
    $mixedManifest = Single-File $mixedRoot "manifest.json"
    & ./build/qualification/Test-SustainedMixedLoadEvidence.ps1 -EvidencePath $mixedEvidence -DescriptorPath "qualification/soak/sustained-mixed-load.json" -ExpectedSourceCommit $source -ExpectedProfile $selected[0].mixed
    $mixed = Get-Content -LiteralPath $mixedEvidence -Raw | ConvertFrom-Json

    $stage = "BUILD_BOUND_SOFTWARE_EVIDENCE"
    $result = [ordered]@{
        schemaVersion = "1.0"
        qualification = "rtaime-realtime-playout-software-matrix"
        sourceCommit = $source
        buildCommit = $source
        sdk = "10.0.401"
        profile = $Profile
        descriptorSha256 = (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash.ToLowerInvariant()
        startedAtUtc = $startedAtUtc
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        runId = $env:GITHUB_RUN_ID
        runAttempt = $env:GITHUB_RUN_ATTEMPT
        runner = [ordered]@{
            machine = [Environment]::MachineName
            os = [Environment]::OSVersion.VersionString
            architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            logicalProcessors = [Environment]::ProcessorCount
        }
        completion = "SOFTWARE_COLLECTION_VERIFIED_NOT_REALTIME_APPROVAL"
        program = $program
        managedManifest = Artifact $managedPath
        distinctNativeSequence = [ordered]@{
            status = "SOURCE_BOUND_INTEGRATION_ASSERTIONS"
            trx = Artifact $seqTrx
            actualSourceFormat = "1080p50"
            independentToneFrequenciesHz = @(440, 880)
        }
        monitoring = [ordered]@{
            status = "GEOMETRY_AND_ALLOCATION_ASSERTIONS_NOT_DISPATCHER_P99"
            trx = Artifact $monitorTrx
        }
        dualDeck = $decks
        stagedMixed = [ordered]@{
            status = "STAGED_SOFTWARE_SESSIONS_NOT_ONE_CONTINUOUS_PROGRAM"
            frames = [ulong]$mixed.frames
            recoveries = [ulong]$mixed.recoveries
            p99UpperBinMilliseconds = Read-Metric $mixed.latency.p99 "mixed load P99 upper bin"
            evidence = Artifact $mixedEvidence
            manifest = Artifact $mixedManifest
        }
        actualNative25Fps = "UNVERIFIED"
        gpuDriverVram = "UNVERIFIED"
        physicalStorageLatencyAndCache = "UNVERIFIED"
        physicalSdiNdiGenlock = "UNVERIFIED"
        physicalAvSyncAndEndToEnd = "UNVERIFIED"
        physicalDeviceLoss = "UNVERIFIED"
        continuousNativeMultitrackSoak = "UNVERIFIED"
        allPhysicalAcceptance = "UNVERIFIED"
        nextAction = "Run approved exact-device SSD, CUDA, Media I/O and timing-reference workflows; retain raw hashes and physical A/V measurements."
    }
    $pending = Join-Path $root "manifest.pending.json"
    $result | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $pending -Encoding utf8
    $stage = "REVERIFY_AGGREGATED_ARTIFACTS"
    & ./build/qualification/Test-RealtimePlayoutEvidence.ps1 -EvidencePath $pending -ExpectedSourceCommit $source
    Move-Item -LiteralPath $pending -Destination (Join-Path $root "manifest.json") -ErrorAction Stop
    Write-Host "Real-time playout software collection verified for exact source $source."
    Write-Host "Program budget comparisons are observed diagnostics, not an approved latency regression policy."
    Write-Host "Physical GPU, SDI/NDI, storage latency, AV synchronization and continuous native long soak remain UNVERIFIED."
    Write-Host "Evidence: $root"
} catch {
    $failed = [ordered]@{
        schemaVersion = "1.0"; qualification = "rtaime-realtime-playout-software-matrix"
        sourceCommit = $source; profile = $Profile; completion = "FAILED"
        failedStage = $stage; failure = $_.Exception.Message
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        allPhysicalAcceptance = "UNVERIFIED"
    }
    $failed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root "failure.json") -Encoding utf8
    throw
} finally {
    $env:RTAIME_REFERENCE_MEDIA_REGRESSION = $previousReference
    $env:RTAIME_FFMPEG_PATH = $previousEncoder
    $env:PATH = $previousPath
    Pop-Location
}
