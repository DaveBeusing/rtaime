<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Broadcast Interoperability Qualification

## Scope and evidence semantics

This is an evidence-producing and verifying **qualification inventory**, not a
product certificate, an EBU certification or approval to enable untested media
transport. The [requirement matrix](../qualification/broadcast/interoperability-matrix.json)
defines 15 version-pinned EBU, SMPTE, AMWA and internal reference documents,
28 scoped requirements and 13 runnable software suites. The
[reference configurations](../qualification/broadcast/reference-configurations.json)
separate VIRTUAL, SOFTWARE, REFERENCE_HARDWARE-HD and
REFERENCE_HARDWARE-UHD profiles. Hardware identities (GPU/driver/NIC/switch,
codec, display, recorder, receiver and PTP) remain explicitly null until
independently inventoried on a physical reference platform.

Every requirement identifies a standard organization, document and edition,
implementer, assessor, exact profile, fixture, signal/result expectation and
source of evidence. A generated report adds the actual test date, run/attempt,
source SHA, TRX SHA-256 and classification.

| Report status | Meaning |
| --- | --- |
| QUALIFIED | Source-bound **software-only** suite with all expected test classes present, passed TRX and verified digest |
| UNVERIFIED | No measured or validated evidence for this scope; no PASS implied |
| FAILED | Executed software tests failed or evidence is contradictory |
| SUPPORTED | Reserved for separately approved product-support policy; **not emitted or accepted** by this report |

The Release Readiness Index uses the existing PASS/FAIL/UNVERIFIED vocabulary:
a valid software QUALIFIED maps only to a software PASS domain. Physical
requirements always stay UNVERIFIED until independent qualifying evidence
and release approvals exist. All automated reports retain NO-GO and
releasePromotionPerformed=false. Neither product version nor static
hardware-qualification policy is changed.

## Pinned source editions and software boundaries

| Publisher / source | Exact edition | What is currently measured |
| --- | --- | --- |
| EBU R 128 | v5.0 (2023-11) | Software loudness/true-peak regression; external calibrated meter UNVERIFIED |
| EBU R 103 | v3.0 (2020-05) | 8/10-bit numeric video range and authority negative tests; external waveform UNVERIFIED |
| EBU R 153 | v2.0 (2023-06) | HLG reference conversion only; physical UHD/HDR contribution UNVERIFIED |
| EBU R 154 | v2.0 (2023-06) | File profile/rejection tests; external UHD/HDR interchange UNVERIFIED |
| EBU R 143 | v3.0 (2025-03) | Selected broadcast gateway negative software cases; full vendor/deployed assessment UNVERIFIED |
| EBU Tech 3371 | v3.0 (2023-04) | Reference node maturity matrix only; physical EBU/JT-NM qualification UNVERIFIED |
| EBU QC Data Model | 2026-04, Catalogue API v3 | Pinned XML/schema interoperability, not all real-content QC measurements |
| SMPTE ST 2110-20 | 2022 | RTP video packetization and local UDP reference, not external ST 2110-21 shaping |
| SMPTE ST 2110-30 | 2025 | Existing L24 software reference subset; full 2025 edition conformance UNVERIFIED |
| SMPTE ST 2022-7 | 2019 | Logical dual-leg packet merge only; disjoint NIC/switch recovery UNVERIFIED |
| SMPTE ST 2059-2 | 2021 | Physical PTP lock/holdover/clock accuracy UNVERIFIED |
| AMWA IS-04 | v1.3.3 | Software registry observer/virtual resource validation |
| AMWA IS-05 | v1.2.0 | Dry-run virtual staging only; real RTP activation UNVERIFIED |

