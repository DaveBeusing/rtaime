# Color-Accurate Media Monitoring

## Implemented behavior

The monitoring plane carries an explicit `ColorDescription` alongside RGBA8 frame geometry and timing. The description represents primaries, transfer function, matrix, nominal range, bit depth, alpha semantics and metadata authority. Missing metadata remains explicitly missing; the system does not infer Rec.709, sRGB or range from resolution, file type or presentation context.

Monitoring contract version 1.1 serializes the color description in the monitoring frame header. `VideoFormat` owns the source color description so the RuntimeHost monitoring tap can preserve it when producing source and Program monitoring frames.

## Operator display path

The Operator performs exactly one presentation color conversion while converting the monitoring RGBA payload into the WPF BGRA32 presentation buffer. Complete supported RGB descriptions are transformed deterministically to the sRGB display encoding. Full-range sRGB is an identity transfer; Rec.709 transfer is linearized and encoded to sRGB; limited-range RGB is expanded before transfer conversion.

Incomplete or unknown color metadata is not guessed. Pixels remain numerically unchanged apart from the required RGBA-to-BGRA channel ordering and the monitoring status reports `DISPLAY PASSTHROUGH` with the explicit metadata state.

The transform is presentation-only. It does not mutate Runtime, Program output, recording, source media or authoritative GPU state.

## Performance characteristics

The transform uses a cached 256-entry lookup table per distinct complete color description. A monitoring frame therefore performs one linear pass into the BGRA32 WPF buffer and does not allocate an additional color-conversion surface. Unknown metadata uses the same single pass without a lookup transform.

The existing monitoring plane remains intentionally downscaled and sampled independently of Program continuity. This change does not add production-frame CPU readback.

## Qualification

Contract tests cover monitoring-wire color metadata round-trip and explicit unknown metadata. Operator tests cover unknown passthrough, full-range sRGB identity and Rec.709 limited-range black/white endpoint mapping.

Creative grading, LUT authoring, monitor calibration and unsupported HDR behavior remain outside this monitoring display path.
