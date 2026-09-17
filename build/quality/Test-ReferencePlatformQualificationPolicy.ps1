# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$profilePath = Join-Path $repositoryRoot "qualification/reference-platform/reference-platform.json"
$runnerPath = Join-Path $repositoryRoot "build/qualification/Invoke-ReferencePlatformQualification.ps1"
$resultVerifierPath = Join-Path $repositoryRoot "build/qualification/Test-ReferencePlatformQualificationResult.ps1"
$bindingVerifierPath = Join-Path $repositoryRoot "build/qualification/Test-QualificationEvidenceBinding.ps1"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$documentationPath = Join-Path $repositoryRoot "docs/qualification/ReferencePlatformQualification.md"

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
	if (-not (Test-Path -LiteralPath $directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

foreach ($required in @(
	$profilePath,
	$runnerPath,
	$resultVerifierPath,
	$bindingVerifierPath,
	$requiredGatesPath,
	$documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $required -PathType Leaf) "Required AP-39 artifact is missing: '$required'."
}

$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
Assert-Condition ([string]$profile.schemaVersion -eq "1.0") "Reference-platform profile schema must remain 1.0."
Assert-Condition ([string]$profile.profile -eq "rtaime-v1-reference-platform") "Reference-platform profile identity changed."
Assert-Condition ([string]$profile.platform.operatingSystemFamily -eq "Windows") "V1 reference operating-system family must remain Windows."
Assert-Condition ([string]$profile.platform.architecture -eq "x64") "V1 reference architecture must remain x64."
Assert-Condition ([string]$profile.platform.dotnetSdk -eq "10.0.401") "V1 reference-platform SDK must match the pinned repository SDK."
Assert-Condition (@($profile.platform.developmentFormats).Count -eq 2) "Reference-platform profile must declare exactly the two V1 development formats."
Assert-Condition (@($profile.platform.developmentFormats) -contains "1080p50") "Reference-platform profile must retain 1080p50."
Assert-Condition (@($profile.platform.developmentFormats) -contains "1080p59.94") "Reference-platform profile must retain 1080p59.94."
foreach ($host in @("rtaime.ControlHost", "rtaime.RuntimeHost", "rtaime.AIHost", "rtaime.Operator")) {
	Assert-Condition (@($profile.platform.requiredHosts) -contains $host) "Reference-platform profile is missing required host '$host'."
}

$scenarios = @($profile.softwareScenarios)
Assert-Condition ($scenarios.Count -eq 10) "Reference-platform profile must declare exactly Q01-Q10."
Assert-Condition (@($scenarios.id | Sort-Object -Unique).Count -eq 10) "Reference-platform scenario IDs must be unique."
foreach ($index in 1..10) {
	$id = "Q{0:D2}" -f $index
	$scenario = @($scenarios | Where-Object { [string]$_.id -eq $id })
	Assert-Condition ($scenario.Count -eq 1) "Reference-platform profile is missing '$id'."
	Assert-Condition ([bool]$scenario[0].mandatory) "V1 reference-platform scenario '$id' must remain mandatory."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$scenario[0].evidenceGroup)) "Scenario '$id' is missing its evidence group."
	Assert-Condition ([string]$scenario[0].testProject -eq "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj") "Scenario '$id' must reuse the V1 Integration test project."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$scenario[0].filter)) "Scenario '$id' is missing its test filter."
}

$q01 = @($scenarios | Where-Object id -eq "Q01")[0]
Assert-Condition ([string]$q01.filter -match 'ExecutableHostLifecycleTests') "Q01 must remain bound to executable host lifecycle evidence."
foreach ($id in @("Q02", "Q03", "Q04", "Q05", "Q06", "Q07", "Q08")) {
	$scenario = @($scenarios | Where-Object id -eq $id)[0]
	Assert-Condition ([string]$scenario.evidenceGroup -eq "v1-end-to-end") "Scenario '$id' must reuse the single V1 end-to-end execution evidence group."
	Assert-Condition ([string]$scenario.filter -match 'V1EndToEndProofTests\.V1_reference_workload_executes_end_to_end_for_both_development_formats') "Scenario '$id' must remain bound to the V1 end-to-end proof."
}
$q09 = @($scenarios | Where-Object id -eq "Q09")[0]
Assert-Condition ([string]$q09.filter -match 'RecordingStorageExhaustionIntegrationTests') "Q09 must remain bound to controlled recording storage-exhaustion evidence."
$q10 = @($scenarios | Where-Object id -eq "Q10")[0]
Assert-Condition ([string]$q10.filter -match 'OperatorProcessRecoveryTests') "Q10 must remain bound to real Operator process restart/resynchronization evidence."

