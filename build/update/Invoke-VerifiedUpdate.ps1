# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[Parameter(Mandatory)]
	[string]$StateRoot,
	[ValidateSet('PREVIEW', 'STABLE')]
	[string]$Channel,
	[string]$Repository = 'DaveBeusing/rtaime',
	[string]$PinnedVersion = '',
	[string]$WorkPath = '',
	[switch]$AcknowledgeProcessesStopped,
	[switch]$QualificationMode,
	[string]$QualificationBundlePath = '',
	[string]$QualificationStateCatalogPath = '',
	[ValidateSet('NONE', 'AFTER_SOFTWARE_ACTIVATION', 'AFTER_STATE_MIGRATION')]
	[string]$QualificationFailurePoint = 'NONE'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$coordinator = Join-Path $PSScriptRoot 'Invoke-CoordinatedUpgrade.ps1'
if (-not (Test-Path -LiteralPath $coordinator -PathType Leaf)) {
	throw "Coordinated update entrypoint is unavailable at '$coordinator'. Software-only production update is not permitted once persistent-state migration is coordinated."
}

$arguments = @{
	InstallPath = $InstallPath
	StateRoot = $StateRoot
	Channel = $Channel
	Repository = $Repository
	PinnedVersion = $PinnedVersion
}
if (-not [string]::IsNullOrWhiteSpace($WorkPath)) { $arguments.WorkPath = $WorkPath }
if ($AcknowledgeProcessesStopped) { $arguments.AcknowledgeProcessesStopped = $true }
if ($QualificationMode) {
	$arguments.QualificationMode = $true
	$arguments.QualificationBundlePath = $QualificationBundlePath
	$arguments.QualificationStateCatalogPath = $QualificationStateCatalogPath
	$arguments.QualificationFailurePoint = $QualificationFailurePoint
} elseif ($QualificationFailurePoint -ne 'NONE') {
	throw 'Qualification failure injection is permitted only in QualificationMode.'
}

return & $coordinator @arguments
