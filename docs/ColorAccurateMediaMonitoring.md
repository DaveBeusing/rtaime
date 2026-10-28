# Color-Accurate Media Monitoring

## Implemented behavior

The monitoring plane carries an explicit `ColorDescription` alongside RGBA8 frame geometry and timing. The description represents primaries, transfer function, matrix, nominal range, bit depth, alpha semantics and metadata authority. Missing metadata remains explicitly missing; the system does not infer Rec.709, sRGB or range from resolution, file type or presentation context.

Monitoring contract version 1.3 retains the established color semantics and carries the full `VideoFormat` color description with an optional shared GPU monitoring resource. RuntimeHost therefore publishes the same color authority for the bounded CPU fallback and GPU-resident presentation observation.

## Operator display paths

The CPU/WPF fallback converts the bounded monitoring RGBA payload into a frozen BGRA32 presentation bitmap. Complete supported RGB descriptions are transformed deterministically to sRGB display encoding. Full-range sRGB is an identity transfer; Rec.709 transfer is linearized and encoded to sRGB; limited-range RGB is expanded before transfer conversion.

The GPU path applies the same transfer/range intent in the D3D11 presentation shader while sampling the shared RGBA8 resource directly. It does not rewrite or mutate the Runtime resource.

Incomplete or unknown color metadata is not guessed on either path. The CPU path keeps numeric color values unchanged apart from channel layout; the GPU path samples them without a fabricated transfer. Diagnostics report the explicit color state and active presentation path.

## Performance characteristics

The CPU transform uses a cached 256-entry lookup table per distinct complete color description and performs one linear pass into the bounded BGRA32 fallback bitmap.

The GPU path performs the color transform during presentation sampling. It does not create an intermediate full-resolution CPU bitmap or add a production-frame GPU-to-CPU readback. Runtime/provider GPU export remains sampled independently of Program continuity.

## Qualification

Contract tests cover color metadata and shared-resource interop round trips. Operator tests cover unknown passthrough, full-range sRGB identity and Rec.709 limited-range black/white endpoints. Geometry and presentation tests qualify physical-pixel placement independently from the graphics device.

Physical GPU/WPF color equivalence, calibrated monitor output, creative grading, LUT authoring and HDR behavior remain outside software-only CI and require dedicated reference-hardware/display qualification.

## Operator Color Review workflow

The existing COMPOSITING destination now contains a reversible **LAYERS / GRAPH** versus **COLOR REVIEW** presentation mode. The review view borrows the confirmed Program and Preview frame identities, the existing GPU/CPU monitoring fallbacks, the shared ROI/zoom pixel-inspection control, and the single validated Inspector Color Grade editing flow. **Program A / confirmed Preview B** split, wipe and Difference are read-only monitoring comparisons; unavailable metadata or incompatible resources retain the existing explicit compare rejection and CPU fallback behavior.

Each of the four color scopes displays the selected source's evidence-bound snapshot: histogram, luma waveform, RGB parade and vectorscope. The selected source format and sampled frame sequence are visible, and incomplete color semantics explicitly disable vectorscope rather than inferring primaries, transfer, matrix or range. ROI remains a source-pixel inspection, not a production crop. Brightness (-1..1), Contrast (0..2) and Saturation (0..2) are edited only through the existing bounded Color Grade processing-node draft in the shared Inspector; **Apply** still uses its original authorized ControlHost path. A missing or unsupported grade node shows guidance instead of a false local preview or uncommitted grading effect.

Software tests and real UI qualification for the new mode are **PENDING** on its exact final PR head. Monitor calibration, physical output color accuracy, creative LUT authoring, and HDR-grade/output support remain **UNVERIFIED**; the UI makes no such claim.

Comparison overlays use an independent FIT projection, so they capture pointer hit-testing and disable standalone Program monitor ROI/zoom commands until comparison is switched off. This prevents an ROI selected against an underlying differently zoomed frame from being mislabeled as a compared image coordinate.


## EBU R 103 source-code signal levels (sampled diagnostic)

