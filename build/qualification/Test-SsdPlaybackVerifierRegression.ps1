# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-ssd-verifier-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($root)
$path = Join-Path $root "fixture.json"
$sha = "a" * 40
$now = [DateTimeOffset]::UtcNow
$read = @{ Calls = 1000; MaximumMilliseconds = 4; Latency = @{ Count = 1000; Capacity = 17
    PercentileMethod = "WHOLE_RUN_NEAREST_RANK_EXPONENTIAL_UPPER_BOUND"; P50 = 1; P95 = 2; P99 = 4 } }
$playback = @{ DecodedFrames = 1000; ReadFailures = 0; VideoTimestampRegressions = 0; AudioTimestampDiscontinuities = 0
    Decode = $read; NativeVideoRead = $read; NativeAudioRead = $read; Seek = $read }
$queue = @{ Capacity = 8; QueuedFrames = 4; Failure = $null }
$base = @{
    schemaVersion = "1.0"; sourceCommit = $sha; buildCommit = $sha; profile = "8h"; capturedAt = $now.ToString("O")
    status = "PASS"; cleanup = "PASS"; failure = $null; cleanupFailure = $null
    result = @{
        schemaVersion = "1.0"; qualification = "rtaime-ssd-playback"; sdk = "10.0.401"
        sourceCommit = $sha; buildCommit = $sha; profile = "8h"
        startedAt = $now.AddHours(-8).ToString("O"); elapsedSeconds = 28800; requestedSeconds = 28800
        capacity = 2; resourceCapacity = 128; startSequence = 10; finalSequence = 1000; takes = 20
        media = @(@{ sha256 = "b"*64; bytes = 1000 },@{ sha256 = "c"*64; bytes = 1000 })
        deckProbes = @(@{ videoCodec = "H264" },@{ videoCodec = "H264" })
        samples = @(0..120 | ForEach-Object { @{ Milliseconds = [Math]::Min(28799000, $_ * 240000)
            Resources = @{ Capacity = 2; Reserved = 2 }; First = $queue; Second = $queue } })
        resourceWindows = @(1..7 | ForEach-Object { @{ Index = $_; Samples = 100; MeanPrivateBytes = 1000000; MeanHandles = 20 } })
        firstPlayback = $playback; secondPlayback = $playback; privateGrowth = 100; handleGrowth = 1
        resourcePlateau = "PASS"; startMissed = 2; missedDelta = 1; finalStats = @{ MissedSlots = 3 }
        softwareStatus = "PASS"; timingStatus = "FAIL"
        physicalStatus = "UNVERIFIED"; physicalOutputLoss = "UNVERIFIED"; gpuUtilization = "UNVERIFIED"
        vram = "UNVERIFIED"; physicalAvDrift = "UNVERIFIED"; fileOnlyReadLatency = "UNVERIFIED"
        decoderAcceleration = "UNVERIFIED"; cacheState = "UNVERIFIED"; pressure = "UNVERIFIED"
    }
}
function Copy-Fixture { $base | ConvertTo-Json -Depth 32 | ConvertFrom-Json -AsHashtable }
function Write-Fixture($value) { $value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding utf8 }
function Verify { & (Join-Path $PSScriptRoot "Test-SsdPlaybackEvidence.ps1") -EvidencePath $path -ExpectedSourceCommit $sha -ExpectedProfile "8h" }
$count = 0
function Rejected($value, [string]$reason) {
    Write-Fixture $value
    $rejected = $false
    try { Verify } catch { $rejected = $true }
    if (-not $rejected) { throw "SSD verifier accepted invalid synthetic fixture: $reason" }
    $script:count++
}
try {
    Write-Fixture $base; Verify
    $v = Copy-Fixture; $v.sourceCommit = "d"*40; Rejected $v "foreign source"
    $v = Copy-Fixture; $v.result.buildCommit = "d"*40; Rejected $v "foreign binary"
    $v = Copy-Fixture; $v.result.profile = "10m"; Rejected $v "profile substitution"
    $v = Copy-Fixture; $v.result.elapsedSeconds = 600; Rejected $v "short run"
    $v = Copy-Fixture; $v.capturedAt = $now.AddHours(-7).ToString("O"); Rejected $v "wall-time mismatch"
    $v = Copy-Fixture; $v.cleanup = "FAIL"; Rejected $v "unreleased decoder"
    $v = Copy-Fixture; $v.result.media[1].sha256 = ""; Rejected $v "missing media identity"
    $v = Copy-Fixture; $v.result.samples = @($v.result.samples[0]); Rejected $v "truncated resource coverage"
    $v = Copy-Fixture; $v.result.samples[0].First.QueuedFrames = 9; Rejected $v "unbounded queue"
    $v = Copy-Fixture; $v.result.firstPlayback.ReadFailures = 1; Rejected $v "read fault hidden"
    $v = Copy-Fixture; $v.result.firstPlayback.Decode.Latency.P99 = 0; Rejected $v "invalid quantile"
    $v = Copy-Fixture; $v.result.firstPlayback.Decode.Latency.Count = 2; Rejected $v "sample count mismatch"
    $v = Copy-Fixture; $v.result.timingStatus = "PASS"; Rejected $v "hidden Program misses"
    $v = Copy-Fixture; $v.result.physicalStatus = "PASS"; Rejected $v "false physical claim"
    $v = Copy-Fixture; $v.result.resourceWindows[6].MeanPrivateBytes = 1000000000; Rejected $v "hidden plateau growth"
    $v = Copy-Fixture; $v.result.resourceWindows[4].Index = 99; Rejected $v "missing hourly window"
    Write-Host "SSD verifier: 1 valid synthetic fixture and $count rejected fixtures PASS; no physical endurance was executed."
} finally {
    # Exact fresh temporary directory only.
    if ([IO.Path]::GetFileName($root) -notlike "rtaime-ssd-verifier-*") { throw "Unsafe temporary cleanup target." }
    Remove-Item -LiteralPath $root -Recurse -Force
}
