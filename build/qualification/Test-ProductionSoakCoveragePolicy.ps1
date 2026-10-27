# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param([string]$DescriptorPath = "qualification/soak/production-playout-coverage.json")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
function Require([bool]$valid, [string]$reason) { if (-not $valid) { throw "Production soak coverage policy: $reason" } }
function Read-Json([string]$path) {
    $full = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $repo $path }
    $file = Get-Item -LiteralPath $full -ErrorAction Stop
    Require ($file.Length -gt 0 -and $file.Length -le 1MB) "Unbounded/missing descriptor '$path'."
    return Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 40
}
$d = Read-Json $DescriptorPath
$ssd = Read-Json "qualification/soak/ssd-playback.json"
$mixed = Read-Json "qualification/soak/sustained-mixed-load.json"
$renderer = Read-Json "qualification/renderer/renderer-qualification.json"
$software = Read-Json "qualification/performance/realtime-playout-matrix.json"
$policy = Read-Json "docs/Governance/RepositoryPolicy.json"
Require ($d.schemaVersion -ceq "1.0" -and $d.qualification -ceq "rtaime-production-soak-coverage" -and
    $d.sdk -ceq "10.0.401" -and $d.releaseDecisionWithoutExternalEvidence -ceq "NO-GO" -and
    $d.releaseScope -ceq "READ_ONLY_EVIDENCE_INVENTORY_NOT_STABLE_APPROVAL") "Invalid release boundary."
Require ((@($d.requiredChecks) -join ",") -ceq (@($policy.requiredChecks) -join ",") -and
    (@($d.requiredChecks) -join ",") -ceq "CI,Quality,Security,Packaged E2E,Provider Smoke") "Drifted five Required Gates."
Require (@($d.cadences).Count -eq 3 -and
    (@($d.cadences | ForEach-Object format) -join ",") -ceq "1080p25,1080p50,1080p59.94" -and
    $d.cadences[0].admission -ceq "UNVERIFIED_NATIVE_PROGRAM_NO_QUALIFIED_PRODUCER" -and
    @($software.cadence | Where-Object format -EQ "1080p25")[0].evidence -ceq "UNVERIFIED") "False native 25-fps capability claim."
foreach ($key in @("ssd","mixed","renderer","softwareMatrix")) {
    Require (Test-Path -LiteralPath (Join-Path $repo $d.$key.descriptor) -PathType Leaf) "Missing $key descriptor."
    Require (Test-Path -LiteralPath (Join-Path $repo $d.$key.evidenceVerifier) -PathType Leaf) "Missing $key authoritative verifier."
}
Require (Test-Path -LiteralPath (Join-Path $repo $d.ssd.diagnosticVerifier) -PathType Leaf) "Missing bounded replay diagnostic verifier."
Require ($d.ssd.resourceBudget.maximumPrivateGrowthBytes -eq 536870912 -and
    $d.ssd.resourceBudget.maximumHandleGrowth -eq 64 -and
    $d.ssd.resourceBudget.maximumPrivateGrowthBytes -eq $ssd.resourcePolicy.maximumPrivateGrowthBytes -and
    $d.ssd.resourceBudget.maximumHandleGrowth -eq $ssd.resourcePolicy.maximumHandleGrowth) "SSD 512-MiB / 64-handle budget may not be relaxed."
Require ($d.mixed.resourceBudget.maximumPrivateGrowthBytes -eq 536870912 -and
    $d.mixed.resourceBudget.maximumHandleGrowth -eq 128 -and
    $d.mixed.resourceBudget.maximumPrivateGrowthBytes -eq $mixed.resourcePolicy.maximumPrivateGrowthBytes -and
    $d.mixed.resourceBudget.maximumHandleGrowth -eq $mixed.resourcePolicy.maximumHandleGrowth) "Managed 512-MiB / 128-handle budget drift."
Require ((@($d.ssd.formats) -join ",") -ceq "1080p50,1080p59.94" -and
    (@($d.ssd.profiles | ForEach-Object id) -join ",") -ceq (@($ssd.profiles | ForEach-Object name) -join ",")) "SSD profile set changed."
