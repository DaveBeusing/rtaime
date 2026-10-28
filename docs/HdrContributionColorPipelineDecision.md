<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# HDR Contribution Color Pipeline Decision

**Decision date:** 2026-10-28  
**Status:** Native-format contracts, offline reference and fail-closed capacity planning implemented; real-time HDR GPU, 2160p Program, HDR recording/output and physical HDR monitoring **NOT IMPLEMENTED / UNVERIFIED**.  
**Scope:** EBU R 153 v2.0 (June 2023) live contribution. EBU R 154 v2.0 file-exchange formats are a separate admission and writer concern.

## Context

The production `VideoFormat`/`PixelFormat` model, Media 1.0 contract,
Monitoring 1.3 wire, Windows decoder, CUDA compositor, Operator shared D3D11
monitor and recording/network output providers handle RGBA8/HD semantics.
Native high-depth/HDR metadata cannot be truthfully represented by a `ColorDescription`
with a changed numeric bit depth alone. The [UHD SDR capacity preflight](UhdSdrPipelineAdmission.md)
provides separate checked 2160p SDR RGB sizing but is **not** an HDR path.

EBU R 153 v2.0 Annex A live candidate is 3840×2160 progressive,
50 Hz or 60000/1001 Hz, 10-bit Y′CbCr 4:2:2, BT.2100/BT.2020 primaries,
BT.2100 HLG transfer, BT.2020 non-constant-luminance matrix and video levels
assessed against EBU R 103. **25p is an R 154 file-exchange case, not the R 153
live baseline.** The ITU/EBU recommendations are different documents from
SMPTE ST 2110 packetization and hardware transport conformance.

A physical monitor assessment must use EBU Tech 3320 **v4.1** together with
an appropriate measurement procedure. An 8-bit sRGB proxy does not validate
HDR display tone mapping or a Grade 1 monitor.

## Decision and alternatives

Keep all existing media/persistence/wire identities intact. Define a new,
**explicitly opt-in**, typed native HDR memory/metadata reference in
`rtaime.Media.Contracts`. It is separate from the existing RGBA8 `VideoFormat`,
which remains the only live Program type. This avoids introducing new enum
values into an established Media 1.0/Monitoring 1.3 wire without peer negotiation.

Alternatives considered:

1. **Relabel RGBA8 as HLG with BitDepth=10 — rejected.** Source precision and
   Rec.2020/HLG color, native chroma and output metadata are lost.
2. **Silently use P010 as equivalent to 4:2:2 — rejected.** P010 is 4:2:0,
   needs an independently qualified chroma reconstruction and cannot carry an
   unchanged 4:2:2 signal.
3. **Add independent 10-bit contracts and test reference processing first —
   selected.** Preserves legacy compatibility and explicitly exposes the real
   GPU/decoder/record/signal qualification dependency.

## Implemented, narrowly scoped software behavior

- `HdrContributionVideoProfile` recognizes *only* exact live 2160p50/59.94,
  native 4:2:2, CICP 9/18/9, limited range, 10-bit, opaque and authoritative
  source-bound metadata. PQ, Rec.709, 4:2:0, 25p, inferred color and unknown
  range do not match this contribution tuple.
- `HdrNativeFrameLayout` validates physical plane geometry, stride,
  non-overlap, positive dimensions and bounded memory for little-endian
  high-10-bit planar 4:2:2, separate P010 4:2:0 and linear RGBA16F staging.
  The formats have distinct byte footprints at 3840×2160: **33,177,600**,
  **24,883,200**, **66,355,200** bytes, respectively. High-precision staging
  does **not** imply a resident GPU texture or supported kernel.
- `HdrNative422Packing` losslessly roundtrips high-10-bit planes to
  Cb-Y0-Cr-Y1, five bytes/two pixels, without 8-bit downshifting or assuming
  709 color. This is a strict **offline** format reference, not a new live
  ST 2110 profile (whose existing provider remains HD-only).
- `HdrReferenceColorPipeline` can decode 10-bit 4:2:2 vectors to
  floating-point scene-linear BT.2020 using the HLG inverse OETF and a
  BT.2020 nonconstant matrix, produce RGBA16F staging bytes, and composite
  premultiplied linear reference samples with matching PTS/metadata. It is
  not a calibrated display EOTF/OOTF, GPU implementation or broadcast-grade
  color pipeline. Deliberately clipped out-of-gamut reference samples must
  never replace source pixels in Program.
- `HdrSdrPreviewPolicy` explicitly opts into an **illustrative**
  Rec.2020→Rec.709 scene-linear matrix, Reinhard scene-referred highlight
  compression and sRGB encoding for a separate RGBA8 preview. The source
  native buffer, metadata and frame timing are unchanged. The transform is
  **not** claimed to meet ITU-R BT.2408/EBU TR 070 single-master output
  conversion requirements or a calibrated monitor. No implicit fallback,
  HDR Program down-mapping or recording conversion.