These are **different publishers and standard families**. EBU recommendations
are not a generic certificate for SMPTE, IEEE or AMWA requirements. The
software reference for an older transport subset does not establish compliance
with a newer pinned edition. The [JSON matrix](../qualification/broadcast/interoperability-matrix.json)
retains exact source links, including [EBU LIST](https://tech.ebu.ch/list)
and the [AMWA version map](https://specs.amwa.tv/nmos/by-type/).

## Reproducible software signal suites

The 13 allowlisted suites reuse existing unit, contract and integration
tests rather than reimplementing the products they measure. They cover
997-Hz stereo calibration, gated LUFS and true peak; R 103 SDR range
vectors; native HLG precision; UHD-SDR format admission; file
interchange; rational A/V clocks; ST 2110 video/PCM RTP and UDP
loopback; ST 2022-7 logical packet impairments; NMOS observer/virtual
IS-05 rejection; EBU QC 2026-04 XML; broadcast security negatives;
and committed Program/Aux continuity and failure reporting.

On Windows x64 with .NET SDK 10.0.401, after the canonical Release build:

~~~powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
$head = (git rev-parse HEAD).Trim()
./build/qualification/Invoke-BroadcastInteroperabilityQualification.ps1 -ExpectedSourceCommit $head -RunId 1 -RunAttempt 1 -RunTests
./build/qualification/Test-BroadcastInteroperabilityReport.ps1 -ExpectedSourceCommit $head -ReportPath artifacts/qualification/broadcast/interoperability-report.json -RequireSoftwareQualified
~~~

The numeric run ID in this local example is **not** GitHub Actions or physical
qualification evidence. Required Gates supply the real GitHub workflow run
and attempt. They retain all three project TRX files and the report in
one source-bound artifact. The release-readiness workflow downloads and
re-verifies that artifact and records the exact input hashes.

The verifier rejects a missing/duplicate/incomplete test class, failed or
empty TRX, tampered digest, wrong project/source/edition/profile, out-of-range
test duration, replayed foreign evidence, fabricated physical equipment and
unsupported status transitions. Missing runners remain UNVERIFIED/NO-GO.
The negative regression never retains its synthetic evidence for release.

## Independent physical reference-device qualification

Physical qualification is a **separate external lab exercise**, not a hosted
Windows software job. Collect a dated, signed-off session report with
unique device and build identities and actual measured input/output:

| Device/test dimension | Physical evidence required |
| --- | --- |
| Platform | Windows build, exact source/build SHA, product package and SDK identity |
| GPU | Device PCI ID, model, VBIOS and driver, VRAM, render latency, independent output capture |
| RTP network | Two disjoint NIC identities and addresses, switch model/firmware/ports, packet capture, RTP/SDP/SSRC/sequence, A/B loss, jitter and failover |
| PTP | Grandmaster identity, ST 2059-2 clock profile, lock/loss/relock and at least 5 min independently measured holdover |
| HD and UHD/HDR | 1080p50/59.94 and 2160p50/59.94, SDR ramps, HLG 10-bit 4:2:2 reference pattern, output colorimetry and metadata |
| Audio and A/V | -23 LUFS and true peak against a calibrated meter, pulse/flash A/V sync, drift and external physical receiver |
| NMOS | Real IS-04 registration/query and IS-05 activation/rollback with external hardware RTP sender/receiver |
| Recording/QC | Named recorder and codec/decoder, readback digest, independent MXF/UHD/HDR/metadata inspection |
| Fault isolation | Primary/secondary NIC outage, common-mode failure, high-skew/reorder, sink/decoder/storage fault and no-black/samples measurements |
| Assessment | Independent assessor, UTC dates, exact device/driver/fixture, expected and measured values, logs/captures with SHA-256, deviations, acceptance signatures |

A green managed test, software-staged source or virtual PTP simulation is
not sufficient to qualify these physical profiles. The existing
[Qualification Evidence Provenance](QualificationEvidenceProvenance.md)
and physical release-binding verifiers remain authoritative for the
twelve existing release hardware dimensions. A future broadcast-specific
hardware binding type requires its own reviewed admission, cryptographic
provenance, measurements, negative tests and support policy before use.

## Release integration and safeguards

1. The qualification runner executes only the matrix-selected tests and
   records per-project TRX, SHA-256, source commit, run identity and outcomes.
2. The independent verifier reconstructs suite/test coverage and requires
   matching matrix/device identities; no false hardware/certification PASS.
3. Required Gates CI publishes a supplemental, source-bound evidence artifact;
   Quality runs negative regression cases for stale/tampered/missing,
   unsupported, or falsely supported results.
4. The Release Readiness Index retains report and TRX hashes, maps each
   requirement to software PASS, FAIL or UNVERIFIED, and keeps physical
   broadcast domains UNVERIFIED.
5. Signed ReleaseEvidence, external product support, packaged candidate,
   Stable approvals and version allocation are not automatically affected.

Report schema:
[schemas/qualification/v1/broadcast-interoperability-report.schema.json](../schemas/qualification/v1/broadcast-interoperability-report.schema.json).
No existing IPC, SQLite, release trust schema or VersionPrefix changes.
