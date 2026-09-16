# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Invoke-ExpectedFailure {
	param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Action)
	$failed = $false
	try { & $Action } catch { $failed = $true }
	Assert-Condition $failed "Negative update case '$Name' unexpectedly succeeded."
	Write-Host "Negative case PASS: $Name"
}

$bundle = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition (Test-Path -LiteralPath $bundle -PathType Leaf) "Qualification bundle is missing."
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('rtaime-update-foundation-{0}' -f [Guid]::NewGuid().ToString('N'))
$installPath = Join-Path $tempRoot 'rtaime'
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
	& (Join-Path $repositoryRoot 'build/release/Install-OfflineRelease.ps1') -BundlePath $bundle -InstallPath $installPath
	foreach ($toolName in @(
		'update-policy.json',
		'Get-InstalledReleaseState.ps1',
		'Resolve-UpdateDiscovery.ps1',
		'Get-VerifiedUpdateCandidate.ps1',
		'Test-DownloadedRelease.ps1',
		'New-UpdatePlan.ps1',
		'Invoke-AtomicSoftwareReplacement.ps1',
		'Invoke-SoftwareRollback.ps1',
		'Invoke-VerifiedUpdate.ps1'
	)) {
		Assert-Condition (Test-Path -LiteralPath (Join-Path $installPath "tools/$toolName") -PathType Leaf) "Offline package is missing update tool '$toolName'."
	}

	$currentStatePath = Join-Path $tempRoot 'current-state.json'
	$current = & (Join-Path $PSScriptRoot 'Get-InstalledReleaseState.ps1') -InstallPath $installPath -OutputPath $currentStatePath -QualificationMode
	Assert-Condition ([string]$current.integrityVerification -eq 'PASS') "Qualification installation inspection failed."
	$originalManifestHash = (Get-FileHash -LiteralPath (Join-Path $installPath 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()

	Invoke-ExpectedFailure -Name 'post-commit automatic rollback' -Action {
		& (Join-Path $PSScriptRoot 'Invoke-AtomicSoftwareReplacement.ps1') `
			-BundlePath $bundle `
			-InstallPath $installPath `
			-QualificationMode `
			-SimulatePostCommitFailure
	}
	Assert-Condition (Test-Path -LiteralPath $installPath -PathType Container) "Automatic rollback did not restore active installation."
	Assert-Condition (-not (Test-Path -LiteralPath "$installPath.rollback")) "Automatic rollback left a stale rollback slot."
	Assert-Condition ((Get-FileHash -LiteralPath (Join-Path $installPath 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() -eq $originalManifestHash) "Automatic rollback changed installed manifest."

	& (Join-Path $PSScriptRoot 'Invoke-AtomicSoftwareReplacement.ps1') `
		-BundlePath $bundle `
		-InstallPath $installPath `
		-QualificationMode
	Assert-Condition (Test-Path -LiteralPath "$installPath.rollback" -PathType Container) "Successful replacement did not retain rollback slot."

	Invoke-ExpectedFailure -Name 'second update while rollback slot exists' -Action {
		& (Join-Path $PSScriptRoot 'Invoke-AtomicSoftwareReplacement.ps1') -BundlePath $bundle -InstallPath $installPath -QualificationMode
	}

	& (Join-Path $PSScriptRoot 'Invoke-SoftwareRollback.ps1') `
		-InstallPath $installPath `
		-AcknowledgeProcessesStopped `
		-QualificationMode
	Assert-Condition (Test-Path -LiteralPath $installPath -PathType Container) "Manual rollback lost active installation."
	Assert-Condition (Test-Path -LiteralPath "$installPath.rollback" -PathType Container) "Manual rollback did not retain previous active version."

	$syntheticRoot = Join-Path $tempRoot 'synthetic'
	New-Item -ItemType Directory -Path $syntheticRoot -Force | Out-Null
	$currentSyntheticPath = Join-Path $syntheticRoot 'current.json'
	$trustedFingerprint = ('f' * 64)
	$currentSynthetic = [ordered]@{
		productName = 'rtaime'
		productVersion = '1.2.3-preview.1'
		channel = 'PREVIEW'
		sourceCommit = ('a' * 40)
		releaseRecordId = ('b' * 64)
		activeTrustedReleaseKeyFingerprints = @($trustedFingerprint)
		integrityVerification = 'PASS'
	}
	[System.IO.File]::WriteAllText($currentSyntheticPath, ($currentSynthetic | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

	$candidateSynthetic = Join-Path $syntheticRoot 'candidate'
	New-Item -ItemType Directory -Path $candidateSynthetic -Force | Out-Null
	$candidate = [ordered]@{
		channel = 'PREVIEW'
		candidateId = ('c' * 64)
		product = [ordered]@{ name = 'rtaime'; version = '1.2.3-preview.2' }
		source = [ordered]@{ tag = 'v1.2.3-preview.2'; sourceCommit = ('d' * 40) }
		trust = [ordered]@{ signerClass = 'EXTERNAL_CONTROLLED'; productionTrust = 'PASS'; keyFingerprint = $trustedFingerprint }
		publicationReadiness = [ordered]@{ status = 'PASS' }
		bundle = [ordered]@{ fileName = 'rtaime-1.2.3-preview.2-win-x64.zip'; sha256 = ('e' * 64) }
	}
	[System.IO.File]::WriteAllText((Join-Path $candidateSynthetic 'release-candidate.json'), ($candidate | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	$plan = & (Join-Path $PSScriptRoot 'New-UpdatePlan.ps1') -CurrentStatePath $currentSyntheticPath -CandidatePath $candidateSynthetic -OutputPath (Join-Path $syntheticRoot 'plan.json')
	Assert-Condition ([string]$plan.decision -eq 'UPDATE_ALLOWED') "Valid Preview update plan was rejected."

	$candidate.trust.keyFingerprint = ('9' * 64)
	[System.IO.File]::WriteAllText((Join-Path $candidateSynthetic 'release-candidate.json'), ($candidate | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -Name 'target signing key not enrolled by current installation' -Action {
		& (Join-Path $PSScriptRoot 'New-UpdatePlan.ps1') -CurrentStatePath $currentSyntheticPath -CandidatePath $candidateSynthetic -OutputPath (Join-Path $syntheticRoot 'unknown-key.json') | Out-Null
	}
	$candidate.trust.keyFingerprint = $trustedFingerprint

	$candidate.product.version = '1.2.3-preview.1'
	$candidate.source.tag = 'v1.2.3-preview.1'
	[System.IO.File]::WriteAllText((Join-Path $candidateSynthetic 'release-candidate.json'), ($candidate | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -Name 'same-version reinstall' -Action {
		& (Join-Path $PSScriptRoot 'New-UpdatePlan.ps1') -CurrentStatePath $currentSyntheticPath -CandidatePath $candidateSynthetic -OutputPath (Join-Path $syntheticRoot 'reinstall.json') | Out-Null
	}

	$currentSynthetic.productVersion = '1.2.3'
	$currentSynthetic.channel = 'STABLE'
	[System.IO.File]::WriteAllText($currentSyntheticPath, ($currentSynthetic | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	$candidate.product.version = '1.2.4-preview.1'
	$candidate.channel = 'PREVIEW'
	$candidate.source.tag = 'v1.2.4-preview.1'
	[System.IO.File]::WriteAllText((Join-Path $candidateSynthetic 'release-candidate.json'), ($candidate | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	Invoke-ExpectedFailure -Name 'stable to preview transition' -Action {
		& (Join-Path $PSScriptRoot 'New-UpdatePlan.ps1') -CurrentStatePath $currentSyntheticPath -CandidatePath $candidateSynthetic -OutputPath (Join-Path $syntheticRoot 'transition.json') | Out-Null
	}

	Write-Host "Update foundation qualification PASS"
	Write-Host "Packaged update tools: PASS"
	Write-Host "Automatic rollback: PASS"
	Write-Host "Manual rollback slot swap: PASS"
	Write-Host "Current-install trust enrollment gate: PASS"
	Write-Host "Same-version reinstall rejection: PASS"
	Write-Host "Stable-to-Preview rejection: PASS"
} finally {
	if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
