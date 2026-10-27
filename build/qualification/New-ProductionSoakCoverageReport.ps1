# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [string[]]$SsdEvidencePaths = @(),
    [string[]]$MixedEvidencePaths = @(),
    [string]$RealtimeEvidencePath = "",
    [string]$RequiredGatesPath = "",
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
& (Join-Path $PSScriptRoot "Test-ProductionSoakCoveragePolicy.ps1")
$head = (& git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedSourceCommit) {
    throw "Production soak inventory must be generated from its exact checked-out source."
}
$policyFile = Join-Path $root "qualification/soak/production-playout-coverage.json"
$policy = Get-Content -LiteralPath $policyFile -Raw | ConvertFrom-Json -Depth 32
$statuses = [Collections.Generic.List[object]]::new()
$artifacts = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
function Add-Status([string]$id, [string]$status, [string]$scope, [string]$reason) {
    if ($status -cnotin @("PASS","FAIL","UNVERIFIED") -or -not $seen.Add($id)) {
        throw "Duplicate/unsupported evidence status key: $id."
    }
    $statuses.Add([ordered]@{ id=$id; status=$status; scope=$scope; reason=$reason })
}
function Read-Input([string]$path, [long]$maxBytes) {
    $absolute = if ([IO.Path]::IsPathRooted($path)) { [IO.Path]::GetFullPath($path) }
        else { [IO.Path]::GetFullPath((Join-Path $root $path)) }
    $file = Get-Item -LiteralPath $absolute -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt $maxBytes -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Missing, oversized or redirected evidence artifact."
    }
    $hash = (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant()
    return @{ file=$file.FullName; sha256=$hash; data=(Get-Content -LiteralPath $absolute -Raw | ConvertFrom-Json -Depth 64) }
}
function Add-Artifact([string]$key, $input) {
    $artifacts.Add([ordered]@{ key=$key; path=$input.file; sha256=$input.sha256 })
}
$gateState = "UNVERIFIED"
$gateReason = "No authenticated same-run GitHub Required Gates snapshot supplied."
if ($RequiredGatesPath) {
    try {
        $input = Read-Input $RequiredGatesPath (2MB)
        & (Join-Path $root "build/release/Test-RequiredGateEvidence.ps1") -EvidencePath $input.file -ExpectedSourceCommit $ExpectedSourceCommit | Out-Null
        $gateState = "PASS"
        $gateReason = "Existing five-gate verifier accepted the exact-source GitHub run/jobs snapshot."
        Add-Artifact "requiredGates" $input
    } catch {
        $gateState = "FAIL"
        $gateReason = "Supplied Required Gates input rejected: " + $_.Exception.Message
    }
}
Add-Status "requiredGates" $gateState "GITHUB_EXACT_SOURCE_FIVE_JOBS" $gateReason
$ssdRows = @{}
foreach ($path in @($SsdEvidencePaths)) {
    $key = "unknown"
    try {
        $input = Read-Input $path (256KB)
        $p = [string]$input.data.profile
        $format = [string]$input.data.result.programFormat
        $key = "$format/$p"
        if ($p -cnotin @($policy.ssd.profiles | ForEach-Object id) -or
            $format -cnotin @($policy.ssd.formats) -or $ssdRows.ContainsKey($key)) {
            throw "Unknown or duplicate actual SSD format/profile."
        }
        & (Join-Path $PSScriptRoot "Test-SsdPlaybackEvidence.ps1") -EvidencePath $input.file -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $p -ExpectedFormat $format | Out-Null
        $ssdRows[$key] = @{ status= if ($input.data.result.timingStatus -eq "FAIL") { "FAIL" } else { "PASS" }
            reason= "Existing verifier accepted exact source, elapsed duration, two media hashes, resource limit, host teardown; timing=$($input.data.result.timingStatus); not physical scan-out."
        }
        Add-Artifact "ssd/$key" $input
        $diagnostic = Join-Path (Split-Path -Parent $input.file) "ssd-playback-memory-diagnostic.json"
        if (Test-Path -LiteralPath $diagnostic -PathType Leaf) {
            $memory = Read-Input $diagnostic 65536
            & (Join-Path $PSScriptRoot "Test-SsdPlaybackMemoryDiagnostic.ps1") -EvidencePath $memory.file -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $p -ExpectedFormat $format | Out-Null
            Add-Artifact "ssdMemory/$key" $memory
            $ssdRows[$key].reason += " Sparse writer/GC/private-byte observations checked; attribution and physical retention not proven."
        }
    } catch {
        if ($ssdRows.ContainsKey($key)) { throw "Ambiguous SSD evidence for $key: $($_.Exception.Message)" }
        $ssdRows[$key] = @{ status="FAIL"; reason="Provided SSD evidence invalid: $($_.Exception.Message)" }
    }
}
foreach ($format in @($policy.ssd.formats)) {
    foreach ($profile in @($policy.ssd.profiles)) {
        $key = "$format/$($profile.id)"
        $row = if ($ssdRows.ContainsKey($key)) { $ssdRows[$key] } else {
            @{ status="UNVERIFIED"; reason="No completed exact-source $($profile.seconds)-second two-deck outcome and cleanup evidence provided." }
        }
        Add-Status "ssd/$key" $row.status "SOFTWARE_DUAL_DECK_NOT_PHYSICAL_OUTPUT" $row.reason
    }
}
$allowedSsdKeys = @($policy.ssd.formats | ForEach-Object {
    $format = $_
    foreach ($p in @($policy.ssd.profiles)) { "$format/$($p.id)" }
})
foreach ($key in @($ssdRows.Keys)) {
    if ($key -notin $allowedSsdKeys) {
        Add-Status "ssd/invalid/$key" "FAIL" "INVALID_INPUT" $ssdRows[$key].reason
    }
}
$mixedRows = @{}
foreach ($path in @($MixedEvidencePaths)) {
    $key = "unknown"
    try {
        $input = Read-Input $path (2MB)
        $key = [string]$input.data.profile
        if ($key -cnotin @($policy.mixed.profiles | ForEach-Object id) -or $mixedRows.ContainsKey($key)) {
            throw "Unknown or duplicate staged mixed soak profile."
        }
        & (Join-Path $PSScriptRoot "Test-SustainedMixedLoadEvidence.ps1") -EvidencePath $input.file -DescriptorPath (Join-Path $root $policy.mixed.descriptor) -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $key | Out-Null
        $mixedRows[$key] = @{ status="PASS"; reason="Bounded staged sessions verified; NOT uninterrupted Program, native network or physical A/V." }
        Add-Artifact "stagedMixed/$key" $input
    } catch {
        $mixedRows[$key] = @{ status="FAIL"; reason="Rejected staged mixed-soak evidence: $($_.Exception.Message)" }
    }
}
foreach ($profile in @($policy.mixed.profiles)) {
    $row = if ($mixedRows.ContainsKey($profile.id)) { $mixedRows[$profile.id] } else {
        @{ status="UNVERIFIED"; reason="No complete exact-source duration $($profile.minimumSeconds) seconds supplied." }
    }
    Add-Status "stagedMixed/$($profile.id)" $row.status "MANAGED_STAGED_SOFTWARE_ONLY" $row.reason
}
foreach ($key in @($mixedRows.Keys)) {
    if ($key -notin @($policy.mixed.profiles | ForEach-Object id)) {
        Add-Status "stagedMixed/invalid/$key" "FAIL" "INVALID_INPUT" $mixedRows[$key].reason
    }
}
$software = "UNVERIFIED"
$softwareReason = "No complete real-time software producer-composition evidence supplied."
if ($RealtimeEvidencePath) {
    try {
        $input = Read-Input $RealtimeEvidencePath (2MB)
        & (Join-Path $PSScriptRoot "Test-RealtimePlayoutEvidence.ps1") -EvidencePath $input.file -ExpectedSourceCommit $ExpectedSourceCommit | Out-Null
        $software = "PASS"
        $softwareReason = "Software producer matrix checked; native 25 fps, uninterrupted Program and physical A/V remain UNVERIFIED."
        Add-Artifact "realtimeSoftware" $input
    } catch {
        $software = "FAIL"
        $softwareReason = "Rejected realtime software evidence: $($_.Exception.Message)"
    }
}
Add-Status "realtimeSoftware" $software "NON_PHYSICAL_PRODUCER_COMPOSITION" $softwareReason
Add-Status "native25" "UNVERIFIED" "NATIVE_REAL_PROGRAM" "No approved exact-native 25-fps Program producer in current qualification."
foreach ($req in @($policy.requiredPhysical)) {
    Add-Status "physical/$req" "UNVERIFIED" "ACTUAL_DEVICE_REQUIRED" "Use existing authorized same-source self-hosted CUDA/Media-I-O/timing evidence binding; software runners cannot satisfy this."
}
Add-Status "governance/masterRuleset" "UNVERIFIED" "GITHUB_ADMIN_REQUIRED" "Live GitHub master ruleset must be verified by an authorized administrator; repository policy alone does not enforce merges."
Add-Status "risk/401" "UNVERIFIED" "OUTSTANDING_MEMORY_FINDING" "59.94 dual-deck replay private-memory growth needs unchanged same-source 512-MiB requalification and causal investigation."
$report = [ordered]@{
    schemaVersion="1.0"; qualification="rtaime-production-soak-coverage"
    repository="DaveBeusing/rtaime"; sourceCommit=$ExpectedSourceCommit
    generatedAtUtc=[DateTimeOffset]::UtcNow.ToString("O")
    descriptorSha256=(Get-FileHash -LiteralPath $policyFile -Algorithm SHA256).Hash.ToLowerInvariant()
    releaseDecision="NO-GO"
    rationale="Supplementary evidence inventory only; missing physical binding, open resource/governance review and unsigned/unapproved release readiness cannot be promoted by this report."
    sourceScope="EXACT_CHECKED_OUT_HEAD_NOT_PRODUCTION_SIGNING_OR_GITHUB_ATTESTATION"
    statusVocabulary=@("PASS","FAIL","UNVERIFIED")
    qualifications=@($statuses)
    referencedArtifacts=@($artifacts)
    hardwarePerformance="UNVERIFIED"; supportedPlatform="UNVERIFIED"
    productionSigning="UNVERIFIED"; unbrokenProductionSoak="UNVERIFIED"
    upstreamAuthority="build/release/New-ReleaseReadinessIndex.ps1 and Test-StableReadiness.ps1; this inventory does not override them."
}
$bytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 32))
if ($bytes.Length -gt 131072) { throw "Production coverage report exceeded fixed 128-KiB bound." }
$out = if ([IO.Path]::IsPathRooted($OutputPath)) { [IO.Path]::GetFullPath($OutputPath) }
    else { [IO.Path]::GetFullPath((Join-Path $root $OutputPath)) }
if (Test-Path -LiteralPath $out) { throw "Cannot overwrite a prior production evidence inventory." }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out))
[IO.File]::WriteAllBytes($out, $bytes)
Write-Host "Production-soak inventory NO-GO: $($statuses.Count) explicit domains; source $ExpectedSourceCommit. No release, signing or physical qualification performed."
