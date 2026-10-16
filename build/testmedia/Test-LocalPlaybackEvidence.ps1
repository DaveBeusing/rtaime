# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidateSet(10,60)][int]$Minutes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Actual long-playback report is missing." }
if ((Get-Item -LiteralPath $Path).Length -gt 256KB) { throw "Playback evidence exceeds the constant report bound." }
$report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
if ($report.timingEvidence.path -cne "local-playback-timing.json" -or $report.timingEvidence.sha256 -notmatch '^[0-9a-f]{64}
if ($report.schemaVersion -ne "1.0" -or $report.sourceCommit -cne $SourceCommit) { throw "Playback schema/source mismatch." }
if ($report.collectionStatus -ne "PASS" -or $null -ne $report.failure -or $null -ne $report.cleanupFailure) { throw "Actual playback or cleanup failed." }
if ($report.asset.RequestedDurationSeconds -ne ($Minutes * 60)) { throw "Playback duration does not match the requested profile." }
if ($report.asset.Sha256 -notmatch '^[0-9a-f]{64}$') { throw "Generated asset hash is absent." }
if ($report.playback.DecodedFrames -lt ($Minutes * 60 * 50 - 4) -or
    $report.playback.DecodedAudioSamples -le 0 -or
    $report.playback.NativeVideoRead.Calls -le 0 -or
    $report.finalSchedule.CommittedBoundaries -le 0) { throw "Actual decoder/Program progress is missing." }
if ($report.resources.Count -lt ($Minutes - 1) -or $report.resources.Count -gt 128) { throw "Resource observation coverage is invalid." }
if ($report.audioStatus -notin @("PASS", "FAIL") -or $report.physicalStatus -ne "UNVERIFIED") { throw "Qualification boundaries are absent." }
Write-Host "Actual long-playback evidence collection PASS: $SourceCommit, $Minutes minutes"
Write-Host "Audio continuity: $($report.audioStatus); physical SSD/GPU qualification: UNVERIFIED"
if ($report.audioStatus -eq "FAIL") { throw "Audio continuity findings require resolution before baseline qualification." }
) { throw "Bounded timing evidence identity is absent." }
$timingPath = Join-Path (Split-Path -Parent $Path) $report.timingEvidence.path
if (-not (Test-Path -LiteralPath $timingPath -PathType Leaf) -or (Get-Item -LiteralPath $timingPath).Length -gt 256KB) { throw "Timing evidence is missing or oversized." }
if ((Get-FileHash -LiteralPath $timingPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $report.timingEvidence.sha256) { throw "Timing evidence hash mismatch." }
$timingReport = Get-Content -LiteralPath $timingPath -Raw | ConvertFrom-Json
if ($timingReport.schemaVersion -ne "1.0" -or $timingReport.sourceCommit -cne $SourceCommit -or $timingReport.sdk -cne $report.sdk) { throw "Timing evidence provenance mismatch." }
if ($timingReport.timing.Capacity -le 0 -or $timingReport.timing.Capacity -gt 2048 -or
    @($timingReport.timing.Samples).Count -le 0 -or @($timingReport.timing.Samples).Count -gt $timingReport.timing.Capacity -or
    $timingReport.timing.Capacity -ne $report.finalTiming.Capacity -or
    $timingReport.timing.RecordedSamples -ne $report.finalTiming.RecordedSamples -or
    $timingReport.timing.OverwrittenSamples -ne $report.finalTiming.OverwrittenSamples -or
    $timingReport.timing.ContentionDroppedSamples -ne $report.finalTiming.ContentionDroppedSamples -or
    ($timingReport.timing.Stages | ConvertTo-Json -Compress -Depth 10) -cne ($report.finalTiming.Stages | ConvertTo-Json -Compress -Depth 10) -or
    @($timingReport.timing.Stages | Where-Object Stage -eq 11).Count -ne 1) { throw "Timing evidence capacity/summary mismatch." }

if ($report.schemaVersion -ne "1.0" -or $report.sourceCommit -cne $SourceCommit) { throw "Playback schema/source mismatch." }
if ($report.collectionStatus -ne "PASS" -or $null -ne $report.failure -or $null -ne $report.cleanupFailure) { throw "Actual playback or cleanup failed." }
if ($report.asset.RequestedDurationSeconds -ne ($Minutes * 60)) { throw "Playback duration does not match the requested profile." }
if ($report.asset.Sha256 -notmatch '^[0-9a-f]{64}$') { throw "Generated asset hash is absent." }
if ($report.playback.DecodedFrames -lt ($Minutes * 60 * 50 - 4) -or
    $report.playback.DecodedAudioSamples -le 0 -or
    $report.playback.NativeVideoRead.Calls -le 0 -or
    $report.finalSchedule.CommittedBoundaries -le 0) { throw "Actual decoder/Program progress is missing." }
if ($report.resources.Count -lt ($Minutes - 1) -or $report.resources.Count -gt 128) { throw "Resource observation coverage is invalid." }
if ($report.audioStatus -notin @("PASS", "FAIL") -or $report.physicalStatus -ne "UNVERIFIED") { throw "Qualification boundaries are absent." }
Write-Host "Actual long-playback evidence collection PASS: $SourceCommit, $Minutes minutes"
Write-Host "Audio continuity: $($report.audioStatus); physical SSD/GPU qualification: UNVERIFIED"
if ($report.audioStatus -eq "FAIL") { throw "Audio continuity findings require resolution before baseline qualification." }
