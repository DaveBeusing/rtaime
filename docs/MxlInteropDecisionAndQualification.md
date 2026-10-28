<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# DMF v2 / MXL v1.0 Interoperability Decision and Qualification

**Engineering disposition (2026-10-28): NO-GO for a native Windows MXL v1.0 bridge.**
**Product capability:** SCOPE APPROVED; integration currently **UNSUPPORTED** on the rtaime Windows-x64 execution platform.
**Software interoperability / latency / hardware evidence:** **UNVERIFIED**.
**Reference source:** rtaime `master` at `eb8d4eb8664e1572b4adcf5d58e6c2410263c233`.
This assessment can be reopened with new, source-bound evidence; it does not remove MXL from the product roadmap.

## Question and decision

Can a versioned, optional MXL 1.0 shared-memory publisher/subscriber be attached to the current
rtaime Runtime media/provider boundary without changing ControlHost authority, requiring the
standard Windows product to ship a Linux SDK, or asserting unmeasured low-copy performance?

**Not with the currently verified platform and SDK combination.** Do not add an MXL protocol
identifier, enabled provider, native dependency, runtime feature flag, IPC payload, or
Windows distribution artifact on the strength of SDK API availability alone. There is no
executable provider to activate safely. Retain the existing native Media I/O, SRT/NDI and
software ST 2110 paths, each with its own qualification limits. This is an interoperability
**adoption gate**, not a decision to reimplement MXL or move internal MediaBuffer ownership to it.

## Exact upstream references and evidence

