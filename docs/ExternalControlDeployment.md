<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Secure External Control

rtaime exposes one optional external control boundary on ControlHost for remote operator clients, automation systems, service tools and ecosystem adapters. The standalone `rtaime.IntegrationHost` consumes this boundary for OSC, MIDI, GPIO/GPI reference control and Companion-style integrations.

The external boundary does not create another Production Authority and does not make RuntimeHost or AIHost remotely addressable.

```text
remote client / automation
  -> rtaime.Client
  -> gRPC over TLS
  -> ControlHost
  -> existing authoritative Control semantics
  -> local RuntimeHost transport
  -> RuntimeHost execution
```

Local Named Pipe control remains the default local transport. External control is disabled by default.

## Authority and transport boundary

The external gRPC service deliberately maps onto the same ControlHost request dispatch used by local control. It does not duplicate Production validation, optimistic revision checks, Runtime prepare/commit sequencing, request idempotency or snapshot composition.

The following invariants apply:

- ControlHost remains the only externally reachable Production Authority.
- RuntimeHost remains execution owner and is not exposed through the external API.
- AIHost remains governed inference and is not exposed through the external API.
- connection state and authenticated transport identity do not become Production identity;
- transport connect/disconnect does not advance Production Revision;
- raw video, audio and GPU frame payloads do not cross the external management boundary;
- local Named Pipe operation remains available when external control is enabled.

## Default state

The default configuration is:

| Setting | Default |
| --- | --- |
| Enabled | `false` |
| Required for ControlHost startup | `false` |
| Bind address | `127.0.0.1` |
| Port | `55101` |
| TLS | required whenever enabled |
| mTLS | optional |
| Maximum connections | `32` |
| Maximum in-flight operations per client | `8` |
| Requests per second per client | `30` |
| Burst | `60` |
| Maximum request | `1 MiB` |
| Maximum response | `4 MiB` |
| Unary request timeout | `5 s` |
| Shutdown drain timeout | `5 s` |

Remote binding must be configured explicitly. Public Internet exposure is not a default or a qualified deployment profile.

## Configuration

ControlHost reads command-line values first, then environment variables.

External-control environment variables are:

- `RTAIME_EXTERNAL_CONTROL_ENABLED`
- `RTAIME_EXTERNAL_CONTROL_REQUIRED`
- `RTAIME_EXTERNAL_CONTROL_BIND_ADDRESS`
- `RTAIME_EXTERNAL_CONTROL_PORT`
- `RTAIME_EXTERNAL_CONTROL_CERTIFICATE_PATH`
- `RTAIME_EXTERNAL_CONTROL_CERTIFICATE_KEY_PATH`
- `RTAIME_EXTERNAL_CONTROL_CERTIFICATE_PASSWORD_ENV`
- `RTAIME_EXTERNAL_CONTROL_CERTIFICATE_THUMBPRINT`
- `RTAIME_EXTERNAL_CONTROL_REQUIRE_MTLS`
- `RTAIME_EXTERNAL_CONTROL_IDENTITIES`
- `RTAIME_EXTERNAL_CONTROL_MAX_CONNECTIONS`
- `RTAIME_EXTERNAL_CONTROL_MAX_INFLIGHT`
- `RTAIME_EXTERNAL_CONTROL_REQUESTS_PER_SECOND`
- `RTAIME_EXTERNAL_CONTROL_REQUEST_BURST`
- `RTAIME_EXTERNAL_CONTROL_MAX_REQUEST_BYTES`
- `RTAIME_EXTERNAL_CONTROL_MAX_RESPONSE_BYTES`
- `RTAIME_EXTERNAL_CONTROL_REQUEST_TIMEOUT_MS`
- `RTAIME_EXTERNAL_CONTROL_SHUTDOWN_TIMEOUT_MS`

Equivalent command-line keys use the lower-case `--external-...` form implemented by ControlHost configuration.

When enabled, exactly one server-certificate source must be configured.

## TLS server identity

The external listener is HTTP/2 over HTTPS and permits TLS 1.2 and TLS 1.3 as supported by the platform.

A server certificate can be loaded from:

1. a PFX/PKCS#12 file;
2. a PEM certificate plus PEM private-key file;
3. the Windows certificate store by certificate thumbprint.

For a PFX file, keep the password outside source control and point `RTAIME_EXTERNAL_CONTROL_CERTIFICATE_PASSWORD_ENV` to the environment variable that holds the password.

For Windows certificate-store loading, ControlHost searches the Personal certificate store and requires an accessible private key.

ControlHost rejects a server certificate that is outside its validity period or does not expose a usable private key.

Production private keys, certificate passwords and bearer tokens must never be committed to the repository or embedded in release artifacts.

## Client authentication

Each allowed external client maps to a stable product-level identity and role.

`RTAIME_EXTERNAL_CONTROL_IDENTITIES` is a bounded JSON array of identity descriptors. A descriptor contains:

- `clientId`;
- `role`;
- optional `certificateThumbprint`;
- optional `tokenEnvironmentVariable`.

The descriptor may name the environment variable containing a bearer token, but the token itself must not be stored in source-controlled configuration.

When mTLS is required, the TLS handshake requires a client certificate and the certificate thumbprint must match an allowed identity. Certificate validity is checked before the identity is accepted.

