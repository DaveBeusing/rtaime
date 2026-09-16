# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$generator = Join-Path $repositoryRoot "build/qualification/New-QualificationEvidenceBinding.ps1"
$verifier = Join-Path $repositoryRoot "build/qualification/Test-QualificationEvidenceBinding.ps1"
$manifestGenerator = Join-Path $repositoryRoot "build/qualification/New-QualificationEvidenceManifest.ps1"
$manifestVerifier = Join-Path $repositoryRoot "build/qualification/Test-QualificationEvidenceManifest.ps1"
$policyPath = Join-Path $repositoryRoot "build/qualification/qualification-evidence-policy.json"
$releaseApply = Join-Path $repositoryRoot "build/release/Apply-QualificationEvidence.ps1"
$releaseGenerator = Join-Path $repositoryRoot "build/release/New-ReleaseEvidence.ps1"
$releaseVerifier = Join-Path $repositoryRoot "build/release/Test-ReleaseEvidence.ps1"
$releasePolicyPath = Join-Path $repositoryRoot "build/release/release-policy.json"
$releaseSchemaPath = Join-Path $repositoryRoot "schemas/release/v1/release-evidence.schema.json"
$qualificationSchemaPath = Join-Path $repositoryRoot "schemas/release/v1/qualification-evidence-manifest.schema.json"
$cudaWorkflowPath = Join-Path $repositoryRoot ".github/workflows/cuda-reference-qualification.yml"
$mediaWorkflowPath = Join-Path $repositoryRoot ".github/workflows/media-io-reference-qualification.yml"
$timingWorkflowPath = Join-Path $repositoryRoot ".github/workflows/timing-reference-qualification.yml"
$documentationPath = Join-Path $repositoryRoot "docs/QualificationEvidenceProvenance.md"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
	$json = $Value | ConvertTo-Json -Depth 32
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

