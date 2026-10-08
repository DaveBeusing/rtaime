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

$gpuPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$runtimeDiagnosticsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostDiagnostics.cs"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/GpuMemoryTransferOptimizationTests.cs"
$performanceTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/GpuProcessingPerformanceTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/GpuMemoryTransferOptimization.md"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"

foreach ($path in @($gpuPath, $runtimePath, $runtimeDiagnosticsPath, $unitTestsPath, $performanceTestsPath, $documentationPath, $requiredGatesPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required GPU transfer optimization artifact is missing: '$path'."
}

$gpu = Get-Content -LiteralPath $gpuPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$runtimeDiagnostics = Get-Content -LiteralPath $runtimeDiagnosticsPath -Raw
$unitTests = Get-Content -LiteralPath $unitTestsPath -Raw
$performanceTests = Get-Content -LiteralPath $performanceTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw

Assert-Condition ($gpu -match 'ReusableUploadSurfaceCapacity\s*=\s*16') "Reusable GPU upload retention must remain explicitly bounded."
Assert-Condition ($gpu -match 'GpuMemoryTransferStatistics') "GPU provider must expose bounded transfer and pool-pressure evidence."
Assert-Condition ($gpu -match 'UploadOperations' -and $gpu -match 'ReadbackOperations' -and $gpu -match 'HostToDeviceOperations' -and $gpu -match 'DeviceToHostOperations' -and $gpu -match 'AvoidedUploadBytes' -and $gpu -match 'AvoidedHostToDeviceBytes') "GPU transfer evidence must distinguish logical operations from device-transfer counters."
Assert-Condition ($gpu -match 'public GpuFrame Materialize\(' -and $gpu -match '\.Upload\(SourceId, Content, timing, Generation\.Initial, "static"\)') "Default static materialization must remain non-retaining."
Assert-Condition ($gpu -match 'MaterializeReusable' -and $gpu -match 'UploadReusable') "Reusable GPU materialization must remain an explicit opt-in."
Assert-Condition ($gpu -match 'ReferenceEquals\(cached\.Content, content\)' -and $gpu -match 'ContentVersion' -and $gpu -match 'ContentGeneration') "Reusable uploads must bind source-buffer identity and invalidate on content or generation changes."
Assert-Condition ($gpu -match 'RetainedForReuse' -and $gpu -match 'Frames\.Count') "Reusable backend surfaces must remain reference/lifetime tracked."
Assert-Condition ($gpu -match 'EnsureReusableUploadCapacityUnsafe' -and $gpu -match 'OrderBy\(pair => pair\.Value\.LastUseOrdinal\)') "Reusable upload cache must retain deterministic bounded eviction."
Assert-Condition ($gpu -match '_reusableUploads\.Clear\(\)') "Provider stop/recovery must clear reusable upload retention."

Assert-Condition ([Regex]::Matches($runtime, 'MaterializeReusable\(_gpu, timing\)').Count -ge 4) "RuntimeHost long-lived compositing layers must opt into reusable uploads."
Assert-Condition ($runtime -match 'GpuMemoryTransfers\s*=>\s*_gpu\.MemoryTransferStatistics') "RuntimeHost must expose GPU transfer/pool evidence."
Assert-Condition ($runtimeDiagnostics -match 'gpu\.transfer\.uploadBytes' -and $runtimeDiagnostics -match 'gpu\.transfer\.hostToDeviceBytes' -and $runtimeDiagnostics -match 'gpu\.transfer\.readbackBytes' -and $runtimeDiagnostics -match 'gpu\.uploadReuse\.surfaces' -and $runtimeDiagnostics -match 'gpu\.readback\.active' -and $runtimeDiagnostics -match 'gpu\.monitoringResources\.active') "Runtime support health diagnostics must publish logical/device transfer and pool-pressure evidence."
Assert-Condition ($runtime -match '_gpu\.RentReadback\(output\)' -and $runtime -notmatch '_gpu\.Readback\(output\)') "Program hot path must retain the reusable host-readback path."

foreach ($testName in @(
	'Static_source_reuses_one_uploaded_surface_across_frame_timings',
	'Static_source_content_mutation_replaces_cached_surface_without_stale_pixels',
	'Recreated_static_source_with_same_identity_does_not_reuse_different_buffer_content',
	'Dynamic_source_reuses_unchanged_generation_and_replaces_after_update',
	'Reusable_upload_cache_is_bounded_and_evicts_oldest_retention',
	'Reusable_surface_is_not_released_until_last_frame_reference_is_disposed',
	'Reusable_retention_is_not_reported_as_active_frame_ownership')) {
	Assert-Condition ($unitTests -match [Regex]::Escape($testName)) "GPU transfer regression '$testName' is required."
}

Assert-Condition ($performanceTests -match 'Reusable_static_1080p_source_avoids_repeated_full_frame_uploads') "1080p reusable-upload transfer-count regression is required."
Assert-Condition ($performanceTests -match 'UploadOperations' -and $performanceTests -match 'AvoidedUploadBytes' -and $performanceTests -match 'HostToDeviceOperations') "Performance regression must assert logical transfer reduction without misclassifying managed-reference work as HtoD."

Assert-Condition ($documentation -match 'Full-frame transfer inventory') "GPU transfer documentation must retain the full-frame transfer inventory."
Assert-Condition ($documentation -match 'backend-native Program-output path is introduced') "GPU transfer documentation must explicitly record the low-copy output decision."
Assert-Condition ($documentation -match '\*\*UNVERIFIED\*\*') "Physical GPU/PCIe performance must remain UNVERIFIED without hardware evidence."
Assert-Condition ($requiredGates -match 'Test-GpuMemoryTransferPolicy\.ps1') "Required Gates must execute the GPU memory transfer policy."

Write-Host "GPU memory transfer policy verification PASS"
Write-Host "Reusable layer-upload cache: bounded explicit opt-in"
Write-Host "Physical PCIe / CUDA performance evidence: UNVERIFIED unless captured on reference hardware"
