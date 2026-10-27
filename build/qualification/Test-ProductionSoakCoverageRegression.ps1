# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$descriptor = Join-Path $repo "qualification/soak/production-playout-coverage.json"
$source = (& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$') {
    throw "Current source SHA required for production soak inventory negative tests."
}
$root = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-soak-matrix-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($root)
$fixture = Join-Path $root "coverage.json"
$reportPath = Join-Path $root "assessment.json"
$invalid = Join-Path $root "invalid-ssd.json"
$policy = Get-Content -LiteralPath $descriptor -Raw | ConvertFrom-Json -AsHashtable -Depth 48
function Copy-Policy { $policy | ConvertTo-Json -Depth 48 | ConvertFrom-Json -AsHashtable }
function Reject-Policy($modified, [string]$reason) {
    $modified | ConvertTo-Json -Depth 48 | Set-Content -LiteralPath $fixture -Encoding utf8
    $rejected = $false
    try { & (Join-Path $PSScriptRoot "Test-ProductionSoakCoveragePolicy.ps1") -DescriptorPath $fixture | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe production soak policy accepted: $reason" }
}
try {
    & (Join-Path $PSScriptRoot "Test-ProductionSoakCoveragePolicy.ps1") | Out-Null
    $candidate = Copy-Policy; $candidate.releaseDecisionWithoutExternalEvidence = "PASS"
    Reject-Policy $candidate "fake release approval"
    $candidate = Copy-Policy; $candidate.requiredChecks = @("CI","Quality","Security")
    Reject-Policy $candidate "missing mandatory checks"
    $candidate = Copy-Policy; $candidate.ssd.resourceBudget.maximumPrivateGrowthBytes = 1073741824
    Reject-Policy $candidate "512-MiB budget relaxed"
    $candidate = Copy-Policy; $candidate.ssd.profiles[1].seconds = 30
    Reject-Policy $candidate "ten-minute resource profile shortened"
    $candidate = Copy-Policy; $candidate.cadences[0].admission = "SUPPORTED"
    Reject-Policy $candidate "invented native 25-fps qualification"
    $candidate = Copy-Policy; $candidate.unresolvedRiskReviews = @()
    Reject-Policy $candidate "memory or branch-protection risk omitted"

    & (Join-Path $PSScriptRoot "New-ProductionSoakCoverageReport.ps1") -ExpectedSourceCommit $source -OutputPath $reportPath | Out-Null
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 48
    if ($report.releaseDecision -cne "NO-GO" -or $report.sourceCommit -cne $source -or
        @($report.qualifications | Where-Object status -EQ "PASS").Count -ne 0 -or
        @($report.qualifications | Where-Object id -EQ "physical/EXTERNAL_AV_SYNC").Count -ne 1 -or
        @($report.qualifications | Where-Object id -EQ "governance/masterRuleset").Count -ne 1) {
        throw "A missing-evidence report fabricated acceptance or omitted blockers."
    }
    $rejectedOverwrite = $false
    try {
        & (Join-Path $PSScriptRoot "New-ProductionSoakCoverageReport.ps1") -ExpectedSourceCommit $source -OutputPath $reportPath | Out-Null
    } catch { $rejectedOverwrite = $true }
    if (-not $rejectedOverwrite) { throw "Previous audit evidence could be overwritten." }
    '{"schemaVersion":"1.0","sourceCommit":"not-a-commit","profile":"10m","status":"PASS"}' |
        Set-Content -LiteralPath $invalid -Encoding utf8
    $invalidReport = Join-Path $root "invalid-report.json"
    & (Join-Path $PSScriptRoot "New-ProductionSoakCoverageReport.ps1") -ExpectedSourceCommit $source -SsdEvidencePaths @($invalid) -OutputPath $invalidReport | Out-Null
    $assessment = Get-Content -LiteralPath $invalidReport -Raw | ConvertFrom-Json -Depth 48
    if ($assessment.releaseDecision -cne "NO-GO" -or
        @($assessment.qualifications | Where-Object { $_.id -like "ssd/invalid/*" -and $_.status -eq "FAIL" }).Count -eq 0) {
        throw "Malformed supplied SSD evidence did not fail closed."
    }
    Write-Host "Production soak matrix regression PASS: six rejected policy changes, missing-evidence NO-GO, immutable output and invalid SSD input rejection. No hardware workload was executed."
} finally {
    if ([IO.Path]::GetFileName($root) -notlike "rtaime-soak-matrix-*") {
        throw "Unsafe temporary qualification cleanup target."
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
