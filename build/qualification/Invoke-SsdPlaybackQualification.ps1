# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("smoke","10m","60m","8h","24h","72h")][string]$Profile,
    [string]$MediaA, [string]$MediaB,
    [ValidateSet("1080p50","1080p59.94")][string]$Format = "1080p50",
    [string]$OutputRoot = "artifacts/qualification/ssd-playback",
    [switch]$NoBuild,
    [switch]$RequirePhysicalPass
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw "Real Windows x64 playback is required." }
if ([string]::IsNullOrWhiteSpace($MediaA) -ne [string]::IsNullOrWhiteSpace($MediaB)) {
    throw "Supply both deck files, or neither for the existing generated reference."
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$source = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$') { throw "Exact source identity is required." }
& git -C $repo diff --quiet HEAD --
if ($LASTEXITCODE -ne 0) { throw "Tracked source differs from the tested commit." }
# Include untracked source: generated artifacts remain governed by .gitignore.
$untracked = @(& git -C $repo ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0 -or $untracked.Count -gt 0) { throw "Untracked files prevent clean-source qualification." }
if ((& dotnet --version).Trim() -ne "10.0.401" -or $LASTEXITCODE -ne 0) { throw "SDK 10.0.401 is required." }
$root = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) } else { [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot)) }
$run = Join-Path $root ([DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmss") + "-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($run)

. (Join-Path $PSScriptRoot "SsdPlaybackStorage.ps1")
$inventory = [ordered]@{
    schemaVersion = "1.1"; sourceCommit = $source; capturedAt = [DateTimeOffset]::UtcNow.ToString("O")
    operatingSystem = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    captureStatus = "INITIAL"; storage = @((Get-SsdFileStorage $MediaA), (Get-SsdFileStorage $MediaB))
    storageAfter = @(); media = @(); outcomeSha256 = $null; completedAt = $null; topology = "UNVERIFIED"
    gpu = @(); decoder = "Windows Media Foundation; selected MFT binary and acceleration UNVERIFIED"
    cache = "UNVERIFIED"; pressure = "UNVERIFIED"; physicalStatus = "UNVERIFIED"
    selectedDecoder = "UNVERIFIED"; fileOnlyReadLatency = "UNVERIFIED"
    physicalOutput = "UNVERIFIED"; gpuTelemetry = "UNVERIFIED"
}
try {
    $inventory.gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop |
        Select-Object Name, DriverVersion, DriverDate, PNPDeviceID)
} catch { $inventory.gpu = @(@{ status = "UNVERIFIED"; reason = $_.Exception.Message }) }
$inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run "environment.json") -Encoding utf8