| Item | Audited basis | Observation |
| --- | --- | --- |
| Facility architecture | [EBU DMF reference architecture v2, 2026-04-15](https://tech.ebu.ch/publications/white-paper-2026-04-15) | A layered facility architecture; not an rtaime API or an MXL product certificate |
| Stable MXL reference | [dmf-mxl/mxl `v1.0.0`](https://github.com/dmf-mxl/mxl/releases/tag/v1.0.0), commit `7fc2d2cd215ae6b8db0038c5c9944ac7e49c009e` | C API for flows; **do not substitute main or v1.1 API behavior** for the v1.0 assessment |
| Build target | [v1.0.0 `CMakePresets.json`](https://github.com/dmf-mxl/mxl/blob/v1.0.0/CMakePresets.json), [`docs/Building.md`](https://github.com/dmf-mxl/mxl/blob/v1.0.0/docs/Building.md) | Linux and Darwin presets; **zero declared native-Windows presets**. WSL2 instructions run a Linux container, not a Windows DLL |
| Memory and process model | [`docs/Usage.md`](https://github.com/dmf-mxl/mxl/blob/v1.0.0/docs/Usage.md), [`lib/src/mxl.cpp`](https://github.com/dmf-mxl/mxl/blob/v1.0.0/lib/src/mxl.cpp) | Sample opens `/dev/shm/mxl`; library constructs a `PosixFlowIoFactory`. Windows memory mapping compatibility is unproven |
| Formats and flow lifecycle | [`lib/include/mxl/flow.h`](https://github.com/dmf-mxl/mxl/blob/v1.0.0/lib/include/mxl/flow.h), [MXL README](https://github.com/dmf-mxl/mxl/tree/v1.0.0) | Grain/samples APIs require explicit reader/writer and buffer lifetime; MXL's documented V210 video / Float32 audio must be mapped to admitted rtaime formats |
| Licensing | [MXL `LICENSE.txt` at v1.0.0](https://github.com/dmf-mxl/mxl/blob/v1.0.0/LICENSE.txt), file SPDX headers | SDK is Apache-2.0; some documentation uses CC-BY-4.0. Transitive dependencies, required notices and redistribution still need a shipment review |
| Current rtaime seam | `src/Media/rtaime.Media/MediaIoFoundation.cs`, `src/Media/rtaime.Media/NativeMediaIoAdapter.cs`, `src/Hosts/rtaime.RuntimeHost/RuntimeMediaIoVerticalSlice.cs` | An existing provider-admission/lease boundary, but **not** a qualified MXL implementation |
| Current product platform | `global.json`, `rtaime.slnx`, `docs/Governance/PlatformSupportMatrix.json` | .NET SDK `10.0.401`, Windows-x64 family; support evidence for the reference platform is independently UNVERIFIED |

The MXL v1.0 C ABI itself exists. This does **not** prove that its native library, POSIX
shared-memory domain, timing or lease implementation can run inside a Windows-native
RuntimeHost. A Linux VM/WSL2 does not share mapped MXL buffers with a Windows process
merely because both run on one workstation.

**No numerical latency, CPU/GPU or memory benchmark is available for this integration.**
This table records source-code/platform findings, not an executed SDK compatibility test.

## Options considered

1. **Load MXL v1.0 into Windows RuntimeHost:** rejected for now. A C header alone does not
   establish a supported Windows binary or a safe interprocess mapping/lifetime model.
2. **Linux MXL worker with a Windows-side proxy:** potentially viable as a *separate*
   transport architecture, but not a native same-host zero-copy path. Requires an explicit
   versioned boundary, copying/transfer accounting, authentication, bounded queues,
   clock and restart semantics, and measured end-to-end delay. No such provider exists here.
3. **Replace the existing buffer pipeline with MXL:** rejected without comparative proof.
   It would alter active playback/Program ownership and introduce an unqualified critical
   dependency.
4. **Keep existing paths; gate MXL adoption on a qualified native/worker proof: SELECTED.**
   No dormant runtime flag or advertised capability is added for a nonexistent executable path.

A newer upstream SDK release may warrant reevaluation, but its platform support and exact
API/license/ABI must be assessed separately; a different version cannot retroactively
qualify v1.0.

## Architecture and interop invariants for a future proof

- ControlHost remains the **only** authoritative production state/control instance.
  RuntimeHost remains execution, clock/cadence, frame/audio, resource and Program owner.
  A future MXL provider exchanges media only; it never commits production state.
- Keep SDK headers, native pointers, OS handles and raw mapped bytes outside the
  stable Core/Media/Provider contracts and management IPC. Introduce a versioned C ABI
  or authenticated separately supervised worker only after the platform boundary is proven.
- Default **OFF and fail closed** on missing/unknown SDK version, mismatched ABI, wrong
  OS/architecture, unqualified format, inadequate resource budget, invalid clock domain,
  missing worker, stale authority or missing qualification evidence.
- Explicitly own reader/writer sessions, flow IDs, producer generations, mapped lease
  handles, buffer release and process teardown. A writer crash or reused flow ID must
  not let a stale lease enter a new Program generation.
- Validate V210 row/stride/visible extent, chroma and color interpretation before conversion
  to current rtaime normalized RGBA8. Audio needs exact Float32 channel/layout/rate and
  rational timestamp/cadence mapping. Timestamp equality is not a completion fence.
- Bound shared-memory bytes, outstanding leases/grains, per-flow queue depth,
  publication wait, cancellation, disposal and recovery. Never perform synchronous
  SDK calls or waiting on shared-memory locks in the Program/audio/render callback.
- Hold a provider-owned frame lease until all consumers release it; enforce a
  generation fence on reconnect and process restart. No universal zero-copy claim:
  every necessary conversion, CPU/GPU transfer and IPC copy must be counted.
- Preserve all existing provider, AJA, ST 2110, NDI/SRT and media-output routes.
  Failed admission must leave existing committed production unchanged.

## Reopening requirements and qualification plan

**Dependency gate — before implementation**

1. Produce a reproducible native **Windows-x64** MXL build/ABI/loading proof against
   a specified upstream tag and commit, **or** approve a separate Linux-worker
   architecture with a real cross-OS media transport. Show executable build inputs,
   compiler/runtime, SDK dependency and OS deployment manifest.
2. Review Apache-2.0 compatibility, notices, third-party licenses, servicing and
   redistributable artifacts on the exact source revision. Record the approved SDK pin.
3. Prove one two-process publisher/subscriber grain and audio-samples round trip
   using the actual SDK on the target execution environment, not a mock implementing
   the MXL function names. Record the exact native and managed versions.
4. Define measurable finite budgets **before** testing: max shared bytes, max
   outstanding leases, publisher/reader depth, timeout, producer count, p50/p95/p99
   publish-to-consume delay, sustainable frame/audio cadence and crash-recovery time.
   Select pass/fail thresholds based on baseline rtaime admission/resource budgets;
   do not invent performance numbers in advance.
5. Execute comparative measurements against the existing compatible rtaime path on the
   same host with identical source/cadence: throughput, end-to-end delay, lock/contention,
   allocations, CPU/GPU time, memory pressure and frame/audio discontinuities.

**Required negative/failure cases**

| Case | Required outcome |
| --- | --- |
| Flag absent/disabled, missing library/worker, Windows without proven native runtime | No capability advertised, no startup/build dependency, standard product works unchanged |
| Unknown SDK/ABI revision, incompatible format/cadence or clock | Reject before allocation and do not silently convert |
| Flow ID collision, two producers, duplicate/reordered/missing grain | Deterministic ownership, ordering and explicit backpressure/failure |
| Reader lease expiration; producer/reader crash and restart | Reclaim or quarantine bounded resources; stale generation cannot publish |
| Full queues, memory exhaustion, late subscriber, timeout or cancellation | Bounded failure, no Program/audio callback blocking or partial commit |
| Long-running video+audio at admitted cadence | Source-bound A/V drift and p50/p95/p99 latency evidence, with negative cases |
| Native dependency absent in Windows CI | Regular restore, managed build, tests and packaging remain operational |

No SDK-backed target test, hardware benchmark or Windows binary packaging was performed
as part of this decision. Mark every such matrix cell **UNVERIFIED**, not PASS or
SOFTWARE QUALIFIED. A successful Linux-only SDK sample is not proof of Windows runtime
integration. Qualification needs source SHA, test assets/versions, machine, OS, compiler,
SDK pin, full measured profile and CI run/attempt.

## Status and implementation impact (2026-10-28)

| Surface | Result |
| --- | --- |
| Windows-native MXL v1.0 provider | **NO-GO / UNSUPPORTED** at this SDK/platform boundary |
| Linux MXL SDK C API | Documented upstream; **not exercised in rtaime** |
| Windows/.NET 10 product build | **No new dependency or code path**; existing baseline unchanged |
| Frame/audio shared-memory path | **NOT IMPLEMENTED**; no publish/subscribe/zero-copy claim |
| Runtime/Control/Operator | **No authority or UI changes** |
| Native, software and physical qualification | **UNVERIFIED**; no measured pass |
| Rollback | Documentation-only decision: revert these documentation links; no code/config/data migration |

**Next technical action:** obtain a reproducible SDK/platform proof or approve a separate
cross-OS worker transport proposal; only then add a short feature branch for the actual
adapter, fail-closed provider registration and source-bound software/fault/latency tests.
