# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnvironmentPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$OutcomePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "SsdPlaybackStorage.ps1")
foreach ($path in @($EnvironmentPath, $OutcomePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -gt 256KB) { throw "Missing or oversized SSD inventory/outcome." }
}
$e = Get-Content -LiteralPath $EnvironmentPath -Raw | ConvertFrom-Json
$o = Get-Content -LiteralPath $OutcomePath -Raw | ConvertFrom-Json
if ($e.schemaVersion -cne "1.1" -or $e.sourceCommit -cne $ExpectedSourceCommit -or
    $o.sourceCommit -cne $ExpectedSourceCommit -or $o.buildCommit -cne $ExpectedSourceCommit -or
    $o.result.sourceCommit -cne $ExpectedSourceCommit -or $o.result.buildCommit -cne $ExpectedSourceCommit -or
    $e.captureStatus -cne "FINAL" -or $e.physicalStatus -cne "UNVERIFIED") { throw "Inventory source/schema/status mismatch." }
if ($e.outcomeSha256 -cne (Get-FileHash -LiteralPath $OutcomePath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Inventory/outcome bytes differ." }
if (@($e.storage).Count -ne 2 -or @($e.storageAfter).Count -ne 2 -or
    @($e.media).Count -ne 2 -or @($o.result.media).Count -ne 2) { throw "Both deck identities are required." }
foreach ($set in @("storage", "storageAfter")) {
    foreach ($disk in @($e.$set)) {
        if ($disk.mapping -ceq "UNVERIFIED") {
            if ([string]::IsNullOrWhiteSpace([string]$disk.reason)) { throw "Unknown mapping needs a reason." }
            continue
        }
        if ($disk.mapping -cne "OBSERVED" -or $disk.deviceKey -cnotmatch '^[0-9a-f]{64}$' -or
            [int]$disk.diskNumber -lt 0 -or [int]$disk.partitionNumber -lt 1 -or [long]$disk.partitionOffset -lt 0 -or
            [long]$disk.volumeSize -le 0 -or [long]$disk.volumeFreeBytes -lt 0 -or
            [long]$disk.volumeFreeBytes -gt [long]$disk.volumeSize -or [long]$disk.fileBytes -le 0 -or
            $disk.storageClass -cnotin @("SSD","UNVERIFIED") -or $disk.physicalStatus -cne "UNVERIFIED") { throw "Invalid observed storage identity/capacity." }
        foreach ($name in @("model","busType","fileSystem","lastWriteUtc")) {
            if ([string]::IsNullOrWhiteSpace([string]$disk.$name)) { throw "Missing observed disk property: $name" }
        }
        $null = [DateTimeOffset]::Parse([string]$disk.lastWriteUtc)
        if (@($disk.controllers).Count -gt 16 -or $disk.controllerStatus -cnotin @("OBSERVED","UNVERIFIED")) { throw "Invalid controller inventory." }
        if ($disk.controllerStatus -ceq "OBSERVED") {
            if (@($disk.controllers).Count -eq 0) { throw "Controller observation is empty." }
            foreach ($controller in @($disk.controllers)) {
                if ([string]::IsNullOrWhiteSpace([string]$controller.pnpDeviceId) -or @($controller.drivers).Count -ne 1 -or
                    [string]::IsNullOrWhiteSpace([string]$controller.drivers[0].DriverVersion)) { throw "Missing controller driver identity." }
            }
        }
    }
    if ((Get-SsdStorageTopology $e.$set) -cne $e.topology) { throw "Topology is not derived from both device identities." }
}
for ($i = 0; $i -lt 2; $i++) {
    if ($e.media[$i].sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $e.media[$i].sha256 -cne $o.result.media[$i].sha256 -or
        [long]$e.media[$i].bytes -ne [long]$o.result.media[$i].bytes) { throw "Inventory/media identity mismatch." }
    $before = $e.storage[$i]; $after = $e.storageAfter[$i]
    if ($before.mapping -cne $after.mapping) { throw "Device mapping changed during playback." }
    if ($before.mapping -ceq "OBSERVED") {
        foreach ($name in @("deviceKey","diskNumber","partitionNumber","partitionOffset","model","firmware","busType","fileSystem","volumeSize","fileBytes","lastWriteUtc")) {
            if ($before.$name -cne $after.$name) { throw "Storage/file identity changed during playback: $name" }
        }
        foreach ($name in @("controllers","diskDrivers")) {
            if (($before.$name | ConvertTo-Json -Depth 12 -Compress) -cne
                ($after.$name | ConvertTo-Json -Depth 12 -Compress)) { throw "Storage driver identity changed: $name" }
        }
        if ([long]$before.fileBytes -ne [long]$e.media[$i].bytes) { throw "File size differs from the decoded media." }
    }
}
if ([DateTimeOffset]::Parse([string]$e.capturedAt) -gt [DateTimeOffset]::Parse([string]$o.result.startedAt) -or
    [DateTimeOffset]::Parse([string]$e.completedAt) -lt [DateTimeOffset]::Parse([string]$o.capturedAt)) { throw "Inventory capture does not bracket playback." }
foreach ($name in @("cache","pressure","selectedDecoder","fileOnlyReadLatency","physicalOutput","gpuTelemetry")) {
    if ($e.$name -cne "UNVERIFIED") { throw "Inventory cannot qualify $name." }
}
Write-Host "SSD inventory correlation PASS; topology $($e.topology); physical acceptance UNVERIFIED."
