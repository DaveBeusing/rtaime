# High-resolution Program admission decision

Status: **PROPOSED / IMPLEMENTATION BLOCKED**. Inventory baseline: `e86c7f46dfc61bb40871af393e8f19105290ef1e`.
This is a design and regression boundary, not an accepted output-format approval.

## Decision and approval record

Keep the production format set at 1920×1080 progressive RGBA8, 50/1 and
60000/1001. Consider 3840×2160 SDR one profile at a time only after the
prerequisites and approvals below. 25/1 is also a new Program cadence;
accepting 25-fps input into the existing 50-fps Program does not provide it.

| Required record | Current evidence | Implementation consequence |
| --- | --- | --- |
| Product Owner approval for each 2160p25, 2160p50, 2160p59.94 Program profile | No accepted approval found in the inspected scope/decision documents | All three profiles BLOCKED |
| Exact GPU, driver, host, storage, codec and output-device compatibility approval | No profile-specific approval/evidence established | No new provider capability or hardware claim |
| Physically demonstrated HD stability under the intended mixed workload | Not established by hosted software runs | Physical prerequisite UNVERIFIED |
| Measured GPU-resident/transfer budget and accepted adoption decision | [GPU evaluation](GpuResidentMediaEvaluation.md) remains NO-GO | No new decode backend or unmeasured UHD fallback |
| Separate native 10-bit/HDR/alpha scope | No accepted approval | Design only; no P010/RGB10/HDR/Fill-Key implementation |

An approval must identify approver/date, source revision, precise rate/color/alpha
tuple, required and excluded consumers, machine/driver/device identities, codec
dependencies, latency/resource limits, fallback policy and retained physical
evidence. A merge, general UHD input envelope, native V210 I/O enum or Main10
SDR-to-RGBA8 decode result is insufficient.

The approved MOV/H.264 AAC/PCM input slice and two independently controlled A/B
decks do not grant a new Program format. Existing saved productions and contract
versions retain their interpretation. No default, runtime format alias,
capability advertisement, schema, decoder, renderer or output profile changes
in this foundation.

## Alternatives and staged adoption

1. **Retain HD and establish evidence (current choice).** Preserve working
   production identities while recording the [cross-layer gaps](HighResolutionMediaCompatibility.md)
   and [qualification plan](HighResolutionMediaQualification.md).
2. Add one approved UHD SDR profile end to end. Reuse the authoritative Program
   renderer, rational schedule and existing provider negotiation. Reject a
   required consumer that cannot accept the exact tuple before live activation.
   This alternative is blocked by the records above.
3. Adopt UHD plus 10-bit/HDR in one change. Rejected: independently unqualified
   color, codec, surface-layout and device risks would obscure failures.

A future implementation must preserve ControlHost durable transactional authority,
RuntimeHost execution/cadence ownership, PreparedExecution revision fencing,
source/generation checks and bounded lease lifetimes. Operator supplies intent;
AIHost supplies no Program authority. No second catalog, state store, playback
clock or renderer is needed. SSD reads, native decode, waits and decoder joins
stay outside the Program boundary.

Format activation should be an explicit validated lifecycle/epoch transition,
not a reinterpretation of an existing sequence or a silent fallback to HD.
Prepare every required sink first; keep committed production unchanged on
negotiation failure. Old peers must reject unsupported format semantics rather
than accept a partially understood payload.

## Separate high-bit-depth and color contract proposal

See [the future contract design](HighResolutionMediaCompatibility.md#separate-10-bit-hdr-and-alpha-contract).
These semantics require another approval and compatibility review even after an
SDR profile passes. Do not add public enum values or bump Media contract versions
until the end-to-end slice and persistence/old-peer behavior are agreed.

## Blocked implementation report

Completed scope: source-derived inventory, arithmetic resource sizing, approval
record, future compatibility matrix, separate color/alpha design and executable
HD/UHD admission regressions. No production-format introduction is authorized.
No measured before/after performance improvement is claimed.

Remaining dependencies: accepted profile scope; qualified physical HD baseline;
measured transfer budget; consumer-specific UHD compatibility; native-resolution
reference files and physical end-to-end acceptance. Generated UHD media and
hardware runs are future qualification work, not evidence supplied by this
design. Required Gates validate this change's exact revision; their software
results cannot clear these production blockers.
