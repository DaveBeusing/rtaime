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

$mediaContractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/MediaIoContracts.cs"
$providerContractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Provider.Contracts/MediaIoProviderContracts.cs"
$foundationPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/MediaIoFoundation.cs"
$abiPath = Join-Path $repositoryRoot "native/Providers/MediaIo/rtaime_media_io_abi.h"
$documentationPath = Join-Path $repositoryRoot "docs/MediaIoDecisionAndFoundation.md"
$contractTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Contracts/MediaIoContractTests.cs"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/MediaIoFoundationTests.cs"

foreach ($path in @($mediaContractsPath, $providerContractsPath, $foundationPath, $abiPath, $documentationPath, $contractTestsPath, $unitTestsPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Media I/O foundation artifact is missing: '$path'."
}

$mediaContracts = Get-Content -LiteralPath $mediaContractsPath -Raw
$providerContracts = Get-Content -LiteralPath $providerContractsPath -Raw
$foundation = Get-Content -LiteralPath $foundationPath -Raw
$abi = Get-Content -LiteralPath $abiPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$contractTests = Get-Content -LiteralPath $contractTestsPath -Raw
$unitTests = Get-Content -LiteralPath $unitTestsPath -Raw

Assert-Condition ($mediaContracts -match 'MediaIoContractVersion[\s\S]*new\(1, 0\)') "Media I/O contract must retain independent 1.0 versioning."
Assert-Condition ($mediaContracts -match 'SurfaceOwnership\.SharedLease') "Media I/O capture must require SharedLease ownership."
Assert-Condition ($mediaContracts -match 'Lifetime\.LeaseId') "Media I/O capture must require explicit lease identity."
Assert-Condition ($mediaContracts -match 'Surface\.Handle') "Media I/O capture must require an opaque surface handle."
Assert-Condition ($mediaContracts -match 'MediaIoOutputFrameDescriptor') "Media I/O output must use an explicit output descriptor."
Assert-Condition ($mediaContracts -match 'output video surfaces require an opaque handle') "Media I/O output descriptors must require opaque surface handles."
Assert-Condition ($mediaContracts -match 'DeviceDirectLease') "Media I/O contract must retain explicit device-direct transfer semantics."
Assert-Condition ($mediaContracts -match 'PinnedHostLease') "Media I/O contract must retain explicit pinned-host transfer semantics."
Assert-Condition ($mediaContracts -notmatch 'byte\[\]|Memory<byte>|ReadOnlyMemory<byte>|Stream\s') "Media I/O stable contract must not expose bulk media payload types."

Assert-Condition ($providerContracts -match 'MediaIoCapabilityKinds') "Provider contracts must expose Media I/O capability kinds."
Assert-Condition ($providerContracts -match 'ProviderResourceId') "Physical Media I/O ports must map to provider resources."
Assert-Condition ($providerContracts -match 'NormalizedVideoFormats') "Provider ports must separate normalized video formats."
Assert-Condition ($providerContracts -match 'NativeVideoFormats') "Provider ports must separate native video formats."
Assert-Condition ($providerContracts -match 'TransferModes') "Provider ports must declare actual transfer modes."
Assert-Condition ($providerContracts -match 'resource\.Kind') "Provider-resource semantics must be validated against port direction."

Assert-Condition ($foundation -match 'IMediaIoProviderAdapter') "Media I/O foundation must define a vendor-neutral adapter seam."
Assert-Condition ($foundation -match 'bool TryAcquire') "Input acquisition must remain non-blocking."
Assert-Condition ($foundation -match 'TrySubmit\(MediaIoOutputFrameDescriptor frame\)') "Output submission must remain explicit, handle-based and non-blocking."
Assert-Condition ($foundation -match 'MediaIoAdmission\.Validate') "Media I/O session admission must be explicit."
Assert-Condition ($foundation -notmatch 'NamedPipe|Http|Socket') "Media I/O foundation must not introduce management or host-to-host transport."

Assert-Condition ($abi -match 'RTAIME_MEDIA_IO_ABI_VERSION_MAJOR 1u') "Native Media I/O ABI major version must remain explicit."
Assert-Condition ($abi -match 'rtaime_media_io_input_try_acquire') "Native ABI must expose non-blocking input acquire."
Assert-Condition ($abi -match 'rtaime_media_io_input_release') "Native ABI must expose explicit input lease release."
Assert-Condition ($abi -match 'rtaime_media_io_output_try_submit') "Native ABI must expose non-blocking output submit."
Assert-Condition ($abi -match 'opaque_handle') "Native ABI must use opaque media handles."
Assert-Condition ($abi -notmatch 'DeckLink|Blackmagic|NTV2|AJA') "Native stable ABI must remain vendor-neutral."
Assert-Condition ($abi -notmatch 'uint8_t\s*\*\s*(pixels|audio|payload)|void\s*\*\s*(pixels|audio|payload)') "Native ABI must not expose bulk media byte pointers as management-style payloads."

foreach ($source in @($mediaContracts, $providerContracts, $foundation)) {
	Assert-Condition ($source -notmatch 'DeckLink|Blackmagic|NTV2|AJA') "Stable managed Media I/O source must remain free of vendor SDK names."
}

Assert-Condition ($documentation -match 'AJA NTV2 SDK') "Media I/O decision must identify the V1 reference adapter path."
Assert-Condition ($documentation -match 'Physical hardware qualification state:\s*\*\*UNVERIFIED\*\*') "Media I/O documentation must not overstate physical qualification."
Assert-Condition ($documentation -match 'DeviceDirectLease[\s\S]*SharedOpaqueHandle[\s\S]*PinnedHostLease') "Documentation must retain zero-copy-first transfer preference."
Assert-Condition ($documentation -match 'Deferred to AP-33') "Physical adapter implementation must remain explicitly deferred to AP-33."

Assert-Condition ($contractTests -match 'contains_no_bulk_media_payload_property') "Contracts must regress bulk-payload exclusion."
Assert-Condition ($contractTests -match 'requires_shared_opaque_lease') "Contracts must regress input lease semantics."
Assert-Condition ($contractTests -match 'Output_frame_requires_opaque_handle') "Contracts must regress output handle semantics."
Assert-Condition ($unitTests -match 'Admission_fails_closed') "Unit tests must regress fail-closed Media I/O admission."
Assert-Condition ($unitTests -match 'releases_exactly_once') "Unit tests must regress idempotent input lease release."

$nativeProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "native/Providers") -Filter *.vcxproj -File -Recurse -ErrorAction SilentlyContinue)
Assert-Condition ($nativeProjects.Count -eq 0) "AP-32 must not introduce a native build project before the AP-33 physical adapter implementation."

Write-Host "Media I/O foundation policy verification PASS"
Write-Host "Reference adapter decision: AJA NTV2; physical hardware state: UNVERIFIED"
Write-Host "Boundary: descriptors + opaque handles + leases; no bulk media over management IPC"
