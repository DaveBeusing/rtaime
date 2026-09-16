# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[string]$OutputPath = "",
	[switch]$QualificationMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

$installRoot = [System.IO.Path]::GetFullPath($InstallPath)
Assert-Condition (Test-Path -LiteralPath $installRoot -PathType Container) "Installed release was not found at '$installRoot'."

$manifestPath = Join-Path $installRoot "bundle-manifest.json"
Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Installed release does not contain bundle-manifest.json."

$verifier = Join-Path $installRoot "tools/Test-OfflineReleaseBundle.ps1"
if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) {
	$verifier = Join-Path $PSScriptRoot "../release/Test-OfflineReleaseBundle.ps1"
}
Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Offline bundle verifier is unavailable for installed release inspection."

if ($QualificationMode) { & $verifier -BundlePath $installRoot } else { & $verifier -BundlePath $installRoot -RequireTrustedProductionKey }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$manifest.productName -eq "rtaime") "Unexpected installed product identity."
Assert-Condition ([string]$manifest.platform.osFamily -eq "Windows") "Installed release is not a Windows package."
Assert-Condition ([string]$manifest.platform.architecture -eq "x64") "Installed release is not x64."
Assert-Condition ([string]$manifest.platform.rid -eq "win-x64") "Installed release RID is not win-x64."

$trustStorePath = Join-Path $installRoot "trust/trusted-release-keys.json"
Assert-Condition (Test-Path -LiteralPath $trustStorePath -PathType Leaf) "Installed release trust store is missing."
$trustStore = Get-Content -LiteralPath $trustStorePath -Raw | ConvertFrom-Json
Assert-Condition ([string]$trustStore.schemaVersion -eq "1.0") "Installed trust store schema is unsupported."
$activeTrustedFingerprints = @(
	$trustStore.keys |
		Where-Object { [string]$_.status -eq "ACTIVE" -and [string]$_.purpose -eq "SOFTWARE_RELEASE" } |
		ForEach-Object { ([string]$_.fingerprint).ToLowerInvariant() } |
		Sort-Object -Unique
)
foreach ($fingerprint in $activeTrustedFingerprints) {
	Assert-Condition ($fingerprint -match '^[0-9a-f]{64}$') "Installed trust store contains invalid active release-key fingerprint '$fingerprint'."
}

$channel = switch ([string]$manifest.releaseStage) {
	"PREVIEW" { "PREVIEW" }
	"STABLE" { "STABLE" }
	"VALIDATED" { "STABLE" }
	"CERTIFIED" { "STABLE" }
	"DEV" { "QUALIFICATION" }
	default { "UNSUPPORTED" }
}

$state = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	installPath = $installRoot
	productName = [string]$manifest.productName
	productVersion = [string]$manifest.productVersion
	releaseStage = [string]$manifest.releaseStage
	channel = $channel
	sourceCommit = ([string]$manifest.sourceCommit).ToLowerInvariant()
	buildCommit = ([string]$manifest.buildCommit).ToLowerInvariant()
	releaseRecordId = [string]$manifest.releaseTrust.releaseRecordId
	releaseKeyFingerprint = ([string]$manifest.releaseTrust.releaseKeyFingerprint).ToLowerInvariant()
	releaseSignerClass = [string]$manifest.releaseTrust.releaseSignerClass
	activeTrustedReleaseKeyFingerprints = $activeTrustedFingerprints
	platform = [ordered]@{
		osFamily = [string]$manifest.platform.osFamily
		architecture = [string]$manifest.platform.architecture
		rid = [string]$manifest.platform.rid
	}
	integrityVerification = "PASS"
	productionTrustRequired = -not $QualificationMode
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
	$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
	Write-JsonFile -Value $state -Path $outputFullPath
}

Write-Host "Installed release inspection PASS"
Write-Host "Version: $($state.productVersion)"
Write-Host "Channel: $($state.channel)"
Write-Host "Source commit: $($state.sourceCommit)"
Write-Host "Active trusted release keys: $($activeTrustedFingerprints.Count)"
Write-Host "Production trust required: $($state.productionTrustRequired)"

return $state
