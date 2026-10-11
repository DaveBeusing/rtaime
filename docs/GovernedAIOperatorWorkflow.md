<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Governed AI operator workflow

## Scope and inventory

This change improves the existing V1 person-segmentation showcase. It adds no inference engine, renderer, model downloader, production action family or production authority. Classification: CONTRACT (private host/client payloads), REALTIME_CRITICAL (fallback and continuity), Operator workflow. Public AI/Control/Runtime contract identities and persistent schemas are unchanged. The existing closed V1 effect scope is retained; no new architectural direction or ADR is introduced.

AIHost advertises installed provider capabilities and model-package descriptors. The browser obtains evidence through Operator → Client → ControlHost → RuntimeHost → AIHost. An empty response means no advertised installed capability. Missing or unsupported discovery is UNVERIFIED/UNSUPPORTED, never a fabricated catalogue. AVAILABLE describes a ready advertised provider with a matching descriptor package, not trained-model quality or physical hardware qualification. Unknown effect kinds are UNSUPPORTED.

Current source implements exactly one capability:

| Item | Evidence |
| --- | --- |
| Capability | ai.person-segmentation |
| Provider | Managed Reference Person Segmentation |
| Package | 1.0.0-reference; synthetic reference package, not a trained production model |
| Input | video-frame-descriptor.v1; existing committed Program sample; virtual RGBA8 1080p50/59.94 software coverage |
| Output | segmentation-mask-descriptor.v1; semantic person and deterministic normalized region; descriptor/opaque handle only |
| Budget | one in-flight showcase request, at most 5 Hz; 500 ms total request, 250 ms connection bound within the total |
| Result use | same submitted surface/sequence and still-current Program source; confidence ≥ 0.90; reported freshness ≤ 100 ms; observation age ≤ 500 ms |
| Fallback | restore previous visual mode; clean committed Program continues; no routing revision change |

No segmentation accuracy, trained-model quality, GPU inference latency or physical resource partitioning is claimed.

## Operator sequence

1. Refresh installed capabilities. Select a provider-advertised row. Selection makes no production request.
2. TEST invokes governed inference on a descriptor of the current committed Program sample. It produces metadata only. Neither Preview nor Program pixels change; it is not a second visual preview renderer.
3. Successful test evidence must match the selected provider and the current Production revision. The browser requires fresh catalogue evidence (30 seconds) and explicit approval of the reference effect on Program.
4. AI ON requests activation through ControlHost. ControlHost rejects a missing/stale expected Production revision, pending production execution, unavailable Runtime or a fresh Runtime authority mismatch.
5. Acceptance arms Runtime execution. EXECUTED is shown only for Runtime RUNNING plus visible effect and separate source/applied sequences. Acceptance does not promise a visible result.
Activation also rejects an already occupied dynamic visual layer, preserving its existing content instead of replacing an unrelated effect.

6. AI OFF requests bypass. Timeout, unavailable provider, invalid result, low/non-finite confidence, source change or expired effect evidence removes the highlight and reports fallback. Inference never blocks the media loop.

Refresh, selection changes, production revision changes, stale/disconnected control, failed test and activation attempts invalidate approval. There is no automatic activation/retry after reconnect. Test and activation cannot run concurrently within the showcase service. Runtime replacement starts with AI bypassed; activation is deliberately not persisted or replayed from a session.

The UI uses existing custom controls, dark resources and the existing workspace scroll container. Capability and diagnostic text wrap; controls expose accessible names and normal keyboard focus.

## Compatibility and authority

Local Named Pipe is the reference workflow. The existing authenticated/RBAC gRPC activation operation supports the revision-bound approval payload; its catalogue/test methods remain explicitly UNSUPPORTED until the external protocol exposes those operations. No external listener or permission is added.

The private control.ai_showcase.set enable payload now requires expectedRevision. Legacy enable requests without it fail closed and must resynchronize/upgrade. Disable remains available without a revision as a safe bypass. Public domain contracts remain unchanged because this is a host command admission requirement, not a new production action or persisted effect format.

AIHost returns observations only. It cannot call Control or Runtime production APIs. Runtime continues to own the existing dynamic visual layer and the committed media loop. New catalogue/test messages carry bounded descriptors, no media bytes. The catalogue is bounded to 64 capability rows and the existing 1 MiB IPC frame limit.

## Validation and limitations

Targeted AI workflow regressions run before unrelated broad CI checks. Tests cover provider evidence, no activation from selection/testing, explicit opt-in, stale revision/operator, failed tests, inference timeout/unavailability, expired results, transport loss, and clean Program continuity. Existing contract, result policy and failure-isolation tests remain required.

Canonical developer build remains ./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release on the pinned SDK. Hosted builds/tests and all five exact-head Required Gates determine integration readiness; source inspection alone is not PASS. GitHub-only execution does not establish a local developer build.

Actual OS kill/restart, production model signing/trust, real GPU inference, hardware arbitration, external peers, soak and physical mixed-monitor DPI/focus inspection remain UNVERIFIED. Managed AIHost shutdown/IPC loss evidence is separately scoped and does not claim an OS crash qualification.

## Deferred proposals

Trained segmentation model packages, background replacement/blur, tracking, enhancement, generative effects and additional provider families require separately approved contracts, admission/fallback policy and qualification evidence. They are not advertised as installed or selectable.
