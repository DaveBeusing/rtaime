# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [string]$RequiredGatesPath = "",
    [string]$ReleaseEvidencePath = "",
    [string]$ReleaseCandidatePath = "",
    [string]$QualificationCandidatePath = "",
    [string]$CoordinatedUpgradeQualificationPath = "",
    [string[]]$MixedLoadEvidencePaths = @(),
    [string[]]$SsdPlaybackEvidencePaths = @(),
    [string[]]$SsdPlaybackEnvironmentPaths = @(),
    [string]$RendererRunsPath = "",
    [Parameter(Mandatory)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($ExpectedSourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Readiness index requires an exact lowercase 40-character source SHA." }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
function Resolve-Input([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $root $Path))
}
$output = Resolve-Input $OutputPath
if (Test-Path -LiteralPath $output) { throw "Readiness index output already exists; preserve retained evidence and use a new output path." }
$inputs = [Collections.Generic.List[object]]::new()
function Retain-Input([string]$Path, [string]$Kind) {
    $full = Resolve-Input $Path
    if ($full -eq $output) { throw "Readiness output cannot replace input evidence." }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing $Kind evidence: '$full'." }
    $inputs.Add([ordered]@{ kind = $Kind; path = $full; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() })
    return $full
}
$domains = [Collections.Generic.List[object]]::new()
function Add-Result([string]$Name, [string]$Status, [string]$Source, [string]$Details) {
    $domains.Add([ordered]@{ name = $Name; status = $Status; source = $Source; details = $Details })
}
$assessedProduct = $null
$identityCandidatePath = if (-not [string]::IsNullOrWhiteSpace($QualificationCandidatePath)) { $QualificationCandidatePath } else { $ReleaseCandidatePath }
if (-not [string]::IsNullOrWhiteSpace($identityCandidatePath)) {
    try {
        $identityRoot = Resolve-Input $identityCandidatePath
        if (Test-Path -LiteralPath $identityRoot -PathType Leaf) { $identityRoot = Split-Path -Parent $identityRoot }
        & (Join-Path $PSScriptRoot "Test-ReleaseCandidate.ps1") -CandidatePath $identityRoot
        $identityCandidate = Get-Content -LiteralPath (Join-Path $identityRoot "release-candidate.json") -Raw | ConvertFrom-Json
        if ([string]$identityCandidate.source.sourceCommit -ne $ExpectedSourceCommit -or
            [string]$identityCandidate.source.buildCommit -ne $ExpectedSourceCommit) { throw "Assessed candidate source/build mismatch." }
        $assessedProduct = $identityCandidate.product
    } catch { Add-Result "assessedCandidateIdentity" "FAIL" $identityCandidatePath $_.Exception.Message }
}
function Invoke-StableAssessment($Parameters) {
    $previousVersion = $env:RTAIME_PRODUCT_VERSION
    $previousStage = $env:RTAIME_RELEASE_STAGE
    try {
        if ($null -ne $assessedProduct) {
            $env:RTAIME_PRODUCT_VERSION = [string]$assessedProduct.version
            $env:RTAIME_RELEASE_STAGE = [string]$assessedProduct.releaseStage
        }
        & (Join-Path $PSScriptRoot "Test-StableReadiness.ps1") @Parameters
    } finally {
        $env:RTAIME_PRODUCT_VERSION = $previousVersion
        $env:RTAIME_RELEASE_STAGE = $previousStage
    }
}
$stableArgs = @{ ExpectedSourceCommit = $ExpectedSourceCommit; RequiredGatesPath = $RequiredGatesPath }
$stable = Invoke-StableAssessment $stableArgs
try {
    foreach ($key in @("ReleaseEvidencePath", "ReleaseCandidatePath", "CoordinatedUpgradeQualificationPath")) {
        $value = Get-Variable -Name $key -ValueOnly
        if (-not [string]::IsNullOrWhiteSpace($value)) { $stableArgs[$key] = $value }
    }
    $stable = Invoke-StableAssessment $stableArgs
} catch { Add-Result "releaseInputVerification" "FAIL" "existing release verifiers" $_.Exception.Message }
foreach ($domain in @($stable.domains)) { $domains.Add($domain) }
foreach ($entry in @(
    @{ path = $RequiredGatesPath; kind = "required-gates" },
    @{ path = $CoordinatedUpgradeQualificationPath; kind = "coordinated-state-upgrade" }
)) {
    if (-not [string]::IsNullOrWhiteSpace($entry.path)) {
        try { $null = Retain-Input $entry.path $entry.kind }
        catch { Add-Result $entry.kind "FAIL" $entry.path $_.Exception.Message }
    }
}
$release = $null
if (-not [string]::IsNullOrWhiteSpace($ReleaseEvidencePath)) {
    try {
        $evidenceRoot = Resolve-Input $ReleaseEvidencePath
        if (Test-Path -LiteralPath $evidenceRoot -PathType Leaf) { $evidenceRoot = Split-Path -Parent $evidenceRoot }
        $releaseFile = Retain-Input (Join-Path $evidenceRoot "release-evidence.json") "release-evidence"
        $release = Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json
        foreach ($name in @("artifact-manifest.json", "compatibility-manifest.json", "qualification-evidence-manifest.json", "sbom.cdx.json")) {
            $null = Retain-Input (Join-Path $evidenceRoot $name) $name
        }
    } catch { Add-Result "releaseEvidenceInventory" "FAIL" $ReleaseEvidencePath $_.Exception.Message }
}
$releaseVerified = @($stable.domains | Where-Object { $_.name -eq "releaseEvidence" -and $_.status -eq "PASS" }).Count -eq 1
foreach ($name in @("ARCHITECTURE", "CONTRACTS", "BEHAVIOR", "FAILURE", "PERFORMANCE", "COMPATIBILITY", "SECURITY", "SUPPLY_CHAIN", "DOCUMENTATION", "COMPLIANCE", "KNOWN_ISSUES")) {
    $status = "UNVERIFIED"; $detail = "No verified current-source release domain evidence supplied."
    if ($null -ne $release) {
        $matches = @($release.evidenceDomains | Where-Object { $_.domain -eq $name })
        if (-not $releaseVerified -or $matches.Count -ne 1) { $status = "FAIL"; $detail = "Release evidence did not verify against the assessed source or domain is ambiguous." }
        else { $status = [string]$matches[0].status; $detail = "Status retained from verified exact-source release evidence; software domains do not grant physical support." }
    }
    Add-Result "releaseDomain.$name" $status "release-evidence.json" $detail
}
$releasePolicy = Get-Content -LiteralPath (Join-Path $root "build/release/release-policy.json") -Raw | ConvertFrom-Json
foreach ($requirement in @($releasePolicy.hardwareQualification)) {
    $name = [string]$requirement.requirement
    $status = "UNVERIFIED"; $detail = "No verified physical qualification binding for the current source."
    if ($null -ne $release) {
        if (-not $releaseVerified) { $status = "FAIL"; $detail = "Physical claims cannot be inherited from rejected release evidence." }
        else {
            $compatibility = Get-Content -LiteralPath (Join-Path $evidenceRoot "compatibility-manifest.json") -Raw | ConvertFrom-Json
            $matches = @($compatibility.hardwareQualification | Where-Object { $_.requirement -eq $name })
            if ($matches.Count -ne 1) { $status = "FAIL"; $detail = "Missing or ambiguous physical domain." }
            else { $status = [string]$matches[0].status; $detail = "Retained verified source-bound compatibility status; no substitution from software smoke or a different source." }
        }
    }
    Add-Result "physicalRequirement.$name" $status "compatibility-manifest.json" $detail
}
$packageStatus = "UNVERIFIED"; $packageDetail = "No exact-source verified packaged candidate was supplied."
if (-not [string]::IsNullOrWhiteSpace($QualificationCandidatePath)) {
    try {
        $candidateRoot = Resolve-Input $QualificationCandidatePath
        & (Join-Path $PSScriptRoot "Test-ReleaseCandidate.ps1") -CandidatePath $candidateRoot
        $candidateFile = Retain-Input (Join-Path $candidateRoot "release-candidate.json") "packaged-candidate"
        $candidate = Get-Content -LiteralPath $candidateFile -Raw | ConvertFrom-Json
        if ($null -eq $release -or [string]$candidate.source.sourceCommit -ne $ExpectedSourceCommit -or
            [string]$candidate.source.buildCommit -ne $ExpectedSourceCommit -or
            [string]$candidate.source.buildId -ne [string]$release.buildId -or
            [string]$candidate.product.version -ne [string]$release.productVersion -or
            [string]$candidate.product.releaseStage -ne [string]$release.releaseStage -or
            [string]$candidate.releaseRecord.releaseEvidenceSha256 -ne (Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "Packaged candidate and supplied release evidence do not describe identical source/build/evidence bytes."
        }
        $null = Retain-Input (Join-Path $candidateRoot $candidate.bundle.fileName) "offline-bundle"
        $packageStatus = "PASS"
        $packageDetail = "Existing package verifier passed; candidate, bundle and release evidence match exact source/build/hash. Test signing does not establish production trust."
    } catch { $packageStatus = "FAIL"; $packageDetail = $_.Exception.Message }
}
Add-Result "packagedCandidateIntegrity" $packageStatus $QualificationCandidatePath $packageDetail
Add-Result "independentBuildReproducibility" "UNVERIFIED" "artifact-manifest.json" "One checksummed package is not proof of byte-identical builds from independent controlled environments."
$seenProfiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$mixedResults = @{}
foreach ($path in $MixedLoadEvidencePaths) {
    $profile = "unknown"
    try {
        $full = Retain-Input $path "staged-mixed-load"
        $e = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
        $profile = [string]$e.profile
        if ($profile -notin @("smoke", "8h", "24h", "72h") -or -not $seenProfiles.Add($profile)) { throw "Unknown or duplicate mixed-load profile." }
        & (Join-Path $root "build/qualification/Test-SustainedMixedLoadEvidence.ps1") -EvidencePath $full -DescriptorPath (Join-Path $root "qualification/soak/sustained-mixed-load.json") -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $profile
        $mixedResults[$profile] = @{ status = "PASS"; source = $full; details = "Verified staged managed sessions in one testhost; no physical or uninterrupted production qualification." }
    } catch {
        $mixedResults[$profile] = @{ status = "FAIL"; source = $path; details = $_.Exception.Message }
    }
}
foreach ($profile in @("smoke", "8h", "24h", "72h")) {
    if ($mixedResults.ContainsKey($profile)) {
        $r = $mixedResults[$profile]; Add-Result "stagedMixedLoad.$profile" $r.status $r.source $r.details
    } else { Add-Result "stagedMixedLoad.$profile" "UNVERIFIED" "staged summary.json" "No completed exact-source '$profile' workload evidence supplied; another duration cannot substitute." }
}
if ($mixedResults.ContainsKey("unknown")) { Add-Result "stagedMixedLoad.invalidInput" "FAIL" $mixedResults.unknown.source $mixedResults.unknown.details }
$ssdResults = @{}
foreach ($path in $SsdPlaybackEvidencePaths) {
    try {
        $full = Retain-Input $path "ssd-playback"
        $e = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
        $profile = [string]$e.profile
        if ($profile -notin @("smoke","10m","60m","8h","24h","72h") -or $ssdResults.ContainsKey($profile)) { throw "Unknown or duplicate SSD profile." }
        & (Join-Path $root "build/qualification/Test-SsdPlaybackEvidence.ps1") -EvidencePath $full -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $profile
        $status = if ($e.result.timingStatus -eq "FAIL") { "FAIL" } else { "UNVERIFIED" }
        $ssdResults[$profile] = @{ status = $status; source = $full; details = "Actual dual-deck software observation; physical decoder/storage/output qualification remains UNVERIFIED. Measured Program misses are FAIL." }
    } catch { Add-Result "ssdPlayback.invalidInput" "FAIL" $path $_.Exception.Message }
}
foreach ($profile in @("smoke","10m","60m","8h","24h","72h")) {
    if ($ssdResults.ContainsKey($profile)) {
        $r = $ssdResults[$profile]; Add-Result "ssdPlayback.$profile" $r.status $r.source $r.details
    } else { Add-Result "ssdPlayback.$profile" "UNVERIFIED" "SSD playback outcome" "No verified exact-source media/hardware observation for this duration. Software CI cannot grant SSD support." }
}
# Inventory is correlated observation, never an authorized physical release binding.
if ($SsdPlaybackEnvironmentPaths.Count -eq 0) {
    Add-Result "ssdHardwareInventory" "UNVERIFIED" "SSD environment" "No exact-source device/controller inventory supplied."
}
foreach ($path in $SsdPlaybackEnvironmentPaths) {
    try {
        $full = Retain-Input $path "ssd-environment"
        $outcomePath = Retain-Input (Join-Path (Split-Path -Parent $full) "ssd-playback.outcome.json") "ssd-environment-outcome"
        $outcome = Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json
        & (Join-Path $root "build/qualification/Test-SsdPlaybackEvidence.ps1") -EvidencePath $outcomePath -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $outcome.profile
        & (Join-Path $root "build/qualification/Test-SsdPlaybackEnvironment.ps1") -EnvironmentPath $full -ExpectedSourceCommit $ExpectedSourceCommit -OutcomePath $outcomePath
        $inventory = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
        $status = if ($outcome.result.timingStatus -eq "FAIL") { "FAIL" } else { "UNVERIFIED" }
        Add-Result "ssdHardwareInventory" $status $full "Correlated inventory topology $($inventory.topology); untrusted observation cannot satisfy physical release requirements."
    } catch { Add-Result "ssdHardwareInventory" "FAIL" $path $_.Exception.Message }
}
$rendererResults = @{}
if (-not [string]::IsNullOrWhiteSpace($RendererRunsPath)) {
    try {
        $catalogFile = Retain-Input $RendererRunsPath "renderer-run-index"
        $catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
        foreach ($run in @($catalog.runs)) {
            $profile = [string]$run.profile
            if ($profile -notin @("smoke", "stress", "soak", "soak8h", "soak24h", "soak72h") -or $rendererResults.ContainsKey($profile)) {
                throw "Unknown or duplicate renderer profile."
            }
            $rendererArgs = @{ ExpectedSourceCommit = $ExpectedSourceCommit; ExpectedProfile = $profile }
            foreach ($key in @("EvidencePath", "ManifestPath", "SoftwareEvidencePath", "BaselineComparisonPath")) {
                $path = [string]$run.$key
                if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path (Split-Path -Parent $catalogFile) $path }
                $rendererArgs[$key] = Retain-Input $path "renderer-$key"
            }
            & (Join-Path $root "build/qualification/Test-RendererReferenceQualification.ps1") @args
            $rendererResults[$profile] = @{ status = "PASS"; source = $rendererArgs.ManifestPath; details = "Verified physical renderer workload. Long profile duration is total across both formats; no external Media I/O/A/V synchronization claim." }
        }
    } catch { Add-Result "rendererInputVerification" "FAIL" $RendererRunsPath $_.Exception.Message }
}
foreach ($profile in @("smoke", "stress", "soak", "soak8h", "soak24h", "soak72h")) {
    if ($rendererResults.ContainsKey($profile)) {
        $r = $rendererResults[$profile]; Add-Result "physicalRenderer.$profile" $r.status $r.source $r.details
    } else { Add-Result "physicalRenderer.$profile" "UNVERIFIED" "physical renderer manifest/payload" "No complete exact-source renderer evidence set for this profile." }
}
Add-Result "continuousSingleProduction72h" "UNVERIFIED" "qualification scopes" "Staged managed sessions and 36 hours per renderer format do not establish 72 hours in one uninterrupted production instance."
Add-Result "administrativeBranchProtection" "UNVERIFIED" "docs/Governance/RepositoryPolicy.json" "Source-controlled gates do not prove active remote administrative enforcement; verify the live repository ruleset before release."
$failed = @($domains | Where-Object status -eq "FAIL")
$unverified = @($domains | Where-Object status -eq "UNVERIFIED")
$status = if ($failed.Count -gt 0) { "FAIL" } elseif ($unverified.Count -gt 0) { "UNVERIFIED" } else { "PASS" }
$result = [ordered]@{
    copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
    schemaVersion = "1.0"
    repository = "DaveBeusing/rtaime"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sourceCommit = $ExpectedSourceCommit
    product = $stable.product
    overallStatus = $status
    decision = if ($status -eq "PASS") { "GO" } else { "NO-GO" }
    releasePromotionPerformed = $false
    evidenceInputs = @($inputs)
    domains = @($domains)
    blockers = @($domains | Where-Object { $_.status -in @("FAIL", "UNVERIFIED") } | ForEach-Object { $_.name })
    operationalHandoff = @(
        "Preserve the index and referenced artifact bytes; revalidate all hashes when relocating evidence.",
        "Collect exact-source physical GPU/driver, Media I/O and measured A/V evidence through the existing qualification binding path.",
        "Execute each missing 8h/24h/72h staged and renderer profile; retain its scope and per-format duration.",
        "Obtain approved support/security/deprecation commitments, known-issues and security assessments, external production trust and independent build reproducibility.",
        "Rerun readiness against the final source and intended production candidate; no stage or support promotion is performed."
    )
}
$directory = Split-Path -Parent $output
[void][IO.Directory]::CreateDirectory($directory)
[IO.File]::WriteAllText($output, ($result | ConvertTo-Json -Depth 64) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Evidence-bound release readiness: $status / $($result.decision); source $ExpectedSourceCommit"
return [pscustomobject]$result
