# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CatalogPath,
    [Parameter(Mandatory)][long]$RunId,
    [Parameter(Mandatory)][int]$MaximumRunAttempt,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($RunId -le 0 -or $MaximumRunAttempt -le 0 -or $ExpectedSourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Artifact selection run/source identity is invalid."
}
$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$matches = [Collections.Generic.List[object]]::new()
$pattern = "^rtaime-mixed-soak-smoke-$RunId-([1-9][0-9]*)$"
foreach ($artifact in @($catalog.artifacts)) {
    $match = [Regex]::Match([string]$artifact.name, $pattern)
    if (-not $match.Success) { continue }
    if ([long]$artifact.workflow_run.id -ne $RunId -or [string]$artifact.workflow_run.head_sha -ne $ExpectedSourceCommit) {
        throw "Mixed-load artifact run/source metadata mismatch."
    }
    $attempt = [int]$match.Groups[1].Value
    if ($attempt -gt $MaximumRunAttempt) { throw "Mixed-load artifact belongs to a future attempt." }
    if ($artifact.expired -eq $true) { continue }
    $matches.Add([pscustomobject]@{ name = [string]$artifact.name; attempt = $attempt; id = [long]$artifact.id })
}
$ordered = @($matches | Sort-Object attempt -Descending)
if ($ordered.Count -eq 0) { throw "No retained exact-source mixed-load artifact is available." }
$latest = @($ordered | Where-Object attempt -eq $ordered[0].attempt)
if ($latest.Count -ne 1 -or $latest[0].id -le 0) { throw "Latest mixed-load artifact is ambiguous or invalid." }
return $latest[0]
