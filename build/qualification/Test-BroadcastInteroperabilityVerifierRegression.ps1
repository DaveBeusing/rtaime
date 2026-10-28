# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$head = (& git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw "Need checked-out exact source revision." }
$temporary = Join-Path $root ("artifacts/qualification/broadcast/regression-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($temporary)
$generator = Join-Path $PSScriptRoot "Invoke-BroadcastInteroperabilityQualification.ps1"
$verifier = Join-Path $PSScriptRoot "Test-BroadcastInteroperabilityReport.ps1"
$basePath = Join-Path $temporary "baseline.json"
$mutatedPath = Join-Path $temporary "mutated.json"
$failures = 0
function Write-Json($Object, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Object | ConvertTo-Json -Depth 64) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Expect-Rejected([scriptblock]$Action, [string]$Description) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "Broadcast verifier accepted unsafe evidence: $Description." }
    $script:failures++
}
function Modify-And-Reject([scriptblock]$Mutation, [string]$Description) {
    $modified = Get-Content -LiteralPath $basePath -Raw | ConvertFrom-Json -Depth 64
    & $Mutation $modified
    Write-Json $modified $mutatedPath
    Expect-Rejected { & $verifier -ReportPath $mutatedPath -ExpectedSourceCommit $head } $Description
}
try {
    & $generator -ExpectedSourceCommit $head -OutputDirectory $temporary -OutputPath $basePath
    & $verifier -ReportPath $basePath -ExpectedSourceCommit $head
    $base = Get-Content -LiteralPath $basePath -Raw | ConvertFrom-Json -Depth 64
    if ($base.overallStatus -ne "UNVERIFIED" -or $base.decision -ne "NO-GO" -or
        @($base.suites | Where-Object status -ne "UNVERIFIED").Count -gt 0 -or
        @($base.requirements | Where-Object status -ne "UNVERIFIED").Count -gt 0) {
        throw "Without executed evidence, all broadcast requirements must remain UNVERIFIED/NO-GO."
    }
    Expect-Rejected { & $verifier -ReportPath $basePath -ExpectedSourceCommit ("a" * 40) } "stale/wrong source SHA"
    Expect-Rejected { & $verifier -ReportPath $basePath -ExpectedSourceCommit $head -RequireSoftwareQualified } "missing software runner"
    Modify-And-Reject { param($x) $x.sourceCommit = "f" * 40 } "tampered source SHA"
    Modify-And-Reject { param($x) $x.matrixSha256 = "0" * 64 } "tampered standards matrix hash"
    Modify-And-Reject { param($x) $x.configurationsSha256 = "0" * 64 } "tampered reference device matrix hash"
    Modify-And-Reject { param($x) $x.repository = "other/rtaime" } "foreign repository"
    Modify-And-Reject { param($x) $x.certificationClaim = $true } "forged EBU certification"
    Modify-And-Reject { param($x) $x.releasePromotionPerformed = $true } "forged Stable release promotion"
    Modify-And-Reject { param($x) $x.decision = "GO" } "software-only GO promotion"
    Modify-And-Reject { param($x) $x.overallStatus = "QUALIFIED" } "unqualified summary promotion"
    Modify-And-Reject { param($x) $x.suites[0].status = "QUALIFIED" } "software PASS without TRX"
    Modify-And-Reject { param($x) $x.suites[0].testCount = 42 } "fabricated test count"
    Modify-And-Reject { param($x) $x.suites[0].evidence = @{ path = "../../secrets"; sha256 = "a" * 64 } } "outside-workspace evidence"
    Modify-And-Reject { param($x) $x.requirements[0].status = "SUPPORTED" } "false production support"
    Modify-And-Reject { param($x) $x.requirements[0].status = "QUALIFIED" } "software label without execution"
    Modify-And-Reject { param($x) ($x.requirements | Where-Object scope -eq "physical" | Select-Object -First 1).status = "QUALIFIED" } "software evidence promoted to physical"
    Modify-And-Reject { param($x) ($x.requirements | Where-Object scope -eq "physical" | Select-Object -First 1).evidenceSha256 = "a" * 64 } "fabricated physical evidence"
    Modify-And-Reject { param($x) $x.requirements[0].edition = "unknown" } "different standard edition"
    Modify-And-Reject { param($x) $x.requirements[0].testDateUtc = [DateTimeOffset]::UtcNow.ToString("O") } "unobserved test date"
    Modify-And-Reject { param($x) $x.requirements[0].evidenceSha256 = "a" * 64 } "missing software proof"
    Modify-And-Reject { param($x) $x.configurations[2].hardwareStatus = "QUALIFIED" } "unverified reference platform promoted"
    Modify-And-Reject { param($x) $x.configurations[2].nic = "Two interfaces" } "fabricated NIC hardware"
    Modify-And-Reject { param($x) $x.configurations[2].gpu = "NVIDIA" } "fabricated GPU identity"
    Modify-And-Reject { param($x) $x.requirements = @($x.requirements | Select-Object -Skip 1) } "incomplete requirement scope"
    Modify-And-Reject { param($x) $x.requirements += $x.requirements[0] } "duplicate requirement"
    Modify-And-Reject { param($x) $x.suites = @($x.suites | Select-Object -Skip 1) } "missing suite"
    Modify-And-Reject { param($x) $x.suites[0].status = "FAILED" } "unobserved failure outcome"
    Modify-And-Reject { param($x) $x.requirements[0].profileId = "unknown-profile" } "wrong reference configuration"
    Write-Host "Broadcast interoperability evidence negative qualification PASS: $failures unsafe cases rejected; no hardware/support claim."
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