Color Review now exposes observational, source-bound video level diagnostics from the existing confirmed Program/Preview GPU and CPU scopes. It reads **pre-display-transform** R′G′B′ code samples and derives Y′ using the BT.709 luma coefficients. It never alters Program textures, applies a LUT, legalizes, clips or tonemaps images.

| Limited-range source | Nominal | Preferred | Total |
| --- | --- | --- | --- |
| 8-bit | 16–235 | 5–246 | 1–254 |
| 10-bit | 64–940 | 20–984 | 4–1019 |

These are the inclusive ranges in [EBU R 103 v3.0 (May 2020)](https://tech.ebu.ch/publications/r103), Annex 1 Table 1. The analyzer records absolute counts and percentages of active-image samples outside each band, separately for R′/G′/B′, derived Y′ and an any-component union that counts each pixel only once. Sampled out-of-gamut is indicated only when more than **1%** of sampled active pixels exceed the preferred band. Any pixel outside the total band is a separate warning regardless of that threshold. Source role, frame sequence, dimensions, bit depth, primaries, transfer, matrix, range, metadata authority, sample count, stride and coverage appear in the read-only Color Review diagnostics.

**No false PASS:** The current live GPU and fallback monitoring payloads use RGBA8. A 10-bit source cannot be verified at native precision by this 8-bit payload: the result is **UNAVAILABLE**. Deterministic 10-bit code vectors validate the arithmetic independently but do not constitute a 10-bit Runtime readback implementation. Only complete authoritative Rec.709/Rec.709/IdentityRgb, limited-range and matching code depth are analyzed. Unknown, inferred, full-range, unsupported HDR color or missing native precision produce **UNVERIFIED/UNAVAILABLE** instead of assuming Rec.709 or video range.

The active region is defined in source pixels. Live resources currently treat the complete published active raster as the region; reliable encoded blanking/letterbox masks remain **UNVERIFIED**. The raw RGB payload does not include original Cb/Cr values. Native chroma legal-level counters are explicitly **UNAVAILABLE**; the display-code vectorscope does not replace native chroma samples.

The existing 5 Hz and 262,144-sample scope budget is preserved, as is latest-only resource ownership. The EBU also recommends horizontal quarter-band (1,2,3,4,3,2,1)/16 and vertical half-band (1,2,1)/4 filtering before gamut classification. The new measurements are deliberately **unfiltered sparse samples** and cannot establish whole-image legal-level conformity. Full-native-precision, filtered, hardware-calibrated EBU Tech 3320 or SDI Program-output evidence remains **UNVERIFIED**.

See [VideoSignalRangeCompatibility.md](VideoSignalRangeCompatibility.md) for the qualification matrix and [GpuScopesAndFrameComparison.md](GpuScopesAndFrameComparison.md) for fixed GPU result sizes.


## Native HDR/HLG monitoring boundary (2026-10-28)

The [HDR contribution decision](HdrContributionColorPipelineDecision.md)
introduces an **offline reference** for authoritative BT.2100 HLG
10-bit YCbCr 4:2:2 source codes and an explicit, presentation-only
Reinhard scene-referred BT.2020-to-sRGB mapping. The proxy preserves
its input `FrameTiming` and does not mutate native source bytes.
`HdrSdrPreviewPolicy` is mandatory; unknown/PQ/inferred HLG metadata
cannot be auto-treated as Rec.709 or SDR.

This **does not** add HLG to the current `MonitoringFrameWire` 1.3,
GPU D3D11 shared `RGBA8` presentation shader or Operator UI.
Neither the existing transfer LUT nor `Rgba8` monitoring frame
qualifies as a native HDR display signal. The reference tone map is
not EBU TR 070/ITU-R BT.2408 validated and is not a calibrated
BT.2100 HDR monitor output. A live 10-bit/HDR monitoring pipeline
needs a separately qualified device surface, explicit output EOTF/
OOTF, range/gamut tolerances, measured luminance and EBU Tech 3320
v4.1 reference-hardware assessment. Status: **HARDWARE UNVERIFIED**.
