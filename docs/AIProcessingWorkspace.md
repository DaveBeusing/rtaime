<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# AI Processing Workspace

## Purpose

The AI navigation destination opens an independent, task-oriented Operator workspace
for discovering installed governed inference capabilities and explicitly applying
an approved effect to Program. This is a productive UI over the existing
AIEffectBrowserViewModel, OperatorViewModel and shared Client → ControlHost →
RuntimeHost → AIHost reference path, not a second inference engine or a local
production authority. The existing workspace ID AI and schema-7 layout/shortcut
registration are retained; original workspace identities and stored layouts
remain compatible.

## Workflow

1. Select **AI** using grouped navigation or **Workspace AI** in Commands. The
   transition changes only presentation. It does not start inference or activate an
   effect. Existing saved layout geometry is restored independently.
2. Choose **REFRESH PROVIDERS**. The connected provider advertises capability,
   provider identity/availability, model version, input and output contracts,
   declared resource requirements and provenance. This is discovery, not model
   installation or provider qualification. An empty catalogue or unsupported
   transport remains UNAVAILABLE/UNSUPPORTED.
3. Choose an advertised, available capability. The reference implementation supports
   `ai.person-segmentation`. Selection does not request AI inference or mutate Program.
4. Verify the displayed committed **PROGRAM** source and Production revision. Select
   **TEST · DESCRIPTOR ONLY**. The existing reference test takes a descriptor of
   that committed Program sample. It returns sample/inference metadata without
   changing visible Preview/Program pixels. The request remains governed by backend
   resource admission and deadline limits.
5. A successful test must match the selected provider, an actual source sequence,
   the Production revision and committed Program source. Installed capability
   evidence expires after 30 seconds. Changes to the selection, provider evidence,
   Program revision/source or readiness invalidate prior approval.
6. Explicitly check **I approve this tested effect on Program**, then select
   **AI ON**. The existing revision-bound ControlHost action may reject unavailable
   authority, stale revision, occupied layer or failed Runtime preflight. The UI
   distinguishes request/pending, accepted/armed and actual applied Runtime
   sequence evidence. Accepted does not mean an effect is visible.
7. Observe Runtime confirmation/fallback in **AUTHORITY / EXECUTION**. Use
   **AI OFF** to request bypass; verify authoritative confirmation before assuming
   Program effect state changed. Leaving the workspace or moving focus never
   activates an effect.

## Preview, image detail and A/B

The right-hand image is **CURRENT PROGRAM · SHARED MONITORING**, built using
the existing ProgramViewer and monitoring projection. It shows current output
evidence, **not** a second renderer, the raw original frame or a new AI visual
preview. The reference TEST returns only metadata; no independently addressable
original/result image pair is produced. Therefore visual A/B inspection is
explicitly **NOT AVAILABLE**, rather than changing live Program to simulate a
preview. Pixel detail is limited by actually delivered monitoring data and
active presentation resources. Provider/runtime qualification does not follow
from zooming a reduced image.

## Capability, resource, cost and privacy evidence

The installed reference provider is a synthetic managed segmentation model
package (`1.0.0-reference`) with
`video-frame-descriptor.v1` input and
`segmentation-mask-descriptor.v1` output.
Advertised resource requirements are shown **verbatim**, not interpreted as
measured GPU memory, hardware occupancy or money. Backend reference behavior is
bounded to one in-flight request, at most 5 Hz and a 500-ms request deadline.
These are actual reference-policy limits, not a promise of real GPU performance.
The currently installed provider catalogue determines what can be selected.

No trained-model accuracy, GPU inference rate, physical partitioning, commercial
cost or cloud-service price is known from these descriptors; those values remain
UNVERIFIED. No model download, paid service, cloud account, external media
transmission or opaque background processing starts from workspace navigation,
selection, refresh or hover. Media payloads are not uploaded by this UI.

## Failure and recovery

| Observation | Program-safe response | Operator action |
| --- | --- | --- |
| No installed model / unavailable provider | No activation; existing Program continues | Check provider/AIHost readiness, then refresh |
| Unsupported catalogue/test transport | No pretend capability or fabricated test result | Use the supported local provider transport |
| Failed or missing test sample | No approval | Synchronize and repeat discovery and descriptor test |
| Changed Program source/revision or expired catalogue | Approval invalidated | Synchronize, refresh and retest |
| AI request pending / accepted but not applied | No EXECUTED claim | Wait for actual Runtime status |
| Timeout / pressure / deadline | Safe fallback / clean Program | Inspect AIHost and resource state; retry only by explicit action |
| Stale or source-mismatched result | No stale highlight accepted as current | Synchronize and retest |
| Host loss / reconnect | No automatic reactivation | Recover Runtime, synchronize and perform explicit approval again |

The Runtime media/audio cadence, Control transactions and saved production are
authoritative. A local indicator, draft selection or acknowledgement never
substitutes for committed or observed execution.

## Verification and remaining evidence

The existing `AIEffectBrowserTests` and
`WorkspacePresentationTemplateTests` cover provider/catalogue visibility,
source/revision admission, no mutation from selection or descriptor test, missing
capability, and the independent workspace's layout/command/viewer composition.
The existing AI integration, contract, behavioral, failure and managed Windows
Operator suites remain required. See [Governed AI Operator Workflow](GovernedAIOperatorWorkflow.md) and [Build and Test](BuildAndTest.md).

A source-level view/control test is not proof of a running application. The
real Windows MainWindow, connected activation/off/timeout/reconnect journey,
layout restoration, keyboard focus and all five exact-head Required Gates must
be qualified for merge. Physical GPU/resource arbitration, trained-model
accuracy, actual OS host-kill recovery, mixed-monitor DPI, commercial cost
and long-run latency remain UNVERIFIED without separate correlated evidence.
