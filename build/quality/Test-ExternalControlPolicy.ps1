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

$schemaPath = Join-Path $repositoryRoot "schemas/external-control/v1/external_control.proto"
$configSchemaPath = Join-Path $repositoryRoot "schemas/external-control/v1/external_control_config.schema.json"
$serverPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ExternalControlServer.cs"
$processPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ControlHostProcess.cs"
$dispatcherPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ControlHostIpcServer.cs"
$clientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/GrpcOperatorControlTransport.cs"
$localClientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/NamedPipeOperatorControlTransport.cs"
$architectureTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Architecture/ExternalControlArchitectureTests.cs"
$integrationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ExternalControlIntegrationTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/ExternalControlDeployment.md"
$ipcDocumentationPath = Join-Path $repositoryRoot "docs/ProductionIpcRemoteApi.md"
$securityPath = Join-Path $repositoryRoot "SECURITY.md"

foreach ($path in @(
	$schemaPath,
	$configSchemaPath,
	$serverPath,
	$processPath,
	$dispatcherPath,
	$clientPath,
	$localClientPath,
	$architectureTestsPath,
	$integrationTestsPath,
	$documentationPath,
	$ipcDocumentationPath,
	$securityPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required secure external-control artifact is missing: '$path'."
}

$schema = Get-Content -LiteralPath $schemaPath -Raw
$configSchema = Get-Content -LiteralPath $configSchemaPath -Raw
$server = Get-Content -LiteralPath $serverPath -Raw
$process = Get-Content -LiteralPath $processPath -Raw
$dispatcher = Get-Content -LiteralPath $dispatcherPath -Raw
$client = Get-Content -LiteralPath $clientPath -Raw
$localClient = Get-Content -LiteralPath $localClientPath -Raw
$architectureTests = Get-Content -LiteralPath $architectureTestsPath -Raw
$integrationTests = Get-Content -LiteralPath $integrationTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$ipcDocumentation = Get-Content -LiteralPath $ipcDocumentationPath -Raw
$security = Get-Content -LiteralPath $securityPath -Raw

Assert-Condition ($schema -match 'service ExternalControl' -and $schema -match 'rpc Execute' -and $schema -match 'rpc SubscribeState') "External control must expose one versioned gRPC service with request and state synchronization surfaces."
Assert-Condition ($schema -notmatch 'RuntimeHost|AIHost') "External API schema must not expose RuntimeHost or AIHost."
Assert-Condition ($schema -match 'request_id' -and $schema -match 'correlation_id' -and $schema -match 'host_instance_id' -and $schema -match 'state_version') "External API must preserve request/correlation and remote state identity."
Assert-Condition ($schema -match 'expected_revision' -and $schema -match 'command_id') "Production mutations must preserve optimistic concurrency and stable command identity."
Assert-Condition ($configSchema -match '"enabled"' -and $configSchema -match '"default": false' -and $configSchema -match '"bindAddress"' -and $configSchema -match '127\.0\.0\.1') "External configuration schema must preserve disabled and loopback secure defaults."
Assert-Condition ($configSchema -match '"Observer"' -and $configSchema -match '"Operator"' -and $configSchema -match '"Administrator"') "External configuration schema must define the supported role set."
Assert-Condition ($configSchema -match '"maxConcurrentConnections"' -and $configSchema -match '"requestsPerSecond"' -and $configSchema -match '"maxRequestBytes"' -and $configSchema -match '"requestTimeoutMilliseconds"') "External configuration schema must retain explicit resource and request-time bounds."

Assert-Condition ($server -match 'public bool Enabled \{ get; init; \}' -and $server -match 'BindAddress \{ get; init; \} = "127\.0\.0\.1"') "External control must remain disabled by default and bind to loopback when configured without an explicit address."
Assert-Condition ($server -match 'SslProtocols\.Tls12 \| SslProtocols\.Tls13') "External control must require modern TLS."
Assert-Condition ($server -match 'ClientCertificateMode\.RequireCertificate' -and $server -match 'RequireMutualTls') "External control must retain optional mTLS enforcement."
Assert-Condition ($server -match 'ValidateClientCertificate' -and $server -match 'certificate\.NotBefore' -and $server -match 'certificate\.NotAfter') "External client certificates must be explicitly validated."
Assert-Condition ($server -match 'ExternalControlRole\.Observer' -and $server -match 'ExternalControlRole\.Operator') "Server-side RBAC must preserve Observer and Operator capability separation."
Assert-Condition ($server -match 'MaxConcurrentConnections' -and $server -match 'MaxConcurrentOperationsPerClient' -and $server -match 'RequestsPerSecond' -and $server -match 'RequestBurst') "External control must retain explicit connection, in-flight and request-rate bounds."
Assert-Condition ($server -match 'MaxReceiveMessageSize = _options\.MaxRequestBytes' -and $server -match 'MaxSendMessageSize = _options\.MaxResponseBytes') "gRPC request and response sizes must remain bounded."
Assert-Condition ($server -match 'RequestTimeout' -and $server -match 'operationTimeout\.CancelAfter\(_options\.RequestTimeout\)' -and $server -match 'StatusCode\.DeadlineExceeded') "Unary external control execution must retain a server-side request deadline."
Assert-Condition ($server -match '_dispatcher\.DispatchExternalAsync') "External gRPC requests must terminate in the shared ControlHost dispatch seam."
Assert-Condition ($server -notmatch '"runtime\.' -and $server -notmatch '"ai\.') "External-control transport must not dispatch directly to RuntimeHost or AIHost."
Assert-Condition ($server -match '"authentication"' -and $server -match '"request-validation"' -and $server -match '"resource-limit"') "External security diagnostics must audit authentication, request validation and resource-limit outcomes."
Assert-Condition ($server -match 'Required && !Enabled' -and $server -match 'Required external control failed to start') "Required external control must fail closed and expose explicit failed lifecycle state."

Assert-Condition ($process -match 'await _ipcServer!\.StartAsync' -and $process -match '_externalControlServer\.StartAsync') "Local Named Pipe control must start independently before the optional external surface."
Assert-Condition ($process -match 'ExternalControl = ExternalControlServerOptions\.Load') "External endpoint configuration must be composed through ControlHost configuration."
Assert-Condition ($process -match 'if \(ExternalControl\.Required\)' -and $process -match 'ExternalControl\.Validate\(\)') "Optional external-control validation must remain isolated from mandatory local ControlHost configuration."
Assert-Condition ($dispatcher -match 'DispatchExternalAsync' -and $dispatcher -match 'ControlHost response envelope') "External dispatch must reuse ControlHost response and idempotency semantics."

Assert-Condition ($client -match 'class GrpcOperatorControlTransport : IOperatorControlTransport') "External client transport must conform to the existing Operator control abstraction."
Assert-Condition ($client -match 'Endpoint\.Scheme' -and $client -match 'Uri\.UriSchemeHttps') "External client configuration must reject non-HTTPS endpoints."
Assert-Condition ($client -match 'ExternalControlTrustMode\.System' -and $client -match 'PinnedServerCertificate' -and $client -match 'TestOnlyInsecure') "Client trust modes must keep system/pinned production trust distinct from test-only bypass."
Assert-Condition ($client -match 'RetryNetworkUncertaintyOnce' -and $client -match 'StatusCode\.Unavailable or StatusCode\.DeadlineExceeded') "External uncertain-outcome retry must remain explicitly bounded."
Assert-Condition ($client -match 'RequiresFullSnapshot' -and $client -match 'RemoteHostSessionChangedException') "External client must fail closed into snapshot resynchronization on host replacement."
Assert-Condition ($localClient -match 'class NamedPipeOperatorControlTransport : IOperatorControlTransport') "Local Named Pipe control must remain available through the same client abstraction."

Assert-Condition ($architectureTests -match 'External_control_schema_exposes_only_ControlHost_semantics') "Architecture regression must protect the external Production Authority boundary."
Assert-Condition ($architectureTests -match 'ControlHost_external_transport_does_not_reference_other_host_projects') "Architecture regression must prevent direct host-project coupling."
Assert-Condition ($integrationTests -match 'Grpc_control_reuses_ControlHost_authority_and_named_pipe_remains_available') "Integration qualification must prove shared ControlHost authority and local transport coexistence."
Assert-Condition ($integrationTests -match 'Observer_can_read_but_server_denies_production_mutation') "Integration qualification must prove server-side authorization."
Assert-Condition ($integrationTests -match 'Duplicate_external_mutation_is_idempotent_and_conflicting_request_id_fails_closed') "Integration qualification must prove external idempotency and conflicting request-id rejection."
Assert-Condition ($integrationTests -match 'Mutual_tls_authentication_accepts_pinned_client_certificate') "Integration qualification must exercise mTLS."
Assert-Condition ($integrationTests -match 'Pinned_server_trust_rejects_an_unexpected_certificate_identity' -and $integrationTests -match 'Mutual_tls_rejects_an_unmapped_client_certificate' -and $integrationTests -match 'Expired_required_server_certificate_fails_ControlHost_startup_closed') "Integration qualification must exercise server trust failure, untrusted mTLS client rejection and expired server-certificate failure."
Assert-Condition ($integrationTests -match 'State_version_gap_requires_full_resynchronization') "Integration qualification must exercise state-gap resynchronization."
Assert-Condition ($integrationTests -match 'Rate_limit_rejects_excess_requests_without_affecting_ControlHost_continuity') "Integration qualification must prove bounded rate rejection preserves ControlHost continuity."
Assert-Condition ($integrationTests -match 'Oversized_grpc_message_is_rejected_before_ControlHost_dispatch') "Integration qualification must reject oversized remote messages."

Assert-Condition ($documentation -match 'disabled by default' -and $documentation -match '127\.0\.0\.1' -and $documentation -match 'Public Internet exposure') "Deployment documentation must state secure defaults and the public-network qualification boundary."
Assert-Condition ($documentation -match 'RuntimeHost remains execution owner' -and $documentation -match 'AIHost remains governed inference') "Deployment documentation must preserve host authority boundaries."
Assert-Condition ($documentation -match 'Production private keys, certificate passwords and bearer tokens must never be committed') "Deployment documentation must define the secret boundary."
Assert-Condition ($documentation -match 'TestOnlyInsecure' -and $documentation -match 'not an accepted production deployment mode') "Deployment documentation must reject the test-only trust bypass for production."
Assert-Condition ($ipcDocumentation -match 'ControlHost') "IPC documentation must remain present for the shared local command semantics."
Assert-Condition ($security -match 'Security Policy') "Repository security policy must remain present."

Write-Host "Secure external control policy verification PASS"
Write-Host "Authority: ControlHost only"
Write-Host "Local control: Named Pipe remains available"
Write-Host "External transport: gRPC over TLS with explicit authentication, authorization and bounds"
