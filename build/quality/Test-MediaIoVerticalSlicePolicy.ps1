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
$sdkPinPath = Join-Path $repositoryRoot "native/Providers/AjaNtv2/libajantv2.version.json"
$compatibilityPath = Join-Path $repositoryRoot "build/native/Apply-AjaNtv2BuildCompatibility.ps1"
$bridgePath = Join-Path $repositoryRoot "src/Media/rtaime.Media/NativeMediaIoAdapter.cs"
$pumpPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/MediaIoVerticalSlice.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
$runtimeBridgePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeMediaIoVerticalSlice.cs"
$testPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/MediaIoVerticalSliceTests.cs"
$hardwareTestPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/HardwareMediaIoQualificationTests.cs"
$qualificationPath = Join-Path $repositoryRoot "build/qualification/Invoke-MediaIoReferenceQualification.ps1"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/media-io-reference-qualification.yml"
$requiredGatesPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
$documentationPath = Join-Path $repositoryRoot "docs/MediaIoVerticalSlice.md"

foreach ($path in @(
	$abiPath,
	$cmakePath,
	$nativePath,
	$sdkPinPath,
	$compatibilityPath,
	$bridgePath,
	$pumpPath,
	$runtimePath,
	$runtimeBridgePath,
	$testPath,
	$hardwareTestPath,
	$qualificationPath,
	$workflowPath,
	$requiredGatesPath,
	$documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required AP-33 Media I/O artifact is missing: '$path'."
}

$abi = Get-Content -LiteralPath $abiPath -Raw
$cmake = Get-Content -LiteralPath $cmakePath -Raw
$native = Get-Content -LiteralPath $nativePath -Raw
$sdkPin = Get-Content -LiteralPath $sdkPinPath -Raw | ConvertFrom-Json
$compatibility = Get-Content -LiteralPath $compatibilityPath -Raw
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$pump = Get-Content -LiteralPath $pumpPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$runtimeBridge = Get-Content -LiteralPath $runtimeBridgePath -Raw
$tests = Get-Content -LiteralPath $testPath -Raw
$hardwareTests = Get-Content -LiteralPath $hardwareTestPath -Raw
$qualification = Get-Content -LiteralPath $qualificationPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$requiredGates = Get-Content -LiteralPath $requiredGatesPath -Raw
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

Assert-Condition ([string]$sdkPin.repository -eq 'https://github.com/aja-video/libajantv2.git') "Reference SDK pin must target aja-video/libajantv2."
Assert-Condition ([string]$sdkPin.referenceBranch -eq 'release') "Reference SDK pin must document the AJA release branch."
Assert-Condition ([string]$sdkPin.commit -match '^[0-9a-f]{40}$') "Reference SDK pin must contain an exact 40-character commit SHA."
Assert-Condition ([string]$sdkPin.commit -eq 'aa4d482a47fdd9fd9f2883163e286206ac0d7ae7') "AP-33 reference SDK pin changed without qualification-policy update."

Assert-Condition ($compatibility -match 'aa4d482a47fdd9fd9f2883163e286206ac0d7ae7') "AJA build compatibility must remain pinned to the qualified SDK commit."
Assert-Condition ($compatibility -match 'ajabase/system/windows/infoimpl\.cpp') "AJA build compatibility must remain scoped to the known Windows SDK source."
Assert-Condition ($compatibility -match 'source shape changed') "AJA build compatibility must fail closed if the pinned upstream source shape changes."
Assert-Condition ($compatibility -match 'IWbemServices \*pSvc = NULL') "AJA build compatibility must retain the MSVC goto/declaration fix."
Assert-Condition ($compatibility -match 'Get-FileHash') "AJA build compatibility must emit a patched-source evidence hash."

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

Assert-Condition ($runtime -match 'enum RuntimeMediaIoMode[\s\S]*Virtual[\s\S]*Native') "RuntimeHost must expose explicit virtual/native Media I/O selection."
Assert-Condition ($runtime -match 'RTAIME_RUNTIME_MEDIA_IO') "RuntimeHost Media I/O selection must be environment-configurable."
Assert-Condition ($runtime -match 'if \(_options\.MediaIoMode == RuntimeMediaIoMode\.Native\)[\s\S]*new NativeMediaIoProviderAdapter') "Native RuntimeHost mode must explicitly construct the native provider."
Assert-Condition ($runtime -match 'RunMediaLoopAsync\(_runtime, _mediaIo') "RuntimeHost media loop must receive the physical bridge explicitly."
Assert-Condition ($runtime -match 'mediaIo\?\.PumpInputs\(\)[\s\S]*runtime\.ProcessNextBoundary\(\)[\s\S]*mediaIo\?\.SubmitProgram') "Physical input and Program output must wrap the existing committed runtime boundary."
Assert-Condition ($runtime -notmatch 'catch[\s\S]{0,240}RuntimeMediaIoMode\.Virtual') "Native startup failure must not silently fall back to Virtual Media I/O."
Assert-Condition ($runtimeBridge -match 'SetExternalInputContent') "Runtime Media I/O bridge must only update current execution media content."
Assert-Condition ($runtimeBridge -match 'ProgramPixels') "Runtime Media I/O output must reuse the existing Program readback."

Assert-Condition ($tests -match 'releases_each_provider_lease_before_return') "Unit coverage must regress capture lease release."
Assert-Condition ($tests -match 'surfaces_backpressure_without_queueing') "Unit coverage must regress output backpressure."
Assert-Condition ($tests -match 'fails_closed_without_two_inputs_and_one_output') "Unit coverage must regress the V1 topology requirement."

Assert-Condition ($hardwareTests -match 'RTAIME_MEDIA_IO_REFERENCE_QUALIFICATION') "Physical qualification test must require explicit opt-in."
Assert-Condition ($hardwareTests -match 'MediaIoSignalState\.Locked') "Physical qualification must require locked input signals."
Assert-Condition ($hardwareTests -match 'OutputAccepted') "Physical qualification must prove accepted Program output frames."
Assert-Condition ($hardwareTests -match 'CaptureFailures') "Physical qualification evidence must include capture failures."
Assert-Condition ($hardwareTests -match 'status = "PASSED"') "Physical qualification may emit evidence only after passing assertions."
Assert-Condition ($hardwareTests -match 'driverVersion') "Physical qualification evidence must identify the driver."
Assert-Condition ($hardwareTests -match 'ajaSdkRevision') "Physical qualification evidence must identify the exact AJA SDK revision."

Assert-Condition ($qualification -match 'FullyQualifiedName~HardwareMediaIoQualificationTests') "Qualification runner must execute only the physical Media I/O test."
Assert-Condition ($qualification -match 'status -ne "PASSED"') "Qualification runner must reject non-PASSED evidence."
Assert-Condition ($qualification -match 'transferMode -ne "PinnedHostLease"') "Qualification runner must reject an unexpected transfer mode."
Assert-Condition ($qualification -match 'OutputRejected') "Qualification runner must reject hard Program-output failures."

Assert-Condition ($workflow -match 'rtaime-media-io-reference') "Physical workflow must target the dedicated Media I/O reference runner."
Assert-Condition ($workflow -match 'libajantv2\.version\.json') "Physical workflow must consume the repository SDK pin."
Assert-Condition ($workflow -match 'git -C \$sdkRoot rev-parse HEAD') "Physical workflow must resolve the exact libajantv2 commit."
Assert-Condition ($workflow -match 'differs from pin') "Physical workflow must fail when the resolved SDK differs from the repository pin."
Assert-Condition ($workflow -match 'Apply-AjaNtv2BuildCompatibility\.ps1') "Physical workflow must apply the repository-controlled pinned SDK compatibility patch."
Assert-Condition ($workflow -match 'RTAIME_AJA_SDK_REVISION') "Physical workflow must propagate the exact SDK revision into evidence."
Assert-Condition ($workflow -match 'cmake --build') "Physical workflow must build the native provider from source."
Assert-Condition ($workflow -match 'rtaime_media_io\.dll') "Physical workflow must expose exactly the built native provider DLL."
Assert-Condition ($workflow -match 'Upload immutable qualification evidence') "Physical workflow must retain qualification evidence."

Assert-Condition ($requiredGates -match 'Build pinned native Media I/O provider') "Provider Smoke must compile the pinned native Media I/O provider."
Assert-Condition ($requiredGates -match 'libajantv2\.version\.json') "Provider Smoke must consume the repository SDK pin."
Assert-Condition ($requiredGates -match 'git -C \$sdkRoot rev-parse HEAD') "Provider Smoke must verify the exact SDK commit."
Assert-Condition ($requiredGates -match 'Apply-AjaNtv2BuildCompatibility\.ps1') "Provider Smoke must apply the repository-controlled pinned SDK compatibility patch."
Assert-Condition ($requiredGates -match 'cmake -S native/Providers/AjaNtv2') "Provider Smoke must configure the actual AJA adapter target."
Assert-Condition ($requiredGates -match 'cmake --build \$nativeBuild --config Release') "Provider Smoke must compile the native adapter."
Assert-Condition ($requiredGates -match 'Expected exactly one rtaime_media_io\.dll') "Provider Smoke must verify the native DLL result."

Assert-Condition ($documentation -match 'Physical hardware qualification state[\s\S]*UNVERIFIED') "Documentation must retain UNVERIFIED until real hardware evidence exists."
Assert-Condition ($documentation -match 'PinnedHostLease') "Documentation must identify the AP-33 qualified transfer-mode target."
Assert-Condition ($documentation -match 'DeviceDirectLease.*not advertised') "Documentation must not overstate direct-device transfer support."
Assert-Condition ($documentation -match 'Deferred to Timing/Reference/Latency/Soak Qualification') "Timing/reference qualification must remain deferred to the dedicated timing/reference/latency/soak qualification scope."

$managedContractFiles = @(
	(Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/MediaIoContracts.cs"),
	(Join-Path $repositoryRoot "src/Contracts/rtaime.Provider.Contracts/MediaIoProviderContracts.cs")
)
foreach ($path in $managedContractFiles) {
	$source = Get-Content -LiteralPath $path -Raw
	Assert-Condition ($source -notmatch 'DllImport|CNTV2|libajantv2|AJA\.|NTV2_') "Stable managed contracts must remain free of native/vendor bindings: '$path'."
}

Write-Host "Media I/O vertical slice policy verification PASS"
Write-Host "Topology: SDI1 + SDI2 capture -> Program SDI output; bounded pinned-host transfer"
Write-Host "Runtime selection: explicit virtual/native; native failure is fail-closed"
Write-Host "Native compile evidence: Provider Smoke builds the repository-pinned libajantv2 adapter with a fail-closed MSVC compatibility patch"
Write-Host "Physical hardware evidence state: UNVERIFIED until dedicated self-hosted qualification PASSES"
