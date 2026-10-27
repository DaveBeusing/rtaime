# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidateSet("smoke","10m","60m","8h","24h","72h")][string]$ExpectedProfile,
    [Parameter(Mandatory)][ValidateSet("1080p50","1080p59.94")][string]$ExpectedFormat
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
function Require([bool]$accepted, [string]$reason) {
    if (-not $accepted) { throw "Replay memory diagnostic invalid: $reason" }
}
$item = Get-Item -LiteralPath $EvidencePath -ErrorAction Stop
Require ($item.Length -gt 0 -and $item.Length -le 65536 -and
    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "File missing, oversized or redirected."
$report = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json -Depth 32
Require ($report.schemaVersion -ceq "1.0" -and
    $report.diagnosticMode -ceq "OBSERVATION_ONLY_DEFAULT_REPLAY" -and
    $report.status -ceq "OBSERVED" -and
    $report.sourceIdentityMatches -eq $true -and
    [int]$report.maximumPoints -eq 5 -and [int]$report.maximumBytes -eq 65536 -and
    [int]$report.captureFailures -eq 0 -and [int]$report.omittedPoints -eq 0) "Incomplete or invalid observation mode."
foreach ($field in @("sourceCommit","buildCommit","compiledRuntimeSourceCommit","compiledDiagnosticSourceCommit")) {
    Require ([string]$report.$field -ceq $ExpectedSourceCommit) "Source/build/compiled assembly mismatch ($field)."
}
Require ($report.profile -ceq $ExpectedProfile -and $report.programFormat -ceq $ExpectedFormat -and
    $report.architecture -ceq "X64" -and [int]$report.processorCount -gt 0 -and
    -not [string]::IsNullOrWhiteSpace($report.operatingSystem)) "Profile or platform mismatch."
$origin = [string]$report.originatingCandidateCommit
if ($report.originatingCandidateIdentityPresent -eq $true) {
    Require ($origin -cmatch '^[0-9a-f]{40}$') "Invalid optional originating candidate."
} else {
    Require ([string]::IsNullOrWhiteSpace($origin)) "Unverified originating candidate identity."
}
Require (@($report.media).Count -eq 2) "Expected both exact input media identities."
foreach ($media in @($report.media)) {
    Require ([long]$media.bytes -gt 0 -and [string]$media.sha256 -cmatch '^[0-9a-f]{64}$') "Input media identity missing."
}
$points = @($report.points)
Require ($points.Count -ge 2 -and $points.Count -le 5) "Sparse points missing or unbounded."
$previous = -1.0
$processId = 0
$openedSampled = $false
for ($i=0; $i -lt $points.Count; $i++) {
    $p = $points[$i]
    $elapsed = [double]$p.elapsedSeconds
    Require ([double]::IsFinite($elapsed) -and $elapsed -gt $previous -and
        $elapsed -ge 0) "Nonmonotonic elapsed seconds."
    $previous = $elapsed
    Require ([int]$p.index -eq $i -and [int]$p.processId -gt 0 -and
        [long]$p.privateBytes -ge 0 -and [long]$p.workingSetBytes -ge 0 -and
        [long]$p.managedBytesEstimate -ge 0 -and [long]$p.allocatedManagedBytes -ge 0 -and
        [long]$p.lastGc.committedManagedBytes -ge 0 -and
        [int]$p.handles -ge 0 -and [int]$p.processThreads -ge 0 -and
        [int]$p.managedPoolThreads -ge 0) "Missing or negative process/GC observation."
    if ($processId -ne 0) { Require ([int]$p.processId -eq $processId) "Cross-process samples." }
    $processId = [int]$p.processId
    if ($null -ne $p.writerLifetime) {
        $openedSampled = $true
        Require ([ulong]$p.writerLifetime.Opened -ge [ulong]$p.writerLifetime.Finalized -and
            [ulong]$p.writerLifetime.Finalized -ge 0) "Writer lifecycle counters contradictory."
    }
    if ($null -ne $p.captureQueue) {
        Require ([int]$p.captureQueue.QueueCapacity -ge 1 -and
            [int]$p.captureQueue.QueueCapacity -le 64 -and
            [int]$p.captureQueue.CurrentDepth -ge 0 -and
            [int]$p.captureQueue.CurrentDepth -le [int]$p.captureQueue.QueueCapacity) "Replay queue exceeds finite limit."
    }
    if ($null -ne $p.nativeWriterLifetime) {
        Require ([long]$p.nativeWriterLifetime.ActiveSinks -ge 0 -and
            [long]$p.nativeWriterLifetime.ActiveScratchBuffers -ge 0) "Invalid native writer count."
    }
}
Require $openedSampled "No replay writer lifetime evidence."
Require ([string]$points[0].phase -ceq "playback" -and
    [string]$points[-1].phase -in @("post-host-drain","post-host-stop-attempt")) "Missing playback and teardown checkpoints."
$duration = switch ($ExpectedProfile) {
    "smoke" { 30 } "10m" { 600 } "60m" { 3600 }
    "8h" { 28800 } "24h" { 86400 } "72h" { 259200 }
}
# Diagnostic checks describe coverage, not a new timing assertion: a failed
# workload may still be inspected, but cannot be promoted to software PASS.
Require ($previous -ge $duration - 2) "Insufficient actual elapsed observation for selected profile."
$first = $points[0]
$lastPlayback = @($points | Where-Object phase -EQ "playback")[-1]
$observedGrowth = [long]$lastPlayback.privateBytes - [long]$first.privateBytes
$resource = if ($observedGrowth -gt 536870912) { "OVER_512MIB_SPARSE_OBSERVATION" }
    else { "SPARSE_SAMPLES_NOT_SUFFICIENT_TO_CLEAR_RESOURCE_BUDGET" }
Write-Host ("Replay memory diagnostic OBSERVED; source {0}; profile {1} {2}; private sample delta {3} bytes ({4}); workloadFailed={5}; physical UNVERIFIED." -f
    $ExpectedSourceCommit, $ExpectedProfile, $ExpectedFormat, $observedGrowth, $resource, $report.playbackFailed)
# This verifier NEVER overrides Test-SsdPlaybackEvidence, host cleanup, actual
# resource thresholds, reported timing failures or physical qualification.
