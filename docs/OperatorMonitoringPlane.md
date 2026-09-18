<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Operator Monitoring Plane

## Scope

Operator Monitoring Plane adds non-authoritative visual Preview and Program monitoring to the V1 Operator. The monitoring plane is deliberately separate from the versioned ControlHost management path. It carries visual observation only and cannot mutate production state, commit Runtime execution, change routing, control recording or influence Program continuity.

The authoritative control path remains:

`Operator -> rtaime.Client -> ControlHost`

The visual monitoring path is independent:

`RuntimeHost committed media -> monitoring tap -> bounded monitor worker -> bounded subscriber -> dedicated monitoring Named Pipe -> rtaime.Client monitoring transport -> Operator bitmap presentation`

## Monitoring contract

`rtaime.Media.Contracts` defines a dedicated `MonitoringContractVersion` and `MonitoringFrameDescriptor`. Operator Monitoring Plane uses monitoring contract version `1.0` independently from the primary Media contract version.

Each frame declares:

- stream kind (`Source` or `Program`);
- source identity;
- monitoring width and height;
- RGBA8 pixel format;
- production frame sequence and presentation timing;
- a fixed binary wire header and exact RGBA payload length.

The monitoring wire format is not embedded in ControlHost or RuntimeHost management envelopes.

## Runtime capture boundary

The RuntimeHost monitoring tap is fed from the committed execution path after timed input processing. Source A and Source B use the same immutable RGBA input buffers that feed GPU upload. Program monitoring uses the existing post-composite GPU readback, so CUT, DISSOLVE and V1 visual-layer results are represented by the Program monitor rather than reconstructed in the Operator.

Operator Monitoring Plane does not add an additional Program GPU readback. It reuses the readback already required by the managed V1 reference pipeline.

## Bounded and loss-tolerant behavior

Monitoring is subordinate to Program continuity.

The V1 policy is:

- monitoring is sampled once every four production boundaries;
- the qualified monitoring image is 320x180 RGBA8;
- the runtime monitoring tap retains at most one pending boundary sample;
- a newer sample replaces an older pending sample under pressure;
- each connected monitoring subscriber has a small bounded queue;
- subscriber queues drop old monitor frames rather than block the publisher;
- when no monitoring subscriber exists, Runtime does not enqueue monitor downscale work;
- monitoring reconnects independently from ControlHost synchronization.

A slow, disconnected or failed Operator may therefore observe dropped or stale monitoring frames. That state is acceptable and must never delay Program execution.

## Dedicated transport

RuntimeHost exposes a dedicated output-only Named Pipe at:

`<runtime-endpoint>.monitor`

For the default V1 endpoint this is:

`rtaime.v1.runtime.default.monitor`

The Operator can override it with `RTAIME_MONITOR_ENDPOINT`. Otherwise it derives the endpoint from `RTAIME_RUNTIME_ENDPOINT`.

The monitoring transport carries only frame observations. It exposes no Set Preview, CUT, DISSOLVE or other mutation operation.

## Operator behavior

The Operator renders two visual surfaces:

- **Preview** selects the most recent source monitor frame whose source identity matches the Preview routing received from authoritative ControlHost state.
- **Program** displays the actual Program monitor stream emitted from post-composite Runtime output.

Monitoring health is presented separately from ControlHost connection health. Loss of the monitoring pipe can mark monitoring `STALE` without marking the authoritative control snapshot disconnected or stale.

## Failure isolation

Monitoring must fail open relative to production continuity:

- ControlHost does not depend on the monitoring pipe.
- Runtime execution does not wait for a monitoring consumer.
- the monitoring tap overwrites pending samples under pressure;
- the monitoring server uses bounded per-client queues;
- monitor disconnects are handled as observation loss, not Runtime authority failure;
- the Operator can continue command/control operation when visual monitoring is unavailable, subject to the existing authoritative control readiness rules.


## clean-feed consumer

Program Output / Clean Feed adds a second WPF presentation surface for the existing Program monitoring image. It does not add a monitoring contract version, RuntimeHost render path, new subscriber, or management-IPC payload.

OperatorMonitoringViewModel continues to own the single NamedPipeOperatorMonitoringTransport reader and converts each received Program frame once into a frozen WPF bitmap. Both the in-workspace Program monitor and ProgramOutputWindow bind that same ProgramImage reference.

The clean feed therefore inherits the monitoring plane's bounded/loss-tolerant semantics and its current monitor-grade 320×180 / sample-stride-4 presentation profile. Production continuity remains independent of either WPF surface.

## Verification

`build/quality/Test-OperatorMonitoringPolicy.ps1` checks the architectural separation and bounded-loss behavior structurally.

Integration coverage qualifies:

- monitoring wire-frame round trips;
- bounded subscriber drop behavior;
- sampled source and Program monitor publication;
- dedicated Named Pipe delivery through `NamedPipeOperatorMonitoringTransport`.

The repository Required Gates remain authoritative for CI, Quality, Security, Provider Smoke and Packaged E2E qualification.
