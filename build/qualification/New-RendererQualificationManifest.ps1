# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("smoke", "stress", "soak")][string]$Profile,
    [Parameter(Mandatory)][string]$ExpectedDeviceName,
    [int]$DeviceOrdinal = 0,
    [string]$ProfilePath = "qualification/renderer/renderer-qualification.json",
    [string]$SourceCommit = "",
    [string]$OutputPath = "artifacts/qualification/renderer/manifest.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "Renderer hardware qualification manifest requires Windows." }
if ([Environment]::Is64BitProcess -ne $true) { throw "Renderer hardware qualification manifest requires an x64 process." }
if ($DeviceOrdinal -lt 0) { throw "DeviceOrdinal must be non-negative." }
if ([string]::IsNullOrWhiteSpace($ExpectedDeviceName)) { throw "ExpectedDeviceName is required." }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Resolve-RepositoryPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Path
    )
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
}

$resolvedProfile = Resolve-RepositoryPath $ProfilePath
if (-not (Test-Path -LiteralPath $resolvedProfile -PathType Leaf)) {
    throw "Renderer qualification profile is missing: '$resolvedProfile'."
}
$profileDocument = Get-Content -LiteralPath $resolvedProfile -Raw | ConvertFrom-Json
if ([string]$profileDocument.schemaVersion -ne "1.0") { throw "Renderer qualification profile schema must be 1.0." }
if ([string]$profileDocument.qualification -ne "rtaime-renderer-reference") { throw "Unexpected renderer qualification profile identity." }
$selected = @($profileDocument.profiles | Where-Object { [string]$_.name -eq $Profile })
if ($selected.Count -ne 1) { throw "Renderer qualification profile '$Profile' is not defined exactly once." }

$resolvedCommit = $SourceCommit.Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($resolvedCommit)) {
    $resolvedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0) { throw "Unable to resolve renderer qualification source commit." }
}
if ($resolvedCommit -notmatch '^[0-9a-f]{40}$') { throw "Renderer qualification requires an exact 40-character source commit." }
if ($env:GITHUB_SHA -match '^[0-9a-fA-F]{40}$' -and $resolvedCommit -ne $env:GITHUB_SHA.ToLowerInvariant()) {
    throw "Resolved source commit '$resolvedCommit' differs from workflow source '$($env:GITHUB_SHA)'."
}

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $dotnetVersion -ne "10.0.401") {
    throw "Renderer qualification requires .NET SDK 10.0.401; detected '$dotnetVersion'."
}

$nvidiaSmi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if ($null -eq $nvidiaSmi) { $nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue }
if ($null -eq $nvidiaSmi) { throw "nvidia-smi is required to capture renderer qualification GPU/driver identity." }
$gpuLine = @(& $nvidiaSmi.Source --query-gpu=name,driver_version,pci.bus_id,memory.total --format=csv,noheader,nounits -i $DeviceOrdinal 2>$null)
if ($LASTEXITCODE -ne 0 -or $gpuLine.Count -ne 1) {
    throw "Unable to capture exactly one NVIDIA GPU identity for ordinal '$DeviceOrdinal'."
}
$gpuFields = ([string]$gpuLine[0]).Split(',') | ForEach-Object { $_.Trim() }
if ($gpuFields.Count -ne 4) { throw "Unexpected nvidia-smi renderer qualification identity format." }
if (-not $gpuFields[0].Contains($ExpectedDeviceName.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Detected GPU '$($gpuFields[0])' does not match expected reference device '$ExpectedDeviceName'."
}

$os = Get-CimInstance Win32_OperatingSystem
if ($null -eq $os) { throw "Unable to capture Windows operating-system identity." }
$computer = Get-CimInstance Win32_ComputerSystem
$profileHash = (Get-FileHash -LiteralPath $resolvedProfile -Algorithm SHA256).Hash.ToLowerInvariant()
$resolvedOutput = Resolve-RepositoryPath $OutputPath

$manifest = [ordered]@{
    copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
    schemaVersion = "1.0"
    qualification = "rtaime-renderer-reference"
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sourceCommit = $resolvedCommit
    profile = $Profile
    profileSha256 = $profileHash
    artifactRetentionDays = [int]$profileDocument.artifactRetentionDays
    repository = if ([string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) { "DaveBeusing/rtaime" } else { $env:GITHUB_REPOSITORY }
    workflow = [ordered]@{
        runId = if ([string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) { "local" } else { $env:GITHUB_RUN_ID }
        runAttempt = if ([string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ATTEMPT)) { "local" } else { $env:GITHUB_RUN_ATTEMPT }
        runnerName = if ([string]::IsNullOrWhiteSpace($env:RUNNER_NAME)) { "local" } else { $env:RUNNER_NAME }
    }
    operatingSystem = [ordered]@{
        caption = [string]$os.Caption
        version = [string]$os.Version
        buildNumber = [string]$os.BuildNumber
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        machine = if ($null -eq $computer) { $env:COMPUTERNAME } else { [string]$computer.Model }
    }
    dotnet = [ordered]@{
        sdk = $dotnetVersion
        framework = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    gpu = [ordered]@{
        ordinal = $DeviceOrdinal
        expectedName = $ExpectedDeviceName.Trim()
        detectedName = $gpuFields[0]
        driverVersion = $gpuFields[1]
        pciBusId = $gpuFields[2]
        totalMemoryMiB = [int]$gpuFields[3]
    }
}

Write-JsonFile -Value $manifest -Path $resolvedOutput
Write-Host "Renderer qualification manifest captured"
Write-Host "Source: $resolvedCommit"
Write-Host "GPU: $($gpuFields[0]) / driver $($gpuFields[1])"
Write-Host "Profile: $Profile"
Write-Host "Manifest: $resolvedOutput"
