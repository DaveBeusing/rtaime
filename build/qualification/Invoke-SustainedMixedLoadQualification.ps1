# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("smoke", "8h", "24h", "72h")][string]$Profile,
    [string]$OutputRoot = "artifacts/qualification/mixed-soak",
    [switch]$NoBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw "Mixed-load qualification requires Windows x64." }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) } else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$runRoot = Join-Path $resolvedOutput ([DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmss") + "-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$descriptorPath = Join-Path $repositoryRoot "qualification/soak/sustained-mixed-load.json"
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
$selected = @($descriptor.profiles | Where-Object name -eq $Profile)
if ($selected.Count -ne 1) { throw "Mixed-load profile must be defined exactly once." }
$source = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$') { throw "Exact source revision is required." }
& git -C $repositoryRoot diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw "Tracked source differs from the qualification commit." }
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -ne "10.0.401") { throw "Mixed-load qualification requires SDK 10.0.401." }

Push-Location $repositoryRoot
$names = @("RTAIME_MIXED_SOAK_PROFILE", "RTAIME_MIXED_SOAK_SOURCE", "RTAIME_MIXED_SOAK_BUILD",
    "RTAIME_MIXED_SOAK_SDK", "RTAIME_MIXED_SOAK_DESCRIPTOR", "RTAIME_MIXED_SOAK_EVIDENCE")
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
$evidence = Join-Path $runRoot "summary.json"
try {
    if (-not $NoBuild) {
        ./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Canonical Release build failed." }
    }
    # This factored matrix complements the long staged sessions; it is not a continuous layer-count soak.
    dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release --no-build `
        --filter "FullyQualifiedName~ProgramPipelineMeasurementBaselineTests" `
        --logger "trx;LogFileName=layer-matrix.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "Factored layer/consumer matrix failed." }
    [xml]$matrix = Get-Content -LiteralPath (Join-Path $runRoot "layer-matrix.trx") -Raw
    $matrixCounters = $matrix.TestRun.ResultSummary.Counters
    if ([int]$matrixCounters.executed -lt 1 -or [int]$matrixCounters.failed -ne 0 -or
        [int]$matrixCounters.passed -ne [int]$matrixCounters.executed) {
        throw "Factored matrix did not execute successful tests."
    }

    $env:RTAIME_MIXED_SOAK_PROFILE = $Profile
    $env:RTAIME_MIXED_SOAK_SOURCE = $source
    $env:RTAIME_MIXED_SOAK_BUILD = $source
    $env:RTAIME_MIXED_SOAK_SDK = $sdk
    $env:RTAIME_MIXED_SOAK_DESCRIPTOR = $descriptorPath
    $env:RTAIME_MIXED_SOAK_EVIDENCE = $evidence
    dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build `
        --filter "FullyQualifiedName~SustainedMixedLoadQualificationTests" `
        --logger "trx;LogFileName=staged-soak.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "Staged mixed-load qualification failed." }
    if (-not (Test-Path -LiteralPath $evidence -PathType Leaf)) { throw "The staged test did not emit evidence." }
    ./build/qualification/Test-SustainedMixedLoadEvidence.ps1 -EvidencePath $evidence `
        -DescriptorPath $descriptorPath -ExpectedSourceCommit $source -ExpectedProfile $Profile

    $manifest = [ordered]@{
        schemaVersion = "1.0"
        qualification = "rtaime-staged-mixed-load"
        sourceCommit = $source
        buildCommit = $source
        sdk = $sdk
        descriptorSha256 = (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash.ToLowerInvariant()
        profile = $Profile
        summarySha256 = (Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash.ToLowerInvariant()
        matrixSha256 = (Get-FileHash -LiteralPath (Join-Path $runRoot "layer-matrix.trx") -Algorithm SHA256).Hash.ToLowerInvariant()
        testResultsSha256 = (Get-FileHash -LiteralPath (Join-Path $runRoot "staged-soak.trx") -Algorithm SHA256).Hash.ToLowerInvariant()
        runId = $env:GITHUB_RUN_ID
        runAttempt = $env:GITHUB_RUN_ATTEMPT
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        physicalGpu = "UNVERIFIED"
        nativeNetworkTransport = "UNVERIFIED"
        physicalAvSynchronization = "UNVERIFIED"
        continuousSingleProduction = "UNVERIFIED"
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot "manifest.json") -Encoding utf8
    Write-Host "Staged mixed-load $Profile evidence verified; physical qualification remains UNVERIFIED."
    Write-Host "Evidence: $runRoot"
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process") }
    Pop-Location
}
