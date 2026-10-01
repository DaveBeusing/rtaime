# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$projectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/rtaime.IntegrationHost.csproj"
$configPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/IntegrationConfiguration.cs"
$gatewayPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/IntegrationGateway.cs"
$oscPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/OscIntegrationAdapter.cs"
$midiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/MidiIntegrationAdapter.cs"
$discretePath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/DiscreteIntegrationAdapter.cs"
$companionPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/CompanionIntegrationAdapter.cs"
$schemaPath = Join-Path $repositoryRoot "schemas/integration/v1/integration-gateway.schema.json"
$documentationPath = Join-Path $repositoryRoot "docs/ProductionIntegrationGateway.md"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/ProductionIntegrationGatewayTests.cs"

foreach ($path in @($projectPath, $configPath, $gatewayPath, $oscPath, $midiPath, $discretePath, $companionPath, $schemaPath, $documentationPath, $unitTestsPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Production integration gateway artifact is missing: '$path'."
}

$project = Get-Content -LiteralPath $projectPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw
$gateway = Get-Content -LiteralPath $gatewayPath -Raw
$osc = Get-Content -LiteralPath $oscPath -Raw
$midi = Get-Content -LiteralPath $midiPath -Raw
$discrete = Get-Content -LiteralPath $discretePath -Raw
$companion = Get-Content -LiteralPath $companionPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$unitTests = Get-Content -LiteralPath $unitTestsPath -Raw
$schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json

$references = [Regex]::Matches($project, '<ProjectReference Include="([^"]+)"')
Assert-Condition ($references.Count -eq 1) "IntegrationHost must have exactly one direct production project reference."
Assert-Condition ($references[0].Groups[1].Value -match 'rtaime\.Client\\rtaime\.Client\.csproj$') "IntegrationHost must reach production only through rtaime.Client."
Assert-Condition ($project -notmatch 'RuntimeHost|ControlHost|Provider\.') "IntegrationHost project references must not bypass rtaime.Client."

Assert-Condition ($gateway -match 'Channel\.CreateBounded' -and $gateway -match 'BoundedChannelFullMode\.Wait' -and $gateway -match 'TryWrite') "Gateway input must remain bounded and reject admission when capacity is exhausted."
Assert-Condition ($gateway -match 'OperatorControlClient' -and $gateway -match 'SynchronizeAsync') "Gateway command and feedback paths must use the client snapshot boundary."
Assert-Condition ($gateway -notmatch 'rtaime\.RuntimeHost|rtaime\.ControlHost|rtaime\.Provider') "Gateway implementation must not use production implementations directly."
Assert-Condition ($config -match 'MinimumIntervalMs' -and $config -match 'DebounceMs') "Gateway mappings must retain rate/debounce controls."
Assert-Condition ($config -match 'BearerTokenEnvironmentVariable' -and $config -notmatch 'public string\? BearerToken \{') "Configuration must reference secrets rather than embed bearer-token values."

Assert-Condition ($osc -match 'MaxPacketBytes' -and $osc -match 'SourceAllowlist' -and $osc -match 'TryDecode') "OSC adapter must validate packet size, source policy and malformed input."
Assert-Condition ($midi -match 'WinMmMidiBackend' -and $midi -match 'VirtualMidiBackend' -and $midi -match 'ReconnectLoopAsync') "MIDI adapter must retain Windows and deterministic virtual backends."
Assert-Condition ($discrete -match 'IDiscreteIoBackend' -and $discrete -match 'VirtualDiscreteIoBackend' -and $discrete -match 'ActiveLow') "Discrete control must retain provider seam, reference provider and polarity."
Assert-Condition ($companion -match 'CryptographicOperations\.FixedTimeEquals' -and $companion -match 'UseWebSockets' -and $companion -match 'Status429TooManyRequests') "Companion surface must authenticate, expose feedback and preserve bounded admission."

Assert-Condition ([string]$schema.properties.schemaVersion.const -eq "1.0") "Integration schema must retain version 1.0."
Assert-Condition ($documentation -match 'NMOS' -and $documentation -match 'UNVERIFIED') "NMOS must remain explicitly future/unverified."
Assert-Condition ($documentation -match 'rtaime\.Client' -and $documentation -match 'ControlHost') "Integration documentation must state the governed client/ControlHost boundary."

foreach ($test in @(
	"Osc_codec_round_trips",
	"Midi_adapter_maps_virtual_input",
	"Discrete_reference_provider_applies_input_and_output_polarity",
	"Feedback_resolution_is_snapshot_based",
	"Gateway_recovers_when_ControlHost_is_unavailable_during_startup",
	"Gateway_can_stop_restart_and_resnapshot",
	"Gateway_fans_one_snapshot_out_to_all_registered_adapters",
	"Gateway_drops_newest_inputs_when_the_bounded_queue_is_full",
	"Gateway_serializes_client_snapshot_refresh_with_production_commands",
	"Companion_surface_requires_bearer_authentication"
)) {
	Assert-Condition ($unitTests -match [Regex]::Escape($test)) "Production integration gateway regression coverage is missing '$test'."
}

Write-Host "Production integration gateway policy PASS"
Write-Host "Authority boundary: IntegrationHost -> rtaime.Client -> ControlHost"
Write-Host "Implemented adapters: OSC, Windows MIDI, virtual GPIO/GPI, Companion-compatible HTTP/WebSocket"
Write-Host "NMOS: UNVERIFIED"
