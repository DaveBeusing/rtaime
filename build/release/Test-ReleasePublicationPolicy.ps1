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

$policyPath = Join-Path $repositoryRoot "build/release/release-publication-policy.json"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/release-pipeline.yml"
Assert-Condition (Test-Path -LiteralPath $policyPath -PathType Leaf) "Release publication policy is missing."
Assert-Condition (Test-Path -LiteralPath $workflowPath -PathType Leaf) "Release Pipeline workflow is missing."

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Unsupported release publication policy schema version."
Assert-Condition ($policy.channels.QUALIFICATION.publish -eq $false) "QUALIFICATION must never be publishable."

foreach ($channelName in @("PREVIEW", "STABLE")) {
	$channel = $policy.channels.PSObject.Properties[$channelName].Value
	Assert-Condition ($channel.publish -eq $true) "$channelName must be explicitly publishable."
	Assert-Condition ([string]$channel.requiredSignerClass -eq "EXTERNAL_CONTROLLED") "$channelName publication must require EXTERNAL_CONTROLLED signing."
	Assert-Condition ($channel.requireProductionTrust -eq $true) "$channelName publication must require production trust PASS."
	Assert-Condition ($channel.requireCandidatePublicationReadinessPass -eq $true) "$channelName publication must require candidate publication readiness PASS."
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$channel.channelDescriptorFileName)) "$channelName must define channel discovery metadata."
}
Assert-Condition ($policy.channels.PREVIEW.githubPrerelease -eq $true) "PREVIEW publication must be a GitHub prerelease."
Assert-Condition ($policy.channels.PREVIEW.makeLatest -eq $false) "PREVIEW publication must not become latest Stable."
Assert-Condition ($policy.channels.STABLE.githubPrerelease -eq $false) "STABLE publication must not be a prerelease."
Assert-Condition ($policy.channels.STABLE.makeLatest -eq $true) "STABLE publication must become latest."
Assert-Condition ([string]$policy.immutability.existingReleaseAction -eq "FAIL") "Existing GitHub releases must fail closed rather than being mutated."
Assert-Condition ($policy.immutability.assetOverwriteAllowed -eq $false) "Release asset overwrite must be disabled."
Assert-Condition ($policy.immutability.rebuildAllowed -eq $false) "Publication must never rebuild a candidate."
Assert-Condition ([string]$policy.discovery.authoritativeTrustSource -eq "RELEASE_CANDIDATE") "Release Candidate must remain the authoritative trust source."
Assert-Condition ($policy.discovery.channelDescriptorIsTrustAnchor -eq $false) "Channel descriptor must not become a trust anchor."

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-Condition ($workflow -match '(?m)^permissions:\s*\r?\n\s+contents:\s+read\s*$') "Release Pipeline workflow must default to contents: read."
$publicationMarker = "  publish-release:"
$publicationIndex = $workflow.IndexOf($publicationMarker, [StringComparison]::Ordinal)
Assert-Condition ($publicationIndex -ge 0) "Publication job definition was not found."
$publishBody = $workflow.Substring($publicationIndex)
Assert-Condition ($publishBody -match '(?m)^\s{4}permissions:\s*\r?\n\s{6}contents:\s+write\s*$') "Publication job must request contents: write."
$writePermissionMatches = [Regex]::Matches($workflow, '(?m)^\s+contents:\s+write\s*$')
Assert-Condition ($writePermissionMatches.Count -eq 1) "Release Pipeline workflow must contain exactly one contents: write grant."

Assert-Condition ($publishBody -match 'New-ReleasePublication\.ps1') "Publication job must run New-ReleasePublication.ps1."
Assert-Condition ($publishBody -match 'Test-ReleasePublication\.ps1') "Publication job must verify publication metadata before GitHub mutation."
Assert-Condition ($publishBody -match 'gh\s+release\s+view') "Publication job must refuse an existing GitHub Release."

$directReleaseCreate = $publishBody -match '(?m)&\s+gh\s+release\s+create\b'
$argumentArrayReleaseCreate =
	$publishBody -match '\$ghArgs\s*=\s*@\(\s*"release"\s*,\s*"create"\s*,\s*\$tag' -and
	$publishBody -match '\$ghArgs\s*\+=\s*\$assets' -and
	$publishBody -match '(?m)&\s+gh\s+@ghArgs\b'
Assert-Condition ($directReleaseCreate -or $argumentArrayReleaseCreate) "Publication job must create the GitHub Release from verified assets."
Assert-Condition ($publishBody -match 'Join-Path\s+\$candidatePath\s+"release-candidate\.json"') "Publication assets must include the verified Release Candidate manifest."
Assert-Condition ($publishBody -match 'Join-Path\s+\$candidatePath\s+\(\[string\]\$publication\.bundle\.fileName\)') "Publication assets must use the bundle declared by verified publication metadata."
Assert-Condition ($publishBody -match 'Join-Path\s+\$candidatePath\s+\(\[string\]\$publication\.bundle\.sidecarFileName\)') "Publication assets must use the bundle sidecar declared by verified publication metadata."
Assert-Condition ($publishBody -match 'Join-Path\s+\$publicationPath\s+"release-publication\.json"') "Publication assets must include verified publication metadata."
Assert-Condition ($publishBody -match '--verify-tag') "GitHub Release creation must verify the existing Git tag."
Assert-Condition ($publishBody -match 'actions/download-artifact@v4') "Publication job must consume the previously built Release Candidate artifact."
foreach ($forbidden in @('dotnet\s+build', 'dotnet\s+test', 'Invoke-ReleasePipeline\.ps1', 'New-ReleaseEvidence\.ps1', 'New-ReleaseAttestation\.ps1', 'New-OfflineReleaseBundle\.ps1')) {
	Assert-Condition ($publishBody -notmatch $forbidden) "Publication job must not rebuild or regenerate trust artifacts via '$forbidden'."
}

Write-Host "Release publication policy verification PASS"
Write-Host "QUALIFICATION publication: blocked"
Write-Host "PREVIEW publication: candidate readiness PASS + EXTERNAL_CONTROLLED + production trust PASS"
Write-Host "STABLE publication: candidate readiness PASS + EXTERNAL_CONTROLLED + production trust PASS"
Write-Host "Publication rebuild: blocked"
Write-Host "Publication write authority: isolated to one job"
Write-Host "Channel descriptors are discovery pointers, not trust anchors"
