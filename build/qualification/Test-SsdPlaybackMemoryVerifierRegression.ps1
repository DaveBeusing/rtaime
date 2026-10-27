# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-memory-verifier-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($root)
$path = Join-Path $root "memory.json"
$sha = "a" * 40
$resource = @{
    OpenAttempts = 2; Opened = 2; FinalizeAttempts = 2
    Finalized = 2; AbortAttempts = 0; Aborted = 0; Active = 0
}
$origin = "b" * 40
$base = @{
    schemaVersion = "1.0"; diagnosticMode = "OBSERVATION_ONLY_DEFAULT_REPLAY"; status = "OBSERVED"
    sourceCommit = $sha; buildCommit = $sha; compiledRuntimeSourceCommit = $sha
    compiledDiagnosticSourceCommit = $sha; sourceIdentityMatches = $true
    originatingCandidateCommit = $origin; originatingCandidateIdentityPresent = $true
    profile = "10m"; programFormat = "1080p59.94"
    media = @(@{ sha256 = "b"*64; bytes = 8192 }, @{ sha256 = "c"*64; bytes = 20480 })
    operatingSystem = "Windows"; architecture = "X64"; processorCount = 8
    maximumPoints = 5; maximumBytes = 65536; captureFailures = 0; omittedPoints = 0
    playbackFailed = $true; cleanupSucceeded = $true
    points = @(
        @(0,120,300,600,601) | ForEach-Object -Begin { $i = 0 } -Process {
            $elapsed = $_
            @{
                index = $i++; phase = if ($elapsed -eq 601) { "post-host-drain" } else { "playback" }
                elapsedSeconds = $elapsed; processId = 1234
                privateBytes = [long](1073741824 + $elapsed * 1024 * 1024)
                workingSetBytes = 123456; managedBytesEstimate = 10000
                allocatedManagedBytes = 90000; handles = 300; processThreads = 12
                managedPoolThreads = 9
                lastGc = @{ committedManagedBytes = 18000 }
                writerLifetime = $resource
                captureQueue = @{ QueueDepth = 0; MaximumQueueDepth = 4; QueueCapacity = 8 }
                nativeWriterLifetime = @{ ActiveSinks = 0; ActiveScratchBuffers = 0 }
            }
        }
    )
}
function Copy-Fixture {
    $base | ConvertTo-Json -Depth 32 | ConvertFrom-Json -AsHashtable
}
function Write-Fixture($value) {
    $value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding utf8
}
function Verify {
    & (Join-Path $PSScriptRoot "Test-SsdPlaybackMemoryDiagnostic.ps1") -EvidencePath $path -ExpectedSourceCommit $sha -ExpectedProfile "10m" -ExpectedFormat "1080p59.94"
}
$rejected = 0
function Reject($value, [string]$case) {
    Write-Fixture $value
    $didReject = $false
    try { Verify | Out-Null } catch { $didReject = $true }
    if (-not $didReject) { throw "Diagnostic verifier accepted invalid evidence: $case" }
    $script:rejected++
}
try {
    Write-Fixture $base
    Verify | Out-Null
    $v = Copy-Fixture; $v.sourceCommit = "f"*40; Reject $v "foreign source"
    $v = Copy-Fixture; $v.compiledRuntimeSourceCommit = "f"*40; Reject $v "foreign binary"
    $v = Copy-Fixture; $v.status = "PARTIAL"; Reject $v "partial diagnostics"
    $v = Copy-Fixture; $v.omittedPoints = 1; Reject $v "omitted points"
    $v = Copy-Fixture; $v.points = @($v.points | Select-Object -First 3); Reject $v "short elapsed"
    $v = Copy-Fixture; $v.points[2].elapsedSeconds = 10; Reject $v "nonmonotonic"
    $v = Copy-Fixture; $v.points[0].privateBytes = -1; Reject $v "negative memory"
    $v = Copy-Fixture; $v.points[0].captureQueue.QueueDepth = 99; Reject $v "unbounded queue"
    $v = Copy-Fixture; $v.points[0].writerLifetime.Finalized = 3; Reject $v "impossible writer count"
    $v = Copy-Fixture; $v.originatingCandidateCommit = "not-a-sha"; Reject $v "fake originating candidate"
    $v = Copy-Fixture; $v.programFormat = "1080p50"; Reject $v "wrong cadence"
    $v = Copy-Fixture; $v.media[0].sha256 = ""; Reject $v "missing media hash"
    $v = Copy-Fixture; $v.points[-1].phase = "playback"; Reject $v "missing teardown"
    Write-Host "Memory diagnostic verification: one complete synthetic observation and $rejected fail-closed cases PASS. No playback or physical device qualified."
} finally {
    if ([IO.Path]::GetFileName($root) -notlike "rtaime-memory-verifier-*") {
        throw "Unsafe temporary verifier directory."
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