for ($i = 0; $i -lt @($ssd.profiles).Count; $i++) {
    Require ([int]$d.ssd.profiles[$i].seconds -eq [int]$ssd.profiles[$i].minimumSeconds) "SSD profile duration shortened."
}
Require ((@($d.mixed.profiles | ForEach-Object id) -join ",") -ceq (@($mixed.profiles | ForEach-Object name) -join ",")) "Mixed soak profiles drifted."
for ($i=0; $i -lt @($mixed.profiles).Count; $i++) {
    Require ([int]$d.mixed.profiles[$i].minimumSeconds -eq [int]$mixed.profiles[$i].minimumElapsedSeconds) "Mixed soak duration shortened."
}
Require ((@($d.renderer.longProfiles) -join ",") -ceq "soak8h,soak24h,soak72h" -and
    $renderer.performancePolicy.cudaReferenceQualificationRequired -eq $true -and
    @($renderer.profiles | Where-Object { $_.name -eq "soak72h" }).Count -eq 1) "Physical renderer profiles or hardware ownership weakened."
Require ($software.qualificationLimits.physicalOutput -ceq "UNVERIFIED" -and
    $mixed.physicalGpu -ceq "UNVERIFIED" -and
    $ssd.physicalStatus -ceq "UNVERIFIED") "Software evidence must not grant physical qualification."
Require ((@($d.reportStatusVocabulary) -join ",") -ceq "PASS,FAIL,UNVERIFIED") "Unsupported report status."
$names = @($d.faultCoverage | ForEach-Object scenario)
Require (@($names | Select-Object -Unique).Count -eq 7 -and $names.Count -eq 7) "Missing or repeated bounded fault scenario."
$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $repo "tests") -File -Recurse -Filter "*.cs" |
    ForEach-Object BaseName)
foreach ($f in @($d.faultCoverage)) {
    Require (-not [string]::IsNullOrWhiteSpace($f.evidence) -and
        $f.scope -cmatch '^SOFTWARE_|^IN_PROCESS_' -and
        $f.scope -cnotmatch '^PHYSICAL_PASS') "Fault simulation claimed physical proof."
    foreach ($testName in ([string]$f.evidence -split ';')) {
        Require ($testName -cmatch '^[A-Za-z][A-Za-z0-9]*Tests
Require (@($d.requiredPhysical).Count -eq 8 -and
    @($d.requiredPhysical | Select-Object -Unique).Count -eq 8) "Missing hardware/signoff dimension."
Require ((@($d.unresolvedRiskReviews | ForEach-Object issue) -join ",") -ceq "401,347" -and
    $d.unresolvedRiskReviews[0].requirement -ceq "SAME_SOURCE_REPEAT_CAUSAL_ANALYSIS_AND_NOT_LOWERED_LIMIT" -and
    $d.unresolvedRiskReviews[1].requirement -ceq "ADMINISTRATOR_ENABLE_AND_VERIFY_PR_ONLY_STRICT_FIVE_CHECKS") "Known sustained-memory or live branch-protection risk omitted."
Write-Host "Production soak descriptor PASS: source-bound lanes and unchanged limits; no runtime tests/hardware/release qualification claimed."
 -and
            $testFiles -ccontains $testName) "Unknown/nonexistent qualification source: $testName."
    }
}
Require (@($d.requiredPhysical).Count -eq 8 -and
    @($d.requiredPhysical | Select-Object -Unique).Count -eq 8) "Missing hardware/signoff dimension."
Require ((@($d.unresolvedRiskReviews | ForEach-Object issue) -join ",") -ceq "401,347" -and
    $d.unresolvedRiskReviews[0].requirement -ceq "SAME_SOURCE_REPEAT_CAUSAL_ANALYSIS_AND_NOT_LOWERED_LIMIT" -and
    $d.unresolvedRiskReviews[1].requirement -ceq "ADMINISTRATOR_ENABLE_AND_VERIFY_PR_ONLY_STRICT_FIVE_CHECKS") "Known sustained-memory or live branch-protection risk omitted."
Write-Host "Production soak descriptor PASS: source-bound lanes and unchanged limits; no runtime tests/hardware/release qualification claimed."
