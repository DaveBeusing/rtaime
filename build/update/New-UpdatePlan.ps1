# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$CurrentStatePath,
	[Parameter(Mandatory)]
	[string]$CandidatePath,
	[string]$OutputPath = "artifacts/update/update-plan.json",
	[string]$PinnedVersion = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Parse-RtaimeVersion {
	param([Parameter(Mandatory)][string]$Version)
	$match = [Regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-preview\.(?<preview>\d+))?$')
	Assert-Condition $match.Success "Unsupported rtaime update version '$Version'."
	return [ordered]@{
		major = [int]$match.Groups['major'].Value
		minor = [int]$match.Groups['minor'].Value
		patch = [int]$match.Groups['patch'].Value
		isPreview = $match.Groups['preview'].Success
		preview = if ($match.Groups['preview'].Success) { [int]$match.Groups['preview'].Value } else { -1 }
	}
}

function Compare-RtaimeVersion {
	param([string]$Left, [string]$Right)
	$l = Parse-RtaimeVersion $Left
	$r = Parse-RtaimeVersion $Right
	foreach ($property in @('major', 'minor', 'patch')) {
		if ($l[$property] -lt $r[$property]) { return -1 }
		if ($l[$property] -gt $r[$property]) { return 1 }
	}
	if ($l.isPreview -and -not $r.isPreview) { return -1 }
	if (-not $l.isPreview -and $r.isPreview) { return 1 }
	if ($l.isPreview) {
		if ($l.preview -lt $r.preview) { return -1 }
		if ($l.preview -gt $r.preview) { return 1 }
	}
	return 0
}

$currentStateFullPath = [System.IO.Path]::GetFullPath($CurrentStatePath)
$candidateRoot = [System.IO.Path]::GetFullPath($CandidatePath)
Assert-Condition (Test-Path -LiteralPath $currentStateFullPath -PathType Leaf) "Current installation state was not found."
Assert-Condition (Test-Path -LiteralPath (Join-Path $candidateRoot 'release-candidate.json') -PathType Leaf) "Release Candidate manifest was not found."

$current = Get-Content -LiteralPath $currentStateFullPath -Raw | ConvertFrom-Json
$candidate = Get-Content -LiteralPath (Join-Path $candidateRoot 'release-candidate.json') -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'update-policy.json') -Raw | ConvertFrom-Json

Assert-Condition ([string]$current.productName -eq 'rtaime') "Current installation product identity is invalid."
Assert-Condition ([string]$current.integrityVerification -eq 'PASS') "Current installation integrity is not PASS."
Assert-Condition ([string]$current.channel -in @($policy.supportedCurrentChannels)) "Current installation channel '$($current.channel)' is not eligible for managed updates."
Assert-Condition ([string]$candidate.channel -in @('PREVIEW', 'STABLE')) "Update target must be PREVIEW or STABLE."
Assert-Condition ([string]$candidate.trust.signerClass -eq [string]$policy.trust.requiredSignerClass) "Update target must use EXTERNAL_CONTROLLED signing."
Assert-Condition ([string]$candidate.trust.productionTrust -eq 'PASS') "Update target requires production trust PASS."
Assert-Condition ([string]$candidate.publicationReadiness.status -eq 'PASS') "Update target requires publication readiness PASS."
Assert-Condition ([string]$candidate.product.name -eq 'rtaime') "Update target product identity is invalid."

$targetFingerprint = ([string]$candidate.trust.keyFingerprint).ToLowerInvariant()
Assert-Condition ($targetFingerprint -match '^[0-9a-f]{64}$') "Update target signing-key fingerprint is invalid."
$currentTrustedFingerprints = @($current.activeTrustedReleaseKeyFingerprints | ForEach-Object { ([string]$_).ToLowerInvariant() })
Assert-Condition ($currentTrustedFingerprints -contains $targetFingerprint) "Update target signing key '$targetFingerprint' is not enrolled as active SOFTWARE_RELEASE trust in the currently installed release."

$transitionProperty = $policy.targetChannelTransitions.PSObject.Properties[[string]$current.channel]
Assert-Condition ($null -ne $transitionProperty) "No transition policy exists for current channel '$($current.channel)'."
$allowedTargets = @($transitionProperty.Value)
Assert-Condition ($allowedTargets -contains [string]$candidate.channel) "Transition '$($current.channel)' → '$($candidate.channel)' is not allowed."

if (-not [string]::IsNullOrWhiteSpace($PinnedVersion)) {
	Assert-Condition ([string]$candidate.product.version -eq $PinnedVersion) "Discovered target version '$($candidate.product.version)' does not match pinned version '$PinnedVersion'."
}

$comparison = Compare-RtaimeVersion -Left ([string]$current.productVersion) -Right ([string]$candidate.product.version)
Assert-Condition ($comparison -lt 0) "Target version '$($candidate.product.version)' must be newer than installed version '$($current.productVersion)'."
Assert-Condition ([string]$candidate.source.sourceCommit -ne [string]$current.sourceCommit) "Target source commit equals the installed source commit."

$plan = [ordered]@{
	copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
	schemaVersion = '1.0'
	decision = 'UPDATE_ALLOWED'
	current = [ordered]@{
		version = [string]$current.productVersion
		channel = [string]$current.channel
		sourceCommit = [string]$current.sourceCommit
		releaseRecordId = [string]$current.releaseRecordId
	}
	target = [ordered]@{
		version = [string]$candidate.product.version
		channel = [string]$candidate.channel
		tag = [string]$candidate.source.tag
		sourceCommit = [string]$candidate.source.sourceCommit
		candidateId = [string]$candidate.candidateId
		keyFingerprint = $targetFingerprint
		bundleFileName = [string]$candidate.bundle.fileName
		bundleSha256 = [string]$candidate.bundle.sha256
	}
	replacement = [ordered]@{
		mode = 'SIDE_BY_SIDE_STAGE_THEN_RENAME'
		retainRollbackAfterSuccess = [bool]$policy.replacement.retainRollbackAfterSuccess
		persistentStateMigration = [string]$policy.replacement.persistentStateMigration
		requireExplicitQuiescenceAcknowledgement = [bool]$policy.replacement.requireExplicitQuiescenceAcknowledgement
	}
	createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
[System.IO.File]::WriteAllText($outputFullPath, ($plan | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "Update plan PASS"
Write-Host "Current: $($current.productVersion) / $($current.channel)"
Write-Host "Target: $($candidate.product.version) / $($candidate.channel)"
Write-Host "Target key enrolled by current installation: true"
Write-Host "Persistent state migration: NOT_IMPLEMENTED"

return $plan
