# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

$profilePath = Join-Path $repositoryRoot "qualification/renderer/renderer-qualification.json"
$manifestPath = Join-Path $repositoryRoot "build/qualification/New-RendererQualificationManifest.ps1"
$runnerPath = Join-Path $repositoryRoot "build/qualification/Invoke-RendererReferenceQualification.ps1"
$verifierPath = Join-Path $repositoryRoot "build/qualification/Test-RendererReferenceQualification.ps1"
$hardwareTestPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/RendererReferenceHardwareQualificationTests.cs"
$processingPerformanceTestPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/V1CombinedReferencePerformanceTests.cs"
$cudaWorkflowPath = Join-Path $repositoryRoot ".github/workflows/cuda-reference-qualification.yml"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$documentationPath = Join-Path $repositoryRoot "docs/RendererQualificationAndSoakTesting.md"
$cudaPolicyPath = Join-Path $repositoryRoot "build/qualification/qualification-evidence-policy.json"

foreach ($path in @($profilePath, $manifestPath, $runnerPath, $verifierPath, $hardwareTestPath, $processingPerformanceTestPath, $cudaWorkflowPath, $requiredGatesPath, $documentationPath, $cudaPolicyPath)) {
    Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required renderer qualification artifact is missing: '$path'."
}

foreach ($scriptPath in @($manifestPath, $runnerPath, $verifierPath)) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
    Assert-Condition (@($parseErrors).Count -eq 0) "Renderer qualification script '$scriptPath' contains PowerShell syntax errors: $(@($parseErrors) -join ' | ')"
}

$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
Assert-Condition ([string]$profile.schemaVersion -eq "1.0") "Renderer qualification profile schema must be 1.0."
Assert-Condition ([string]$profile.qualification -eq "rtaime-renderer-reference") "Renderer qualification identity changed."
Assert-Condition ([int]$profile.artifactRetentionDays -eq 90) "Renderer qualification artifacts must retain the explicit 90-day policy."
Assert-Condition (@($profile.formats).Count -eq 2 -and @($profile.formats) -contains "1080p50" -and @($profile.formats) -contains "1080p59.94") "Renderer qualification must retain both V1 formats."
Assert-Condition ((@($profile.compositorLayerCounts) -join ",") -eq "0,1,2,4,8") "Renderer qualification must retain the 0/1/2/4/8 layer matrix."
$profiles = @($profile.profiles)
Assert-Condition ($profiles.Count -eq 3) "Renderer qualification must declare exactly smoke, stress and soak profiles."
foreach ($name in @("smoke", "stress", "soak")) {
    Assert-Condition (@($profiles | Where-Object name -eq $name).Count -eq 1) "Renderer qualification profile '$name' is required."
}
Assert-Condition ([int](@($profiles | Where-Object name -eq "smoke")[0].durationSecondsPerFormat) -eq 30) "Renderer smoke profile must retain 30 seconds per format."
Assert-Condition ([int](@($profiles | Where-Object name -eq "stress")[0].durationSecondsPerFormat) -eq 300) "Renderer stress profile must retain 300 seconds per format."
Assert-Condition ([int](@($profiles | Where-Object name -eq "soak")[0].durationSecondsPerFormat) -eq 3600) "Renderer soak profile must retain one hour per format."
Assert-Condition ([string]$profile.performancePolicy.frameBudgetAssessment -eq "P99_REPORTED") "Renderer frame-budget evidence must retain P99 reporting."
Assert-Condition ([string]$profile.performancePolicy.baselineComparison -eq "REQUIRED_FOR_REGRESSION_CLAIM") "Renderer performance regression claims must require reproducible baseline evidence."
Assert-Condition ([double]$profile.performancePolicy.maximumP99RegressionPercent -eq 20.0) "Renderer calibrated P99 regression policy changed."
Assert-Condition ([bool]$profile.performancePolicy.cudaReferenceQualificationRequired) "Renderer qualification must remain layered on the strict CUDA reference qualification."

$manifest = Get-Content -LiteralPath $manifestPath -Raw
Assert-Condition ($manifest -match '10\.0\.401') "Renderer manifest must bind the pinned SDK."
Assert-Condition ($manifest -match 'nvidia-smi') "Renderer manifest must capture NVIDIA GPU and driver identity."
Assert-Condition ($manifest -match 'profileSha256') "Renderer manifest must hash the qualification profile."
Assert-Condition ($manifest -match 'sourceCommit') "Renderer manifest must bind an exact source SHA."
Assert-Condition ($manifest -match 'pciBusId') "Renderer manifest must retain GPU PCI identity."

