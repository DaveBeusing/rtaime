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
$cudaPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/CudaGpuProcessingBackend.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/GpuCompositorOptimizationTests.cs"
$performanceTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/GpuCompositorPerformanceProfileTests.cs"
$graphicsTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/GraphicsOverlayIntegrationTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/CompositorPipelineOptimization.md"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"

foreach ($path in @(
	$gpuPath,
	$cudaPath,
	$runtimePath,
	$unitTestsPath,
	$performanceTestsPath,
	$graphicsTestsPath,
	$documentationPath,
	$requiredGatesPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required compositor optimization artifact is missing: '$path'."
}

$gpu = Get-Content -LiteralPath $gpuPath -Raw
$cuda = Get-Content -LiteralPath $cudaPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$unitTests = Get-Content -LiteralPath $unitTestsPath -Raw
$performanceTests = Get-Content -LiteralPath $performanceTestsPath -Raw
$graphicsTests = Get-Content -LiteralPath $graphicsTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw

Assert-Condition ($gpu -match 'LayerContributesToComposite\(GpuKeyLayer layer\)\s*=>\s*\r?\n?\s*layer\.Visible && layer\.Opacity != 0') "Compositor pass reduction must be limited to exact hidden/zero-opacity no-op semantics."
Assert-Condition ($gpu -match 'contributingLayerCount' -and $gpu -match 'if \(!LayerContributesToComposite\(layer\)\)\s*\r?\n\s*continue;') "Composite execution must skip only classified no-op layers."
Assert-Condition ($gpu -match 'Math\.Max\(1, contributingLayerCount\)') "Composite execution must retain at least one background transition pass."
Assert-Condition ($gpu -match 'gpu\.composite\.passes:') "Provider observations must retain bounded backend-pass evidence."
Assert-Condition ($gpu -match 'ValidateCompositeRequest\(request\)') "All requested layers must remain validated before no-op pass elimination."

Assert-Condition ($cuda -match 'private const string CompositeKernelPtx') "CUDA composite kernel must remain an embedded startup-loaded artifact."
Assert-Condition ($cuda -match 'cuModuleLoadData') "CUDA backend must load the composite module through the established startup path."
Assert-Condition ($cuda -notmatch '(?i)nvrtc|compileProgram|compileKernel') "Runtime CUDA shader/kernel compilation must not be introduced."

foreach ($testName in @(
	'Ordered_layer_matrix_matches_independent_integer_reference',
	'Hidden_and_zero_opacity_layers_skip_backend_passes_with_exact_pixels',
	'Transparent_boundaries_and_opacity_extremes_are_byte_exact')) {
	Assert-Condition ($unitTests -match [Regex]::Escape($testName)) "Compositor golden regression '$testName' is required."
}
Assert-Condition ($unitTests -match '\[InlineData\(0\)\]' -and
	$unitTests -match '\[InlineData\(1\)\]' -and
	$unitTests -match '\[InlineData\(2\)\]' -and
	$unitTests -match '\[InlineData\(4\)\]' -and
	$unitTests -match '\[InlineData\(8\)\]') "Golden parity must retain 0/1/2/4/8-layer coverage."
Assert-Condition ($unitTests -match 'new\(3, 3, FrameRate\.Fps50') "Golden parity must retain odd-resolution coverage."

Assert-Condition ($performanceTests -match 'Managed_reference_profiles_pass_count_logical_traffic_and_tail_latency') "Managed compositor profiling regression is required."
Assert-Condition ($performanceTests -match 'p95' -and $performanceTests -match 'p99' -and $performanceTests -match 'logicalBytesPerRequest') "Compositor profile must retain tail-latency and logical traffic evidence."

Assert-Condition ($graphicsTests -match 'Bitmap_transform_applies_rotation_anchor_and_crop_deterministically') "Rotation/crop golden integration coverage must remain present."
Assert-Condition ($graphicsTests -match 'Color_grade_processing_node_executes_before_gpu_composite_and_preserves_alpha') "Color Grade pre-composite parity coverage must remain present."

Assert-Condition ($runtime -match '_gpu\.RentReadback\(output\)') "Runtime must retain one post-composite Program readback source."
Assert-Condition ($runtime -match '_monitoringTap\.TryCapture' -and $runtime -match 'new GpuRecordingPayloadLease\(pixels\.Retain\(\)\)') "Monitoring and recording must continue from the same post-composite Program payload."

Assert-Condition ($documentation -match 'fused multi-layer CUDA kernel: not introduced') "Documentation must record the fused-kernel decision."
Assert-Condition ($documentation -match '\*\*UNVERIFIED\*\*') "Physical CUDA performance claims must remain UNVERIFIED without exact-SHA hardware evidence."
Assert-Condition ($documentation -match '0 / 1 / 2 / 4 / 8') "Documentation must retain the required layer-count profiling matrix."
Assert-Condition ($requiredGates -match 'Test-CompositorOptimizationPolicy\.ps1') "Required Gates must execute the compositor optimization policy."

Write-Host "Compositor optimization policy verification PASS"
Write-Host "Safe pass reduction: hidden and zero-opacity layers only"
Write-Host "Fused CUDA multi-layer kernel: NOT INTRODUCED"
Write-Host "Physical CUDA P95/P99 performance evidence: UNVERIFIED"
