# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("smoke", "stress", "soak")][string]$Profile,
    [Parameter(Mandatory)][string]$ExpectedDeviceName,
    [int]$DeviceOrdinal = 0,
    [string]$ProfilePath = "qualification/renderer/renderer-qualification.json",
    [string]$SourceCommit = "",
    [string]$OutputRoot = "artifacts/qualification/renderer",
    [string]$BaselineEvidencePath = "",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "Renderer reference qualification requires Windows reference hardware." }
if ([Environment]::Is64BitProcess -ne $true) { throw "Renderer reference qualification requires an x64 process." }
if ([string]::IsNullOrWhiteSpace($ExpectedDeviceName)) { throw "ExpectedDeviceName is required." }
if ($DeviceOrdinal -lt 0) { throw "DeviceOrdinal must be non-negative." }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$manifestScript = Join-Path $PSScriptRoot "New-RendererQualificationManifest.ps1"
$verifierScript = Join-Path $PSScriptRoot "Test-RendererReferenceQualification.ps1"

function Resolve-RepositoryPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Write-JsonFile {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [System.IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
}

function Invoke-TargetedScenario {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Filter,
        [Parameter(Mandatory)][string]$LogPath
    )
    $arguments = @("test", $Project, "--configuration", "Release", "--no-build", "--filter", $Filter, "--logger", "console;verbosity=normal")
    $output = @(& dotnet @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    [System.IO.File]::WriteAllLines($LogPath, @($output | ForEach-Object { [string]$_ }), [System.Text.UTF8Encoding]::new($false))
    return [ordered]@{
        id = $Id
        project = $Project
        filter = $Filter
        status = if ($exitCode -eq 0) { "PASS" } else { "FAIL" }
        log = [System.IO.Path]::GetRelativePath($repositoryRoot, $LogPath).Replace('\', '/')
        exitCode = $exitCode
    }
}

$resolvedProfilePath = Resolve-RepositoryPath $ProfilePath
if (-not (Test-Path -LiteralPath $resolvedProfilePath -PathType Leaf)) { throw "Renderer qualification profile is missing: '$resolvedProfilePath'." }
if (-not (Test-Path -LiteralPath $manifestScript -PathType Leaf)) { throw "Renderer qualification manifest script is missing." }
if (-not (Test-Path -LiteralPath $verifierScript -PathType Leaf)) { throw "Renderer qualification verifier is missing." }

$profileDocument = Get-Content -LiteralPath $resolvedProfilePath -Raw | ConvertFrom-Json
if ([string]$profileDocument.schemaVersion -ne "1.0") { throw "Renderer qualification profile schema must be 1.0." }
if ([string]$profileDocument.qualification -ne "rtaime-renderer-reference") { throw "Unexpected renderer qualification profile identity." }
$selectedProfiles = @($profileDocument.profiles | Where-Object { [string]$_.name -eq $Profile })
if ($selectedProfiles.Count -ne 1) { throw "Renderer qualification profile '$Profile' is not defined exactly once." }
$selected = $selectedProfiles[0]

$resolvedCommit = $SourceCommit.Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($resolvedCommit)) {
    $resolvedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0) { throw "Unable to resolve renderer qualification source commit." }
}
if ($resolvedCommit -notmatch '^[0-9a-f]{40}$') { throw "Renderer qualification requires an exact 40-character source commit." }

$resolvedOutputRoot = Resolve-RepositoryPath $OutputRoot
if (Test-Path -LiteralPath $resolvedOutputRoot) { Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force }
$logsRoot = Join-Path $resolvedOutputRoot "logs"
New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null

if (-not $NoBuild) {
    Push-Location $repositoryRoot
    try {
        foreach ($project in @(
            "tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj",
            "tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj",
            "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj",
            "tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj")) {
            dotnet build $project --configuration Release
            if ($LASTEXITCODE -ne 0) { throw "Renderer qualification build failed for '$project'." }
        }
    } finally { Pop-Location }
}

$manifestPath = Join-Path $resolvedOutputRoot "manifest.json"
$manifestArgs = @{
    Profile = $Profile
    ExpectedDeviceName = $ExpectedDeviceName
    DeviceOrdinal = $DeviceOrdinal
    ProfilePath = $resolvedProfilePath
    SourceCommit = $resolvedCommit
    OutputPath = $manifestPath
}
& $manifestScript @manifestArgs

