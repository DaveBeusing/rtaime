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
$configPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeNetworkOutputConfiguration.cs"
$providerPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Srt/SrtNetworkOutputProvider.cs"
$transportPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Srt/NativeSrtTransport.cs"
$encoderPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Srt/WindowsMediaFoundationSrtEncoder.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OutputRoutingHealthViewModel.cs"
$testsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/NetworkOutputFoundationTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/NetworkOutputStreaming.md"

foreach ($path in @(
	$contractsPath,
	$bridgePath,
	$configPath,
	$providerPath,
	$transportPath,
	$encoderPath,
	$operatorPath,
	$testsPath,
	$documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required network-output artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$bridge = Get-Content -LiteralPath $bridgePath -Raw
$config = Get-Content -LiteralPath $configPath -Raw
$provider = Get-Content -LiteralPath $providerPath -Raw
$transport = Get-Content -LiteralPath $transportPath -Raw
$encoder = Get-Content -LiteralPath $encoderPath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$tests = Get-Content -LiteralPath $testsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($contracts -match 'NetworkOutputCapabilityKinds' -and $contracts -match '"network\.output"') "Network output must remain a provider capability."
Assert-Condition ($contracts -match 'NetworkOutputProtocolFamily' -and $contracts -match 'Srt\s*=\s*1') "The first network-output protocol must remain explicit SRT."
Assert-Condition ($contracts -match 'NetworkOutputVideoCodec' -and $contracts -match 'H264\s*=\s*1') "The reference video codec must remain explicit H.264."
Assert-Condition ($contracts -match 'NetworkOutputAudioCodec' -and $contracts -match 'AacLc\s*=\s*1') "The reference audio codec must remain explicit AAC-LC."
Assert-Condition ($contracts -match 'QueueCapacity' -and $contracts -match 'queueCapacity is < 1 or > 16') "Network-output queue capacity must remain explicitly bounded."
Assert-Condition ($contracts -match 'PassphraseEnvironmentVariable') "Network-output secrets must be referenced rather than embedded in the contract."
Assert-Condition ($contracts -notmatch 'Password\s*\{') "Network-output contracts must not add durable password fields."

Assert-Condition ($bridge -match 'RuntimeNetworkOutputBridge' -and $bridge -match 'GpuReadbackLease') "Runtime must own the network-output handoff from committed media."
Assert-Condition ($bridge -notmatch 'ControlHost') "Runtime network-output execution must not depend on ControlHost implementation."
Assert-Condition ($bridge -notmatch 'NamedPipe|Grpc') "Bulk network-output media must not move through management IPC transports."

Assert-Condition ($provider -match 'Queue<NetworkOutputProgramSample>' -and $provider -match 'drop_oldest') "The SRT provider must retain bounded live backpressure behavior."
Assert-Condition ($provider -match 'ReconnectInitialDelayMilliseconds' -and $provider -match 'ReconnectMaximumDelayMilliseconds') "The SRT provider must retain bounded reconnect policy."
Assert-Condition ($provider -match 'network\.output\.transport_failed' -and $provider -match 'network\.output\.reconnect_exhausted') "Provider failures must remain explicit health evidence."
Assert-Condition ($provider -notmatch 'ProductionId|ProductionRevision|ControlCommand') "The SRT provider must not acquire Production authority."

Assert-Condition ($transport -match 'MinimumSrtVersion' -and $transport -match '0x010507') "The native SRT runtime baseline must remain pinned to 1.5.7 or newer."
Assert-Condition ($transport -match 'NetworkOutputConnectionMode\.Caller') "The first qualified SRT transport must remain explicit about caller-mode support."
Assert-Condition ($transport -match 'SrtoPassphrase') "SRT encryption/passphrase support must remain provider-local."

Assert-Condition ($encoder -match 'MfVideoFormatH264' -and $encoder -match 'MfAudioFormatAac') "The reference encoder must retain H.264/AAC output."
Assert-Condition ($encoder -match 'ToHundredNanoseconds' -and $encoder -match 'PresentationTimestamp') "Streaming timestamps must derive from Runtime media timing."
Assert-Condition ($encoder -notmatch 'DateTimeOffset\.UtcNow.*Presentation|DateTime\.UtcNow.*Presentation') "Media timestamps must not be generated from wall-clock time."

Assert-Condition ($operator -match 'Runtime/provider evidence' -and $operator -match 'Connected') "Operator streaming state must be derived from Runtime/provider evidence."
Assert-Condition ($tests -match 'Saturated_queue_drops_oldest_complete_sample' -and $tests -match 'Transport_failure_is_observational') "Backpressure and failure-isolation regression coverage is required."
Assert-Condition ($documentation -match 'ControlHost remains Production Authority' -and $documentation -match 'Network output must never indefinitely block Program execution') "Network-output documentation must retain authority and continuity boundaries."

Write-Host "Network output and SRT streaming policy PASS"
