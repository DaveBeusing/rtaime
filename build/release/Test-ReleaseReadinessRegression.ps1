# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$head = (& git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
$wrong = if ($head -eq ("a" * 40)) { "b" * 40 } else { "a" * 40 }
$temp = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-readiness-regression-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($temp)
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Json($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
}
function Expect-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Require $rejected "Expected rejection: $Name"
}
$props = Join-Path $root "Directory.Build.props"
$before = (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash
try {
    $started = [DateTimeOffset]::UtcNow.AddMinutes(-2).ToString("O")
    $completed = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString("O")
    $policy = Get-Content -LiteralPath (Join-Path $root "docs/Governance/RepositoryPolicy.json") -Raw | ConvertFrom-Json
    $jobs = @(); $id = 10
    foreach ($name in @($policy.requiredChecks)) {
        $jobs += @{ id = $id; name = $name; run_id = 1; run_attempt = 2; head_sha = $head
            status = "completed"; conclusion = "success"; started_at = $started; completed_at = $completed
            html_url = "https://github.com/DaveBeusing/rtaime/actions/runs/1/job/$id" }
        $id++
    }
    $fixture = @{ repository = "DaveBeusing/rtaime"; run = @{
        id = 1; name = "Required gates"; path = ".github/workflows/required-gates.yml"
        repository = @{ full_name = "DaveBeusing/rtaime" }; head_sha = $head; run_attempt = 2
        event = "pull_request"; status = "in_progress"; conclusion = $null
        html_url = "https://github.com/DaveBeusing/rtaime/actions/runs/1" }; jobs = $jobs }
    $snapshot = Join-Path $temp "gates.json"
    Write-Json $fixture $snapshot
    & (Join-Path $PSScriptRoot "Test-RequiredGateEvidence.ps1") -EvidencePath $snapshot -ExpectedSourceCommit $head
    $cases = @(
        @{ name = "stale source"; mutate = { param($e) $e.run.head_sha = $wrong } },
        @{ name = "stale job source"; mutate = { param($e) $e.jobs[0].head_sha = $wrong } },
        @{ name = "another run"; mutate = { param($e) $e.jobs[0].run_id = 9 } },
        @{ name = "another attempt"; mutate = { param($e) $e.jobs[0].run_attempt = 1 } },
        @{ name = "pending job"; mutate = { param($e) $e.jobs[0].status = "in_progress" } },
        @{ name = "cancelled job"; mutate = { param($e) $e.jobs[0].conclusion = "cancelled" } },
        @{ name = "failed job"; mutate = { param($e) $e.jobs[0].conclusion = "failure" } },
        @{ name = "missing job"; mutate = { param($e) $e.jobs = @($e.jobs | Select-Object -Skip 1) } },
        @{ name = "duplicate job"; mutate = { param($e) $e.jobs = @($e.jobs) + @($e.jobs[0]) } },
        @{ name = "other repository"; mutate = { param($e) $e.run.repository.full_name = "other/rtaime" } },
        @{ name = "failed workflow"; mutate = { param($e) $e.run.status = "completed"; $e.run.conclusion = "failure" } }
    )
    foreach ($case in $cases) {
        $e = $fixture | ConvertTo-Json -Depth 64 | ConvertFrom-Json
        & $case.mutate $e
        Write-Json $e $snapshot
        Expect-Rejected { & (Join-Path $PSScriptRoot "Test-RequiredGateEvidence.ps1") -EvidencePath $snapshot -ExpectedSourceCommit $head } $case.name
    }
    Write-Json $fixture $snapshot
    $indexPath = Join-Path $temp "readiness.json"
    $r = & (Join-Path $PSScriptRoot "New-ReleaseReadinessIndex.ps1") -ExpectedSourceCommit $head -RequiredGatesPath $snapshot -OutputPath $indexPath
    Require ($r.decision -eq "NO-GO" -and $r.overallStatus -eq "UNVERIFIED" -and $r.releasePromotionPerformed -eq $false) "Five software gates cannot grant Stable readiness."
    Require (@($r.domains | Where-Object { $_.name -eq "requiredGates" -and $_.status -eq "PASS" }).Count -eq 1) "Completed exact-source gates must be individually recognized."
    foreach ($domain in @("productionSigningTrust", "knownIssues", "requiredHardwareEvidence", "independentBuildReproducibility", "stagedMixedLoad.8h", "stagedMixedLoad.24h", "stagedMixedLoad.72h", "continuousSingleProduction72h")) {
        Require (@($r.domains | Where-Object { $_.name -eq $domain -and $_.status -eq "UNVERIFIED" }).Count -eq 1) "Missing $domain must remain UNVERIFIED."
    }
    $indexHash = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
    Expect-Rejected { & (Join-Path $PSScriptRoot "New-ReleaseReadinessIndex.ps1") -ExpectedSourceCommit $head -OutputPath $indexPath } "retained evidence overwrite"
    Require ((Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash -eq $indexHash) "Existing index bytes changed."
    $stale = & (Join-Path $PSScriptRoot "New-ReleaseReadinessIndex.ps1") -ExpectedSourceCommit $wrong -OutputPath (Join-Path $temp "stale.json")
    Require ($stale.overallStatus -eq "FAIL" -and $stale.decision -eq "NO-GO") "Mismatched source must fail the aggregate."
    $oldVersion = $env:RTAIME_PRODUCT_VERSION; $oldStage = $env:RTAIME_RELEASE_STAGE
    try {
        $env:RTAIME_PRODUCT_VERSION = "0.1.0-rc.1"; $env:RTAIME_RELEASE_STAGE = "RELEASE_CANDIDATE"
        $rc = & (Join-Path $PSScriptRoot "New-ReleaseReadinessIndex.ps1") -ExpectedSourceCommit $head -OutputPath (Join-Path $temp "rc.json")
        Require ($rc.product.releaseStage -eq "RELEASE_CANDIDATE" -and $rc.decision -eq "NO-GO" -and $rc.releasePromotionPerformed -eq $false) "An RC identity must remain unpromoted without completed release obligations."
        Require ($env:RTAIME_PRODUCT_VERSION -eq "0.1.0-rc.1" -and $env:RTAIME_RELEASE_STAGE -eq "RELEASE_CANDIDATE") "Assessment failed to restore caller build identity."
    } finally { $env:RTAIME_PRODUCT_VERSION = $oldVersion; $env:RTAIME_RELEASE_STAGE = $oldStage }
    # Exercise the actual platform evidence function without mutating production support policy.
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot "Test-StableReadiness.ps1"), [ref]$tokens, [ref]$errors)
    Require (@($errors).Count -eq 0) "Stable verifier parse errors."
    $script:repositoryRoot = $root
    $script:expectedSourceCommit = $head
    foreach ($name in @("Assert-Condition", "Resolve-RepositoryPath", "Normalize-IdentityValue", "Test-PassedQualificationEvidence")) {
        $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    $platformPath = Join-Path $temp "platform.json"
    Write-Json @{ status = "PASSED"; sourceCommit = $head } $platformPath
    Require (Test-PassedQualificationEvidence $platformPath) "Same-source platform evidence rejected."
    Write-Json @{ status = "PASSED"; sourceCommit = $wrong } $platformPath
    Require (-not (Test-PassedQualificationEvidence $platformPath)) "Stale platform PASS accepted."
    Write-Json @{ status = "UNVERIFIED"; sourceCommit = $head } $platformPath
    Require (-not (Test-PassedQualificationEvidence $platformPath)) "Unverified platform accepted."
    $rendererAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root "build/qualification/Test-RendererReferenceQualification.ps1"), [ref]$tokens, [ref]$errors)
    Require (@($errors).Count -eq 0) "Renderer verifier parse errors."
    $identityFunction = $rendererAst.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq "Assert-RendererEvidenceIdentity" }, $true)
    . ([scriptblock]::Create($identityFunction.Extent.Text))
    $workloadHash = (Get-FileHash -LiteralPath (Join-Path $root "qualification/renderer/renderer-qualification.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestFixture = @{ repository = "DaveBeusing/rtaime"; profileSha256 = $workloadHash; capturedAtUtc = $started
        gpu = @{ ordinal = 0; pciBusId = "00000000:01:00.0"; totalMemoryMiB = 16384 }
        operatingSystem = @{ architecture = "X64"; version = "10.0"; buildNumber = "synthetic" }
        dotnet = @{ processArchitecture = "X64" }; workflow = @{ runId = "local"; runAttempt = "local"; runnerName = "synthetic" } } | ConvertTo-Json -Depth 32 | ConvertFrom-Json
    $executionFixture = @{ deviceOrdinal = 0; capturedAtUtc = $completed }
    Assert-RendererEvidenceIdentity $manifestFixture $executionFixture $workloadHash
    $manifestFixture.profileSha256 = "0" * 64
    Expect-Rejected { Assert-RendererEvidenceIdentity $manifestFixture $executionFixture $workloadHash } "renderer workload mismatch"
    $manifestFixture.profileSha256 = $workloadHash; $manifestFixture.gpu.ordinal = 1
    Expect-Rejected { Assert-RendererEvidenceIdentity $manifestFixture $executionFixture $workloadHash } "renderer device mismatch"
    $manifestFixture.gpu.ordinal = 0; $manifestFixture.workflow.runAttempt = "2"
    Expect-Rejected { Assert-RendererEvidenceIdentity $manifestFixture $executionFixture $workloadHash } "mixed local/remote renderer identity"
    Require ((Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash -eq $before) "Readiness changed the product release identity."
    Write-Host "Release readiness correlation and no-false-Stable regressions PASS. Synthetic fixtures are not release or hardware evidence."
} finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith("rtaime-readiness-regression-", [StringComparison]::Ordinal)) {
        throw "Refusing cleanup outside the dedicated regression directory."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
