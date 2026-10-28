# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportPath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [switch]$RequireSoftwareQualified
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$matrixPath = Join-Path $root "qualification/broadcast/interoperability-matrix.json"
$configPath = Join-Path $root "qualification/broadcast/reference-configurations.json"

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Broadcast interoperability evidence rejected: $Message" }
}
function Read-Json([string]$Path) {
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Missing evidence file '$Path'."
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 64
}
function Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Resolve-Evidence([string]$Relative) {
    Require (-not [string]::IsNullOrWhiteSpace($Relative)) "Empty evidence path."
    Require (-not [IO.Path]::IsPathRooted($Relative)) "Evidence paths must be repository-relative."
    Require ($Relative -notmatch '(^|[\\/])\.\.([\\/]|$)') "Evidence path traversal is forbidden."
    $full = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    Require ($full.StartsWith(($root + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) "Evidence path escapes repository."
    Require ($Relative.Replace('\','/').StartsWith("artifacts/qualification/broadcast/", [StringComparison]::Ordinal)) "Evidence path must remain in the broadcast qualification artifact directory."
    return $full
}
function Exact-Fields($Object, [string[]]$Allowed, [string]$Context) {
    foreach ($field in @($Object.PSObject.Properties.Name)) {
        Require ($Allowed -contains [string]$field) "Unexpected $Context field '$field' (unapproved claim/signature metadata)."
    }
    foreach ($required in $Allowed) {
        Require ($null -ne $Object.PSObject.Properties[$required]) "Missing required $Context field '$required'."
    }
}
function Parse-Trx([string]$Path, [string[]]$Classes) {
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    Require ($file.Length -gt 0 -and $file.Length -le 32MB) "TRX must be a bounded nonempty file."
    $readerSettings = [System.Xml.XmlReaderSettings]::new()
    $readerSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver = $null
    $readerSettings.MaxCharactersInDocument = 32MB
    $reader = [System.Xml.XmlReader]::Create($Path, $readerSettings)
    try {
        $trx = [System.Xml.XmlDocument]::new()
        $trx.XmlResolver = $null
        $trx.Load($reader)
    } finally { $reader.Dispose() }
    $defs = @{}
    foreach ($node in @($trx.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']"))) {
        $id = [string]$node.GetAttribute("id")
        $method = $node.SelectSingleNode("./*[local-name()='TestMethod']")
        Require ($null -ne $method -and $id.Length -gt 0) "TRX test definition lacks identity."
        Require (-not $defs.ContainsKey($id)) "Duplicate TRX test definition."
        $defs[$id] = [string]$method.GetAttribute("className")
    }
    $summary = $trx.SelectSingleNode("//*[local-name()='ResultSummary']")
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    $times = $trx.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
    Require ($null -ne $summary -and $null -ne $counters -and $null -ne $times) "TRX summary, counters or times missing."
    $started = [DateTimeOffset]::MinValue
    $ended = [DateTimeOffset]::MinValue
    Require ([DateTimeOffset]::TryParse([string]$times.GetAttribute("start"), [ref]$started)) "TRX start timestamp invalid."
    Require ([DateTimeOffset]::TryParse([string]$times.GetAttribute("finish"), [ref]$ended)) "TRX end timestamp invalid."
    Require ($ended -ge $started -and $ended -le [DateTimeOffset]::UtcNow.AddMinutes(5)) "TRX timestamps reversed or in the future."
    Require (($ended - $started).TotalMinutes -le 20) "TRX exceeds the 20-minute controlled suite budget."
    $results = @($trx.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    Require ($results.Count -gt 0) "TRX contains no executed unit test results."
    Require ([int]$counters.GetAttribute("total") -eq $results.Count -and
        [int]$counters.GetAttribute("executed") -eq $results.Count) "TRX total/executed counters differ from results."
    Require ([int]$counters.GetAttribute("passed") -le $results.Count) "TRX passed counter exceeds executed tests."
    $seen = @{}
    $counts = @{}
    $classesSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # VSTest TRX uses ResultSummary outcome=Completed for a fully successful run;
    # individual UnitTestResult rows and exact counters must still all be Passed.
    $allPassed = ([string]$summary.GetAttribute("outcome") -in @("Completed","Passed"))
    foreach ($result in $results) {
        $testId = [string]$result.GetAttribute("testId")
        $executionId = [string]$result.GetAttribute("executionId")
        Require ($defs.ContainsKey($testId) -and $executionId.Length -gt 0) "TRX result lacks its test definition or execution identity."
        Require (-not $seen.ContainsKey($executionId)) "Duplicate TRX execution identity."
        $seen[$executionId] = $true
        $className = $defs[$testId]
        Require ($Classes -contains $className) "TRX result class '$className' was not selected in the qualification suite."
        $null = $classesSeen.Add($className)
        $counts[$className] = 1 + $(if ($counts.ContainsKey($className)) { [int]$counts[$className] } else { 0 })
        if ([string]$result.GetAttribute("outcome") -ne "Passed") { $allPassed = $false }
    }
    foreach ($expected in $Classes) {
        Require ($classesSeen.Contains($expected)) "Expected test class '$expected' was not executed."
    }
    if ([int]$counters.GetAttribute("passed") -ne $results.Count -or
        [int]$counters.GetAttribute("failed") -gt 0 -or
        [int]$counters.GetAttribute("error") -gt 0 -or
        [int]$counters.GetAttribute("notExecuted") -gt 0 -or
        [int]$counters.GetAttribute("timeout") -gt 0 -or
        [int]$counters.GetAttribute("aborted") -gt 0 -or
        [int]$counters.GetAttribute("inconclusive") -gt 0) { $allPassed = $false }
    return [pscustomobject]@{
        passed = $allPassed
        count = $results.Count
        date = $ended.ToUniversalTime().ToString("O")
        classes = @($classesSeen | Sort-Object)
        counts = $counts
    }
}

Require ($ExpectedSourceCommit -cmatch '^[0-9a-f]{40}$') "Expected source commit must be an exact lowercase SHA-1."
$matrix = Read-Json $matrixPath
$config = Read-Json $configPath
$report = Read-Json ([IO.Path]::GetFullPath($ReportPath))
Require ($matrix.schemaVersion -eq "1.0" -and $config.schemaVersion -eq "1.0" -and $report.schemaVersion -eq "1.0") "Unsupported matrix/report schema."
Require ($matrix.repository -ceq "DaveBeusing/rtaime" -and $config.repository -ceq $matrix.repository -and $report.repository -ceq $matrix.repository) "Repository identity mismatch."
Require ($report.sourceCommit -ceq $ExpectedSourceCommit) "Report source SHA mismatch."
Exact-Fields $report @("copyright","schemaVersion","repository","sourceCommit","generatedAtUtc",
    "matrixSha256","configurationsSha256","assessmentScope","certificationClaim",
    "releasePromotionPerformed","overallStatus","decision","configurations","suites",
    "requirements") "report"
$reportDate = [DateTimeOffset]::MinValue
Require ([DateTimeOffset]::TryParse([string]$report.generatedAtUtc, [ref]$reportDate) -and
    $reportDate -le [DateTimeOffset]::UtcNow.AddMinutes(5)) "Report generatedAtUtc is absent or in the future."
Require ($report.matrixSha256 -ceq (Hash $matrixPath) -and $report.configurationsSha256 -ceq (Hash $configPath)) "Qualification matrix/configuration hash mismatch."
Require ($matrix.assessmentPolicy -ceq "SCOPED_EVIDENCE_NO_CERTIFICATION") "Matrix policy cannot grant certification."
Require ($report.certificationClaim -ceq $false -and $report.releasePromotionPerformed -ceq $false -and $report.decision -ceq "NO-GO") "Report must not authorize certification or release promotion."
Require (@($matrix.standards).Count -ge 10 -and @($matrix.suites).Count -ge 8) "Normative inventory or suite coverage incomplete."
Require (@($matrix.requirements).Count -ge 20) "Requirement matrix incomplete."
$standards = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($s in @($matrix.standards)) {
    Require ($standards.Add([string]$s.id)) "Duplicate standard identifier."
    Require ([string]$s.edition -ne "" -and [string]$s.organization -in @("EBU","SMPTE","AMWA","rtaime")) "Standard publisher or version not pinned."
    Require ([string]$s.referenceUrl -match '^https://') "Standard source must have a pinned HTTPS reference."
}
$profiles = @{}
$recordedProfiles = @{}
foreach ($profile in @($report.configurations)) {
    Exact-Fields $profile @("profileId","class","hardwareStatus","operatingSystem","gpu","driver",
        "nic","switch","codec","display","recorder","receiver","ptp") "reference configuration"
    Require (-not $recordedProfiles.ContainsKey([string]$profile.profileId)) "Duplicate device matrix configuration."
    $recordedProfiles[[string]$profile.profileId] = $profile
}
foreach ($p in @($config.configurations)) {
    Require (-not $profiles.ContainsKey([string]$p.id)) "Duplicate reference configuration."
    Require ([string]$p.status -eq "UNVERIFIED") "Static configuration cannot assert qualification."
    Require ([string]$p.host -eq "Windows x64 .NET 10.0.401") "Reference platform SDK/OS is not the pinned source baseline."
    if ([string]$p.class -eq "REFERENCE_HARDWARE") {
        foreach ($field in @("gpu","driver","nic","switch","codec","display","recorder","receiver","ptp")) {
            Require ($null -eq $p.$field) "Reference device inventory '$field' requires independent evidence; no placeholder hardware claims."
        }
    }
    $profiles[[string]$p.id] = $p
    Require ($recordedProfiles.ContainsKey([string]$p.id)) "Device matrix profile missing."
    $recorded = $recordedProfiles[[string]$p.id]
    Require ([string]$recorded.class -ceq [string]$p.class -and
        [string]$recorded.operatingSystem -ceq [string]$p.host -and
        [string]$recorded.hardwareStatus -ceq "UNVERIFIED") "Device inventory contradicts pinned profile."
    foreach ($field in @("gpu","driver","nic","switch","codec","display","recorder","receiver","ptp")) {
        Require ([string]$recorded.$field -ceq [string]$p.$field) "Device inventory field '$field' differs from the controlled configuration."
    }
}
Require ($recordedProfiles.Count -eq $profiles.Count) "Unknown device profile in report."
$expectedSuiteIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$actualSuites = @{}
foreach ($actual in @($report.suites)) {
    Exact-Fields $actual @("id","status","testCount","testDateUtc","evidence") "suite"
    if ($null -ne $actual.evidence) {
        Exact-Fields $actual.evidence @("testProject","path","sha256","runId",
            "runAttempt","exitCode") "test evidence"
    }
    Require (-not $actualSuites.ContainsKey([string]$actual.id)) "Duplicate suite result."
    $actualSuites[[string]$actual.id] = $actual
}
foreach ($suite in @($matrix.suites)) {
    $id = [string]$suite.id
    Require ($expectedSuiteIds.Add($id) -and $actualSuites.ContainsKey($id)) "Suite '$id' missing or duplicated."
    $entry = $actualSuites[$id]
    Require ([string]$entry.status -in @("QUALIFIED","FAILED","UNVERIFIED")) "Invalid software suite status."
    Require (@($suite.expectedClasses).Count -gt 0 -and [string]$suite.testProject -match '^tests/rtaime\.Tests\.(Unit|Contracts|Integration)/') "Suite has no qualified test/project."
    if ([string]$entry.status -eq "UNVERIFIED") {
        Require ($null -eq $entry.evidence -and [int]$entry.testCount -eq 0 -and $null -eq $entry.testDateUtc) "UNVERIFIED suite cannot contain an unverified PASS assertion."
        continue
    }
    Require ($null -ne $entry.evidence) "Observed suite requires actual hashed test evidence."
    $e = $entry.evidence
    Require ([string]$e.testProject -ceq [string]$suite.testProject) "TRX/project mismatch."
    Require ([string]$e.runId -match '^[1-9][0-9]*$' -and [int]$e.runAttempt -ge 1) "Workflow run/attempt identity missing."
    $trxFull = Resolve-Evidence ([string]$e.path)
    Require (Test-Path -LiteralPath $trxFull -PathType Leaf) "TRX artifact is missing."
    Require ([string]$e.sha256 -cmatch '^[0-9a-f]{64}$' -and [string]$e.sha256 -ceq (Hash $trxFull)) "TRX digest mismatch."
    $classes = @($matrix.suites | Where-Object { $_.testProject -eq $suite.testProject } | ForEach-Object { $_.expectedClasses } | Sort-Object -Unique)
    $parsed = Parse-Trx $trxFull $classes
    $expectedSuiteCount = 0
    foreach ($testClass in @($suite.expectedClasses)) {
        Require ($parsed.counts.ContainsKey([string]$testClass)) "Suite missing executed class '$testClass'."
        $expectedSuiteCount += [int]$parsed.counts[[string]$testClass]
    }
    Require ([int]$entry.testCount -eq $expectedSuiteCount -and $expectedSuiteCount -ge [int]$suite.minimumTests) "Suite count does not match executed TRX cases."
    # ConvertFrom-Json may materialize ISO-8601 values as DateTime objects.
    # Compare the instant, not a culture-dependent string representation.
    $recordedDate = [DateTimeOffset]$entry.testDateUtc
    $parsedInstant = [DateTimeOffset]::Parse([string]$parsed.date,
        [Globalization.CultureInfo]::InvariantCulture)
    Require ($recordedDate.ToUniversalTime().Ticks -eq
        $parsedInstant.ToUniversalTime().Ticks) "Suite date differs from the recorded TRX."
    $testDate = [DateTimeOffset]::MinValue
    Require ([DateTimeOffset]::TryParse([string]$parsed.date, [ref]$testDate) -and
        $testDate -le $reportDate.AddMinutes(2) -and
        $testDate -ge $reportDate.AddMinutes(-25)) "TRX timestamp is stale or not correlated to the report generation window."
    $calculatedStatus = if ($parsed.passed -and [int]$e.exitCode -eq 0) { "QUALIFIED" } else { "FAILED" }
    Require ([string]$entry.status -eq $calculatedStatus) "Suite state contradicts the executed tests."
}
Require ($actualSuites.Count -eq $expectedSuiteIds.Count) "Unexpected suite result in report."
$actualReqs = @{}
foreach ($entry in @($report.requirements)) {
    Exact-Fields $entry @("id","standardId","organization","edition","profileId",
        "scope","implementer","assessor","fixture","signalExpectation","status",
        "testDateUtc","evidenceSha256") "requirement"
    Require (-not $actualReqs.ContainsKey([string]$entry.id)) "Duplicate requirement result."
    $actualReqs[[string]$entry.id] = $entry
}
$anyFailure = $false
foreach ($requirement in @($matrix.requirements)) {
    $id = [string]$requirement.id
    Require ($actualReqs.ContainsKey($id)) "Missing requirement '$id'."
    Require ($standards.Contains([string]$requirement.standardId) -and $profiles.ContainsKey([string]$requirement.profileId)) "Requirement references unknown standard/profile."
    $actual = $actualReqs[$id]
    Require ([string]$actual.standardId -ceq [string]$requirement.standardId -and
        [string]$actual.profileId -ceq [string]$requirement.profileId -and
        [string]$actual.scope -ceq [string]$requirement.scope -and
        [string]$actual.implementer -ceq [string]$requirement.implementer -and
        [string]$actual.assessor -ceq [string]$requirement.assessor -and
        [string]$actual.fixture -ceq [string]$requirement.fixture -and
        [string]$actual.signalExpectation -ceq [string]$requirement.signalExpectation -and
        [string]$actual.edition -ceq [string](@($matrix.standards | Where-Object id -eq $requirement.standardId)[0].edition) -and
        [string]$actual.organization -ceq [string](@($matrix.standards | Where-Object id -eq $requirement.standardId)[0].organization)) "Requirement metadata was replaced."
    Require ([string]$requirement.defaultStatus -ceq "UNVERIFIED") "Static requirement cannot pre-authorize PASS."
    $expectedStatus = "UNVERIFIED"
    if ([string]$requirement.scope -eq "software") {
        Require ($expectedSuiteIds.Contains([string]$requirement.suiteId)) "Software requirement has no known suite."
        $associatedSuite = $actualSuites[[string]$requirement.suiteId]
        $expectedStatus = [string]$associatedSuite.status
        $expectedHash = if ($expectedStatus -eq "UNVERIFIED") { "" } else { [string]$associatedSuite.evidence.sha256 }
        $expectedDate = if ($expectedStatus -eq "UNVERIFIED") { "" } else { [string]$associatedSuite.testDateUtc }
        Require ([string]$actual.evidenceSha256 -ceq $expectedHash -and
            [string]$actual.testDateUtc -ceq $expectedDate) "Software requirement evidence does not match the verified test suite."
    } else {
        Require ([string]$requirement.scope -eq "physical" -and $null -eq $requirement.suiteId) "Physical requirements cannot inherit software test suites."
        Require ([string]$actual.evidenceSha256 -eq "" -and $null -eq $actual.testDateUtc) "Physical test evidence must be independently qualified; none is admitted by this report."
    }
    Require ([string]$actual.status -ceq $expectedStatus) "Requirement '$id' status contradicts approved evidence."
    if ($expectedStatus -eq "FAILED") { $anyFailure = $true }
}
Require ($actualReqs.Count -eq @($matrix.requirements).Count) "Unexpected requirement result."
$expectedOverall = if ($anyFailure) { "FAILED" } else { "UNVERIFIED" }
Require ([string]$report.overallStatus -ceq $expectedOverall) "Report overall state is inconsistent."
if ($RequireSoftwareQualified) {
    Require (@($report.suites | Where-Object { $_.status -ne "QUALIFIED" }).Count -eq 0) "Not every selected software suite is QUALIFIED."
}
Write-Host "Broadcast interoperability report verified: $($report.overallStatus); no physical/certification/promotion claim."
