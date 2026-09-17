# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$verifier = Join-Path $PSScriptRoot "Test-ProductSecurityAssessment.ps1"
$binder = Join-Path $PSScriptRoot "Bind-ProductSecurityAssessment.ps1"
$bindingVerifier = Join-Path $PSScriptRoot "Test-ProductSecurityAssessmentBinding.ps1"

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

function New-ReleaseEvidenceFixture {
	param([Parameter(Mandatory)][string]$Path)
	$releaseEvidence = [ordered]@{
		copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
		schemaVersion = "1.0"
		productName = "rtaime"
		productVersion = "0.1.0-dev"
		releaseStage = "DEV"
		sourceCommit = "1111111111111111111111111111111111111111"
		buildCommit = "2222222222222222222222222222222222222222"
		buildId = "security-binding-test"
		evidenceDomains = @(
			[ordered]@{
				domain = "SECURITY"
				status = "UNVERIFIED"
				severity = "CRITICAL"
				source = "release-policy"
				details = "Security assessment is not bound."
			}
		)
	}
	Write-JsonFile -Value $releaseEvidence -Path (Join-Path $Path "release-evidence.json")
}

function New-AssessmentFixture {
	param(
		[Parameter(Mandatory)][string]$Path,
		[string]$SourceCommit = "1111111111111111111111111111111111111111",
		[string]$Status = "PASS",
		[string]$SourceReview = "PASS",
		[string]$DependencyReview = "PASS",
		[string]$VulnerabilityTriage = "PASS",
		[string]$SecurityUpdatePath = "PASS",
		[object[]]$Findings = @()
	)
	$assessment = [ordered]@{
		copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
		schemaVersion = "1.0"
		assessmentId = "SECURITY-ASSESSMENT-TEST"
		product = [ordered]@{
			name = "rtaime"
			version = "0.1.0-dev"
		}
		source = [ordered]@{
			sourceCommit = $SourceCommit
			buildCommit = "2222222222222222222222222222222222222222"
			buildId = "security-binding-test"
		}
		status = $Status
		checks = [ordered]@{
			sourceReview = $SourceReview
			dependencyReview = $DependencyReview
			vulnerabilityTriage = $VulnerabilityTriage
			securityUpdatePath = $SecurityUpdatePath
		}
		findings = @($Findings)
		reviewer = [ordered]@{
			identity = "security-review-test"
			role = "PRODUCT_SECURITY_REVIEWER"
		}
		assessedAtUtc = "2026-09-17T00:00:00Z"
	}
	Write-JsonFile -Value $assessment -Path $Path
}

function Invoke-NativePowerShell {
	param([Parameter(Mandatory)][string[]]$Arguments)
	$nativePreferenceVariable = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
	$previousNativePreference = $null
	if ($null -ne $nativePreferenceVariable) {
		$previousNativePreference = [bool]$nativePreferenceVariable.Value
		Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
	}
	try {
		& pwsh @Arguments *> $null
		$exitCode = $LASTEXITCODE
		$global:LASTEXITCODE = 0
		return $exitCode
	} finally {
		if ($null -ne $nativePreferenceVariable) {
			Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $previousNativePreference
		}
	}
}

function Invoke-ExpectedFailure {
	param(
		[Parameter(Mandatory)][string]$CaseName,
		[Parameter(Mandatory)][string[]]$Arguments
	)
	$exitCode = Invoke-NativePowerShell -Arguments $Arguments
	if ($exitCode -eq 0) {
		throw "Negative product-security assessment case '$CaseName' unexpectedly succeeded."
	}
	Write-Host "Negative case PASS: $CaseName"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-security-assessment-{0}" -f [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
	$validRoot = Join-Path $tempRoot "valid"
	New-Item -ItemType Directory -Path $validRoot -Force | Out-Null
	New-ReleaseEvidenceFixture -Path $validRoot
	$validAssessment = Join-Path $tempRoot "valid-assessment.json"
	New-AssessmentFixture -Path $validAssessment
	& $binder -AssessmentPath $validAssessment -OutputPath $validRoot
	& $bindingVerifier -OutputPath $validRoot
	Write-Host "Positive case PASS: exact-source product-security assessment binding"

	$boundAssessment = Join-Path $validRoot "security-assessment.json"
	$bound = Get-Content -LiteralPath $boundAssessment -Raw | ConvertFrom-Json
	$bound.reviewer.role = "TAMPERED"
	Write-JsonFile -Value $bound -Path $boundAssessment
	Invoke-ExpectedFailure -CaseName "tampered bound assessment" -Arguments @("-NoProfile", "-File", $bindingVerifier, "-OutputPath", $validRoot)

	$mismatchRoot = Join-Path $tempRoot "mismatch"
	New-Item -ItemType Directory -Path $mismatchRoot -Force | Out-Null
	New-ReleaseEvidenceFixture -Path $mismatchRoot
	$mismatchAssessment = Join-Path $tempRoot "mismatch-assessment.json"
	New-AssessmentFixture -Path $mismatchAssessment -SourceCommit "3333333333333333333333333333333333333333"
	Invoke-ExpectedFailure -CaseName "source commit mismatch" -Arguments @("-NoProfile", "-File", $binder, "-AssessmentPath", $mismatchAssessment, "-OutputPath", $mismatchRoot)

	$unverifiedAssessment = Join-Path $tempRoot "unverified-assessment.json"
	New-AssessmentFixture -Path $unverifiedAssessment -Status "UNVERIFIED" -SourceReview "UNVERIFIED"
	Invoke-ExpectedFailure -CaseName "unverified assessment cannot promote release SECURITY" -Arguments @(
		"-NoProfile", "-File", $verifier,
		"-AssessmentPath", $unverifiedAssessment,
		"-ExpectedProductVersion", "0.1.0-dev",
		"-ExpectedSourceCommit", "1111111111111111111111111111111111111111",
		"-ExpectedBuildCommit", "2222222222222222222222222222222222222222",
		"-ExpectedBuildId", "security-binding-test",
		"-RequirePass"
	)

	$openHighAssessment = Join-Path $tempRoot "open-high-assessment.json"
	$openHighFinding = [ordered]@{
		id = "SEC-HIGH-1"
		severity = "HIGH"
		status = "OPEN"
		summary = "Open high-severity finding"
		evidenceReference = $null
	}
	New-AssessmentFixture -Path $openHighAssessment -Findings @($openHighFinding)
	Invoke-ExpectedFailure -CaseName "PASS assessment with unresolved HIGH finding" -Arguments @("-NoProfile", "-File", $verifier, "-AssessmentPath", $openHighAssessment, "-RequirePass")

	$failedCheckAssessment = Join-Path $tempRoot "failed-check-assessment.json"
	New-AssessmentFixture -Path $failedCheckAssessment -DependencyReview "FAIL"
	Invoke-ExpectedFailure -CaseName "PASS assessment with failed dependency review" -Arguments @("-NoProfile", "-File", $verifier, "-AssessmentPath", $failedCheckAssessment, "-RequirePass")

	Write-Host "Product security assessment failure qualification PASS"
} finally {
	if (Test-Path -LiteralPath $tempRoot) {
		Remove-Item -LiteralPath $tempRoot -Recurse -Force
	}
}