$hardware = @($profile.hardwareRequirements)
Assert-Condition ($hardware.Count -eq 5) "Reference-platform profile must retain exactly five physical requirements."
$expectedHardware = @{
	REFERENCE_GPU = @{ Type = "CUDA_REFERENCE"; Binding = "cuda-reference.binding.json" }
	PROFESSIONAL_MEDIA_IO = @{ Type = "MEDIA_IO_REFERENCE"; Binding = "media-io-reference.binding.json" }
	GENLOCK = @{ Type = "TIMING_REFERENCE_SOAK"; Binding = "timing-reference-soak.binding.json" }
	PHYSICAL_END_TO_END_LATENCY = @{ Type = "TIMING_REFERENCE_SOAK"; Binding = "timing-reference-soak.binding.json" }
	LONG_SOAK = @{ Type = "TIMING_REFERENCE_SOAK"; Binding = "timing-reference-soak.binding.json" }
}
foreach ($name in $expectedHardware.Keys) {
	$item = @($hardware | Where-Object { [string]$_.requirement -eq $name })
	Assert-Condition ($item.Count -eq 1) "Reference-platform profile is missing physical requirement '$name'."
	Assert-Condition ([bool]$item[0].mandatory) "Physical requirement '$name' must remain mandatory for full reference-platform PASS."
	Assert-Condition ([string]$item[0].qualificationType -eq [string]$expectedHardware[$name].Type) "Physical requirement '$name' has the wrong qualification type."
	Assert-Condition ([string]$item[0].bindingFile -eq [string]$expectedHardware[$name].Binding) "Physical requirement '$name' has the wrong evidence binding."
}

$runner = Get-Content -LiteralPath $runnerPath -Raw
Assert-Condition ($runner -match 'Test-QualificationEvidenceBinding\.ps1') "Reference-platform runner must reuse the source-bound physical evidence verifier."
Assert-Condition ($runner -match 'qualification-result\.json') "Reference-platform runner must emit a machine-readable result."
Assert-Condition ($runner -match 'qualification-summary\.md') "Reference-platform runner must emit a human-readable summary."
Assert-Condition ($runner -match 'environment\.json') "Reference-platform runner must emit environment evidence."
Assert-Condition ($runner -match 'UNVERIFIED') "Reference-platform runner must preserve explicit UNVERIFIED semantics."
Assert-Condition ($runner -match 'RequirePass') "Reference-platform runner must provide an explicit full-PASS gate without making it the CI default."

$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
Assert-Condition ($requiredGates -match 'Invoke-ReferencePlatformQualification\.ps1') "Required Gates must execute the CI-safe reference-platform qualification profile."
Assert-Condition ($requiredGates -match 'Test-ReferencePlatformQualificationPolicy\.ps1') "Required Gates quality job must verify AP-39 qualification policy."
Assert-Condition ($requiredGates -match 'artifacts/qualification/reference-platform') "Required Gates must retain AP-39 qualification evidence as a workflow artifact."

