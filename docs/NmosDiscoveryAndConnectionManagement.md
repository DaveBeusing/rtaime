<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# NMOS IS-04 Discovery & IS-05 Connection Management

**Ownership:** IntegrationHost network adapter, no Production Authority.
**Version baseline:** [AMWA IS-04 v1.3.3](https://specs.amwa.tv/is-04/releases/v1.3.3/) (API v1.3), [AMWA IS-05 v1.2.0](https://specs.amwa.tv/is-05/releases/v1.2.0/) (API v1.2).
**Scope:** experimental software reference and read-only registry discovery. **No complete NMOS interoperability, physical ST 2110 support or AMWA certification is claimed.**

## Product capability boundary

RuntimeHost's currently admitted network-output registry contains **SRT and NDI**, not an RTP/ST 2110 sender or receiver provider. A network source, output role or SRT/NDI session never becomes a real NMOS Sender/Receiver by metadata conversion. NMOS runs in the optional outer IntegrationHost; ControlHost remains the only Production Authority.

| Capability | Software implementation | Remaining qualification |
| --- | --- | --- |
| IS-04 Query API v1.3 | Read-only six-collection bounded snapshot, strict parent/version/identity checking | External registry, DNS-SD, Query WebSocket and HA UNVERIFIED |
| Registry outage and recovery | Stale resources cleared, retry/backoff, full resnapshot | Real network partition/failover UNVERIFIED |
| Node API, registration, heartbeat and shutdown | Loopback-only VIRTUAL reference with four synthetic resource types | Real production registration/PKI UNVERIFIED |
| IS-05 single/bulk staged/active REST | VIRTUAL disabled-only staging and atomic bulk rejection | Real RTP admission and scheduled activation UNAVAILABLE |
| Native SDP/transport files | Unsupported and rejected in virtual mode | ST 2110/RTP/SDP and NIC evidence UNAVAILABLE |
| Source/Flow publication | Intentionally empty for virtual test data | Real flow/source capability UNAVAILABLE |
| Operator NMOS preview | Read-only adapter snapshot and local test JSON status | Production WPF panel and transport UNVERIFIED |

## Runtime/security invariants

The optional adapter defaults to **observer**, requires an exact v1.3 Query root and can only READ the remote registry. For example: https://registry.example/x-nmos/query/v1.3/. Observer never publishes resources or enqueues Production mutations. URI credentials, queries, fragments, alternate-version paths and redirect responses are rejected. HTTPS uses normal platform certificate validation; HTTP is allowed only for literal IP loopback in test environments. The client accepts at most 512 results per type and 512 KiB per HTTP response with a bounded timeout. Bad status, malformed JSON, unsupported UUID/TAI resource versions, duplicate identities or parent mismatch invalidate the entire observation. No stale registry snapshot can authorize a connection. Read-only plan generation always marks production eligibility false until separately admitted by real providers and ControlHost.

**Virtual interop is explicitly isolated:** Requires both testMode=true and dryRun=true, with the API bound to IPv4 loopback. Node, Device, Sender and Receiver identities are four distinct stable UUIDs supplied by deployment configuration; no random identity generation occurs on process restart. The synthetic Node exposes a disabled Sender with null flow_id and a disabled Receiver, clearly tagged VIRTUAL_NO_MEDIA. Source/Flow collections are empty. Custom transport urn:x-nmos:transport:virtual is not a real RTP/SDP transport. Local clients may inspect http://127.0.0.1:55130/x-nmos/node/v1.3/, http://127.0.0.1:55130/x-nmos/connection/v1.2/ and the read-only /rtaime/nmos/v1/topology status endpoint.

IS-05 requests must be bounded JSON. Only master_enable=false, null peer IDs, one empty transport leg, null transport file, and null/immediate disable activation are accepted. Activation that would transmit or receive, a nonempty SDP or destination IP/port, scheduled absolute/relative activation, an unknown/duplicate ID or a bulk partial failure is rejected without changing a Production route. Valid disabled-only single/bulk changes advance a local virtual revision only, never ControlHost/Runtime state.

Registry registration is permitted only against a loopback v1.3 Registration API. Resource POST order is Node, Device, Sender, Receiver; heartbeat POST uses health/nodes/{nodeId} every five seconds by default. A 404 heartbeat triggers parent-first re-registration, a 409 version conflict fails closed, and shutdown attempts child-first deletion; network failures degrade locally with bounded retry. None of these operations can block Program/audio/render cadence.

## Compatibility and example

The integration gateway schema remains version 1.0 and the new nmos adapter is additive and disabled unless configured. Existing OSC, MIDI, GPIO and Companion implementations and mappings do not change. There is no SQLite migration, new production wire schema, external public listener or product-version bump. To run a local virtual interop configuration, enable the IntegrationHost, set testMode and dryRun to true and use an adapter entry of kind nmos with mode virtualInterop, bindAddress 127.0.0.1, unique nodeId/deviceId/senderId/receiverId UUIDs and a private loopback port. Add registrationApiUrl only when a local test Registry API is available. For observer configuration, set mode observer and queryApiUrl to the exact HTTPS v1.3 Query root; omit all virtual IDs.

## Tests and evidence

NmosDiscoveryAndConnectionTests exercise allowed/denied endpoint schemes, duplicate/unstable identity prevention, six bounded Query collections and referential integrity, registry 5xx and 404/409 recovery, stale invalidation, invalid SDP and scheduled activation, atomic bulk, local Node/IS-05 REST and **zero Production triggers**. The Required Gates CI job runs this test suite, while the Quality job verifies ownership, configuration and documented limitations.

The independent AMWA nmos-testing conformance suite, DNS-SD/mDNS, Query WebSocket subscriptions, NMOS Security BCPs/OAuth, real RTP ST 2110 transport, SDP operating-point checks, physical NIC/PAS, external registry failover, authentic TAI/PTP scheduled execution, and a dedicated Operator WPF network topology interface remain **UNVERIFIED**.

**Production dependency:** Introduce an actually qualified RTP/ST 2110 provider in RuntimeHost with explicit media, network interface, clock, SDP and resource admission; then expose only confirmed resources and delegate every real IS-05 activation through the existing ControlHost prepare/commit authority. Until then every physical activation remains rejected and no source/flow is advertised.
