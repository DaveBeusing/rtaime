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

$abiPath = Join-Path $repositoryRoot "native/Providers/MediaIo/rtaime_media_io_abi.h"
$cmakePath = Join-Path $repositoryRoot "native/Providers/AjaNtv2/CMakeLists.txt"
$nativePath = Join-Path $repositoryRoot "native/Providers/AjaNtv2/rtaime_media_io_aja.cpp"
$bridgePath = Join-Path $repositoryRoot "src/Media/rtaime.Media/NativeMediaIoAdapter.cs"
$pumpPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/MediaIoVerticalSlice.cs"
$testPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/MediaIoVerticalSliceTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/MediaIoVerticalSlice.md"

foreach ($path in @($abiPath, $cmakePath, $nativePath, $bridgePath, $pumpPath, $testPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required AP-33 Media I/O artifact is missing: '$path'."
}

$abi = Get-Content -LiteralPath $abiPath -Raw
$cmake = Get-Content -LiteralPath $cmakePath -Raw
$native = Get-Content -LiteralPath $nativePath -Raw
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$pump = Get-Content -LiteralPath $pumpPath -Raw
$tests = Get-Content -LiteralPath $testPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($abi -match 'RTAIME_MEDIA_IO_ABI_VERSION_MAJOR 1u') "Media I/O ABI major must remain 1."
Assert-Condition ($abi -match 'RTAIME_MEDIA_IO_ABI_VERSION_MINOR 1u') "AP-33 Media I/O ABI minor must be 1."
Assert-Condition ($abi -match 'rtaime_media_io_get_provider_info') "ABI must expose provider identity metadata for qualification."
Assert-Condition ($abi -match 'audio_sample_count[\s\S]*audio_channel_count') "Output ABI must carry embedded audio dimensions."
Assert-Condition ($abi -notmatch 'AJA|NTV2|DeckLink|Blackmagic') "Stable Media I/O ABI must remain vendor-neutral."

Assert-Condition ($cmake -match 'libajantv2') "AJA adapter build must consume libajantv2."
Assert-Condition ($cmake -match 'RTAIME_AJA_NTV2_ROOT') "AJA SDK location must be explicit."
Assert-Condition ($cmake -match 'RTAIME_AJA_SDK_REVISION') "AJA SDK revision must be evidence-addressable."
Assert-Condition ($cmake -match 'target_link_libraries\(rtaime_media_io PRIVATE ajantv2\)') "Native adapter must link the SDK target only at the provider boundary."

Assert-Condition ($native -match 'kPortCount = 3') "AP-33 native topology must expose exactly two capture ports plus one Program output."
Assert-Condition ($native -match 'NTV2_INPUTSOURCE_SDI1') "AP-33 must use SDI input 1."
Assert-Condition ($native -match 'NTV2_INPUTSOURCE_SDI2') "AP-33 must use SDI input 2."
Assert-Condition ($native -match 'NTV2_CHANNEL3') "AP-33 must reserve the third channel for Program output."
Assert-Condition ($native -match 'NTV2_FBF_RGBA') "AJA frame stores must normalize to RGBA."
Assert-Condition ($native -match 'AutoCirculateInitForInput') "Physical capture must use bounded AJA AutoCirculate input."
Assert-Condition ($native -match 'HasAvailableInputFrame') "Capture acquire must be non-blocking."
Assert-Condition ($native -match 'CanAcceptMoreOutputFrames') "Program output must surface hardware backpressure."
Assert-Condition ($native -match 'RTAIME_MEDIA_IO_PINNED_HOST_LEASE') "AP-33 must explicitly qualify the pinned-host transfer mode."
Assert-Condition ($native -match 'config->transfer_mode != RTAIME_MEDIA_IO_PINNED_HOST_LEASE') "Native adapter must fail closed for unqualified transfer modes."
Assert-Condition ($native -match 'GetDisplayName') "Physical evidence must be able to identify the AJA adapter."
Assert-Condition ($native -match 'GetDriverVersionString') "Physical evidence must be able to identify the AJA driver."

Assert-Condition ($bridge -match 'DllImport') "Managed Media I/O bridge must bind the stable native ABI."
Assert-Condition ($bridge -match 'AbiMinor = 1') "Managed bridge must request ABI 1.1."
Assert-Condition ($bridge -match 'NativeMediaIoProviderMetadata') "Managed bridge must surface physical provider metadata."
Assert-Condition ($bridge -match 'PinnedHostMediaIoMemory') "Managed bridge must retain explicit pinned-host ownership semantics."
Assert-Condition ($bridge -notmatch 'aja-video|libajantv2|CNTV2|NTV2_') "Managed Media implementation must not bind vendor SDK types directly."

Assert-Condition ($pump -match 'Take\(2\)') "Vertical slice must select exactly two inputs."
Assert-Condition ($pump -match 'Take\(1\)') "Vertical slice must select exactly one Program output."
Assert-Condition ($pump -match 'using \(lease\)') "Capture leases must be released before PumpInputs returns."
Assert-Condition ($pump -match 'GCHandleType\.Pinned') "Program output must pin only the synchronous submit payload."
Assert-Condition ($pump -notmatch 'Channel<|ConcurrentQueue|Queue<') "AP-33 vertical slice must not introduce an unbounded managed media queue."

Assert-Condition ($tests -match 'releases_each_provider_lease_before_return') "Unit coverage must regress capture lease release."
Assert-Condition ($tests -match 'surfaces_backpressure_without_queueing') "Unit coverage must regress output backpressure."
Assert-Condition ($tests -match 'fails_closed_without_two_inputs_and_one_output') "Unit coverage must regress the V1 topology requirement."

Assert-Condition ($documentation -match 'Physical hardware qualification state[\s\S]*UNVERIFIED') "Documentation must retain UNVERIFIED until real hardware evidence exists."
Assert-Condition ($documentation -match 'PinnedHostLease') "Documentation must identify the AP-33 qualified transfer-mode target."
Assert-Condition ($documentation -match 'DeviceDirectLease.*not advertised') "Documentation must not overstate direct-device transfer support."
Assert-Condition ($documentation -match 'Deferred to AP-34') "Timing/reference qualification must remain deferred to AP-34."

$managedContractFiles = @(
	Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/MediaIoContracts.cs",
	Join-Path $repositoryRoot "src/Contracts/rtaime.Provider.Contracts/MediaIoProviderContracts.cs"
)
foreach ($path in $managedContractFiles) {
	$source = Get-Content -LiteralPath $path -Raw
	Assert-Condition ($source -notmatch 'DllImport|CNTV2|libajantv2|AJA\.|NTV2_') "Stable managed contracts must remain free of native/vendor bindings: '$path'."
}

Write-Host "Media I/O vertical slice policy verification PASS"
Write-Host "Topology: SDI1 + SDI2 capture -> Program SDI output; bounded pinned-host transfer"
Write-Host "Physical hardware evidence state: UNVERIFIED until dedicated self-hosted qualification PASSES"
