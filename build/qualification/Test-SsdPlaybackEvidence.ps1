# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidateSet("smoke","10m","60m","8h","24h","72h")][string]$ExpectedProfile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf) -or
    (Get-Item -LiteralPath $EvidencePath).Length -gt 256KB) { throw "Missing or oversized SSD playback evidence." }
$e = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
$r = $e.result
$required = switch ($ExpectedProfile) { "smoke" {30} "10m" {600} "60m" {3600} "8h" {28800} "24h" {86400} "72h" {259200} }
if ($e.schemaVersion -cne "1.0" -or $r.schemaVersion -cne "1.0" -or
    $r.qualification -cne "rtaime-ssd-playback" -or $r.sdk -cne "10.0.401") { throw "Unknown SSD evidence schema or SDK." }
foreach ($item in @($e,$r)) {
    if ($item.sourceCommit -cne $ExpectedSourceCommit -or $item.buildCommit -cne $ExpectedSourceCommit -or
        $item.profile -cne $ExpectedProfile) { throw "SSD playback source/build/profile mismatch." }
}
if ($e.status -cne "PASS" -or $e.cleanup -cne "PASS" -or $null -ne $e.failure -or $null -ne $e.cleanupFailure -or
    $r.softwareStatus -cne "COLLECTED") { throw "Playback or cleanup did not complete." }
$elapsed = [double]$r.elapsedSeconds
if (-not [double]::IsFinite($elapsed) -or $elapsed -lt $required -or $r.requestedSeconds -ne $required) {
    throw "Named duration was not exercised."
}
$start = [DateTimeOffset]::Parse([string]$r.startedAt)
$end = [DateTimeOffset]::Parse([string]$e.capturedAt)
if (($end - $start).TotalSeconds -lt $required -or
    ($end - $start).TotalSeconds -gt $elapsed + 120) { throw "Wall duration disagrees with playback." }
if ($r.capacity -ne 2 -or $r.resourceCapacity -ne 128 -or @($r.samples).Count -lt 1 -or
    @($r.samples).Count -gt 128 -or @($r.resourceWindows).Count -gt 96 -or
    [ulong]$r.finalSequence -le [ulong]$r.startSequence -or [int]$r.takes -le 0) { throw "Missing Program progress or invalid capacity." }
if (@($r.media).Count -ne 2 -or @($r.deckProbes).Count -ne 2) { throw "Both actual file/profile identities are required." }
foreach ($media in @($r.media)) {
    if ($media.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$media.bytes -le 0) { throw "Missing media hash/size." }
}
foreach ($probe in @($r.deckProbes)) { if ($null -eq $probe) { throw "Actual input profile was not probed." } }
$previous = -1.0
foreach ($sample in @($r.samples)) {
    $time = [double]$sample.Milliseconds
    if (-not [double]::IsFinite($time) -or $time -le $previous -or $time -lt 0 -or $time -gt ($elapsed * 1000) -or
        ($previous -ge 0 -and $time - $previous -gt [Math]::Max(2, $required / 60.0) * 1000) -or
        $sample.Resources.Capacity -ne 2 -or $sample.Resources.Reserved -ne 2) { throw "Invalid resource coverage or owners." }
    $previous = $time
    foreach ($deck in @($sample.First,$sample.Second)) {
        if ($deck.Capacity -ne 8 -or $deck.QueuedFrames -lt 0 -or $deck.QueuedFrames -gt 8 -or $null -ne $deck.Failure) {
            throw "Invalid bounded decoder queue or failure."
        }
    }
}
if ($previous / 1000 -lt $required - [Math]::Max(1, $required / 120.0) - 2) {
    throw "Final resource observations do not cover the requested duration."
}
foreach ($playback in @($r.firstPlayback,$r.secondPlayback)) {
    if ([ulong]$playback.DecodedFrames -le 200 -or [ulong]$playback.ReadFailures -ne 0 -or
        [ulong]$playback.VideoTimestampRegressions -ne 0 -or [ulong]$playback.AudioTimestampDiscontinuities -ne 0) {
        throw "Decode progress or continuity failed."
    }
    foreach ($name in @("Decode","NativeVideoRead","NativeAudioRead","Seek")) {
        $read = $playback.$name
        $hist = $read.Latency
        if ($hist.Capacity -ne 17 -or $hist.PercentileMethod -cne "WHOLE_RUN_NEAREST_RANK_EXPONENTIAL_UPPER_BOUND" -or
            [ulong]$hist.Count -ne [ulong]$read.Calls) { throw "Read histogram scope/count mismatch." }
        if ([ulong]$read.Calls -eq 0) {
            if ($null -ne $hist.P50 -or $null -ne $hist.P95 -or $null -ne $hist.P99) { throw "Empty histogram invented quantiles." }
            continue
        }
        $previousValue = -1.0
        foreach ($value in @($hist.P50,$hist.P95,$hist.P99,$read.MaximumMilliseconds)) {
            if ($null -eq $value -or -not [double]::IsFinite([double]$value) -or [double]$value -lt $previousValue) { throw "Invalid read quantiles." }
            $previousValue = [double]$value
        }
    }
}
if ([long]$r.privateGrowth -gt 536870912 -or [long]$r.handleGrowth -gt 64) { throw "Existing hosted resource budget exceeded." }
if ([ulong]$r.finalStats.MissedSlots -lt [ulong]$r.startMissed -or
    [ulong]$r.missedDelta -ne ([ulong]$r.finalStats.MissedSlots - [ulong]$r.startMissed)) { throw "Program miss counters do not reconcile." }
$timing = if ([ulong]$r.missedDelta -gt 0) { "FAIL" } else { "UNVERIFIED" }
if ($r.timingStatus -cne $timing) { throw "Program losses cannot be represented as qualified timing." }
foreach ($name in @("physicalStatus","physicalOutputLoss","gpuUtilization","vram","physicalAvDrift",
    "fileOnlyReadLatency","decoderAcceleration","cacheState","pressure")) {
    if ($r.$name -cne "UNVERIFIED") { throw "Software observation cannot qualify $name." }
}
if ($required -ge 28800) {
    $closed = [long][Math]::Floor($elapsed / 3600)
    $windows = @($r.resourceWindows | Where-Object { $_.Index -gt 0 -and $_.Index -lt $closed } | Sort-Object Index)
    if ($r.resourcePlateau -cne "PASS" -or $windows.Count -lt 3 -or
        $windows[0].Index -ne 1 -or $windows[-1].Index -ne $closed - 1) { throw "Missing post-warmup plateau." }
    for ($i = 0; $i -lt $windows.Count; $i++) {
        $window = $windows[$i]
        foreach ($value in @($window.MeanPrivateBytes,$window.MeanHandles)) {
            if (-not [double]::IsFinite([double]$value) -or [double]$value -lt 0) { throw "Invalid resource window." }
        }
        if ($window.Samples -lt 2 -or ($i -gt 0 -and $window.Index -ne $windows[$i-1].Index + 1) -or
            [double]$window.MeanPrivateBytes - [double]$windows[0].MeanPrivateBytes -gt 536870912 -or
            [double]$window.MeanHandles - [double]$windows[0].MeanHandles -gt 64) { throw "Resource plateau is not demonstrated." }
    }
}
Write-Host "SSD playback collection verification PASS ($ExpectedProfile); Program timing $timing; physical UNVERIFIED."