- `HdrContributionFrameEvidenceWire` is a separate, fixed 384-byte,
  little-endian **v1 reference sidecar**. It carries the exact HDR native
  profile, source color metadata/provenance, rational frame rate,
  PTS/timebase, tight plane-size claim and SHA-256 for the *actual*
  unchanged native bytes. Unknown versions, malformed UTF-8, inconsistent
  transfer/range/layout and forged sizes reject. Existing Media 1.0,
  Monitoring 1.3 and stored Shows remain unchanged; the sidecar is not sent
  to legacy peers without an independently negotiated adapter.
- `HdrContributionAdmission` rejects absent/duplicate consumer capabilities,
  unqualified 10-bit/HLG metadata preservation at Program, recording, file
  export, NDI, SRT, ST 2110, SDI and MXO, missing PTS, GPU half-float
  mismatch, missing explicit SDR monitor policy and finite memory/transfer
  exhaustion. It can produce a **capacity-only** result for synthetic
  profiles; `ProductionAdmitted` is **always false** until actual
  verified providers, transactional reservation and physical evidence exist.

## Owned boundaries and prohibited shortcuts

- **ControlHost** remains the sole authoritative Production controller.
  Its Prepare/Commit/revision fence is not bypassed by the software reference.
- **RuntimeHost** retains the single real Program cadence and playback
  owner. No second live renderer or playback clock is introduced.
- Actual decoder, GPU allocations, fences, shader/kernel color math, device
  transfer, P010 reconstruction and Program sinks remain unimplemented.
  A subsequent change must bind these objects to observed source/driver/
  device identities, finite readback/retention/queue accounting and
  failure-isolated rollback before enabling any HDR runtime/Operator selection.
- **Operator** currently receives only RGBA8 monitoring; the new offline SDR
  proxy is **not connected** to its production GPU presentation path.
- **Recording** and **output providers** currently have no precise 10-bit
  HLG/metadata roundtrip; a mixed or unsupported sink is rejected before
  authoritative execution. Neither the ST 2110 reference packing nor the
  existence of a file interchange catalogue changes this fact.
- Legacy checkpoint, CLI, format defaults and old-peer semantics remain
  stable. Persisting HDR in a Show requires a dedicated versioned migration,
  acceptance of peer negotiation and full rollback testing.

## Qualification / acceptance gates

1. Real native 2160p50 and 60000/1001 HLG input with source-provenance
   metadata, pixel/pitch/bit-precision, driver/decoder fingerprint and
   exact 48-kHz sample windows; no surrogate 8-bit RGBA promotion.
2. GPU **device-resident** RGBA16F or another accepted high-precision
   rendering format with correct HLG/OOTF reference processing, BT.2020
   gamut, alpha compositing, memory/fence lifetime, native transfers and
   frame deadline evidence. Reference CPU math is **not** proof of this.
3. Color bars (including EBU Tech 3373 if licensed/available), ten-bit
   ramps, R 103 source-code excursions, chroma subsampling, negative
   colors/clipping, HLG vs PQ, HLG-to-SDR mapping and reference display
   results on a named/calibrated hardware device.
4. Source→Preview→Program→record/reopen→physical receiver continuous
   30–120-minute runs, retained original metadata/CICP/timebase and
   source hash; isolated error, overload, mode switch and recovery.
5. Required exact-source **CI, Quality, Security, Provider Smoke,
   Packaged E2E**, plus independently acquired reference monitor,
   output, timing and storage evidence. No green managed test is an HDR
   broadcast interoperability or EBU conformance certificate.

Until these gates pass the product HDR feature is in scope but the
**production status remains UNSUPPORTED; hardware status UNVERIFIED/NO-GO**.

## References

- [EBU R 153 v2.0 (June 2023) – live UHD/HDR contribution](https://tech.ebu.ch/publications/r153)
- [EBU R 154 v2.0 (June 2023) – UHD/HDR file exchange](https://tech.ebu.ch/publications/r154)
- [EBU R 103 v3.0 (May 2020) – video signal tolerance](https://tech.ebu.ch/publications/r103)
- [EBU Tech 3320 v4.1 – reference monitor requirements](https://tech.ebu.ch/publications/tech3320)
- [High resolution matrix](HighResolutionMediaCompatibility.md),
  [Color Monitoring](ColorAccurateMediaMonitoring.md),
  [Video Range](VideoSignalRangeCompatibility.md),
  [GPU surface design](GpuMediaSurfaceDesign.md),
  [Source-bound qualification](QualificationEvidenceProvenance.md)
