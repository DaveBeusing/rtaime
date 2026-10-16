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
if ($report.audioStatus -eq "FAIL") { Write-Warning "Audio continuity findings require resolution before production qualification." }