foreach ($required in @(
	$generator,
	$verifier,
	$manifestGenerator,
	$manifestVerifier,
	$policyPath,
	$releaseApply,
	$releaseGenerator,
	$releaseVerifier,
	$releasePolicyPath,
	$releaseSchemaPath,
	$qualificationSchemaPath,
	$cudaWorkflowPath,
	$mediaWorkflowPath,
	$timingWorkflowPath,
	$documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $required -PathType Leaf) "Required qualification provenance artifact is missing: '$required'."
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Qualification evidence policy schema must remain 1.0."
Assert-Condition ([string]$policy.repository -eq "DaveBeusing/rtaime") "Qualification evidence policy repository identity changed."
$bindings = @($policy.bindings)
Assert-Condition ($bindings.Count -eq 3) "Qualification evidence policy must declare exactly the three V1 physical qualification types."
Assert-Condition ((@($bindings | Where-Object qualificationType -eq "CUDA_REFERENCE").releaseRequirements -contains "REFERENCE_GPU")) "CUDA qualification must map to REFERENCE_GPU."
Assert-Condition ((@($bindings | Where-Object qualificationType -eq "MEDIA_IO_REFERENCE").releaseRequirements -contains "PROFESSIONAL_MEDIA_IO")) "Media I/O qualification must map to PROFESSIONAL_MEDIA_IO."
$timingRequirements = @((@($bindings | Where-Object qualificationType -eq "TIMING_REFERENCE_SOAK")).releaseRequirements)
foreach ($required in @("GENLOCK", "PHYSICAL_END_TO_END_LATENCY", "LONG_SOAK")) {
	Assert-Condition ($timingRequirements -contains $required) "Timing qualification must map to '$required'."
}

$releasePolicy = Get-Content -LiteralPath $releasePolicyPath -Raw | ConvertFrom-Json
$hardwareRequirements = @($releasePolicy.hardwareQualification)
Assert-Condition ($hardwareRequirements.Count -eq 5) "Release policy must retain all five physical qualification requirements."
foreach ($hardware in $hardwareRequirements) {
	Assert-Condition ([string]$hardware.status -eq "UNVERIFIED") "Static release policy must never pre-mark '$($hardware.requirement)' as PASS."
}

foreach ($workflowPath in @($cudaWorkflowPath, $mediaWorkflowPath, $timingWorkflowPath)) {
	$workflow = Get-Content -LiteralPath $workflowPath -Raw
	Assert-Condition ($workflow -match 'New-QualificationEvidenceBinding\.ps1') "Physical qualification workflow '$workflowPath' must create source-bound evidence."
	Assert-Condition ($workflow -match 'Test-QualificationEvidenceBinding\.ps1') "Physical qualification workflow '$workflowPath' must verify source-bound evidence before upload."
	Assert-Condition ($workflow -match 'github\.run_id') "Physical qualification workflow '$workflowPath' must retain workflow run provenance."
	Assert-Condition ($workflow -match 'github\.run_attempt') "Physical qualification workflow '$workflowPath' must retain workflow attempt provenance."
}

$releaseGeneratorSource = Get-Content -LiteralPath $releaseGenerator -Raw
$releaseApplySource = Get-Content -LiteralPath $releaseApply -Raw
$releaseVerifierSource = Get-Content -LiteralPath $releaseVerifier -Raw
Assert-Condition ($releaseGeneratorSource -match 'Apply-QualificationEvidence\.ps1') "Release evidence generation must apply qualification evidence before verification."
Assert-Condition ($releaseApplySource -match 'Copy-Item[\s\S]*bindingTarget') "Release binding must copy exact qualification binding bytes into the release evidence bundle."
Assert-Condition ($releaseApplySource -match 'Copy-Item[\s\S]*payloadTarget') "Release binding must copy exact qualification payload bytes into the release evidence bundle."
Assert-Condition ($releaseApplySource -match 'Get-Sha256[\s\S]*sourceBinding') "Release binding must verify source binding hashes."
Assert-Condition ($releaseApplySource -match 'Get-Sha256[\s\S]*sourcePayload') "Release binding must verify source payload hashes."
Assert-Condition ($releaseVerifierSource -match 'qualificationEvidenceManifest') "Release verification must require the qualification evidence manifest."
Assert-Condition ($releaseVerifierSource -match 'Qualification binding source commit mismatch') "Release verification must fail closed on a qualification source-commit mismatch."

$testRoot = Join-Path $repositoryRoot "artifacts/quality/qualification-evidence-binding"
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
$bindingRoot = Join-Path $testRoot "bindings"
$payloadRoot = Join-Path $testRoot "payloads"
New-Item -ItemType Directory -Path $bindingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
$sourceCommit = "1111111111111111111111111111111111111111"

try {
	$cudaPayloadPath = Join-Path $payloadRoot "cuda.json"
	$cases = @(
		foreach ($index in 1..8) {
			[ordered]@{
				format = if ($index -le 4) { "1080p50" } else { "1080p59.94" }
				operation = "CASE_$index"
				samples = 10
				frameBudgetMilliseconds = 20.0
				p50Milliseconds = 1.0
				p95Milliseconds = 2.0
				maximumMilliseconds = 3.0
				pixelCorrect = $true
				surfaceLifetimeCorrect = $true
				timingBudgetMet = $true
			}
		}
	)
	Write-JsonFile -Value ([ordered]@{
		schemaVersion = "1.0"
		status = "PASSED"
		expectedDeviceName = "Synthetic Reference GPU"
		deviceOrdinal = 0
		detectedDeviceName = "Synthetic Reference GPU"
		totalMemoryBytes = 1
		cases = $cases
		failures = @()
	}) -Path $cudaPayloadPath
	& $generator -QualificationType "CUDA_REFERENCE" -EvidencePath $cudaPayloadPath -SourceCommit $sourceCommit -RunId "synthetic-cuda" -RunAttempt 1 -OutputPath (Join-Path $bindingRoot "cuda-reference.binding.json")

	$mediaPayloadPath = Join-Path $payloadRoot "media.json"
	Write-JsonFile -Value ([ordered]@{
		schemaVersion = "1.0"
		status = "PASSED"
		ajaSdkRevision = "2222222222222222222222222222222222222222"
		transferMode = "PinnedHostLease"
		statistics = [ordered]@{ CaptureFailures = 0; OutputRejected = 0 }
	}) -Path $mediaPayloadPath
	& $generator -QualificationType "MEDIA_IO_REFERENCE" -EvidencePath $mediaPayloadPath -SourceCommit $sourceCommit -RunId "synthetic-media" -RunAttempt 1 -OutputPath (Join-Path $bindingRoot "media-io-reference.binding.json")

	$timingPayloadPath = Join-Path $payloadRoot "timing.json"
	Write-JsonFile -Value ([ordered]@{
		schemaVersion = "1.0"
		status = "PASSED"
		soakSeconds = 1800
		reference = [ordered]@{ referenceLossObserved = $true; referenceRelockObserved = $true; finalOutput = "Locked" }
		physicalEndToEndLatency = [ordered]@{ status = "PASSED"; sampleCount = 30 }
	}) -Path $timingPayloadPath
	& $generator -QualificationType "TIMING_REFERENCE_SOAK" -EvidencePath $timingPayloadPath -SourceCommit $sourceCommit -RunId "synthetic-timing" -RunAttempt 1 -OutputPath (Join-Path $bindingRoot "timing-reference-soak.binding.json")

	$manifestPath = Join-Path $testRoot "qualification-evidence-manifest.json"
	& $manifestGenerator -SourceCommit $sourceCommit -BindingRoot $bindingRoot -OutputPath $manifestPath
	& $manifestVerifier -ManifestPath $manifestPath -ExpectedSourceCommit $sourceCommit
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	Assert-Condition (@($manifest.requirements | Where-Object status -eq "PASSED").Count -eq 5) "Synthetic qualification manifest must satisfy all five mapped release requirements."

	$expectedFailure = $false
	try {
		& $manifestVerifier -ManifestPath $manifestPath -ExpectedSourceCommit "3333333333333333333333333333333333333333"
	} catch {
		$expectedFailure = $true
	}
	Assert-Condition $expectedFailure "Qualification manifest verification must fail closed for a different release source commit."

	Add-Content -LiteralPath $cudaPayloadPath -Value " "
	$expectedFailure = $false
	try {
		& $verifier -BindingPath (Join-Path $bindingRoot "cuda-reference.binding.json") -ExpectedSourceCommit $sourceCommit -ExpectedQualificationType "CUDA_REFERENCE"
	} catch {
		$expectedFailure = $true
	}
	Assert-Condition $expectedFailure "Qualification binding verification must fail closed after payload tampering."
} finally {
	if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "Qualification evidence provenance policy verification PASS"
Write-Host "Static hardware policy: UNVERIFIED; only exact source-bound PASSED evidence may promote release compatibility evidence"
