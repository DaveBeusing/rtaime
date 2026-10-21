# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [string]$DescriptorPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
if ([string]::IsNullOrWhiteSpace($DescriptorPath)) {
    $DescriptorPath = Join-Path $repositoryRoot "qualification/performance/managed-allocation-gc.json"
}
$validator = Join-Path $PSScriptRoot "Test-ManagedAllocationGcEvidence.ps1"
$sourceRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))
$sample = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ([string]$sample.profile -ne "smoke") {
    throw "The verifier regression matrix requires a bounded one-iteration smoke artifact."
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-managed-gc-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporary | Out-Null

function Write-Json {
    param([string]$Path, $Value)
    $Value | ConvertTo-Json -Depth 64 |
        Set-Content -LiteralPath $Path -Encoding utf8
}

function Check-Fails {
    param([string]$Name, [scriptblock]$Mutation, [string]$Baseline, [string]$Policy)
    $fixture = Join-Path $temporary $Name
    Copy-Item -LiteralPath $sourceRoot -Destination $fixture -Recurse
    $manifestPath = Join-Path $fixture "manifest.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    & $Mutation $manifest $fixture
    Write-Json $manifestPath $manifest
    $rejected = $false
    try {
        $parameters = @{
            EvidencePath = $manifestPath
            ExpectedSourceCommit = $ExpectedSourceCommit
            DescriptorPath = $DescriptorPath
        }
        if ($Baseline) { $parameters.BaselineEvidencePath = $Baseline }
        if ($Policy) { $parameters.ApprovedPolicyPath = $Policy }
        & $validator @parameters *> $null
    } catch { $rejected = $true }
    if (-not $rejected) { throw "The evidence verifier accepted the invalid $Name fixture." }
}

try {
    $goodCompare = Join-Path $temporary "comparison-good.json"
    & $validator -EvidencePath $EvidencePath -ExpectedSourceCommit $ExpectedSourceCommit -DescriptorPath $DescriptorPath -ComparisonOutputPath $goodCompare | Out-Null
    $result = Get-Content -LiteralPath $goodCompare -Raw | ConvertFrom-Json
    if ([string]$result.status -ne "UNVERIFIED" -or
        [string]$result.reason -ne "NO_APPROVED_BASELINE") {
        throw "Missing baseline must be UNVERIFIED, never PASS."
    }

    & $validator -EvidencePath $EvidencePath -ExpectedSourceCommit $ExpectedSourceCommit -DescriptorPath $DescriptorPath -BaselineEvidencePath $EvidencePath -ComparisonOutputPath $goodCompare | Out-Null
    $result = Get-Content -LiteralPath $goodCompare -Raw | ConvertFrom-Json
    if ([string]$result.status -ne "UNVERIFIED" -or
        [string]$result.reason -ne "NO_APPROVED_MANAGED_REGRESSION_THRESHOLD" -or
        @($result.deltas).Count -ne 16) {
        throw "Comparable but unapproved numerical evidence cannot be promoted to PASS."
    }

    Check-Fails "source-mismatch" {
        param($m, $root)
        $m.sourceCommit = "0000000000000000000000000000000000000000"
    } "" ""
    Check-Fails "runner-mismatch" {
        param($m, $root)
        $m.runner.runnerId = "another-approved-machine"
    } $EvidencePath ""
    Check-Fails "gc-no-events" {
        param($m, $root)
        $m.eventSampleCompleteness = "PASS"
    } "" ""
    Check-Fails "unsupported-hardware" {
        param($m, $root)
        $m.hardwareGpu = "PASS"
    } "" ""
    Check-Fails "missing-repeated-runs" {
        param($m, $root)
        $m.profile = "repeat"
    } "" ""
    Check-Fails "unobserved-scalar-guard" {
        param($m, $root)
        $m.scalarGuard.status = "UNVERIFIED"
    } "" ""
    Check-Fails "missing-artifact" {
        param($m, $root)
        $report = Join-Path $root ($m.runs[0].rateReports[0].path)
        Remove-Item -LiteralPath $report
    } "" ""
    Check-Fails "unverified-lease-owner" {
        param($m, $root)
        $reference = $m.runs[0].rateReports[0]
        $path = Join-Path $root $reference.path
        $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $data.profiles[0].transfers.ReadbackPool.ActiveBuffers = 1
        Write-Json $path $data
        $reference.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    } "" ""
    Check-Fails "nan-or-negative-tail" {
        param($m, $root)
        $reference = $m.runs[0].rateReports[0]
        $path = Join-Path $root $reference.path
        $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $data.profiles[0].p99Milliseconds = -1
        Write-Json $path $data
        $reference.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    } "" ""
    Check-Fails "producer-source-mismatch" {
        param($m, $root)
        $reference = $m.runs[0].rateReports[0]
        $path = Join-Path $root $reference.path
        $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $data.buildCommit = "ffffffffffffffffffffffffffffffffffffffff"
        Write-Json $path $data
        $reference.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    } "" ""

    # A zero-growth budget exists only inside this discarded verifier fixture.
    # It is not repository policy and must never be reused as a supported budget.
    $policyPath = Join-Path $temporary "fixture-policy.json"
    $policy = [ordered]@{
        schemaVersion = "1.0"
        qualification = "rtaime-managed-allocation-gc-regression-policy"
        status = "APPROVED"
        descriptorSha256 = (Get-FileHash -LiteralPath $DescriptorPath -Algorithm SHA256).Hash.ToLowerInvariant()
        runnerId = [string]$sample.runner.runnerId
        maximumP99IncreasePercent = 0
        maximumThreadAllocationIncreasePercent = 0
    }
    Write-Json $policyPath $policy
    Check-Fails "over-approved-p99" {
        param($m, $root)
        $reference = $m.runs[0].rateReports[0]
        $path = Join-Path $root $reference.path
        $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $old = [double]$data.profiles[0].p99Milliseconds
        $data.profiles[0].p99Milliseconds = $old + 10
        $data.profiles[0].maximumMilliseconds = [Math]::Max(
            [double]$data.profiles[0].maximumMilliseconds, $old + 10)
        Write-Json $path $data
        $reference.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    } $EvidencePath $policyPath

    Write-Host "Managed allocation/GC verifier negative regressions PASS; no synthetic profile becomes accepted evidence."
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