$rendererEvidencePath = Join-Path $resolvedOutputRoot "renderer-reference.json"
$previous = @{
    Enabled = $env:RTAIME_RENDERER_REFERENCE_QUALIFICATION
    Device = $env:RTAIME_RENDERER_REFERENCE_DEVICE
    DeviceOrdinal = $env:RTAIME_RENDERER_REFERENCE_DEVICE_ORDINAL
    Profile = $env:RTAIME_RENDERER_REFERENCE_PROFILE
    Duration = $env:RTAIME_RENDERER_DURATION_SECONDS_PER_FORMAT
    Warmup = $env:RTAIME_RENDERER_WARMUP_BOUNDARIES
    Samples = $env:RTAIME_RENDERER_COMPOSITOR_SAMPLES
    Backpressure = $env:RTAIME_RENDERER_RECORDING_BACKPRESSURE_BOUNDARIES
    Evidence = $env:RTAIME_RENDERER_REFERENCE_EVIDENCE
    SourceCommit = $env:RTAIME_RENDERER_SOURCE_COMMIT
}
try {
    $env:RTAIME_RENDERER_REFERENCE_QUALIFICATION = "1"
    $env:RTAIME_RENDERER_REFERENCE_DEVICE = $ExpectedDeviceName.Trim()
    $env:RTAIME_RENDERER_REFERENCE_DEVICE_ORDINAL = $DeviceOrdinal.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:RTAIME_RENDERER_REFERENCE_PROFILE = $Profile
    $env:RTAIME_RENDERER_DURATION_SECONDS_PER_FORMAT = ([int]$selected.durationSecondsPerFormat).ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:RTAIME_RENDERER_WARMUP_BOUNDARIES = ([int]$selected.warmupBoundaries).ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:RTAIME_RENDERER_COMPOSITOR_SAMPLES = ([int]$selected.compositorSamplesPerCase).ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:RTAIME_RENDERER_RECORDING_BACKPRESSURE_BOUNDARIES = ([int]$selected.recordingBackpressureBoundaries).ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:RTAIME_RENDERER_REFERENCE_EVIDENCE = $rendererEvidencePath
    $env:RTAIME_RENDERER_SOURCE_COMMIT = $resolvedCommit

    Push-Location $repositoryRoot
    try {
        $hardwareFilter = "FullyQualifiedName~RendererReferenceHardwareQualificationTests.Renderer_reference_profile_must_pass_when_explicitly_enabled"
        dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release --no-build --filter $hardwareFilter
        if ($LASTEXITCODE -ne 0) { throw "Renderer reference hardware qualification failed." }
    } finally { Pop-Location }
} finally {
    $env:RTAIME_RENDERER_REFERENCE_QUALIFICATION = $previous.Enabled
    $env:RTAIME_RENDERER_REFERENCE_DEVICE = $previous.Device
    $env:RTAIME_RENDERER_REFERENCE_DEVICE_ORDINAL = $previous.DeviceOrdinal
    $env:RTAIME_RENDERER_REFERENCE_PROFILE = $previous.Profile
    $env:RTAIME_RENDERER_DURATION_SECONDS_PER_FORMAT = $previous.Duration
    $env:RTAIME_RENDERER_WARMUP_BOUNDARIES = $previous.Warmup
    $env:RTAIME_RENDERER_COMPOSITOR_SAMPLES = $previous.Samples
    $env:RTAIME_RENDERER_RECORDING_BACKPRESSURE_BOUNDARIES = $previous.Backpressure
    $env:RTAIME_RENDERER_REFERENCE_EVIDENCE = $previous.Evidence
    $env:RTAIME_RENDERER_SOURCE_COMMIT = $previous.SourceCommit
}

$scenarioDefinitions = @(
    @{ Id = "CLIP_SEEK"; Project = "tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj"; Filter = "FullyQualifiedName~MediaDeckControllerTests.Open_transport_seek_and_marker_commands_reconcile_confirmed_state" },
    @{ Id = "RENDERER_BACKEND_RECOVERY"; Project = "tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj"; Filter = "FullyQualifiedName~GpuLifecycleRecoveryTests" },
    @{ Id = "RESIZE_DPI_CHURN"; Project = "tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj"; Filter = "FullyQualifiedName~MonitoringVisualPerformanceQualificationTests.Sustained_resize_and_dpi_churn_does_not_create_unbounded_target_revisions" },
    @{ Id = "MONITORING_TRANSPORT_RECONNECT"; Project = "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj"; Filter = "FullyQualifiedName~OperatorMonitoringPlaneTests.Dedicated_named_pipe_monitoring_transport_reconnects_after_server_restart|FullyQualifiedName~OperatorMonitoringPlaneTests.Sustained_shared_monitoring_replacement_remains_bounded_and_returns_to_baseline" },
    @{ Id = "PROCESS_RESTART"; Project = "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj"; Filter = "FullyQualifiedName~ProcessSupervisionRecoveryTests.Killed_RuntimeHost_is_restarted_and_Control_reapplies_same_authority_revision" }
)
$softwareScenarios = [System.Collections.Generic.List[object]]::new()
Push-Location $repositoryRoot
try {
    foreach ($scenario in $scenarioDefinitions) {
        $fileName = ([string]$scenario.Id).ToLowerInvariant().Replace('_', '-') + ".log"
        $softwareScenarios.Add((Invoke-TargetedScenario -Id ([string]$scenario.Id) -Project ([string]$scenario.Project) -Filter ([string]$scenario.Filter) -LogPath (Join-Path $logsRoot $fileName)))
    }
} finally { Pop-Location }