When mTLS is not required, a configured certificate identity can still authenticate with a presented client certificate. A token-bound identity can authenticate with a bearer token loaded from the configured environment variable. Token comparison is constant-time after hashing.

## Authorization

Authorization is enforced in ControlHost after authentication.

The current roles are:

| Role | Current capability |
| --- | --- |
| `Observer` | ping, authoritative snapshot and supported read-only catalogue/deck/show/rundown queries |
| `Operator` | Observer capabilities plus normal production-control mutations |
| `Administrator` | superset role reserved for administrative capabilities where they are explicitly implemented |

UI visibility is not an authorization control. An authenticated client without the required role receives an explicit denial from the server.

## Client trust

`GrpcOperatorControlTransport` requires an HTTPS endpoint.

Supported server trust modes are:

- `System` — normal platform certificate-chain and hostname validation;
- `PinnedServerCertificate` — explicit certificate-thumbprint pinning plus validity-period validation;
- `TestOnlyInsecure` — test-only certificate bypass and not an accepted production deployment mode.

The client can also load a PFX client certificate or a PEM certificate/private-key pair for mTLS.

## Client SDK discovery

`GrpcOperatorControlTransport.DiscoverAsync` performs the versioned `ping` operation and returns the active API version, ControlHost instance identity, remote StateVersion, readiness projection, authenticated client identity/role and the advertised external-control capabilities.

This discovery surface is intentionally specific to the gRPC transport. Production-control operations continue to use the shared `IOperatorControlTransport` abstraction so Operator production logic does not branch on local versus external transport.

## API and compatibility

The first external API is `rtaime.external_control.v1`, with API version `1.0`.

Every request carries API version, request identity, correlation identity, declared client name and declared client version.

Production mutations additionally retain the existing Control contract version, stable command identity, Production identity and expected Production Revision.

Unsupported API versions fail closed. The transport does not silently downgrade to a different command model.

## Idempotency and optimistic concurrency

External mutations reuse the same stable request/command identity and ControlHost request-result cache as local control.

If a network outcome is uncertain, the client may retry once with the same request identity. A duplicate request with the same canonical content receives the cached result. Reusing the same request identity with different content fails closed.

Expected Production Revision conflicts remain Control-layer concurrency conflicts. The external transport does not bypass or replace the authoritative revision checks.

The idempotency cache is process-local. After ControlHost replacement, clients must resynchronize from a full snapshot before continuing mutations.

## State synchronization

A full authoritative snapshot establishes `HostInstanceId`, `StateVersion` and current Production/operational state.

The external state subscription publishes only bounded state-version notifications. Each notification contains the host identity, the state version it is based on, the resulting state version and a sequence number.

A HostInstanceId replacement, stream loss or state-version gap requires a new full snapshot. Stream notifications are not a second authority and do not replace snapshot-based synchronization.

## Resource protection

ControlHost applies explicit limits to the remote surface:

- maximum concurrent connections;
- maximum in-flight operations per authenticated client;
- per-client token-bucket request rate and burst;
- gRPC request and response message size;
- server-side unary request deadline;
- bounded identity configuration;
- bounded audit history;
- bounded state-subscription cadence.

A slow, disconnected or rate-limited remote client must not block ControlHost mutation progress or Runtime Program continuity.

## Lifecycle and readiness

The external server is composed with ControlHost but is optional by default.

When external control is disabled, local ControlHost startup and Named Pipe control operate normally.

When external control is enabled but not marked required, a failure to configure or start the external listener is recorded as a failed external-control state without converting the optional surface into Production Authority.

When `RTAIME_EXTERNAL_CONTROL_REQUIRED=true`, failure to establish the configured secure listener is a ControlHost startup failure.

The external-control lifecycle exposes explicit `Disabled`, `Configured`, `Active`, `Degraded`, `Failed`, `Unverified` and `Stopped` states. Shutdown uses a bounded server drain timeout.

## Audit and diagnostics

The server keeps a bounded in-process audit record for security-relevant control-boundary events, including authentication failures, authorization denials and request outcomes.

Audit records carry bounded identity/role/operation/outcome information. Credentials, bearer tokens, certificate passwords and private-key material are not recorded.

High-rate media-frame logging is not part of this boundary.

## Qualified software behavior and remaining boundaries

Repository qualification covers the implemented software boundary, including shared ControlHost authority, local Named Pipe coexistence, TLS transport, mTLS authentication, authorization, idempotent retry semantics, state resynchronization, message-size limits and rate limiting.

The following remain outside the claims of this implementation unless separately qualified:

- direct public-Internet deployment;
- enterprise PKI enrollment, rotation and revocation operations;
- OAuth/OIDC or cloud identity-provider integration;
- cluster discovery or distributed Production Authority;
- NMOS;
- OSC, MIDI, GPIO or Companion adapters;
- WAN latency/performance qualification;
- remote raw-media or GPU-frame transport.

Those capabilities must not be inferred from a green software CI run.


## Production integration gateway

For OSC, MIDI, GPIO/GPI reference control and Companion-style HTTP/WebSocket integration, see [ProductionIntegrationGateway.md](ProductionIntegrationGateway.md). IntegrationHost remains an external client of this secure boundary; it does not move Production Authority out of ControlHost.
