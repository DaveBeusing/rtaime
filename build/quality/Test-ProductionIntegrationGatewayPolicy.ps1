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

# NMOS v1.3/v1.2 remains an outer optional network adapter, not a second Production Authority.
$nmosDiscoveryPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/NmosRegistryDiscovery.cs"
$nmosAdapterPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/NmosIntegrationAdapter.cs"
$nmosRegistrationPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/NmosRegistrationClient.cs"
$nmosResourcesPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/NmosVirtualResources.cs"
$nmosConnectionsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/NmosVirtualConnections.cs"
$nmosTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/NmosDiscoveryAndConnectionTests.cs"
$nmosDocumentationPath = Join-Path $repositoryRoot "docs/NmosDiscoveryAndConnectionManagement.md"

foreach ($path in @($nmosDiscoveryPath, $nmosAdapterPath, $nmosRegistrationPath, $nmosResourcesPath, $nmosConnectionsPath, $nmosTestsPath, $nmosDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "NMOS evidence artifact is missing: $path"
}
$nmosDiscovery = Get-Content -LiteralPath $nmosDiscoveryPath -Raw
$nmosAdapter = Get-Content -LiteralPath $nmosAdapterPath -Raw
$nmosRegistration = Get-Content -LiteralPath $nmosRegistrationPath -Raw
$nmosResources = Get-Content -LiteralPath $nmosResourcesPath -Raw
$nmosConnections = Get-Content -LiteralPath $nmosConnectionsPath -Raw
$nmosTests = Get-Content -LiteralPath $nmosTestsPath -Raw
$nmosDocumentation = Get-Content -LiteralPath $nmosDocumentationPath -Raw

Assert-Condition ($config.Contains("NmosOperatingMode.Observer") -and $config.Contains("!testMode || !dryRun") -and $config.Contains("Virtual NMOS APIs are restricted to IPv4 loopback")) "NMOS must remain observer-by-default with loopback-only virtual admission."
Assert-Condition ($config.Contains('/x-nmos/query/v1.3/') -and $config.Contains('/x-nmos/registration/v1.3/')) "NMOS must pin IS-04 v1.3 Query and Registration URLs."
Assert-Condition ($gateway.Contains("IntegrationAdapterKind.Nmos => new NmosIntegrationAdapter(adapter, gateway)")) "The existing gateway must own NMOS adapter composition."
Assert-Condition ($nmosDiscovery.Contains('SpecificationRelease = "v1.3.3"') -and $nmosDiscovery.Contains("ValidateParents") -and $nmosDiscovery.Contains("duplicate resource identities")) "IS-04 Query must keep version, bounded complete snapshots and duplicate/parent rejection."
Assert-Condition ($nmosRegistration.Contains('health/nodes/') -and $nmosRegistration.Contains('UnregistrationOrder()') -and $nmosRegistration.Contains('HttpStatusCode.NotFound')) "Registration must heartbeat, re-register after expiry and delete child-first."
Assert-Condition ($nmosResources.Contains('VIRTUAL_NO_MEDIA') -and $nmosResources.Contains('flow_id = (string?)null') -and $nmosResources.Contains('urn:x-nmos:transport:virtual')) "Virtual resources must never advertise actual RTP Flow/SDP media."
Assert-Condition ($nmosConnections.Contains('master_enable') -and $nmosConnections.Contains('activate_immediate') -and $nmosConnections.Contains('Validate(sender, patch)') -and $nmosConnections.Contains('StageBulk')) "IS-05 must validate disabled-only staging and all-or-none bulk requests."
Assert-Condition ($nmosAdapter.Contains('HasAdmittedLocalRtpProviders') -eq $false -and $nmosAdapter.Contains('NmosRegistrationClient') -and $nmosAdapter.Contains('RefreshDiscoveryAsync')) "NMOS must observe provider-less registries and not assert RTP admission."
Assert-Condition ($nmosAdapter -notmatch 'DispatchExternalAsync|SelectPreviewAsync|CutProgramAsync|RouteOutputRoleAsync|ActivateSceneAsync|ProductionCommand') "NMOS must never dispatch Production mutations directly."
Assert-Condition ($schema.properties.adapters.items.properties.kind.enum -contains 'nmos' -and
	$schema.properties.adapters.items.properties.nmos.properties.mode.enum -contains 'virtualInterop') "The NMOS schema must remain additive with an explicit virtual test mode."
Assert-Condition ($nmosDocumentation.Contains('UNVERIFIED') -and $nmosDocumentation.Contains('ST 2110') -and $nmosDocumentation.Contains('dryRun=true')) "NMOS interoperability documentation must retain explicit hardware/security/production boundaries."
foreach ($name in @(
	"Registry_discovery_validates_full_parent_chain_without_claiming_local_rtp",
	"Registry_outage_invalidates_previous_topology_and_recovery_uses_new_generation",
	"Registration_orders_resources_recovers_404_heartbeat_and_deletes_children_first",
	"Is05_staging_rejects_sdp_stream_activation_scheduled_time_and_unknown_id",
	"Is05_disabled_immediate_staging_and_bulk_are_atomic_without_program_route",
	"Virtual_node_rest_serves_stable_identity_and_rejects_activation_without_triggers"
)) {
	Assert-Condition ($nmosTests.Contains($name)) "Missing NMOS negative qualification: $name"
}
Write-Host "NMOS v1.3/v1.2 observer and virtual test policy PASS; production RTP/SDP authority UNVERIFIED"

Write-Host "Production integration gateway policy PASS"
Write-Host "Authority boundary: IntegrationHost -> rtaime.Client -> ControlHost"
Write-Host "Implemented adapters: OSC, Windows MIDI, virtual GPIO/GPI, Companion HTTP/WebSocket, read-only NMOS discovery and virtual interop"
Write-Host "NMOS external conformance and hardware ST 2110: UNVERIFIED"
