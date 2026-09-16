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
	if (-not $Condition) {
		throw $Message
	}
}

$policyPath = Join-Path $repositoryRoot "build/release/release-channels.json"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$releaseWorkflowPath = Join-Path $repositoryRoot ".github/workflows/release-pipeline.yml"
$bootstrapWorkflowPath = Join-Path $repositoryRoot ".github/workflows/bootstrap-validation.yml"
$buildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"

Assert-Condition (Test-Path -LiteralPath $policyPath -PathType Leaf) "Release channel policy is missing."
Assert-Condition (Test-Path -LiteralPath $requiredGatesPath -PathType Leaf) "Required Gates workflow is missing."
Assert-Condition (Test-Path -LiteralPath $releaseWorkflowPath -PathType Leaf) "Release Pipeline workflow is missing."
Assert-Condition (-not (Test-Path -LiteralPath $bootstrapWorkflowPath)) "Legacy bootstrap-validation.yml must be removed after single-pipeline consolidation."

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Unsupported release channel policy schema version."

$channelNames = @($policy.channels.PSObject.Properties.Name)
$expectedChannels = @("QUALIFICATION", "PREVIEW", "STABLE")
Assert-Condition ($channelNames.Count -eq $expectedChannels.Count) "Release channel policy must define exactly QUALIFICATION, PREVIEW and STABLE."
foreach ($channelName in $expectedChannels) {
	Assert-Condition ($channelNames -contains $channelName) "Release channel policy is missing '$channelName'."
}

$qualification = $policy.channels.QUALIFICATION
Assert-Condition ([string]$qualification.releaseStage -eq "DEV") "QUALIFICATION must map to DEV."
Assert-Condition ($qualification.tagRequired -eq $false) "QUALIFICATION must not require a tag."
Assert-Condition (@($qualification.allowedSignerClasses).Count -eq 1 -and [string]$qualification.allowedSignerClasses[0] -eq "TEST_EPHEMERAL") "QUALIFICATION must use TEST_EPHEMERAL signing only."
Assert-Condition ($qualification.requireTrustedProductionKey -eq $false) "QUALIFICATION must not claim production signing trust."
Assert-Condition ($qualification.publicationEligible -eq $false) "QUALIFICATION must not be publication eligible."

$preview = $policy.channels.PREVIEW
Assert-Condition ([string]$preview.releaseStage -eq "PREVIEW") "PREVIEW channel must map to PREVIEW stage."
Assert-Condition ($preview.tagRequired -eq $true) "PREVIEW channel must require a tag."
Assert-Condition (@($preview.allowedSignerClasses) -contains "TEST_EPHEMERAL") "PREVIEW channel must support TEST_EPHEMERAL qualification."
Assert-Condition (@($preview.allowedSignerClasses) -contains "EXTERNAL_CONTROLLED") "PREVIEW channel must allow externally controlled signing."
Assert-Condition ($preview.requireTrustedProductionKey -eq $false) "PREVIEW candidate creation may qualify with a test key, but publication must apply the stricter AP-22 policy."
Assert-Condition ($preview.publicationEligible -eq $true) "PREVIEW must be publication-eligible when production trust PASS is established."

$stable = $policy.channels.STABLE
Assert-Condition ([string]$stable.releaseStage -eq "STABLE") "STABLE channel must map to STABLE stage."
Assert-Condition ($stable.tagRequired -eq $true) "STABLE channel must require a tag."
Assert-Condition (@($stable.allowedSignerClasses).Count -eq 1 -and [string]$stable.allowedSignerClasses[0] -eq "EXTERNAL_CONTROLLED") "STABLE must allow EXTERNAL_CONTROLLED signing only."
Assert-Condition ($stable.requireTrustedProductionKey -eq $true) "STABLE must require active production signing-key trust."
Assert-Condition ($stable.publicationEligible -eq $true) "STABLE candidate readiness must be publication eligible."

[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersion = $buildProps.SelectSingleNode("//RtaimeProductVersion").InnerText.Trim()
$releaseStage = $buildProps.SelectSingleNode("//RtaimeReleaseStage").InnerText.Trim().ToUpperInvariant()
Assert-Condition ($releaseStage -in @("DEV", "PREVIEW", "STABLE")) "Source release stage '$releaseStage' is not supported by the release-channel pipeline."
if ($releaseStage -eq "DEV") {
	Assert-Condition ($productVersion -match [string]$qualification.versionPattern) "DEV source version '$productVersion' does not match QUALIFICATION policy."
} elseif ($releaseStage -eq "PREVIEW") {
	Assert-Condition ($productVersion -match [string]$preview.versionPattern) "PREVIEW source version '$productVersion' does not match PREVIEW policy."
} elseif ($releaseStage -eq "STABLE") {
	Assert-Condition ($productVersion -match [string]$stable.versionPattern) "STABLE source version '$productVersion' does not match STABLE policy."
}

$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
$releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
foreach ($workflow in @($requiredGates, $releaseWorkflow)) {
	Assert-Condition ($workflow -match 'Invoke-ReleasePipeline\.ps1') "All packaged/release-candidate workflows must call Invoke-ReleasePipeline.ps1."
	foreach ($forbiddenDirectCall in @(
		'New-ReleaseEvidence\.ps1',
		'New-ReleaseAttestation\.ps1',
		'New-OfflineReleaseBundle\.ps1',
		'Install-OfflineRelease\.ps1'
	)) {
		Assert-Condition ($workflow -notmatch $forbiddenDirectCall) "Workflow bypasses the authoritative release orchestrator via '$forbiddenDirectCall'."
	}
}

Assert-Condition ($releaseWorkflow -match '(?m)^\s+tags:\s*$') "Release Pipeline workflow must have a tag trigger."
Assert-Condition ($releaseWorkflow -match "(?m)^\s+-\s+'v\*'\s*$") "Release Pipeline workflow must constrain tag trigger to v*."
Assert-Condition ($releaseWorkflow -match '(?m)^permissions:\s*\r?\n\s+contents:\s+read\s*$') "Release Pipeline workflow must default to read-only contents permission."
Assert-Condition ($releaseWorkflow -match '(?m)^\s+publish-release:\s*$') "Release Pipeline workflow must define a distinct publication job."

& (Join-Path $PSScriptRoot "Test-ReleasePublicationPolicy.ps1")

Write-Host "Release channel policy verification PASS"
Write-Host "Channels: QUALIFICATION, PREVIEW, STABLE"
Write-Host "Current source: $productVersion / $releaseStage"
Write-Host "Single release orchestrator: enforced"
Write-Host "Trusted Preview/Stable publication: enabled through separate publication policy"
Write-Host "Publication authority: isolated to publish-release job"
