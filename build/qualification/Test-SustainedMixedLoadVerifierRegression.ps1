# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$descriptor = Join-Path $repo "qualification/soak/sustained-mixed-load.json"
$verifier = Join-Path $PSScriptRoot "Test-SustainedMixedLoadEvidence.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-soak-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root | Out-Null
$path = Join-Path $root "fixture.json"
$sha = "a" * 40
# Synthetic verifier fixtures are not measured qualification evidence.
$now = [DateTimeOffset]::UtcNow
$base = @{
    schemaVersion = "1.0"; qualification = "rtaime-staged-mixed-load"; scope = "STAGED_MANAGED_SESSIONS_IN_ONE_TESTHOST"
    sourceCommit = $sha; buildCommit = $sha; sdk = "10.0.401"; profile = "8h"
    descriptorSha256 = (Get-FileHash -LiteralPath $descriptor -Algorithm SHA256).Hash.ToLowerInvariant()
    requiredSeconds = 28800; elapsedSeconds = 28800.0; cycles = 100; minimumCycles = 4
    startedAtUtc = $now.AddHours(-8).ToString("O"); capturedAtUtc = $now.ToString("O")
    completion = "STAGED_SOFTWARE_SOAK_PASS"; failure = $null; formats = @("1080p50", "1080p59.94")
    frames = 1000; stages = @(100, 100, 100, 100, 10); recoveries = 30; softwareAudioDriftSamples = 0
    latency = @{ count = 1000; capacity = 10001; percentileMethod = "WHOLE_RUN_NEAREST_RANK_UPPER_BIN_BOUND"; p50 = 1.0; p95 = 2.0; p99 = 3.0; maximum = 4.0 }
    cleanup = "ASSERTED_AFTER_EACH_MIXED_SESSION"; resourcePlateau = "PASS"; longSoak = "STAGED_SOFTWARE_SOAK_PASS"
    resourcePolicy = @{ maximumPrivateGrowth = 536870912; maximumHandleGrowth = 128 }
    resourceWindows = @(1..8 | ForEach-Object { @{ index = $_; samples = 60; meanPrivateBytes = 1000000.0; meanHandles = 20.0 } })
    physicalGpu = "UNVERIFIED"; nativeNetworkTransport = "UNVERIFIED"; physicalAvSynchronization = "UNVERIFIED"
    continuousSingleProduction = "UNVERIFIED"
}
function Write-Fixture($value) { $value | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $path -Encoding utf8 }
function Clone-Fixture { return ($base | ConvertTo-Json -Depth 15 | ConvertFrom-Json -AsHashtable) }
function Assert-Rejected($value, [string]$reason) {
    Write-Fixture $value
    $rejected = $false
    try { & $verifier -EvidencePath $path -DescriptorPath $descriptor -ExpectedSourceCommit $sha -ExpectedProfile "8h" }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Verifier accepted invalid fixture: $reason" }
}
try {
    Write-Fixture $base
    & $verifier -EvidencePath $path -DescriptorPath $descriptor -ExpectedSourceCommit $sha -ExpectedProfile "8h"
    $v = Clone-Fixture; $v.elapsedSeconds = 60; Assert-Rejected $v "short duration"
    $v = Clone-Fixture; $v.sourceCommit = "b" * 40; Assert-Rejected $v "foreign source"
    $v = Clone-Fixture; $v.descriptorSha256 = "0" * 64; Assert-Rejected $v "foreign workload"
    $v = Clone-Fixture; $v.physicalGpu = "PASS"; Assert-Rejected $v "false physical qualification"
    $v = Clone-Fixture; $v.stages[3] = 0; Assert-Rejected $v "missing audio stage"
    $v = Clone-Fixture; $v.resourceWindows = @($v.resourceWindows[0]); Assert-Rejected $v "insufficient plateau evidence"
    $v = Clone-Fixture; $v.resourceWindows[6].meanPrivateBytes = 1000000000.0; Assert-Rejected $v "hidden memory growth"
    $v = Clone-Fixture; $v.resourceWindows[6].meanHandles = 1000.0; Assert-Rejected $v "hidden handle growth"
    $v = Clone-Fixture; $v.latency.p99 = [double]::NaN; Assert-Rejected $v "non-finite quantile"
    Write-Host "Mixed-load verifier positive and negative fixtures PASS; no real long-soak claim."
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith("rtaime-soak-verifier-")) { throw "Unsafe fixture cleanup target." }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
