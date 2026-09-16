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
$qualificationTestPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/CudaReferenceHardwareQualificationTests.cs"
$runnerPath = Join-Path $repositoryRoot "build/qualification/Invoke-CudaReferenceQualification.ps1"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/cuda-reference-qualification.yml"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$documentationPath = Join-Path $repositoryRoot "docs/CudaReferenceHardwareQualification.md"

foreach ($path in @($qualificationSourcePath, $qualificationTestPath, $runnerPath, $workflowPath, $requiredGatesPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required CUDA qualification artifact is missing: '$path'."
}

$source = Get-Content -LiteralPath $qualificationSourcePath -Raw
$tests = Get-Content -LiteralPath $qualificationTestPath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($source -match 'CudaQualificationStatus[\s\S]*Unverified[\s\S]*Passed[\s\S]*Failed') "CUDA qualification must distinguish UNVERIFIED, PASSED and FAILED."
Assert-Condition ($source -match 'GpuBackendKind\.NvidiaCuda') "Qualification must require the NVIDIA CUDA backend."
Assert-Condition ($source -match 'HardwareAccelerated') "Qualification must require hardware acceleration."
Assert-Condition ($source -match 'Hd1080p50Rgba8') "Qualification must cover 1080p50."
Assert-Condition ($source -match 'Hd1080p59_94Rgba8') "Qualification must cover 1080p59.94."
Assert-Condition ([Regex]::Matches($source, 'cases\.Add\(RunCase').Count -eq 8) "Qualification must declare exactly eight V1 format/operation cases."
Assert-Condition ($source -match 'PixelCorrect') "Qualification must verify deterministic output pixels."
Assert-Condition ($source -match 'SurfaceLifetimeCorrect') "Qualification must verify GPU surface lifetime."
Assert-Condition ($source -match 'P95Milliseconds') "Qualification must record P95 latency."
Assert-Condition ($source -match 'maximum <= frameBudget \* 2\.0') "Qualification must retain a maximum-latency guard."
Assert-Condition ($source -match 'p95 <= frameBudget') "Qualification must retain the one-frame P95 guard."

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
Assert-Condition ($documentation -match 'P95 must be within one frame period') "Documentation must state the timing qualification guard."
Assert-Condition ($requiredGates -notmatch 'Invoke-CudaReferenceQualification\.ps1') "Generic Required Gates must not masquerade as physical CUDA qualification."

Write-Host "CUDA qualification policy verification PASS"
Write-Host "Hardware evidence state: UNVERIFIED unless the dedicated self-hosted workflow produces PASSED evidence"
Write-Host "Profile: 1080p50 + 1080p59.94, CUT/DISSOLVE/layer, pixel/lifetime/timing evidence"
