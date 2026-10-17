# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("smoke","10m","60m","8h","24h","72h")][string]$Profile,
    [string]$MediaA, [string]$MediaB,
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

function Get-FileStorage([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @{ mapping = "UNVERIFIED"; reason = "Generated reference location is selected by the existing fixture." }
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Input file is missing: $full" }
    try {
        $drive = [IO.Path]::GetPathRoot($full).TrimEnd('\').TrimEnd(':')
        if ($drive.Length -ne 1) { throw "Only a directly mapped local volume can identify this disk." }
        $partitions = @(Get-Partition -DriveLetter $drive -ErrorAction Stop)
        if ($partitions.Count -ne 1) { throw "Volume mapping is ambiguous." }
        $disk = Get-Disk -Number $partitions[0].DiskNumber -ErrorAction Stop
        $volume = Get-Volume -DriveLetter $drive -ErrorAction Stop
        return @{ mapping = "OBSERVED"; diskNumber = $disk.Number; model = $disk.FriendlyName
            busType = [string]$disk.BusType; firmware = [string]$disk.FirmwareVersion
            fileSystem = [string]$volume.FileSystem; volumeSize = $volume.Size
            # Serial numbers and source paths are deliberately not redistributed.
            storageClass = "UNVERIFIED"; controllerDriver = "UNVERIFIED" }
    } catch { return @{ mapping = "UNVERIFIED"; reason = $_.Exception.Message } }
}
$inventory = [ordered]@{
    schemaVersion = "1.0"; sourceCommit = $source; capturedAt = [DateTimeOffset]::UtcNow.ToString("O")
    operatingSystem = [Environment]::OSVersion.VersionString; architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    storage = @((Get-FileStorage $MediaA), (Get-FileStorage $MediaB))
    gpu = @(); decoder = "Windows Media Foundation; selected MFT binary and acceleration UNVERIFIED"
    cache = "UNVERIFIED"; pressure = "UNVERIFIED"; physicalStatus = "UNVERIFIED"
}
try {
    $inventory.gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop |
        Select-Object Name, DriverVersion, DriverDate, PNPDeviceID)
} catch { $inventory.gpu = @(@{ status = "UNVERIFIED"; reason = $_.Exception.Message }) }
$inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run "environment.json") -Encoding utf8

$names = @("RTAIME_SSD_PLAYBACK_PROFILE","RTAIME_SSD_MEDIA_A","RTAIME_SSD_MEDIA_B",
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
    ./build/qualification/Test-SsdPlaybackEvidence.ps1 -EvidencePath $report -ExpectedSourceCommit $source -ExpectedProfile $Profile
    $manifest = [ordered]@{
        schemaVersion = "1.0"; qualification = "rtaime-ssd-playback"; sourceCommit = $source
        buildCommit = $source; profile = $Profile; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        descriptorSha256 = (Get-FileHash -LiteralPath qualification/soak/ssd-playback.json -Algorithm SHA256).Hash.ToLowerInvariant()
        files = @("ssd-playback.outcome.json","environment.json","playback.trx","faults.trx" | ForEach-Object {
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
