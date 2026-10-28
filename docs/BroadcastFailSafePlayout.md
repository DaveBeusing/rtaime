<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Broadcast Fail-Safe Playout — Program Continuity and Recovery

**Scope:** Software continuity observation and fail-closed admission for Program and Aux output roles. ControlHost remains the sole Production Authority; RuntimeHost supplies only committed execution and *completed software output* evidence. **No automatic, qualified backup device, HoldLast replay, Audio Mute, Slate, ST 2022-7 or physical no-black-frame capability is claimed.**

## Current production boundary

The existing Runtime/Control transaction, durable State/Journal recovery, Preview/Program CUT/DISSOLVE, fault-isolated Recorder/Network sinks, input pipelines and Output-Health/HCenter remain the sole owners. This change adds an explicit, bounded per-role failure-policy vocabulary, an incident/continuity tracker and a read-only Operator status projection. It does not create a second source selection, output pipeline, CG/graphics renderer, hardware watchdog or autonomous routing authority.

| Failure mode | Existing protection / new observation | Real output guarantee |
| --- | --- | --- |
| Failed Preview, Scene/TAKE preparation, unsupported output binding | Existing ControlHost staged/prepare/commit rollback; tracker admitted only after successful **Runtime commit** | Confirmed Control Program authority remains unchanged by rejected operations; physical continuous scan-out UNVERIFIED |
| Missing local SSD, decoder EOS/crash, NDI input lost, missing Program media | Existing input lifetime and fallback may provide black; a committed Program source marked **LOST** now latches an incident even when a black fallback FrameDescriptor is produced | **No no-black guarantee.** Hold last complete image/audio is not implemented or falsely asserted |
| GPU/compositing/readback or Program sink boundary exception | Existing boundary reset and frame-sequence resync; new Program continuity fault latch preserves last complete *metadata* | An in-flight or partially written frame is **not** a complete output observation; physical recovery UNVERIFIED |
| Aux input or provider failure | Aux role incident/failure scoped to Aux; Program role stays separately observed if its own boundaries complete | Physical Aux continuity UNVERIFIED; no automatic Program re-route |
| Recording, replay, network publisher, QC, notification or Integration failure | Existing isolated consumers and bounded queues; no alternative route delegated to these subsystems | Program state is not automatically changed; actual hardware scan-out UNVERIFIED |
| Operator/UI death, ControlHost or RuntimeHost restart | Existing supervisor/reconnect, snapshot reconciliation and ambiguity fencing; no new unapproved restart replay | Runtime-local incident history is **not durable across process loss**; rehydrated status must be marked UNVERIFIED until fresh acknowledged observations |
| Output network reconnect | Existing per-target send/queue evidence and fenced reconnect; no capability invented | SRT/NDI transport recovery is not guaranteed lossless or black-free |

## Explicit per-role failure policy and admission

The additive Runtime.Contracts types are BroadcastFailurePolicy and BroadcastContinuitySnapshot. The vocabulary distinguishes **ReportAndPreserveAuthority**, **HoldLastCompleteFrameAndAudio**, **MuteAudio**, and **ApprovedSlate**, with a required manual re-arm and bounded HoldLast window (1 to 300 frames). These are *intent*, **not** silently switched Program output algorithms. Only **ReportAndPreserveAuthority** currently passes EnsureRuntimeAdmitted, separately for each committed Program/Aux binding. All other modes throw NotSupportedException until actual ControlHost authorization, admitted frame/audio and output-provider capability, safe PTP/timebase behaviour and qualified sink execution are implemented. There is no externally writable per-role policy configuration, and no user-facing fallback selector that would suggest unsupported deployment readiness.

The Runtime continuity tracker is initialized **only after** TransactionalRuntime commits a prepared plan. It captures AuthorityStateId/AuthorityRevision from the prepared ControlHost-supplied execution contract, RuntimeExecutionRevision and the exact Program/Aux source/sink IDs. Note: a Runtime commit is not, by itself, proof that the subsequent ControlHost confirmation response arrived; Operator continues using existing Control/Runtime reconciliation and stale-state fences. Failed Prepare, rejected Commit and unrelated Preview/QC/Integration interactions must not rebind these observations.

## Deterministic episode state machine

