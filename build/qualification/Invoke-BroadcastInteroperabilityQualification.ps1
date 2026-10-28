# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [long]$RunId = 0,
    [int]$RunAttempt = 0,
    [switch]$RunTests,
    [string]$OutputDirectory = "artifacts/qualification/broadcast",
    [string]$OutputPath = "artifacts/qualification/broadcast/interoperability-report.json"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$matrixPath = Join-Path $root "qualification/broadcast/interoperability-matrix.json"
$configPath = Join-Path $root "qualification/broadcast/reference-configurations.json"
$verifier = Join-Path $PSScriptRoot "Test-BroadcastInteroperabilityReport.ps1"
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Resolve-Workspace([string]$Path) {
    $resolved = if ([IO.Path]::IsPathRooted($Path)) { [IO.Path]::GetFullPath($Path) }
                else { [IO.Path]::GetFullPath((Join-Path $root $Path)) }
    Require ($resolved.StartsWith(($root + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) "Output must be within the repository workspace."
    Require ($resolved.Replace('\','/').Contains("/artifacts/qualification/broadcast/") -or
        $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar).Replace('\','/').EndsWith("/artifacts/qualification/broadcast")) "Output must remain in the broadcast evidence directory."
    return $resolved
}
function Get-Trx([string]$Path) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $classes = @{}
    foreach ($def in @($xml.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']"))) {
        $method = $def.SelectSingleNode("./*[local-name()='TestMethod']")
        if ($null -ne $method) { $classes[[string]$def.GetAttribute("id")] = [string]$method.GetAttribute("className") }
    }
    $summary = $xml.SelectSingleNode("//*[local-name()='ResultSummary']")
    $times = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
    $end = [DateTimeOffset]::Parse([string]$times.GetAttribute("finish"), [Globalization.CultureInfo]::InvariantCulture)
    $results = foreach ($row in @($xml.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']"))) {
        $testId = [string]$row.GetAttribute("testId")
        [pscustomobject]@{ className = if ($classes.ContainsKey($testId)) { $classes[$testId] } else { "" }
            outcome = [string]$row.GetAttribute("outcome") }
    }
    return [pscustomobject]@{ date = $end.ToUniversalTime().ToString("O");
        overallPassed = ([string]$summary.GetAttribute("outcome") -in @("Completed","Passed"));
        tests = @($results) }
}
Require ($ExpectedSourceCommit -cmatch '^[0-9a-f]{40}$') "ExpectedSourceCommit must be an exact lowercase Git SHA."
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json -Depth 64
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -Depth 64
$outDir = Resolve-Workspace $OutputDirectory
$outFile = Resolve-Workspace $OutputPath
Require (-not (Test-Path -LiteralPath $outFile)) "Report already exists; preserve source-bound evidence."
[void][IO.Directory]::CreateDirectory($outDir)
if ($RunTests) {
    Require ($RunId -gt 0 -and $RunAttempt -gt 0) "RunTests requires a concrete workflow run ID and attempt."
    $head = (& git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
    Require ($LASTEXITCODE -eq 0 -and $head -eq $ExpectedSourceCommit) "Checked-out source commit does not match requested qualification SHA."
}
$projectRuns = @{}
foreach ($group in @($matrix.suites | Group-Object testProject | Sort-Object Name)) {
    $project = [string]$group.Name
    $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
    $trxFile = Join-Path $outDir "$projectName.trx"
    if (-not $RunTests) { continue }
    $allClasses = @($group.Group | ForEach-Object { $_.expectedClasses } | Sort-Object -Unique)
    $filter = (@($allClasses | ForEach-Object { "FullyQualifiedName~$_" }) -join "|")
    Require (@($allClasses).Count -gt 0) "Suite group has no tests."
    Require (Test-Path -LiteralPath (Join-Path $root $project) -PathType Leaf) "Suite test project missing."
    Write-Host "Broadcast software qualification $projectName against $ExpectedSourceCommit"
    Require (-not (Test-Path -LiteralPath $trxFile)) "A retained TRX file already exists; no stale evidence reuse."
    $testArgs = @("test", (Join-Path $root $project), "--configuration", "Release", "--no-build",
        "--filter", $filter, "--logger", "trx;LogFileName=$projectName.trx",
        "--results-directory", $outDir)
    & dotnet @testArgs
    $exitCode = [int]$LASTEXITCODE
    Require (Test-Path -LiteralPath $trxFile -PathType Leaf) "Missing TRX from $projectName (exit $exitCode)."
    $projectRuns[$project] = [pscustomobject]@{
        trxPath = "artifacts/qualification/broadcast/$projectName.trx"
        sha256 = (Get-FileHash -LiteralPath $trxFile -Algorithm SHA256).Hash.ToLowerInvariant()
        exitCode = $exitCode
        parsed = Get-Trx $trxFile
    }
}
$recordedSuites = foreach ($suite in @($matrix.suites)) {
    $id = [string]$suite.id
    $status = "UNVERIFIED"
    $count = 0
    $stamp = $null
    $evidence = $null
    if ($projectRuns.ContainsKey([string]$suite.testProject)) {
        $run = $projectRuns[[string]$suite.testProject]
        $tests = @($run.parsed.tests | Where-Object { @($suite.expectedClasses) -contains [string]$_.className })
        $count = $tests.Count
        $stamp = [string]$run.parsed.date
        $allExpected = @($suite.expectedClasses | Where-Object { $wanted = $_; @($tests | Where-Object className -eq $wanted).Count -eq 0 }).Count -eq 0
        $passed = ($allExpected -and $tests.Count -ge [int]$suite.minimumTests -and
            @($tests | Where-Object outcome -ne "Passed").Count -eq 0 -and
            $run.parsed.overallPassed -and $run.exitCode -eq 0)
        $status = if ($passed) { "QUALIFIED" } else { "FAILED" }
        $evidence = [ordered]@{
            testProject = [string]$suite.testProject
            path = [string]$run.trxPath
            sha256 = [string]$run.sha256
            runId = [string]$RunId
            runAttempt = [int]$RunAttempt
            exitCode = [int]$run.exitCode
        }
    }
    [ordered]@{ id = $id; status = $status; testCount = $count; testDateUtc = $stamp; evidence = $evidence }
}
$standardMap = @{}
foreach ($standard in @($matrix.standards)) { $standardMap[[string]$standard.id] = $standard }
$suiteMap = @{}
foreach ($suite in @($recordedSuites)) { $suiteMap[[string]$suite.id] = $suite }
$records = foreach ($row in @($matrix.requirements)) {
    $standard = $standardMap[[string]$row.standardId]
    $status = if ([string]$row.scope -eq "software") { [string]$suiteMap[[string]$row.suiteId].status } else { "UNVERIFIED" }
    $evidence = if ([string]$row.scope -eq "software" -and $status -ne "UNVERIFIED") {
        $suiteMap[[string]$row.suiteId].evidence
    } else { $null }
    [ordered]@{
        id = [string]$row.id
        standardId = [string]$row.standardId
        organization = [string]$standard.organization
        edition = [string]$standard.edition
        profileId = [string]$row.profileId
        scope = [string]$row.scope
        implementer = [string]$row.implementer
        assessor = [string]$row.assessor
        fixture = [string]$row.fixture
        signalExpectation = [string]$row.signalExpectation
        status = $status
        testDateUtc = if ($null -ne $evidence) { [string]$suiteMap[[string]$row.suiteId].testDateUtc } else { $null }
        evidenceSha256 = if ($null -ne $evidence) { [string]$evidence.sha256 } else { $null }
    }
}
$anyFailed = @($recordedSuites | Where-Object status -eq "FAILED").Count -gt 0
$report = [ordered]@{
    copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
    schemaVersion = "1.0"
    repository = "DaveBeusing/rtaime"
    sourceCommit = $ExpectedSourceCommit
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    matrixSha256 = (Get-FileHash -LiteralPath $matrixPath -Algorithm SHA256).Hash.ToLowerInvariant()
    configurationsSha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    assessmentScope = "software source evidence and unverified physical assessment inventory"
    certificationClaim = $false
    releasePromotionPerformed = $false
    overallStatus = if ($anyFailed) { "FAILED" } else { "UNVERIFIED" }
    decision = "NO-GO"
    configurations = @($config.configurations | ForEach-Object {
        [ordered]@{ profileId = [string]$_.id; class = [string]$_.class; hardwareStatus = "UNVERIFIED";
            operatingSystem = [string]$_.host; gpu = $_.gpu; driver = $_.driver; nic = $_.nic;
            switch = $_.switch; codec = $_.codec; display = $_.display;
            recorder = $_.recorder; receiver = $_.receiver; ptp = $_.ptp }
    })
    suites = @($recordedSuites)
    requirements = @($records)
}
$json = $report | ConvertTo-Json -Depth 64
[IO.File]::WriteAllText($outFile, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
& $verifier -ReportPath $outFile -ExpectedSourceCommit $ExpectedSourceCommit -RequireSoftwareQualified:$RunTests
if ($RunTests -and @($recordedSuites | Where-Object status -ne "QUALIFIED").Count -gt 0) {
    throw "Incomplete or failed source-bound broadcast software qualification; report retained at '$outFile'."
}
Write-Host "Broadcast interoperability report: $outFile (physical UNVERIFIED; no release promotion)."
