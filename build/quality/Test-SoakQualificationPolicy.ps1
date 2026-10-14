# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$d = Get-Content -LiteralPath (Join-Path $repo "qualification/soak/sustained-mixed-load.json") -Raw | ConvertFrom-Json
if ([string]$d.schemaVersion -ne "1.0" -or [string]$d.lane -ne "MANAGED_STAGED_SINGLE_TESTHOST") { throw "Unexpected staged qualification scope." }
$durations = @{ smoke = 0; "8h" = 28800; "24h" = 86400; "72h" = 259200 }
if (@($d.profiles).Count -ne 4) { throw "Expected exactly four named staged workloads." }
foreach ($entry in $durations.GetEnumerator()) {
    $profile = @($d.profiles | Where-Object name -eq $entry.Key)
    if ($profile.Count -ne 1 -or [int]$profile[0].minimumElapsedSeconds -ne $entry.Value -or
        [int]$profile[0].minimumCycles -lt 2) { throw "Named staged duration is inconsistent." }
}
if ((@($d.formats) -join ",") -ne "1080p50,1080p59.94" -or (@($d.layerCounts) -join ",") -ne "0,1,2,4,8") { throw "Factored format/layer matrix changed." }
if ([int]$d.resourcePolicy.capacity -ne 96 -or [int]$d.resourcePolicy.windowHours -ne 1 -or
    [int]$d.resourcePolicy.minimumCompletedPostWarmupWindows -ne 3 -or
    [long]$d.resourcePolicy.maximumPrivateGrowthBytes -ne 536870912 -or [int]$d.resourcePolicy.maximumHandleGrowth -ne 128) {
    throw "Declared resource plateau policy changed."
}
foreach ($artifact in @("src/Runtime/rtaime.Runtime/BoundedSoakEvidence.cs",
    "tests/rtaime.Tests.Unit/BoundedSoakEvidenceTests.cs",
    "tests/rtaime.Tests.Integration/SustainedMixedLoadQualificationTests.cs",
    "build/qualification/Invoke-SustainedMixedLoadQualification.ps1",
    "build/qualification/Test-SustainedMixedLoadEvidence.ps1",
    "build/qualification/Test-SustainedMixedLoadVerifierRegression.ps1",
    "schemas/qualification/v1/staged-mixed-load.schema.json")) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $artifact) -PathType Leaf)) { throw "Missing bounded-soak artifact: $artifact" }
}
$collector = Get-Content -LiteralPath (Join-Path $repo "src/Runtime/rtaime.Runtime/BoundedSoakEvidence.cs") -Raw
if ($collector -match 'File\.Write|HttpClient|Socket|NamedPipe|StreamWriter' -or $collector -notmatch 'new ulong\[') {
    throw "Soak collectors must retain fixed storage and perform no disk/network I/O."
}
$required = Get-Content -LiteralPath (Join-Path $repo ".github/workflows/required-gates.yml") -Raw
foreach ($gate in @("Bounded soak summary regressions", "Soak evidence verifier negative regressions",
    "Staged mixed-load software smoke", "Renderer workload cycle software regressions")) {
    if (-not $required.Contains($gate)) { throw "Missing source-head soak gate: $gate" }
}
Write-Host "Bounded soak qualification policy PASS; physical and unexecuted long runs remain UNVERIFIED."
