# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ExpectedDeviceName,
	[int]$DeviceOrdinal = 0,
	[int]$SampleIterations = 30,
	[int]$WarmupIterations = 4,
	[string]$EvidencePath = "artifacts/qualification/cuda-reference.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "CUDA reference qualification requires the Windows V1 reference platform." }
if (-not [Environment]::Is64BitProcess) { throw "CUDA reference qualification requires an x64 process." }
if ([string]::IsNullOrWhiteSpace($ExpectedDeviceName)) { throw "ExpectedDeviceName is required." }
if ($DeviceOrdinal -lt 0) { throw "DeviceOrdinal must be non-negative." }
if ($SampleIterations -lt 10) { throw "SampleIterations must be at least 10." }
if ($WarmupIterations -lt 0) { throw "WarmupIterations must be non-negative." }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$resolvedEvidence = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $EvidencePath))
$evidenceDirectory = Split-Path -Parent $resolvedEvidence
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
if (Test-Path -LiteralPath $resolvedEvidence) { Remove-Item -LiteralPath $resolvedEvidence -Force }

$previous = @{
	Enabled = $env:RTAIME_CUDA_REFERENCE_QUALIFICATION
	Device = $env:RTAIME_CUDA_REFERENCE_DEVICE
	Ordinal = $env:RTAIME_CUDA_REFERENCE_DEVICE_ORDINAL
	Samples = $env:RTAIME_CUDA_REFERENCE_SAMPLES
	Warmup = $env:RTAIME_CUDA_REFERENCE_WARMUP
	Evidence = $env:RTAIME_CUDA_REFERENCE_EVIDENCE
}

try {
	$env:RTAIME_CUDA_REFERENCE_QUALIFICATION = "1"
	$env:RTAIME_CUDA_REFERENCE_DEVICE = $ExpectedDeviceName.Trim()
	$env:RTAIME_CUDA_REFERENCE_DEVICE_ORDINAL = $DeviceOrdinal.ToString([Globalization.CultureInfo]::InvariantCulture)
	$env:RTAIME_CUDA_REFERENCE_SAMPLES = $SampleIterations.ToString([Globalization.CultureInfo]::InvariantCulture)
	$env:RTAIME_CUDA_REFERENCE_WARMUP = $WarmupIterations.ToString([Globalization.CultureInfo]::InvariantCulture)
	$env:RTAIME_CUDA_REFERENCE_EVIDENCE = $resolvedEvidence

	Push-Location $repositoryRoot
	try {
		dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj `
			--configuration Release `
			--filter "FullyQualifiedName~CudaReferenceHardwareQualificationTests.Reference_hardware_profile_must_pass_when_explicitly_enabled"
		if ($LASTEXITCODE -ne 0) { throw "CUDA reference hardware qualification test failed." }
	}
	finally {
		Pop-Location
	}

	if (-not (Test-Path -LiteralPath $resolvedEvidence -PathType Leaf)) {
		throw "CUDA qualification completed without producing evidence at '$resolvedEvidence'."
	}

	$evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
	if ($evidence.schemaVersion -ne "1.0") { throw "Unsupported CUDA qualification evidence schema '$($evidence.schemaVersion)'." }
	if ($evidence.status -ne "PASSED") { throw "CUDA qualification evidence status is '$($evidence.status)', expected 'PASSED'." }
	if ($evidence.expectedDeviceName -ne $ExpectedDeviceName.Trim()) { throw "CUDA qualification evidence device expectation changed during execution." }
	if (@($evidence.cases).Count -ne 8) { throw "CUDA qualification evidence must contain exactly eight V1 cases." }
	if (@($evidence.cases | Where-Object { -not $_.pixelCorrect -or -not $_.surfaceLifetimeCorrect -or -not $_.timingBudgetMet }).Count -ne 0) {
		throw "CUDA qualification evidence contains a failed functional, lifetime, or timing case."
	}

	Write-Host "CUDA reference hardware qualification PASS"
	Write-Host "Device: $($evidence.detectedDeviceName)"
	Write-Host "Evidence: $resolvedEvidence"
}
finally {
	$env:RTAIME_CUDA_REFERENCE_QUALIFICATION = $previous.Enabled
	$env:RTAIME_CUDA_REFERENCE_DEVICE = $previous.Device
	$env:RTAIME_CUDA_REFERENCE_DEVICE_ORDINAL = $previous.Ordinal
	$env:RTAIME_CUDA_REFERENCE_SAMPLES = $previous.Samples
	$env:RTAIME_CUDA_REFERENCE_WARMUP = $previous.Warmup
	$env:RTAIME_CUDA_REFERENCE_EVIDENCE = $previous.Evidence
}
