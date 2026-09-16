# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ExpectedAdapterName,
	[Parameter(Mandatory)][string]$AjaSdkRevision,
	[int]$MinimumFrames = 30,
	[ValidateSet("1080p50", "1080p59.94")][string]$Format = "1080p50",
	[switch]$RequireExternalReference,
	[string]$EvidencePath = "artifacts/qualification/media-io-reference.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "Media I/O reference qualification requires the Windows V1 reference platform." }
if (-not [Environment]::Is64BitProcess) { throw "Media I/O reference qualification requires an x64 process." }
if ([string]::IsNullOrWhiteSpace($ExpectedAdapterName)) { throw "ExpectedAdapterName is required." }
if ([string]::IsNullOrWhiteSpace($AjaSdkRevision)) { throw "AjaSdkRevision is required." }
if ($MinimumFrames -lt 10) { throw "MinimumFrames must be at least 10." }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$resolvedEvidence = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $EvidencePath))
$evidenceDirectory = Split-Path -Parent $resolvedEvidence
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
if (Test-Path -LiteralPath $resolvedEvidence) { Remove-Item -LiteralPath $resolvedEvidence -Force }

$previous = @{
	Enabled = $env:RTAIME_MEDIA_IO_REFERENCE_QUALIFICATION
	Adapter = $env:RTAIME_MEDIA_IO_EXPECTED_ADAPTER
	Sdk = $env:RTAIME_AJA_SDK_REVISION
	Frames = $env:RTAIME_MEDIA_IO_REFERENCE_FRAMES
	Format = $env:RTAIME_MEDIA_IO_REFERENCE_FORMAT
	External = $env:RTAIME_MEDIA_IO_REFERENCE_EXTERNAL
	Evidence = $env:RTAIME_MEDIA_IO_REFERENCE_EVIDENCE
}

try {
	$env:RTAIME_MEDIA_IO_REFERENCE_QUALIFICATION = "1"
	$env:RTAIME_MEDIA_IO_EXPECTED_ADAPTER = $ExpectedAdapterName.Trim()
	$env:RTAIME_AJA_SDK_REVISION = $AjaSdkRevision.Trim()
	$env:RTAIME_MEDIA_IO_REFERENCE_FRAMES = $MinimumFrames.ToString([Globalization.CultureInfo]::InvariantCulture)
	$env:RTAIME_MEDIA_IO_REFERENCE_FORMAT = $Format
	$env:RTAIME_MEDIA_IO_REFERENCE_EXTERNAL = $RequireExternalReference.IsPresent.ToString().ToLowerInvariant()
	$env:RTAIME_MEDIA_IO_REFERENCE_EVIDENCE = $resolvedEvidence

	Push-Location $repositoryRoot
	try {
		dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj `
			--configuration Release `
			--no-build `
			--filter "FullyQualifiedName~HardwareMediaIoQualificationTests.Reference_hardware_profile_must_pass_when_explicitly_enabled"
		if ($LASTEXITCODE -ne 0) { throw "Media I/O reference hardware qualification test failed." }
	}
	finally {
		Pop-Location
	}

	if (-not (Test-Path -LiteralPath $resolvedEvidence -PathType Leaf)) {
		throw "Media I/O qualification completed without producing evidence at '$resolvedEvidence'."
	}

	$evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
	if ($evidence.schemaVersion -ne "1.0") { throw "Unsupported Media I/O qualification evidence schema '$($evidence.schemaVersion)'." }
	if ($evidence.status -ne "PASSED") { throw "Media I/O qualification evidence status is '$($evidence.status)', expected 'PASSED'." }
	if ($evidence.ajaSdkRevision -ne $AjaSdkRevision.Trim()) { throw "Media I/O qualification SDK revision changed during execution." }
	if ($evidence.detectedAdapter.IndexOf($ExpectedAdapterName.Trim(), [StringComparison]::OrdinalIgnoreCase) -lt 0) {
		throw "Detected adapter '$($evidence.detectedAdapter)' does not match expected '$ExpectedAdapterName'."
	}
	if ($evidence.transferMode -ne "PinnedHostLease") { throw "AP-33 qualification must prove the PinnedHostLease path." }
	if ([uint64]$evidence.statistics.CapturedA -lt [uint64]$MinimumFrames) { throw "Source A capture evidence is below the required frame count." }
	if ([uint64]$evidence.statistics.CapturedB -lt [uint64]$MinimumFrames) { throw "Source B capture evidence is below the required frame count." }
	if ([uint64]$evidence.statistics.OutputAccepted -lt [uint64]$MinimumFrames) { throw "Program output evidence is below the required frame count." }
	if ([uint64]$evidence.statistics.CaptureFailures -ne 0) { throw "Media I/O qualification recorded capture failures." }
	if ([uint64]$evidence.statistics.OutputRejected -ne 0) { throw "Media I/O qualification recorded hard Program-output rejection." }

	Write-Host "Media I/O reference hardware qualification PASS"
	Write-Host "Adapter: $($evidence.detectedAdapter)"
	Write-Host "Driver: $($evidence.driverVersion)"
	Write-Host "AJA SDK: $($evidence.ajaSdkRevision)"
	Write-Host "Evidence: $resolvedEvidence"
}
finally {
	$env:RTAIME_MEDIA_IO_REFERENCE_QUALIFICATION = $previous.Enabled
	$env:RTAIME_MEDIA_IO_EXPECTED_ADAPTER = $previous.Adapter
	$env:RTAIME_AJA_SDK_REVISION = $previous.Sdk
	$env:RTAIME_MEDIA_IO_REFERENCE_FRAMES = $previous.Frames
	$env:RTAIME_MEDIA_IO_REFERENCE_FORMAT = $previous.Format
	$env:RTAIME_MEDIA_IO_REFERENCE_EXTERNAL = $previous.External
	$env:RTAIME_MEDIA_IO_REFERENCE_EVIDENCE = $previous.Evidence
}
