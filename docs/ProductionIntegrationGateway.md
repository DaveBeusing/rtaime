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
| OSC over UDP | Implemented | bounded message subset: int32, float32, string and bool; source allowlist; malformed/oversized rejection; string feedback |
| MIDI on Windows | Implemented | WinMM short messages for Note On/Off, Control Change and Program Change; deterministic virtual backend for CI; reconnect loop |
| GPIO/GPI-style discrete control | Implemented reference boundary | deterministic virtual provider, edge/state input, output/tally, polarity; physical device drivers are not qualified |
| Companion / Stream Deck-friendly HTTP | Implemented | authenticated bounded HTTP action surface plus WebSocket feedback; intended for generic HTTP integrations |
| NMOS IS-04 / IS-05 | **UNVERIFIED / future** | no NMOS discovery, registry or connection-management implementation is claimed |

Physical GPIO device matrices, device-specific MIDI quirks and NMOS conformance are outside this capability until separately implemented and qualified.

## Command model

Mappings are versioned configuration, not Production state. Trigger keys resolve only to the bounded action set:

- Preview select, CUT and DISSOLVE;
- Scene activation;
- Show Control ARM, GO and CANCEL;
- recording start/stop;
- output-role routing;
- media-deck play/pause/stop and frame cue;
- approved audio-input gain/mute.

Mappings use stable rtaime identities, never Operator control names. There is no scripting engine, arbitrary user code or macro language.

The gateway owns one bounded multi-producer/single-consumer input queue. Excess events are dropped rather than expanding memory without limit. Each mapping has independent debounce and minimum-interval controls. All access to the stateful `OperatorControlClient`, including snapshot refresh, is serialized so periodic feedback refresh cannot race Production commands. One deliberate resynchronization/retry is permitted only for stale-session/revision conflicts.

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

## Configuration and secrets

The schema is `schemas/integration/v1/integration-gateway.schema.json`. A secret-free example is `docs/examples/ProductionIntegrationGateway.example.json`.

Start the optional host with:

```powershell
$env:RTAIME_INTEGRATION_UPSTREAM_TOKEN = "<external-control credential>"
$env:RTAIME_COMPANION_TOKEN = "<companion credential>"
./rtaime.IntegrationHost.exe --config=C:\rtaime\integration-gateway.json
```

Alternatively set `RTAIME_INTEGRATION_CONFIG` to the configuration path.

Bearer tokens and certificate passwords are referenced by environment-variable name; secret values are not valid mapping/configuration fields. The upstream endpoint must use HTTPS. Test-only insecure trust is accepted only when `testMode=true`.

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

`testMode=true` selects deterministic virtual MIDI behavior where configured and permits the external-control test-only trust mode. `dryRun=true` admits and resolves mappings without applying Production mutations. The virtual MIDI and discrete providers are used by normal CI and do not require production hardware.

## NMOS path

Future NMOS work belongs in the same outer adapter boundary. IS-04 discovery/registry and IS-05 connection management may translate qualified external state into bounded gateway triggers or observational feedback, but they must not introduce a second Production Authority. Until a dedicated adapter and conformance evidence exist, NMOS remains **UNVERIFIED**.