$softwareEvidencePath = Join-Path $resolvedOutputRoot "software-scenarios.json"
$softwareStatus = if (@($softwareScenarios | Where-Object { [string]$_.status -eq "FAIL" }).Count -eq 0) { "PASS" } else { "FAIL" }
Write-JsonFile -Value ([ordered]@{
    copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
    schemaVersion = "1.0"
    sourceCommit = $resolvedCommit
    profile = $Profile
    status = $softwareStatus
    scenarios = $softwareScenarios
}) -Path $softwareEvidencePath
if ($softwareStatus -ne "PASS") { throw "Renderer qualification software fault scenarios contain one or more failures." }

$baselineResultPath = Join-Path $resolvedOutputRoot "baseline-comparison.json"
$baselineStatus = "UNVERIFIED"
$baselineItems = @()
if (-not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
    $resolvedBaseline = [System.IO.Path]::GetFullPath($BaselineEvidencePath)
    if (-not (Test-Path -LiteralPath $resolvedBaseline -PathType Leaf)) { throw "Renderer qualification baseline evidence is missing: '$resolvedBaseline'." }

    $currentEvidence = Get-Content -LiteralPath $rendererEvidencePath -Raw | ConvertFrom-Json
    $baselineEvidence = Get-Content -LiteralPath $resolvedBaseline -Raw | ConvertFrom-Json
    if ([string]$baselineEvidence.schemaVersion -ne "1.0" -or [string]$baselineEvidence.qualification -ne "rtaime-renderer-reference" -or [string]$baselineEvidence.status -ne "PASSED") {
        throw "Renderer qualification baseline must be PASSED schema 1.0 renderer-reference evidence."
    }
    if ([string]$baselineEvidence.detectedDeviceName -ne [string]$currentEvidence.detectedDeviceName) { throw "Renderer qualification baseline GPU identity differs from the current reference GPU." }
    if ([string]$baselineEvidence.profile -ne $Profile) { throw "Renderer qualification baseline profile differs from '$Profile'." }

    $maximumRegressionPercent = [double]$profileDocument.performancePolicy.maximumP99RegressionPercent
    $comparison = [System.Collections.Generic.List[object]]::new()
    foreach ($current in @($currentEvidence.runtime)) {
        $matches = @($baselineEvidence.runtime | Where-Object { [string]$_.format -eq [string]$current.format })
        if ($matches.Count -ne 1) { throw "Renderer baseline is missing format '$($current.format)'." }
        $currentP99 = [double]$current.cpuBoundary.p99Milliseconds
        $baselineP99 = [double]$matches[0].cpuBoundary.p99Milliseconds
        if (-not [double]::IsFinite($currentP99) -or -not [double]::IsFinite($baselineP99) -or $baselineP99 -le 0) { throw "Renderer baseline contains invalid P99 evidence for '$($current.format)'." }
        $regressionPercent = (($currentP99 - $baselineP99) / $baselineP99) * 100.0
        $passed = $regressionPercent -le $maximumRegressionPercent
        $comparison.Add([ordered]@{
            format = [string]$current.format
            baselineP99Milliseconds = $baselineP99
            currentP99Milliseconds = $currentP99
            regressionPercent = $regressionPercent
            maximumAllowedRegressionPercent = $maximumRegressionPercent
            status = if ($passed) { "PASS" } else { "FAIL" }
        })
    }
    $baselineItems = @($comparison)
    $baselineStatus = if (@($baselineItems | Where-Object { [string]$_.status -eq "FAIL" }).Count -eq 0) { "PASS" } else { "FAIL" }
}
Write-JsonFile -Value ([ordered]@{
    copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
    schemaVersion = "1.0"
    sourceCommit = $resolvedCommit
    profile = $Profile
    status = $baselineStatus
    comparison = $baselineItems
    note = if ($baselineStatus -eq "UNVERIFIED") { "No approved baseline evidence was supplied. Current P99 is retained, but no performance-regression claim is made." } else { "Current runtime P99 was compared with supplied same-device, same-profile PASSED evidence." }
}) -Path $baselineResultPath
if ($baselineStatus -eq "FAIL") { throw "Renderer qualification P99 regression exceeded the calibrated baseline policy." }

$verifyArgs = @{
    EvidencePath = $rendererEvidencePath
    ManifestPath = $manifestPath
    SoftwareEvidencePath = $softwareEvidencePath
    BaselineComparisonPath = $baselineResultPath
    ExpectedSourceCommit = $resolvedCommit
    ExpectedProfile = $Profile
}
& $verifierScript @verifyArgs

Write-Host "Renderer reference qualification PASS"
Write-Host "Source: $resolvedCommit"
Write-Host "Profile: $Profile"
Write-Host "Baseline comparison: $baselineStatus"
Write-Host "External AJA/genlock/physical output qualification: UNVERIFIED in this renderer workflow"
