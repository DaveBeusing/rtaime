# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = "docs/SupportCompatibilityMatrix.md",
	[switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$tick = [char]96

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Read-Json {
	param([Parameter(Mandatory)][string]$Path)
	$fullPath = Resolve-RepositoryPath $Path
	if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Required support matrix source is missing: '$Path'." }
	return Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
}

function Safe-Cell {
	param($Value)
	if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return "—" }
	return ([string]$Value).Replace("|", "\|").Replace([string][char]13, " ").Replace([string][char]10, " ")
}

$support = Read-Json "docs/Governance/ProductSupportPolicy.json"
$platform = Read-Json "docs/Governance/PlatformSupportMatrix.json"

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->")
$lines.Add("")
$lines.Add("# Support and Compatibility Matrix")
$lines.Add("")
$lines.Add("> Generated from $tick" + "docs/Governance/ProductSupportPolicy.json" + "$tick and $tick" + "docs/Governance/PlatformSupportMatrix.json" + "$tick. Do not edit support states directly in this document.")
$lines.Add("")
$lines.Add("## Version support")
$lines.Add("")
$lines.Add("| Channel class | Support status | Support mode |")
$lines.Add("| --- | --- | --- |")
$lines.Add("| QUALIFICATION / DEV | NOT_APPLICABLE | Internal qualification; not a customer support commitment |")
$lines.Add("| PREVIEW | $([string]$support.supportedVersions.preview.status) | $([string]$support.supportedVersions.preview.supportMode) |")
$lines.Add("| STABLE | $([string]$support.lifecycle.stableRelease.status) | Approved duration required before support can become PASS |")
$lines.Add("")
$stableLines = @($support.supportedVersions.stableLines)
if ($stableLines.Count -eq 0) {
	$lines.Add("No Stable version line is currently declared supported.")
} else {
	$lines.Add("| Stable line | Status | Support start | Maintenance end | Security end | EOL |")
	$lines.Add("| --- | --- | --- | --- | --- | --- |")
	foreach ($line in $stableLines | Sort-Object versionLine) {
		$lines.Add("| $(Safe-Cell $line.versionLine) | $(Safe-Cell $line.status) | $(Safe-Cell $line.supportStart) | $(Safe-Cell $line.maintenanceEnd) | $(Safe-Cell $line.securityEnd) | $(Safe-Cell $line.eolDate) |")
	}
}
$lines.Add("")
$lines.Add("## Platform configurations")
$lines.Add("")
$lines.Add("| Configuration | OS | Architecture | Runtime | GPU | Media I/O | Support status | Evidence |")
$lines.Add("| --- | --- | --- | --- | --- | --- | --- | --- |")
foreach ($configuration in @($platform.configurations) | Sort-Object id) {
	$runtime = @($configuration.runtimeRequirements | ForEach-Object { "$($_.name) >= $($_.minimumVersion)" }) -join "; "
	$gpu = "$(Safe-Cell $configuration.gpu.class) / driver $(Safe-Cell $configuration.gpu.driverRange)"
	$mediaIo = "$(Safe-Cell $configuration.mediaIo.provider) / device $(Safe-Cell $configuration.mediaIo.device) / driver $(Safe-Cell $configuration.mediaIo.driverRange)"
	$evidence = @($configuration.qualificationEvidence) -join "; "
	$lines.Add("| $(Safe-Cell $configuration.id) | $(Safe-Cell $configuration.platform.osFamily) $(Safe-Cell $configuration.platform.edition) $(Safe-Cell $configuration.platform.versionFamily) | $(Safe-Cell $configuration.platform.architecture) | $(Safe-Cell $runtime) | $(Safe-Cell $gpu) | $(Safe-Cell $mediaIo) | $(Safe-Cell $configuration.supportStatus) | $(Safe-Cell $evidence) |")
}
$lines.Add("")
$lines.Add("## Compatibility rules")
$lines.Add("")
$lines.Add("- Driver support policy: **$([string]$platform.compatibilityRules.driverRangePolicy)**.")
$lines.Add("- Provider SDK policy: **$([string]$platform.compatibilityRules.providerSdkPolicy)**.")
$lines.Add("- Firmware policy: **$([string]$platform.compatibilityRules.firmwarePolicy)**.")
$lines.Add("- A material driver/provider/firmware change requires **$([string]$platform.compatibilityRules.materialChangePolicy)**.")
$lines.Add("- A configuration without required qualification evidence must remain **UNVERIFIED** and cannot be projected as **SUPPORTED**.")
$lines.Add("")
$lines.Add("## Support commitment boundary")
$lines.Add("")
$lines.Add("Stable maintenance duration, security-fix duration, EOL notification lead time, security remediation targets and contract deprecation notice remain " + $tick + "UNVERIFIED" + $tick + " until explicitly approved in the machine-readable support policy.")
$lines.Add("")

$rendered = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
$resolvedOutput = Resolve-RepositoryPath $OutputPath

if ($Verify) {
	if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Leaf)) {
		throw "Generated support/compatibility projection is missing: '$OutputPath'."
	}
	$existing = Get-Content -LiteralPath $resolvedOutput -Raw
	$normalizedExisting = $existing.Replace([string][char]13, "")
	$normalizedRendered = $rendered.Replace([string][char]13, "")
	if ($normalizedExisting -ne $normalizedRendered) {
		throw "Support/compatibility projection drifted from its source-controlled policies. Regenerate with New-SupportCompatibilityMatrix.ps1."
	}
	Write-Host "Support/compatibility matrix projection PASS"
	return
}

$directory = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
[System.IO.File]::WriteAllText($resolvedOutput, $rendered, [System.Text.UTF8Encoding]::new($false))
Write-Host "Support/compatibility matrix generated: $resolvedOutput"
