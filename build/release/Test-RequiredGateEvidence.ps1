# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$policy = Get-Content -LiteralPath (Join-Path $root "docs/Governance/RepositoryPolicy.json") -Raw | ConvertFrom-Json
$e = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ($ExpectedSourceCommit -notmatch '^[0-9a-f]{40}$' -or [string]$e.repository -ne "DaveBeusing/rtaime") {
    throw "Required Gates repository/source identity is invalid."
}
$r = $e.run
if ([string]$r.name -ne "Required gates" -or [string]$r.path -ne ".github/workflows/required-gates.yml" -or
    [string]$r.head_sha -ne $ExpectedSourceCommit -or [long]$r.id -le 0 -or [int]$r.run_attempt -le 0 -or
    [string]$r.event -notin @("push", "pull_request") -or
    [string]$r.repository.full_name -ne "DaveBeusing/rtaime" -or
    [string]$r.html_url -ne "https://github.com/DaveBeusing/rtaime/actions/runs/$($r.id)") {
    throw "Required Gates workflow/source/run provenance does not match."
}
$jobs = @($e.jobs)
foreach ($name in @($policy.requiredChecks)) {
    $matches = @($jobs | Where-Object { [string]$_.name -eq [string]$name })
    if ($matches.Count -ne 1) { throw "Required Gate '$name' is missing or duplicated." }
    $j = $matches[0]
    if ([long]$j.id -le 0 -or [long]$j.run_id -ne [long]$r.id -or [int]$j.run_attempt -ne [int]$r.run_attempt -or
        [string]$j.head_sha -ne $ExpectedSourceCommit -or
        [string]$j.html_url -ne "https://github.com/DaveBeusing/rtaime/actions/runs/$($r.id)/job/$($j.id)" -or
        [string]$j.status -ne "completed" -or [string]$j.conclusion -ne "success" -or
        [string]::IsNullOrWhiteSpace([string]$j.completed_at)) {
        throw "Required Gate '$name' has no successful completed exact-source/attempt result."
    }
    $start = [DateTimeOffset]::Parse([string]$j.started_at, [Globalization.CultureInfo]::InvariantCulture)
    $end = [DateTimeOffset]::Parse([string]$j.completed_at, [Globalization.CultureInfo]::InvariantCulture)
    if ($end -lt $start -or $end -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { throw "Required Gate time provenance is invalid." }
}
# The containing workflow may still be recording this index after all five required jobs completed.
# A failed/cancelled run is rejected; in-progress is accepted only for those completed jobs.
if ([string]$r.status -notin @("completed", "in_progress") -or
    ([string]$r.status -eq "completed" -and [string]$r.conclusion -ne "success")) {
    throw "Required Gates workflow is failed, cancelled or not running/completed."
}
Write-Host "Exact-source Required Gates verification PASS ($($r.id), attempt $($r.run_attempt))."