$testRoot = Join-Path $repositoryRoot "artifacts/quality/reference-platform-qualification"
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$environmentPath = Join-Path $testRoot "environment.json"
$softwareLogPath = Join-Path $testRoot "software.log"
$hardwareEvidencePath = Join-Path $testRoot "physical.binding.json"
[System.IO.File]::WriteAllText($softwareLogPath, "synthetic software evidence`n", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($hardwareEvidencePath, "{}`n", [System.Text.UTF8Encoding]::new($false))
Write-JsonFile -Value ([ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	profile = "rtaime-v1-reference-platform"
	platformStatus = "PASS"
}) -Path $environmentPath

try {
	$relativeEnvironment = [System.IO.Path]::GetRelativePath($repositoryRoot, $environmentPath).Replace('\', '/')
	$relativeSoftware = [System.IO.Path]::GetRelativePath($repositoryRoot, $softwareLogPath).Replace('\', '/')
	$relativeHardware = [System.IO.Path]::GetRelativePath($repositoryRoot, $hardwareEvidencePath).Replace('\', '/')
	$syntheticScenarios = @(
		foreach ($index in 1..10) {
			[ordered]@{
				id = "Q{0:D2}" -f $index
				name = "Synthetic Q$index"
				mandatory = $true
				evidenceGroup = "synthetic"
				status = "PASS"
				testProject = "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj"
				filter = "Synthetic"
				evidencePath = $relativeSoftware
			}
		}
	)
	$syntheticHardware = @(
		foreach ($item in $hardware) {
			[ordered]@{
				id = [string]$item.id
				requirement = [string]$item.requirement
				mandatory = $true
				qualificationType = [string]$item.qualificationType
				status = "UNVERIFIED"
				evidencePath = ""
			}
		}
	)
	$sourceCommit = "1111111111111111111111111111111111111111"
	$unverifiedResultPath = Join-Path $testRoot "unverified-result.json"
	Write-JsonFile -Value ([ordered]@{
		copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
		schemaVersion = "1.0"
		profile = "rtaime-v1-reference-platform"
		sourceCommit = $sourceCommit
		status = "UNVERIFIED"
		softwareStatus = "PASS"
		hardwareStatus = "UNVERIFIED"
		counts = [ordered]@{ PASS = 10; FAIL = 0; NOT_APPLICABLE = 0; UNVERIFIED = 5 }
		environmentPath = $relativeEnvironment
		scenarios = $syntheticScenarios
		hardwareRequirements = $syntheticHardware
	}) -Path $unverifiedResultPath
	& $resultVerifierPath -ResultPath $unverifiedResultPath -ExpectedSourceCommit $sourceCommit | Out-Null

	$falsePassPath = Join-Path $testRoot "false-pass-result.json"
	$falsePass = Get-Content -LiteralPath $unverifiedResultPath -Raw | ConvertFrom-Json
	$falsePass.status = "PASS"
	Write-JsonFile -Value $falsePass -Path $falsePassPath
	$expectedFailure = $false
	try {
		& $resultVerifierPath -ResultPath $falsePassPath -ExpectedSourceCommit $sourceCommit | Out-Null
	} catch {
		$expectedFailure = $true
	}
	Assert-Condition $expectedFailure "Result verification must reject an overall PASS while mandatory hardware evidence remains UNVERIFIED."

	$allPassHardware = @(
		foreach ($item in $syntheticHardware) {
			[ordered]@{
				id = [string]$item.id
				requirement = [string]$item.requirement
				mandatory = $true
				qualificationType = [string]$item.qualificationType
				status = "PASS"
				evidencePath = $relativeHardware
			}
		}
	)
	$passResultPath = Join-Path $testRoot "pass-result.json"
	Write-JsonFile -Value ([ordered]@{
		copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
		schemaVersion = "1.0"
		profile = "rtaime-v1-reference-platform"
		sourceCommit = $sourceCommit
		status = "PASS"
		softwareStatus = "PASS"
		hardwareStatus = "PASS"
		counts = [ordered]@{ PASS = 15; FAIL = 0; NOT_APPLICABLE = 0; UNVERIFIED = 0 }
		environmentPath = $relativeEnvironment
		scenarios = $syntheticScenarios
		hardwareRequirements = $allPassHardware
	}) -Path $passResultPath
	& $resultVerifierPath -ResultPath $passResultPath -ExpectedSourceCommit $sourceCommit | Out-Null
} finally {
	if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "Reference-platform qualification policy verification PASS"
Write-Host "CI-safe software qualification and physical source-bound qualification remain explicitly separated."
