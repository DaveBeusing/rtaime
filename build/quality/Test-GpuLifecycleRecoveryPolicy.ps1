# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

$providerPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$cudaPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/CudaGpuProcessingBackend.cs"
$monitoringPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuSharedMonitoringBackend.cs"
$testsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/GpuLifecycleRecoveryTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/GpuLifecycleRecovery.md"

foreach ($path in @($providerPath, $cudaPath, $monitoringPath, $testsPath, $documentationPath)) {
    Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required GPU lifecycle recovery artifact is missing: '$path'."
}

$provider = Get-Content -LiteralPath $providerPath -Raw
$cuda = Get-Content -LiteralPath $cudaPath -Raw
$monitoring = Get-Content -LiteralPath $monitoringPath -Raw
$tests = Get-Content -LiteralPath $testsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

foreach ($state in @("Unavailable", "Starting", "Ready", "Degraded", "Failed", "Recovering", "Stopped")) {
    Assert-Condition ($provider -match "GpuProviderState\.$state") "GPU lifecycle state '$state' must remain explicitly represented."
}

Assert-Condition ($provider -match "GpuProviderLifecycleReasonCodes") "GPU lifecycle failures must retain stable reason codes."
Assert-Condition ($provider -match "public void Recover\(\)") "GPU provider must expose explicit controlled recovery."
Assert-Condition ($provider -match "advanceGeneration: true") "Successful GPU start/recovery must rotate lifecycle generation."
Assert-Condition ($provider -match "MonitoringUnavailable" -and $provider -match "MarkDegradedUnsafe") "Monitoring failure must degrade evidence without becoming a second Program authority."
Assert-Condition ($provider -match "ReadbackPoolExhausted") "Readback lease exhaustion must be explicit and bounded."
Assert-Condition ($provider -match "_unreleasedBackendSurfaces") "Failed backend surface releases must remain tracked."
Assert-Condition ($provider -match "TryReleaseMonitoringResourceUnsafe") "Monitoring resource release must remain retryable."
Assert-Condition ($provider -match "ProviderAvailabilityState\.Unavailable" -and $provider -match "ProviderAvailabilityState\.Degraded") "Lifecycle state must project through the existing provider health contract."

Assert-Condition ($cuda -match "_surfaces\.TryGetValue\(surfaceId") "CUDA release must retain surface identity until cleanup succeeds."
Assert-Condition ($cuda -match "_surfaces\.Remove\(surfaceId\)") "CUDA release must remove surface identity only after release succeeds."
Assert-Condition ($cuda -match "TryPeek\(out var pointer\)") "CUDA pooled allocation cleanup must not pop ownership before a successful free."
Assert-Condition ($cuda -match "CleanupContext\(\)") "CUDA partial-start and stop paths must use explicit context cleanup."
Assert-Condition ($cuda -match "CudaD3D11MonitoringInterop\.TryCreate") "CUDA monitoring must support bounded interop recreation."
Assert-Condition ($monitoring -match "release\(\);[\s\S]*_release = null") "Monitoring backend resources must retain release ownership until callback success."

foreach ($name in @(
    "Start_failure_is_explicit_and_recovery_rotates_generation",
    "Cuda_classified_upload_failure_fails_closed",
    "Cuda_classified_composite_failure_fails_closed_until_recovery",
    "Cuda_classified_readback_failure_returns_host_lease_and_fails_closed",
    "Stop_failure_is_explicit_and_retryable",
    "Readback_pool_exhaustion_degrades_without_losing_the_active_lease",
    "Monitoring_export_failure_is_isolated_from_program_and_can_recover",
    "Monitoring_release_failure_remains_tracked_and_is_retryable")) {
    Assert-Condition ($tests -match [regex]::Escape($name)) "Missing GPU lifecycle recovery regression: '$name'."
}

Assert-Condition ($documentation -match "UNVERIFIED") "GPU lifecycle documentation must preserve the physical-hardware evidence boundary."
Assert-Condition ($documentation -match "managed reference" -and $documentation -match "must not") "Documentation must prohibit silent production fallback to the managed reference backend."

Write-Host "GPU lifecycle recovery policy PASS"
