<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Production Integration Gateway

## Status

The production integration gateway is a post-V1 optional capability implemented by the standalone `rtaime.IntegrationHost` process. It is a client of Production Authority, never Production Authority itself.

```text
OSC / MIDI / GPI / Companion-style clients
                 |
                 v
        rtaime.IntegrationHost
                 |
           rtaime.Client
                 |
        gRPC/TLS external control
                 |
             ControlHost
                 |
        normal prepare/commit path
                 |
            RuntimeHost
```

`IntegrationHost` has one direct production project reference: `rtaime.Client`. It cannot reference ControlHost, RuntimeHost, Operator or concrete providers directly. All production-changing actions therefore retain the same command identities, optimistic revision checks, idempotency behavior and ControlHost authority used by existing clients.

## Implemented integration matrix

| Integration | Status | Qualification boundary |
| --- | --- | --- |
| OSC over UDP | Implemented, **loopback only** | bounded message subset: int32, float32, string and bool; source allowlist; malformed/oversized rejection; string feedback; direct remote UDP ingress is disallowed |
| MIDI on Windows | Implemented | WinMM short messages for Note On/Off, Control Change and Program Change; deterministic virtual backend for CI; reconnect loop |
| GPIO/GPI-style discrete control | Implemented reference boundary | deterministic virtual provider, edge/state input, output/tally, polarity; physical device drivers are not qualified |
| Companion / Stream Deck-friendly HTTP | Implemented, **loopback only** | bearer-authenticated HTTP action surface plus WebSocket feedback; plaintext remote binding is rejected; use only through a separately qualified secure local bridge |
| NMOS IS-04 / IS-05 | **UNVERIFIED / future** | no NMOS discovery, registry or connection-management implementation is claimed |

Physical GPIO device matrices, device-specific MIDI quirks and NMOS conformance are outside this capability until separately implemented and qualified.

## Command model

Mappings are versioned configuration, not Production state. Trigger keys resolve only to the bounded action set:
- Production Macro execute by stable Macro identity;

- Preview select, CUT and DISSOLVE;
- Scene activation;
- Show Control ARM, GO and CANCEL;
- recording start/stop;
- output-role routing;
- media-deck play/pause/stop and frame cue;
- approved audio-input gain/mute.

Mappings use stable rtaime identities, never Operator control names. There is no scripting engine, arbitrary user code or macro language.

The gateway owns one bounded multi-producer/single-consumer input queue. Excess events are dropped rather than expanding memory without limit. Each mapping has independent debounce and minimum-interval controls. All access to the stateful `OperatorControlClient`, including snapshot refresh, is serialized so periodic feedback refresh cannot race Production commands. One deliberate resynchronization/retry is permitted only for a typed pre-dispatch snapshot requirement or a known pre-execution host/revision rejection with no earlier transport uncertainty. A host change discovered in a successful mutation response, an uncertain original outcome, and request-identity conflicts do not permit a fresh action. Snapshot refresh and feedback failures after a completed action degrade observation without replaying that action; this includes the client's own post-action snapshot refresh.

## Feedback model

One gateway snapshot pump refreshes `OperatorControlClient` and fans cached state out to all adapters. Adapters do not independently poll ControlHost. Feedback is observational and includes:

- Preview and Program source identity;
- active Scene identity;
- recording state;
- Runtime readiness;
- media-deck state;
- show-control state;
- output-role health.

Adapter failure or IntegrationHost failure cannot stop Program. A deployment may mark an adapter required for IntegrationHost startup, but that requirement does not alter ControlHost/RuntimeHost readiness.

### Synchronization and recovery ordering

The gateway starts its configured adapters, completes an initial ControlHost snapshot attempt, and only then starts its existing periodic snapshot pump. An initial synchronization failure leaves the gateway **DEGRADED** while that single pump continues bounded refresh attempts at the configured minimum interval. On a successful initial snapshot the gateway becomes **HEALTHY**; after a ControlHost startup/refresh failure, a successful newer snapshot restores **HEALTHY** with a recovery detail. Adapter failures, input drops, and command failures remain separate causes of degradation and are not cleared merely by a successful ControlHost poll.

Every ControlHost synchronization is serialized with Production commands through the client gate and receives a monotonic gateway-local observation version when completed or failed. Feedback publication is additionally serialized and accepts only the latest successful observation from the currently active gateway lifetime. An older successful snapshot or delayed exception cannot replace a newer observation or override its health state. A failed refresh marks **DEGRADED** without falsely publishing the previous snapshot as fresh; cached feedback remains observational rather than new Production authority.

