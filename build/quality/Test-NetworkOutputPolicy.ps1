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

$contractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Provider.Contracts/NetworkOutput.cs"
$bridgePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeNetworkOutputBridge.cs"
$registryPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeNetworkOutputProviderRegistry.cs"
$configPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeNetworkOutputConfiguration.cs"
$srtProviderPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Srt/SrtNetworkOutputProvider.cs"
$transportPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Srt/NativeSrtTransport.cs"
$encoderPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Srt/WindowsMediaFoundationSrtEncoder.cs"
$ndiProviderPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Ndi/NdiNetworkOutputProvider.cs"
$ndiRuntimePath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Ndi/NativeNdiRuntime.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OutputRoutingHealthViewModel.cs"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/NetworkOutputFoundationTests.cs"
$configTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/NetworkOutputConfigurationIntegrationTests.cs"
$architectureTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Architecture/NetworkOutputArchitectureTests.cs"
$bundlePolicyPath = Join-Path $repositoryRoot "build/release/offline-bundle-policy.json"
$bundleVerifierPath = Join-Path $repositoryRoot "build/release/Test-OfflineReleaseBundle.ps1"
$preflightPath = Join-Path $repositoryRoot "build/release/Invoke-OfflinePreflight.ps1"
$documentationPath = Join-Path $repositoryRoot "docs/NetworkOutputStreaming.md"
$dependencyDocumentationPath = Join-Path $repositoryRoot "docs/NdiRuntimeDependency.md"