- A newly committed output role begins at AWAITING_FIRST_BOUNDARY and is **UNVERIFIED** until a completed provider video and corresponding audio position are observed.
- A completed normal Program/Aux software boundary advances last complete frame sequence and audio sample position, without storing large GPU/PCM buffers. It is **SOFTWARE OBSERVED**, not physical scan-out proof.
- Program/aux signal LOST, Aux sink fault, or a thrown Program boundary latches one FAILED incident with monotonic generation, source/sink/authority/runtime revision, timestamp and bounded cause. It does **not** copy an unfinished frame, select a new source, replay PCM, emit slate or perform fallback I/O.
- A later successful frame changes FAILED to RECOVERED AWAITING RE-ARM. The incident remains visible, including after unrelated Control revision changes with the same source/sink. This is **UNVERIFIED**, not a health PASS.
- A genuinely changed and successfully Runtime-committed role source/sink creates a new observation identity; old source/frame evidence cannot be promoted to the new role. Old incidents remain in bounded local history. That new role still requires fresh completed frame/audio observation and ControlHost-confirmed routing.
- Incident history retains **at most 32 entries**, while repeated failure observations within one episode do not inflate the history. It is not a durable audit substitute.

**Acknowledgement and re-arm:** The existing Health Center can acknowledge an operational *notification* locally, but that action **does not** repair a Media sink or authorize a Production re-arm. A dedicated authenticated ControlHost ACK/re-arm request with ExpectedRevision, incident generation, actual admitted fallback capability and immutable journal is **not implemented**. Consequently a recovered role with unchanged source/sink remains RECOVERED AWAITING RE-ARM / UNVERIFIED. An Operator UI must never display an invented successful re-arm. There is no unattended failover/restart authority.

## Operator status and performance impact

The existing RuntimeOutputRoleSnapshot carries an incident error or read-only CONTINUITY evidence string; the existing IPC/ControlHost/Operator role projection passes through this confirmed health data without a new polling loop or wire version. OUTPUTS shows CONTINUITY INCIDENT ACTIVE, RECOVERED AWAITING RE-ARM, SOFTWARE OBSERVED or UNVERIFIED in the selected output details. The existing Health Center alarm/notification UX remains the location for an informational acknowledgement. Program/Preview and Clean Feed are not incorrectly promoted to physical output senders.

No new frame buffer, PCM clone, network send, GPU copy, timebase or inline decoder is introduced. The successful-boundary metadata update is constant work; only new incident transitions allocate bounded diagnostic records. The current virtual Program provider is *not* hardware-qualified. Real GPU/decoder, 50/59.94/25 fps and long-duration soak/black-free playout require independently measured reference-hardware evidence.

## Tests, CI and qualification

- Contract: role/mode/hold limit, unknown policy rejection, unsupported fallback admission, UTC/identity/incident validity.
- Runtime integration: initial unverified vs completed A/V, Program input loss and recovery/re-arm latch, independent Aux/Preview loss preserving Program, no reset on unrelated authority revision, bounded incident/duplicate episode, source/sink replacement invalidating last-complete evidence.
- Operator: read-only evidence and recovered/faulted strings, no invented Program mutation or re-arm button.
- Required Gates CI explicitly runs targeted Contracts/Integration/Operator and retained relevant failure isolation suites; Quality validates actual commit/boundary ownership, no autonomous Program fallback, and honest support claims. Full CI, Quality, Security, Provider Smoke and Packaged E2E on the **exact PR source commit** are required before merge.
- The accepted failure injection here uses virtual reference providers and software signal-state fault injection. No physical GPU crash recovery/SSD power loss/SDI redundancy or sample-exact delivered A/V soak metrics are claimed as measured.

## Next qualified increment

To deliver actual HoldLast/Mute/Slate, implement provider capability discovery, explicit ControlHost policy command with expected AuthorityRevision and durable persistence/migration; prepare/commit fallback resource reservation, bounded frame/audio buffers and timestamp/clock discipline; an authenticated manual ACK/re-arm command and journal, no replay after ambiguous commit; fault-injected real physical sink/GPU/decoder and long 25/50/59.94 soak with measured video/audio continuity. Only then enable a nonconservative BroadcastFailureAction. ST 2022-7 remains an independent media transport responsibility.

References: [Transactional Runtime Commit](TransactionalRuntimeCommit.md), [Output Routing Health](OutputRoutingHealth.md), [Process Recovery and Supervision](ProcessRecoveryAndSupervision.md), [Production Deployment](ProductionDeploymentBaseline.md), [EBU Tech 3371](https://tech.ebu.ch/publications/tech3371), [EBU R 143](https://tech.ebu.ch/publications/r143). Neither EBU document is a certificate of broadcast continuity.