$hardwareTest = Get-Content -LiteralPath $hardwareTestPath -Raw
Assert-Condition ($hardwareTest -match 'RTAIME_RENDERER_REFERENCE_QUALIFICATION') "Renderer hardware test must require explicit opt-in."
Assert-Condition ($hardwareTest -match 'CudaGpuProcessingBackend') "Renderer hardware qualification must use the CUDA backend."
Assert-Condition ($hardwareTest -match 'new\[\] \{ 0, 1, 2, 4, 8 \}') "Renderer hardware qualification must execute 0/1/2/4/8 layer cases."
Assert-Condition ($hardwareTest -match 'GpuTransition\.CutToA' -and $hardwareTest -match 'GpuTransition\.Dissolve\(128\)') "Renderer hardware qualification must cover CUT and DISSOLVE."
Assert-Condition ($hardwareTest -match 'SHA256\.HashData') "Renderer hardware qualification must retain full-frame integrity comparison."
Assert-Condition ($hardwareTest -match 'TryExportMonitoringResource') "Renderer hardware qualification must exercise CUDA/D3D11 monitoring export."
Assert-Condition ($hardwareTest -match 'StartRecordingAsync' -and $hardwareTest -match 'BackpressureQualificationWriter') "Renderer hardware qualification must exercise bounded recording/backpressure."
Assert-Condition ($hardwareTest -match 'MonitoringHub\.Subscribe' -and $hardwareTest -match 'monitoringDisconnected' -and $hardwareTest -match 'monitoringReconnected') "Renderer hardware qualification must exercise monitoring disconnect/reconnect."
Assert-Condition ($hardwareTest -match 'P50' -or $hardwareTest -match 'p50Milliseconds') "Renderer hardware qualification must retain P50 evidence."
Assert-Condition ($hardwareTest -match 'p95Milliseconds' -and $hardwareTest -match 'p99Milliseconds') "Renderer hardware qualification must retain P95/P99 evidence."
Assert-Condition ($hardwareTest -match 'GpuVramUsedBytes') "Renderer hardware qualification must retain VRAM evidence."
Assert-Condition ($hardwareTest -match 'ActiveGpuSurfaces' -and $hardwareTest -match 'ActiveBuffers' -and $hardwareTest -match 'ActiveResources') "Renderer hardware qualification must fail on leaked GPU/readback/monitoring ownership."
Assert-Condition ($hardwareTest -match 'physicalExternalOutput[\s\S]*UNVERIFIED') "Renderer qualification must keep professional external output UNVERIFIED."
Assert-Condition ($hardwareTest -match 'physicalDeviceFaultInjection[\s\S]*UNVERIFIED') "Renderer qualification must not fabricate destructive GPU fault evidence."
Assert-Condition ($hardwareTest -match 'typedProcessingAcceleration[\s\S]*UNVERIFIED') "Renderer qualification evidence must keep managed typed-processing acceleration explicitly UNVERIFIED."

$processingPerformance = Get-Content -LiteralPath $processingPerformanceTestPath -Raw
foreach ($scenario in @("none", "color-grade", "chroma-key", "chroma-key-color-grade", "color-grade-chroma-key", "maximum-mixed-stack")) {
    Assert-Condition ($processingPerformance -match [Regex]::Escape($scenario)) "Managed compositing qualification must retain processing scenario '$scenario'."
}
Assert-Condition ($processingPerformance -match 'Hd1080p50Rgba8' -and $processingPerformance -match 'Hd1080p59_94Rgba8') "Managed compositing processing qualification must retain both V1 formats."
Assert-Condition ($processingPerformance -match 'GC\.GetAllocatedBytesForCurrentThread') "Managed compositing processing qualification must retain allocation-growth evidence."
Assert-Condition ($processingPerformance -match 'ActiveGpuSurfaces') "Managed compositing processing qualification must prove GPU surface ownership returns to baseline."

$runner = Get-Content -LiteralPath $runnerPath -Raw
foreach ($scenario in @("CLIP_SEEK", "RENDERER_BACKEND_RECOVERY", "RESOURCE_PRESSURE", "INTEROP_FAILURE_RECOVERY", "RESIZE_DPI_CHURN", "MONITORING_TRANSPORT_RECONNECT", "PROCESS_RESTART")) {
    Assert-Condition ($runner -match [Regex]::Escape($scenario)) "Renderer qualification runner must retain software scenario '$scenario'."
}
Assert-Condition ($runner -match 'BaselineEvidencePath') "Renderer qualification runner must support approved baseline comparison."
Assert-Condition ($runner -match 'maximumP99RegressionPercent') "Renderer qualification runner must use the declared calibrated regression threshold."
Assert-Condition ($runner -match 'No approved baseline evidence was supplied') "Missing baseline must remain explicitly UNVERIFIED rather than assumed PASS."