Stopping cancels the lifetime, invalidates its published feedback, and prevents late startup, refresh, or adapter callbacks from republishing health. Restart creates a new lifetime and resynchronizes with ControlHost before its pump starts. This adds no second poller, Production command authority, or retry/timeout expansion. Snapshot sequence numbers are gateway publication identifiers; they are not ControlHost revisions or proof of physical output health.

## Configuration and secrets

The schema is `schemas/integration/v1/integration-gateway.schema.json`. A secret-free example is `docs/examples/ProductionIntegrationGateway.example.json`.

Start the optional host with:

```powershell
$env:RTAIME_INTEGRATION_UPSTREAM_TOKEN = "<external-control credential>"
$env:RTAIME_COMPANION_TOKEN = "<companion credential>"
./rtaime.IntegrationHost.exe --config=C:\rtaime\integration-gateway.json
```

Alternatively set `RTAIME_INTEGRATION_CONFIG` to the configuration path.

Bearer tokens and certificate passwords are referenced by environment-variable name; secret values are not valid mapping/configuration fields. The upstream endpoint must use an HTTPS origin without embedded credentials, paths, queries or fragments. Test-only insecure trust is accepted only when `testMode=true`, `dryRun=true` and the upstream is an IPv4/IPv6 loopback HTTPS origin.

## Failure behavior

- configuration validation fails closed before adapter startup;
- protocol packets are bounded and malformed OSC is rejected;
- adapter queues cannot grow without limit;
- a missing ControlHost degrades IntegrationHost while Program remains owned by the engine; the snapshot pump keeps retrying and returns the gateway to healthy state after synchronization recovers;
- stop/start creates a fresh bounded input lifetime and performs a new authoritative snapshot synchronization before normal operation resumes;
- optional adapter failures are isolated;
- required adapter failures fail IntegrationHost startup only;
- no adapter bypasses `rtaime.Client` to reach RuntimeHost or a provider.

## Test and diagnostic mode

`testMode=true` selects deterministic virtual MIDI behavior where configured. The external-control test-only TLS bypass additionally requires `dryRun=true` and a loopback HTTPS origin. `dryRun=true` admits and resolves mappings without applying Production mutations. The virtual MIDI and discrete providers are used by normal CI and do not require production hardware.

## Broadcast security deployment boundary

OSC UDP and Companion HTTP/WebSocket are unauthenticated-at-network-layer or bearer-over-HTTP protocols. Configuring either to listen on non-loopback IPv4, IPv6 or wildcard addresses fails at validation, including direct adapter construction. An empty OSC sender allowlist defaults to **loopback sources only**. Companion HTTP enforces bounded request size and bounded Kestrel connections. Do not bind unencrypted adapters through a publicly exposed proxy without independent TLS, client authorization, anti-replay policy, firewall and monitoring qualification. The security review and remaining EBU R 143 v3.0 P1/P2 controls are tracked in [BroadcastSecurityR143Assessment.md](BroadcastSecurityR143Assessment.md).

The schema remains version `1.0`; existing remote listener configurations must migrate to local binding and a separately qualified secure bridge/tunnel. NMOS IS-04/IS-05, ST 2110 control, and web-graphics adapters are not present and must remain fail-closed/disabled until explicitly implemented and qualified.

## NMOS path

Future NMOS work belongs in the same outer adapter boundary. IS-04 discovery/registry and IS-05 connection management may translate qualified external state into bounded gateway triggers or observational feedback, but they must not introduce a second Production Authority. Until a dedicated adapter and conformance evidence exist, NMOS remains **UNVERIFIED**.


## Production Macro trigger mapping

`ProductionMacroExecute` is a bounded IntegrationHost mapping action. Its `targetId` is a stable Production Macro identity.

Adapters do not receive, store or expand the Macro action list. When a trigger is admitted, IntegrationHost calls `OperatorControlClient.ExecuteProductionMacroAsync(targetId)`; ControlHost resolves and validates the durable Macro and owns all sequential execution, failure handling, frame timing and recovery.

The existing bounded gateway queue, mapping debounce/rate limits, adapter authentication and upstream gRPC/TLS authorization apply unchanged. See `docs/ProductionMacros.md`.
