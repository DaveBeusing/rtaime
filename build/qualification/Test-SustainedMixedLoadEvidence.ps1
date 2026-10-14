# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$DescriptorPath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidateSet("smoke", "8h", "24h", "72h")][string]$ExpectedProfile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($ExpectedSourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Expected exact source SHA is required." }
$e = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
$d = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
$selected = @($d.profiles | Where-Object name -eq $ExpectedProfile)
if ($selected.Count -ne 1) { throw "Expected profile is missing or duplicated." }
$profile = $selected[0]
$required = switch ($ExpectedProfile) { "smoke" { 0 } "8h" { 28800 } "24h" { 86400 } "72h" { 259200 } }
if ([string]$e.schemaVersion -ne "1.0" -or [string]$e.qualification -ne "rtaime-staged-mixed-load" -or
    [string]$e.scope -ne "STAGED_MANAGED_SESSIONS_IN_ONE_TESTHOST") { throw "Unknown staged evidence schema or scope." }
if ([string]$e.sourceCommit -ne $ExpectedSourceCommit -or [string]$e.buildCommit -ne $ExpectedSourceCommit) {
    throw "Staged evidence source/build differs from the qualified commit."
}
if ([string]$e.sdk -ne "10.0.401" -or [string]$e.profile -ne $ExpectedProfile) { throw "Wrong SDK or profile." }
$hash = (Get-FileHash -LiteralPath $DescriptorPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ([string]$e.descriptorSha256 -ne $hash) { throw "Staged evidence workload hash mismatch." }
if ([int]$profile.minimumElapsedSeconds -ne $required -or [int]$e.requiredSeconds -ne $required) { throw "Named duration is inconsistent." }
$elapsed = [double]$e.elapsedSeconds
if (-not [double]::IsFinite($elapsed) -or $elapsed -lt $required -or [ulong]$e.cycles -lt [ulong]$profile.minimumCycles) {
    throw "Staged run did not complete its real elapsed duration or repeated workload cycles."
}
$completion = if ($ExpectedProfile -eq "smoke") { "SHORT_SOFTWARE_PASS" } else { "STAGED_SOFTWARE_SOAK_PASS" }
if ([string]$e.completion -ne $completion -or $null -ne $e.failure) { throw "Staged run is not successfully complete." }
if ((@($e.formats) -join ",") -ne "1080p50,1080p59.94" -or [ulong]$e.frames -eq 0 -or
    [ulong]$e.latency.count -eq 0 -or @($e.stages).Count -ne 5 -or @($e.stages | Where-Object { [ulong]$_ -eq 0 }).Count -gt 0) {
    throw "Required formats or workload stages were not observed."
}
if ([int]$e.latency.capacity -ne 10001 -or [int]$e.latency.capacity -gt 65537 -or
    [string]$e.latency.percentileMethod -ne "WHOLE_RUN_NEAREST_RANK_UPPER_BIN_BOUND" -or
    @($e.resourceWindows).Count -gt 96) { throw "Unbounded or incorrectly scoped observation evidence." }
$values = @([double]$e.latency.p50, [double]$e.latency.p95, [double]$e.latency.p99, [double]$e.latency.maximum)
foreach ($value in $values) { if (-not [double]::IsFinite($value) -or $value -lt 0) { throw "Invalid latency evidence." } }
if ($values[0] -gt $values[1] -or $values[1] -gt $values[2] -or $values[2] -gt $values[3]) { throw "Invalid quantile ordering." }
if ([string]$e.cleanup -ne "ASSERTED_AFTER_EACH_MIXED_SESSION" -or [int]$e.softwareAudioDriftSamples -ne 0 -or
    [ulong]$e.recoveries -lt 3) { throw "Cleanup, audio or recovery evidence is incomplete." }
foreach ($name in @("physicalGpu", "nativeNetworkTransport", "physicalAvSynchronization", "continuousSingleProduction")) {
    if ([string]$e.$name -ne "UNVERIFIED") { throw "Software stages cannot qualify '$name'." }
}
if ($ExpectedProfile -eq "smoke") {
    if ([string]$e.longSoak -ne "UNVERIFIED") { throw "A short run cannot qualify long soak." }
} elseif ([string]$e.longSoak -ne $completion -or [string]$e.resourcePlateau -ne "PASS") {
    throw "Long software soak requires elapsed duration and post-warmup resource plateau evidence."
}
Write-Host "Staged mixed-load evidence verification PASS ($ExpectedProfile); physical status UNVERIFIED."