$verifier = Get-Content -LiteralPath $verifierPath -Raw
Assert-Condition ($verifier -match 'compositorCases\.Count -ne 20') "Renderer evidence verifier must require the full 20-case compositor matrix."
Assert-Condition ($verifier -match 'sequenceDiscontinuities') "Renderer evidence verifier must reject sequence discontinuities."
Assert-Condition ($verifier -match 'activeGpuSurfaces' -and $verifier -match 'activeReadbackBuffers' -and $verifier -match 'activeSharedMonitoringResources') "Renderer verifier must enforce cleanup invariants."
Assert-Condition ($verifier -match 'UploadHostToDevice' -and $verifier -match 'KernelGpuElapsed' -and $verifier -match 'MonitoringExport') "Renderer verifier must require CUDA timing breakdown including monitoring export."

$workflow = Get-Content -LiteralPath $cudaWorkflowPath -Raw
Assert-Condition ($workflow -match 'workflow_dispatch:') "Renderer physical qualification must remain manual-only through the CUDA reference workflow."
Assert-Condition ($workflow -notmatch '(?m)^\s*(pull_request|push):') "Renderer physical qualification must never run as hosted PR/push CI."
Assert-Condition ($workflow -match 'rtaime-cuda-reference') "Renderer qualification must use the dedicated CUDA reference runner."
Assert-Condition ($workflow -match 'Invoke-CudaReferenceQualification\.ps1') "Renderer workflow must retain strict CUDA reference qualification before the production-path extension."
Assert-Condition ($workflow -match 'Invoke-RendererReferenceQualification\.ps1') "CUDA self-hosted workflow must execute renderer production-path qualification."
Assert-Condition ($workflow -match 'renderer_profile') "Renderer workflow must expose smoke/stress/soak profile selection."
Assert-Condition ($workflow -match 'retention-days:\s*90') "Renderer workflow must retain qualification artifacts for 90 days."

$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
Assert-Condition ($requiredGates -match 'Test-RendererQualificationPolicy\.ps1') "Required Gates must validate renderer qualification structure."
Assert-Condition ($requiredGates -notmatch 'Invoke-RendererReferenceQualification\.ps1') "Hosted Required Gates must never masquerade as physical renderer qualification."

$bindingPolicy = Get-Content -LiteralPath $cudaPolicyPath -Raw | ConvertFrom-Json
$cudaBinding = @($bindingPolicy.bindings | Where-Object qualificationType -eq "CUDA_REFERENCE")
Assert-Condition ($cudaBinding.Count -eq 1 -and [string]$cudaBinding[0].payloadSchemaVersion -eq "1.1") "CUDA provenance policy must accept the current schema 1.1 physical payload."

$documentation = Get-Content -LiteralPath $documentationPath -Raw
Assert-Condition ($documentation -match 'smoke' -and $documentation -match 'stress' -and $documentation -match 'soak') "Renderer qualification documentation must explain all three profiles."
Assert-Condition ($documentation -match 'UNVERIFIED') "Renderer qualification documentation must retain explicit physical evidence boundaries."
Assert-Condition ($documentation -match 'P99') "Renderer qualification documentation must describe P99 and baseline comparison semantics."
Assert-Condition ($documentation -match '0 / 1 / 2 / 4 / 8') "Renderer qualification documentation must retain the hardware layer matrix."
Assert-Condition ($documentation -match 'managed Runtime layer-materialization path') "Renderer qualification documentation must state the current typed-processing execution boundary."
Assert-Condition ($documentation -match 'does \*\*not\*\* promote managed Color Grade or Chroma Key processing to a CUDA PASS') "Renderer qualification documentation must not fabricate CUDA typed-processing evidence."
Assert-Condition ($documentation -match 'Physical typed-processing acceleration/performance remains \*\*UNVERIFIED\*\*') "Physical typed-processing performance must remain explicitly UNVERIFIED."

Write-Host "Renderer qualification policy verification PASS"
Write-Host "Hosted CI validates structure only; physical CUDA/D3D11/long-run evidence remains UNVERIFIED until the self-hosted workflow runs."
