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

$qualificationSourcePath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/CudaReferenceHardwareQualification.cs"
$runtimeHostPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
$qualificationTestPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/CudaReferenceHardwareQualificationTests.cs"
$runnerPath = Join-Path $repositoryRoot "build/qualification/Invoke-CudaReferenceQualification.ps1"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/cuda-reference-qualification.yml"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$documentationPath = Join-Path $repositoryRoot "docs/CudaReferenceHardwareQualification.md"

foreach ($path in @($qualificationSourcePath, $runtimeHostPath, $qualificationTestPath, $runnerPath, $workflowPath, $requiredGatesPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required CUDA qualification artifact is missing: '$path'."
}

$source = Get-Content -LiteralPath $qualificationSourcePath -Raw
$runtimeHost = Get-Content -LiteralPath $runtimeHostPath -Raw
$tests = Get-Content -LiteralPath $qualificationTestPath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($source -match 'CudaQualificationStatus[\s\S]*Unverified[\s\S]*Passed[\s\S]*Failed') "CUDA qualification must distinguish UNVERIFIED, PASSED and FAILED."
Assert-Condition ($source -match 'GpuBackendKind\.NvidiaCuda') "Qualification must require the NVIDIA CUDA backend."
Assert-Condition ($source -match 'HardwareAccelerated') "Qualification must require hardware acceleration."
Assert-Condition ($source -match 'new\[\]\s*\{\s*VideoFormat\.Hd1080p50Rgba8,\s*VideoFormat\.Hd1080p59_94Rgba8\s*\}') "Qualification must execute both V1 formats from one explicit matrix."
Assert-Condition ([Regex]::Matches($source, 'cases\.Add\(RunCase').Count -eq 4) "Qualification must declare exactly four operations per V1 format."
foreach ($operation in @('CUT_A', 'CUT_B', 'DISSOLVE_50', 'DISSOLVE_LAYER')) {
	Assert-Condition ($source -match [Regex]::Escape($operation)) "CUDA qualification operation '$operation' is required."
}
Assert-Condition ($source -match 'cases\.Count == 8') "Qualification must require all eight V1 format/operation results before PASS."
Assert-Condition ($source -match 'PixelCorrect') "Qualification must verify deterministic output pixels."
Assert-Condition ($source -match 'SurfaceLifetimeCorrect') "Qualification must verify GPU surface lifetime."
Assert-Condition ($source -match 'P95Milliseconds') "Qualification must record P95 latency."
Assert-Condition ($source -match 'ProductionP95LatencyCeilingMilliseconds\s*=\s*5\.0') "CUDA qualification must enforce the 5 ms P95 production ceiling."
Assert-Condition ($source -match 'ProductionMaximumLatencyCeilingMilliseconds\s*=\s*10\.0') "CUDA qualification must enforce the 10 ms maximum production ceiling."
Assert-Condition ($source -match 'p95 <= ProductionP95LatencyCeilingMilliseconds') "CUDA qualification must apply the production P95 latency ceiling."
Assert-Condition ($runtimeHost -match 'CudaGpuProcessingBackend\.Detect\(\)' -and $runtimeHost -match 'new CudaGpuProcessingBackend\(\)') "Production RuntimeHost must prefer the CUDA backend when hardware acceleration is available."
Assert-Condition ($runtimeHost -match 'new ManagedReferenceGpuBackend\(\)') "Production RuntimeHost must retain the managed reference fallback for environments without CUDA."

Assert-Condition ($tests -match 'RTAIME_CUDA_REFERENCE_QUALIFICATION') "Hardware test must require explicit qualification opt-in."
Assert-Condition ($tests -match 'RTAIME_CUDA_REFERENCE_DEVICE') "Hardware test must require an expected device identity."
Assert-Condition ($tests -match 'RTAIME_CUDA_REFERENCE_EVIDENCE') "Hardware test must require an explicit evidence path."
Assert-Condition ($tests -match 'Unverified_report_can_never_be_serialized_as_passed') "Regression coverage must ensure UNVERIFIED cannot be represented as PASSED."

Assert-Condition ($runner -match '\$evidence\.status -ne "PASSED"') "Qualification wrapper must reject non-PASSED evidence."
Assert-Condition ($runner -match 'cases\)\.Count -ne 8') "Qualification wrapper must require exactly eight cases."
Assert-Condition ($runner -match 'pixelCorrect') "Qualification wrapper must inspect pixel correctness."
Assert-Condition ($runner -match 'surfaceLifetimeCorrect') "Qualification wrapper must inspect surface lifetime."
Assert-Condition ($runner -match 'timingBudgetMet') "Qualification wrapper must inspect timing evidence."

Assert-Condition ($workflow -match 'workflow_dispatch:') "CUDA reference workflow must be manual."
Assert-Condition ($workflow -notmatch '(?m)^\s*(pull_request|push):') "CUDA hardware qualification must not run implicitly on generic hosted CI."
Assert-Condition ($workflow -match '- self-hosted') "CUDA reference workflow must require a self-hosted runner."
Assert-Condition ($workflow -match '- rtaime-cuda-reference') "CUDA reference workflow must require the dedicated reference-hardware runner label."
Assert-Condition ($workflow -match 'Invoke-CudaReferenceQualification\.ps1') "CUDA workflow must execute the qualification wrapper."
Assert-Condition ($workflow -match 'upload-artifact@v4') "CUDA workflow must retain qualification evidence."

Assert-Condition ($documentation -match 'Current qualification state[\s\S]*UNVERIFIED') "Documentation must explicitly retain UNVERIFIED state until physical evidence exists."
Assert-Condition ($documentation -match 'There is no silent fallback') "Documentation must prohibit silent hardware-to-managed fallback."
Assert-Condition ($documentation -match 'P95 must be at or below 5 ms') "Documentation must state the production P95 latency ceiling."
Assert-Condition ($requiredGates -notmatch 'Invoke-CudaReferenceQualification\.ps1') "Generic Required Gates must not masquerade as physical CUDA qualification."

Write-Host "CUDA qualification policy verification PASS"
Write-Host "Hardware evidence state: UNVERIFIED unless the dedicated self-hosted workflow produces PASSED evidence"
Write-Host "Profile: 1080p50 + 1080p59.94, CUT/DISSOLVE/layer, pixel/lifetime/timing evidence"
