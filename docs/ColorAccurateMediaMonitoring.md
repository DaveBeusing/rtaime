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
