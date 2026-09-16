# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[ValidateSet('PREVIEW', 'STABLE')]
	[string]$Channel,
	[string]$Repository = 'DaveBeusing/rtaime',
	[string]$PinnedVersion = '',
	[string]$WorkPath = '',
	[switch]$AcknowledgeProcessesStopped
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

Assert-Condition $AcknowledgeProcessesStopped "Managed update requires explicit acknowledgement that rtaime processes/services using the software installation have been stopped."
$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
$policyPathCandidates = @((Join-Path $PSScriptRoot 'update-policy.json'), (Join-Path $PSScriptRoot '../update/update-policy.json'))
$policyPath = $policyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Update policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ($Repository -eq [string]$policy.repository) "Managed update repository '$Repository' is not allowed by policy."

if ([string]::IsNullOrWhiteSpace($WorkPath)) {
	$WorkPath = "$installRoot$([string]$policy.replacement.workStateSuffix)"
}
$workRoot = [System.IO.Path]::GetFullPath($WorkPath)
if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

$currentStatePath = Join-Path $workRoot 'installed-release.json'
$current = & (Join-Path $PSScriptRoot 'Get-InstalledReleaseState.ps1') -InstallPath $installRoot -OutputPath $currentStatePath
Assert-Condition ([string]$current.channel -in @($policy.supportedCurrentChannels)) "Installed channel '$($current.channel)' cannot use managed production updates."

$discoveryPath = Join-Path $workRoot 'discovery.json'
& (Join-Path $PSScriptRoot 'Resolve-UpdateDiscovery.ps1') `
	-Channel $Channel `
	-Repository $Repository `
	-CurrentVersion ([string]$current.productVersion) `
	-PinnedVersion $PinnedVersion `
	-OutputPath $discoveryPath | Out-Null

$downloadRoot = Join-Path $workRoot 'downloaded-release'
$download = & (Join-Path $PSScriptRoot 'Get-VerifiedUpdateCandidate.ps1') -DiscoveryPath $discoveryPath -OutputPath $downloadRoot

$planPath = Join-Path $workRoot 'update-plan.json'
$plan = & (Join-Path $PSScriptRoot 'New-UpdatePlan.ps1') `
	-CurrentStatePath $currentStatePath `
	-CandidatePath ([string]$download.candidatePath) `
	-OutputPath $planPath `
	-PinnedVersion $PinnedVersion

& (Join-Path $PSScriptRoot 'Invoke-AtomicSoftwareReplacement.ps1') `
	-BundlePath ([string]$download.bundlePath) `
	-InstallPath $installRoot

$newStatePath = Join-Path $workRoot 'installed-release-after.json'
$newState = & (Join-Path $PSScriptRoot 'Get-InstalledReleaseState.ps1') -InstallPath $installRoot -OutputPath $newStatePath
Assert-Condition ([string]$newState.productVersion -eq [string]$plan.target.version) "Activated installation version differs from update plan target."
Assert-Condition ([string]$newState.sourceCommit -eq [string]$plan.target.sourceCommit) "Activated installation source commit differs from update plan target."

$receipt = [ordered]@{
	copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
	schemaVersion = '1.0'
	status = 'PASS'
	installPath = $installRoot
	rollbackPath = "$installRoot$([string]$policy.replacement.rollbackSlotSuffix)"
	currentBefore = $plan.current
	activeAfter = [ordered]@{
		version = [string]$newState.productVersion
		channel = [string]$newState.channel
		sourceCommit = [string]$newState.sourceCommit
	}
	targetCandidateId = [string]$plan.target.candidateId
	targetBundleSha256 = [string]$plan.target.bundleSha256
	persistentStateMigration = 'NOT_IMPLEMENTED'
	productionPackageActivation = 'NOT_PERFORMED'
	completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$receiptPath = Join-Path $workRoot 'update-receipt.json'
[System.IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 32) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "Verified managed update PASS"
Write-Host "Installed: $($plan.current.version) → $($newState.productVersion)"
Write-Host "Rollback slot: $($receipt.rollbackPath)"
Write-Host "Automatic update scheduling: false"
Write-Host "Persistent state migration: NOT_IMPLEMENTED"

return $receipt