foreach ($path in @(
	$contractsPath,
	$bridgePath,
	$registryPath,
	$configPath,
	$srtProviderPath,
	$transportPath,
	$encoderPath,
	$ndiProviderPath,
	$ndiRuntimePath,
	$operatorPath,
	$unitTestsPath,
	$configTestsPath,
	$architectureTestsPath,
	$bundlePolicyPath,
	$bundleVerifierPath,
	$preflightPath,
	$documentationPath,
	$dependencyDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required network-output artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$registry = Get-Content -LiteralPath $registryPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw
$srtProvider = Get-Content -LiteralPath $srtProviderPath -Raw
$transport = Get-Content -LiteralPath $transportPath -Raw
$encoder = Get-Content -LiteralPath $encoderPath -Raw
$ndiProvider = Get-Content -LiteralPath $ndiProviderPath -Raw
$ndiRuntime = Get-Content -LiteralPath $ndiRuntimePath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$unitTests = Get-Content -LiteralPath $unitTestsPath -Raw
$configTests = Get-Content -LiteralPath $configTestsPath -Raw
$architectureTests = Get-Content -LiteralPath $architectureTestsPath -Raw
$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
$bundleVerifier = Get-Content -LiteralPath $bundleVerifierPath -Raw
$preflight = Get-Content -LiteralPath $preflightPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$dependencyDocumentation = Get-Content -LiteralPath $dependencyDocumentationPath -Raw

Assert-Condition ($contracts -match 'NetworkOutputCapabilityKinds' -and $contracts -match '"network\.output"') "Network output must remain a provider capability."
Assert-Condition ($contracts -match 'Srt\s*=\s*1' -and $contracts -match 'Ndi\s*=\s*2') "SRT and NDI protocol families must remain explicit stable contract values."
Assert-Condition ($contracts -match 'SrtNetworkOutputSettings' -and $contracts -match 'NdiNetworkOutputSettings') "Network output configuration must remain typed by provider protocol."
Assert-Condition ($contracts -match 'QueueCapacity' -and $contracts -match 'queueCapacity is < 1 or > 16') "Network-output queue capacity must remain explicitly bounded."
Assert-Condition ($contracts -match 'PassphraseEnvironmentVariable') "SRT secrets must remain referenced rather than embedded."
Assert-Condition ($contracts -notmatch 'Processing\.NDI|NDIlib_|NativeLibrary|NdiVideoFrame') "Stable provider contracts must not leak NDI SDK/native ABI types."
Assert-Condition ($contracts -notmatch 'Password\s*\{') "Network-output contracts must not add durable password fields."

Assert-Condition ($bridge -match 'IRuntimeNetworkOutputProviderRegistry' -and $bridge -match 'INetworkOutputSession') "Runtime bridge must compose network providers through protocol-neutral abstractions."
Assert-Condition ($bridge -match 'GpuReadbackLease') "Runtime must own the network-output handoff from committed media."
Assert-Condition ($bridge -notmatch 'SrtNetworkOutputSession|NdiNetworkOutputSession|rtaime\.Provider\.Srt|rtaime\.Provider\.Ndi') "Runtime bridge must not hard-code SRT or NDI session implementations."
Assert-Condition ($bridge -notmatch 'ControlHost') "Runtime network-output execution must not depend on ControlHost implementation."
Assert-Condition ($bridge -notmatch 'NamedPipe|Grpc') "Bulk network-output media must not move through management IPC transports."
Assert-Condition ($registry -match 'NetworkOutputProtocolFamily\.Srt' -and $registry -match 'NetworkOutputProtocolFamily\.Ndi') "Runtime provider registry must explicitly route both SRT and NDI."

Assert-Condition ($config -match 'ParseProtocol' -and $config -match '"srt"' -and $config -match '"ndi"') "Runtime configuration must parse explicit SRT and NDI protocols while preserving the SRT default."
Assert-Condition ($config -match 'HasSrtOnlyFields' -and $config -match 'must not define SRT') "NDI configuration must reject mixed SRT-only fields."
Assert-Condition ($config -match 'must not define NDI sourceName') "SRT configuration must reject NDI-only fields."

Assert-Condition ($srtProvider -match 'INetworkOutputSession' -and $srtProvider -match 'Queue<NetworkOutputProgramSample>' -and $srtProvider -match 'drop_oldest') "SRT provider must retain the common bounded session contract and live backpressure behavior."
Assert-Condition ($srtProvider -match 'ReconnectInitialDelayMilliseconds' -and $srtProvider -match 'ReconnectMaximumDelayMilliseconds') "SRT provider must retain bounded reconnect policy."
Assert-Condition ($srtProvider -match 'network\.output\.transport_failed' -and $srtProvider -match 'network\.output\.reconnect_exhausted') "SRT provider failures must remain explicit health evidence."
Assert-Condition ($srtProvider -notmatch 'ProductionId|ProductionRevision|ControlCommand') "SRT provider must not acquire Production authority."

Assert-Condition ($transport -match 'MinimumSrtVersion' -and $transport -match '0x010507') "Native SRT runtime baseline must remain pinned to 1.5.7 or newer."
Assert-Condition ($transport -match 'NetworkOutputConnectionMode\.Caller') "The first qualified SRT transport must remain explicit about caller-mode support."
Assert-Condition ($transport -match 'SrtoPassphrase') "SRT encryption/passphrase support must remain provider-local."
Assert-Condition ($encoder -match 'MfVideoFormatH264' -and $encoder -match 'MfAudioFormatAac') "SRT reference encoder must retain H.264/AAC output."
Assert-Condition ($encoder -match 'ToHundredNanoseconds' -and $encoder -match 'PresentationTimestamp') "SRT timestamps must derive from Runtime media timing."

Assert-Condition ($ndiProvider -match 'INetworkOutputSession' -and $ndiProvider -match 'Queue<NetworkOutputProgramSample>' -and $ndiProvider -match 'drop_oldest') "NDI provider must use the common bounded session model and drop-oldest backpressure."
Assert-Condition ($ndiProvider -match 'Hd1080p50Rgba8' -and $ndiProvider -match 'Hd1080p59_94Rgba8') "NDI software baseline must remain bounded to qualified 1080p50/59.94 formats."
Assert-Condition ($ndiProvider -match 'network\.output\.ndi_runtime_unavailable' -and $ndiProvider -match 'NetworkOutputLifecycleState\.Faulted') "Missing or incompatible NDI runtime must fail closed with explicit evidence."
Assert-Condition ($ndiProvider -notmatch 'ProductionId|ProductionRevision|ControlCommand') "NDI provider must not acquire Production authority."
Assert-Condition ($ndiRuntime -match 'RTAIME_NDI_LIBRARY_PATH' -and $ndiRuntime -match 'NDI_RUNTIME_DIR_V6' -and $ndiRuntime -match 'NDI_RUNTIME_DIR_V5') "NDI runtime discovery must be explicit."
Assert-Condition ($ndiRuntime -match 'NativeLibrary\.TryLoad' -and $ndiRuntime -match 'NDIlib_send_send_video_v2' -and $ndiRuntime -match 'NDIlib_send_send_audio_v3') "NDI native interop must remain isolated in the provider adapter boundary."
Assert-Condition ($ndiRuntime -match 'InterleavedToPlanar' -and $ndiRuntime -match 'ToHundredNanoseconds') "NDI media conversion must preserve Runtime audio layout and media timing."

$ndiRuntimeRequirements = @($bundlePolicy.externalProviderRuntimes | Where-Object { [string]$_.provider -eq 'NDI' })
Assert-Condition ($ndiRuntimeRequirements.Count -eq 1) "Offline policy must declare exactly one NDI runtime prerequisite."
Assert-Condition ($ndiRuntimeRequirements[0].bundled -eq $false -and [string]$ndiRuntimeRequirements[0].redistributionStatus -eq 'EXTERNAL_RUNTIME_NOT_BUNDLED') "NDI runtime must not be bundled without an approved redistribution decision."
Assert-Condition ([string]$ndiRuntimeRequirements[0].interoperabilityStatus -eq 'UNVERIFIED') "NDI interoperability must remain UNVERIFIED."
Assert-Condition ($bundleVerifier -match 'must not carry the external NDI runtime binary') "Offline bundle verification must reject accidentally bundled NDI runtime binaries."
Assert-Condition ($preflight -match 'RequireNdiRuntime' -and $preflight -match 'external NDI runtime was not found') "Offline preflight must support fail-closed NDI runtime qualification."

Assert-Condition ($operator -match 'NDI High Bandwidth' -and $operator -match '48 kHz Float32') "Operator must present NDI semantics without fabricating SRT bitrate/codec evidence."
Assert-Condition ($unitTests -match 'Ndi_saturated_queue_drops_oldest' -and $unitTests -match 'Missing_NDI_runtime_fails_closed' -and $unitTests -match 'Ndi_send_failure_recovers') "NDI backpressure, runtime-missing and recovery regression coverage is required."
Assert-Condition ($unitTests -match 'Ndi_submission_preserves_Runtime_audio_cadence') "NDI qualification must protect Runtime audio cadence."
Assert-Condition ($configTests -match 'Legacy_SRT_descriptor_without_protocol_remains_compatible' -and $configTests -match 'NDI_descriptor_rejects_mixed_SRT_fields') "Runtime configuration qualification must protect SRT compatibility and mixed-field rejection."
Assert-Condition ($architectureTests -match 'Runtime_bridge_uses_provider_registry' -and $architectureTests -match 'do_not_leak_NDI_runtime_types') "Architecture tests must protect provider-neutral Runtime and stable contracts."
Assert-Condition ($documentation -match 'ControlHost remains Production Authority' -and $documentation -match 'Network output must never indefinitely block Program execution') "Network-output documentation must retain authority and continuity boundaries."
Assert-Condition ($dependencyDocumentation -match 'not bundled' -and $dependencyDocumentation -match 'UNVERIFIED') "NDI dependency documentation must retain redistribution and interoperability boundaries."

Write-Host "Network output SRT/NDI provider policy PASS"
Write-Host "Runtime provider selection: protocol-neutral registry"
Write-Host "SRT compatibility: retained"
Write-Host "NDI runtime: external, fail-closed, interoperability UNVERIFIED"
