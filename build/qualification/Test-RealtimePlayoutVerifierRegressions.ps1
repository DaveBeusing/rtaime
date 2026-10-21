# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$descriptor = Join-Path $repo "qualification/performance/realtime-playout-matrix.json"
$verifier = Join-Path $PSScriptRoot "Test-RealtimePlayoutEvidence.ps1"
$source = "a" * 40
$root = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-playout-negative-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($root)
$path = Join-Path $root "manifest.pending.json"
$fake = [ordered]@{
    schemaVersion = "1.0"
    qualification = "rtaime-realtime-playout-software-matrix"
    sourceCommit = $source
    buildCommit = $source
    sdk = "10.0.401"
    profile = "smoke"
    descriptorSha256 = (Get-FileHash -LiteralPath $descriptor -Algorithm SHA256).Hash.ToLowerInvariant()
    completion = "SOFTWARE_COLLECTION_VERIFIED_NOT_REALTIME_APPROVAL"
    runner = @{ architecture = "X64"; os = "Windows"; machine = "negative-fixture" }
    actualNative25Fps = "UNVERIFIED"
    gpuDriverVram = "UNVERIFIED"
    physicalStorageLatencyAndCache = "UNVERIFIED"
    physicalSdiNdiGenlock = "UNVERIFIED"
    physicalAvSyncAndEndToEnd = "UNVERIFIED"
    physicalDeviceLoss = "UNVERIFIED"
    continuousNativeMultitrackSoak = "UNVERIFIED"
    allPhysicalAcceptance = "UNVERIFIED"
    managedManifest = @{ path = "../escape.json"; sha256 = ("f" * 64) }
}
function Must-Reject([string]$label, [string]$expected) {
    $fake | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8
    $rejected = $false
    try {
        & $verifier -EvidencePath $path -ExpectedSourceCommit $source | Out-Null
    } catch {
        if (-not $_.Exception.Message.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Verifier rejected $label for the wrong reason: $($_.Exception.Message)"
        }
        $rejected = $true
    }
    if (-not $rejected) { throw "Verifier accepted corrupt evidence: $label" }
}
try {
    $fake.sourceCommit = "b" * 40
    Must-Reject "source substitution" "Unbound software completion"
    $fake.sourceCommit = $source
    $fake.completion = "PASS"
    Must-Reject "premature completion" "Unbound software completion"
    $fake.completion = "SOFTWARE_COLLECTION_VERIFIED_NOT_REALTIME_APPROVAL"
    $fake.allPhysicalAcceptance = "PASS"
    Must-Reject "physical promotion" "Software-only evidence cannot qualify"
    $fake.allPhysicalAcceptance = "UNVERIFIED"
    Must-Reject "directory traversal" "Unsafe evidence path"
    $dummy = Join-Path $root "placeholder.json"
    Set-Content -LiteralPath $dummy -Value "{}" -Encoding utf8
    $fake.managedManifest = @{ path = "placeholder.json"; sha256 = ("f" * 64) }
    Must-Reject "producer digest mismatch" "Artifact SHA256 mismatch"
    Write-Host "Real-time playout evidence verifier negative guards PASS (5 distinct corruptions)."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
