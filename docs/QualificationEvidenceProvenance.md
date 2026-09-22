<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Qualification Evidence Provenance & Release Binding

## Purpose

Qualification Evidence Provenance closes the trust gap between the physical qualification workflows and the release-evidence system. A hardware qualification result is not release evidence merely because a workflow emitted a JSON file with `status: PASSED`.

A physical result may affect release compatibility evidence only when all of the following are true:

- the payload itself passes the qualification-specific fail-closed checks;
- the binding names the exact rtaime source commit that produced the qualification run;
- the binding retains the GitHub workflow run ID and run attempt;
- the payload SHA-256 matches the bytes that were bound;
- the product version and release stage match the source tree;
- the qualification type is authorized by repository policy to satisfy the claimed release requirement;
- the release source commit exactly matches the binding source commit;
- the exact binding and payload bytes are copied into the offline release-evidence bundle and their hashes remain unchanged.

## Qualification types and release requirements

`build/qualification/qualification-evidence-policy.json` is the V1 mapping authority:

| Qualification type | Release requirements |
| --- | --- |
| `CUDA_REFERENCE` | `REFERENCE_GPU` |
| `MEDIA_IO_REFERENCE` | `PROFESSIONAL_MEDIA_IO` |
| `TIMING_REFERENCE_SOAK` | `GENLOCK`, `PHYSICAL_END_TO_END_LATENCY`, `LONG_SOAK` |

The static release policy intentionally keeps every physical requirement at `UNVERIFIED`. Editing `release-policy.json` cannot create a hardware `PASS`.

Reference Platform Qualification projects the same three source-bound qualification types into twelve explicit physical evidence dimensions:

| Qualification type | Reference-platform dimensions |
| --- | --- |
| `CUDA_REFERENCE` | `CUDA_GPU_EXECUTION`, `GPU_FRAME_LATENCY`, `GPU_SURFACE_LIFETIME` |
| `MEDIA_IO_REFERENCE` | `PROFESSIONAL_MEDIA_IO` |
| `TIMING_REFERENCE_SOAK` | `SUSTAINED_FRAME_CADENCE`, `DROPPED_FRAME_BEHAVIOR`, `SYSTEM_TELEMETRY_CONTINUITY`, `TIMING_REFERENCE_LOCK`, `AUDIO_VIDEO_SYNCHRONIZATION`, `RECOVERY_UNDER_LOAD`, `PHYSICAL_END_TO_END_LATENCY`, `LONG_SOAK_STABILITY` |

A binding may satisfy multiple dimensions only because its qualification-specific verifier validates the corresponding payload fields. A binding type alone is never sufficient evidence.

## Binding format

Each dedicated self-hosted physical workflow creates a schema `1.0` binding after the qualification payload has passed. The binding contains:

- repository identity;
- qualification type;
- `PASSED` status;
- product version and release stage;
- exact source commit;
- workflow run ID and run attempt;
- payload repository-relative path, schema version and SHA-256;
- release requirements authorized for that qualification type.

`New-QualificationEvidenceBinding.ps1` creates the binding. `Test-QualificationEvidenceBinding.ps1` revalidates the payload, source identity, product identity and hash chain.

## Release integration

`New-ReleaseEvidence.ps1` calls `Apply-QualificationEvidence.ps1` before normal release verification.

The application stage:

1. builds and verifies a source-bound qualification evidence manifest;
2. leaves requirements without valid bindings as `UNVERIFIED`;
3. derives `supported-performance.json` exclusively from measurements in verified same-source physical payloads;
4. records the supported-performance status, path and SHA-256 in the qualification evidence manifest;
5. copies each accepted binding, payload and supported-performance evidence byte-for-byte into `artifacts/release-evidence/qualification/`;
6. rechecks SHA-256 after the copy;
7. writes `qualification-evidence-manifest.json` into the release-evidence bundle;
8. maps only manifest `PASSED` requirements to compatibility-manifest `PASS`;
9. updates the release-evidence hash references.

`Test-ReleaseEvidence.ps1` then verifies the release-local manifest, copied bytes, hashes, source commit, workflow provenance and compatibility statuses again. The offline bundle therefore remains self-contained; it does not depend on the original GitHub artifact still being available.

## Fail-closed cases

The binding and release stages reject, among other cases:

- a different source commit;
- an unknown qualification type;
- a payload schema mismatch;
- payload or binding tampering after qualification;
- a non-`PASSED` payload;
- missing functional/lifetime/timing CUDA cases;
- Media I/O evidence without an exact AJA SDK commit or `PinnedHostLease`;
- Media I/O capture or hard Program-output failures;
- timing evidence below the 30-minute soak floor;
- timing evidence without reference loss and re-lock;
- timing evidence without at least 30 independent physical latency samples;
- timing evidence without required audio/video synchronization measurements;
- timing evidence without continuous CPU/RAM/GPU/VRAM telemetry evidence;
- a static release policy that attempts to pre-mark hardware evidence as passed.

## Evidence state

Qualification Evidence Provenance changes provenance and release binding only. It does **not** manufacture physical evidence.

Until the dedicated reference-hardware workflows actually run and their bindings are supplied to a release build, the corresponding requirements remain `UNVERIFIED`:

- reference GPU;
- professional Media I/O;
- genlock/reference loss and re-lock;
- physical end-to-end latency;
- long soak.

Normal GitHub-hosted Required Gates validate the mechanism and fail-closed regressions. They are not physical hardware qualification evidence.

The Supported Performance Matrix follows the same boundary: it lists only values extracted from verified same-source physical payloads. Missing physical evidence keeps the matrix `UNVERIFIED` and produces no substituted or configured performance claim.