$names = @("RTAIME_SSD_PLAYBACK_PROFILE","RTAIME_SSD_PROGRAM_FORMAT","RTAIME_SSD_MEDIA_A","RTAIME_SSD_MEDIA_B",
    "RTAIME_REFERENCE_MEDIA_REGRESSION","RTAIME_REFERENCE_MEDIA_OUTPUT","RTAIME_SOURCE_COMMIT",
    "RTAIME_BUILD_COMMIT","RTAIME_BASELINE_SDK")
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
Push-Location $repo
try {
    if (-not $NoBuild) {
        ./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Canonical build failed." }
    }
    ./build/quality/Test-LocalMediaPlaybackPolicy.ps1
    # Staged software fault evidence, not physical hot unplug or sustained slow SSD.
    dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~AsynchronousLocalMediaDecodeTests|FullyQualifiedName~LocalMediaDecoderSafetyTests|FullyQualifiedName~MultiDeckLocalMediaIntegrationTests" --logger "trx;LogFileName=faults.trx" --results-directory $run
    if ($LASTEXITCODE -ne 0) { throw "Media lifetime/concurrency/fault prerequisites failed." }
    $env:RTAIME_SSD_PLAYBACK_PROFILE = $Profile
    $env:RTAIME_SSD_PROGRAM_FORMAT = $Format
    $env:RTAIME_SSD_MEDIA_A = if ($MediaA) { [IO.Path]::GetFullPath($MediaA) } else { $null }
    $env:RTAIME_SSD_MEDIA_B = if ($MediaB) { [IO.Path]::GetFullPath($MediaB) } else { $null }
    $env:RTAIME_REFERENCE_MEDIA_REGRESSION = "1"
    $env:RTAIME_REFERENCE_MEDIA_OUTPUT = $run
    $env:RTAIME_SOURCE_COMMIT = $source
    $env:RTAIME_BUILD_COMMIT = $source
    $env:RTAIME_BASELINE_SDK = "10.0.401"
    dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~MultiDeckSsdPlaybackQualificationTests" --logger "console;verbosity=detailed" --logger "trx;LogFileName=playback.trx" --results-directory $run
    if ($LASTEXITCODE -ne 0) { throw "Actual dual-deck playback or cleanup failed; retain failed reports." }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $run "playback.trx") -Raw
    if ([int]$trx.TestRun.ResultSummary.Counters.executed -ne 1 -or
        [int]$trx.TestRun.ResultSummary.Counters.passed -ne 1) { throw "Actual playback test was not executed." }
    $report = Join-Path $run "ssd-playback.outcome.json"
    ./build/qualification/Test-SsdPlaybackEvidence.ps1 -EvidencePath $report -ExpectedSourceCommit $source -ExpectedProfile $Profile -ExpectedFormat $Format
    $outcome = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $inventory.storageAfter = @((Get-SsdFileStorage $MediaA), (Get-SsdFileStorage $MediaB))
    $inventory.topology = Get-SsdStorageTopology $inventory.storage
    $inventory.media = @($outcome.result.media)
    $inventory.outcomeSha256 = (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()
    $inventory.completedAt = [DateTimeOffset]::UtcNow.ToString("O")
    $inventory.captureStatus = "FINAL"
    $environmentPath = Join-Path $run "environment.json"
    $inventory | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $environmentPath -Encoding utf8
    ./build/qualification/Test-SsdPlaybackEnvironment.ps1 -EnvironmentPath $environmentPath -ExpectedSourceCommit $source -OutcomePath $report
    $support = [ordered]@{
        schemaVersion = "1.0"; sourceCommit = $source; profile = $Profile; programFormat = $Format
        evidenceSha256 = (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()
        environmentSha256 = (Get-FileHash -LiteralPath (Join-Path $run "environment.json") -Algorithm SHA256).Hash.ToLowerInvariant()
        media = @($outcome.result.media); probedProfiles = @($outcome.result.deckProbes)
        hardwareTuple = @{ topology = $inventory.topology; storage = @($inventory.storage)
            gpu = @($inventory.gpu); selectedDecoder = "UNVERIFIED"; trust = "OBSERVATION_ONLY" }
        acceptance = @(
            @{ requirement = "SOURCE_BUILD_MEDIA_IDENTITY"; status = "PASS" }
            @{ requirement = "STORAGE_DEVICE_MAPPING"; status = if ($inventory.topology -eq "UNVERIFIED") { "UNVERIFIED" } else { "OBSERVED" } }
            @{ requirement = "SUSTAINED_PROGRAM_CADENCE"; status = $outcome.result.timingStatus }
            @{ requirement = "RESOURCE_PLATEAU"; status = $outcome.result.resourcePlateau }
            foreach ($requirement in @("CACHE_AND_PRESSURE_MEASUREMENT","PINNED_DECODER_AND_ACCELERATION","GPU_VRAM_TELEMETRY","PHYSICAL_OUTPUT_LOSS_AND_AV","FAULT_RECOVERY")) {
                @{ requirement = $requirement; status = "UNVERIFIED" }
            }
        )
        softwareCollection = "PASS"; programTiming = $outcome.result.timingStatus
        supportStatus = if ($outcome.result.timingStatus -eq "FAIL") { "FAIL" } else { "UNVERIFIED" }
        physicalStatus = "UNVERIFIED"; releasePromotionPerformed = $false
        missing = @("Pinned selected decoder/acceleration", "Measured cache/pressure",
            "GPU/VRAM", "Physical output/A-V", "Physical storage latency", "Approved hardware acceptance")
    }
    $support | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $run "support-profile.json") -Encoding utf8
    $manifest = [ordered]@{
        schemaVersion = "1.0"; qualification = "rtaime-ssd-playback"; sourceCommit = $source
        buildCommit = $source; profile = $Profile; programFormat = $Format; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        descriptorSha256 = (Get-FileHash -LiteralPath qualification/soak/ssd-playback.json -Algorithm SHA256).Hash.ToLowerInvariant()
        files = @("ssd-playback.outcome.json","environment.json","support-profile.json","playback.trx","faults.trx" | ForEach-Object {
            @{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $run $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        softwareStatus = "PASS"; physicalStatus = "UNVERIFIED"
        nextAction = "Retain exact device/controller/decoder identities and measured cache, storage pressure, GPU/VRAM and physical output/A-V evidence; execute each required duration and fault profile."
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run "manifest.json") -Encoding utf8
    Write-Host "SSD software collection verified: $Profile / $source; physical UNVERIFIED."
    Write-Host "Evidence: $run"
    if ($RequirePhysicalPass) { throw "Physical qualification is UNVERIFIED; software collection cannot satisfy RequirePhysicalPass." }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process") }
    Pop-Location
}
