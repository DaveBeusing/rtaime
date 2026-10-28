<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Video signal range / colorimetry compatibility

**Standards:** [EBU R 103 v3.0, May 2020](https://tech.ebu.ch/publications/r103), Annex 1 and Table 1. [EBU Tech 3320](https://tech.ebu.ch/publications/tech3320) is a separate reference for physical monitor requirements; its conformity is not claimed.

## Code-space and measurement matrix

| Source semantics | Pixel precision at observation | Software behavior | Qualification |
| --- | --- | --- | --- |
| 8-bit limited Rec.709 primaries, Rec.709 transfer, identity RGB, authoritative | RGBA8 monitoring native code values | Original R′/G′/B′ and derived Y′, nominal/preferred/total and unique-pixel union | IMPLEMENTED, SAMPLED AND UNFILTERED |
| 10-bit limited, authoritative Rec.709 identity RGB | Native 10-bit reference vectors | Exact 64–940 / 20–984 / 4–1019 thresholds; arithmetic tests only | SOFTWARE-REFERENCE ONLY |
| 10-bit upstream source observed via RGBA8 shared/fallback surface | Converted 8-bit, native 10-bit absent | No numeric 10-bit compliance conclusion | UNAVAILABLE |
| Full-range file RGB (0–255 or 0–1023) | Varied | Not interpreted as limited range for broadcast | UNVERIFIED |
| Unknown, inferred or incomplete color authority | Varied | No invented primaries, transfer or range | UNVERIFIED |
| HDR PQ/HLG, Rec.2020, YCbCr-native or unsupported matrices | No native-format-qualified Program analysis | No false Rec.709 or native chroma assumptions | UNVERIFIED / UNAVAILABLE |
| SDI output signal, physical display measurements | Not collected here | No hardware gamut, legal level or display calibration claim | UNVERIFIED |

For limited range only, EBU R 103 v3 values are 8-bit nominal 16–235, preferred 5–246, total 1–254, and 10-bit nominal 64–940, preferred 20–984, total 4–1019 (all inclusive). The recommended out-of-gamut reporting threshold is an exceedance of **more than 1% of the active picture** outside preferred code limits. The current measured percentage covers only **active-area sampled pixels**; its result must not be represented as a full-picture PASS.

## Evidence and ownership

The single reusable `VideoRangeProfile` / `VideoRangeLimits` code-space specification lives in `src/Contracts/rtaime.Media.Contracts/VideoRangeContracts.cs`, next to the existing authoritative `ColorDescription`. Future HDR or native provider work must extend this rulebook rather than defining competing R 103 thresholds. This additive managed model introduces no new on-wire monitoring fields or release-compatibility migration.

Color Review consumes the single, already-confirmed Program A or Preview B monitoring source through OperatorMonitoringViewModel, with no extra decoder, Program GPU texture write, raw frame CPU readback or authority mutation. The existing opt-in GPU shader is extended by 15 uint counters (60 bytes), appended after 21,504 legacy scope values. The resulting fixed buffer is 86,076 bytes. The RGBA8 CPU fallback uses the exact same raw-code counting rules within its existing coalesced sample pass. Every observation preserves frame sequence, source color metadata, active raster, sample stride/count and sampling coverage. Selected source switching, disconnect and resource replacement revoke old scope evidence.

The four measured channels are **R′, G′, B′, and derived Y′**; a fifth union statistic counts each violating pixel once. Native chroma Cb/Cr sample values are not available in the monitoring RGB payload. Chroma legal-level counts are therefore explicitly **UNAVAILABLE**, not zero, and a display-code vectorscope cannot qualify original chroma codes.

EBU R 103 recommends a horizontal (1,2,3,4,3,2,1)/16 and vertical (1,2,1)/4 filter for digital signal measurements. That spatial filtering is **not** implemented in this bounded scope sample. A clean sample cannot prove that unsampled active-image pixels meet the recommendation. The implementation intentionally returns sampled / unavailable / unverified verdicts, never certified legal-range PASS. Full active-picture filtered and native YCbCr/10-bit hardware-path qualification is a separate future deliverable.

## Targeted qualification

The `VideoRangeContractTests` and `VideoSignalRangeAnalysisTests` suites cover exact inclusive 8-/10-bit boundaries and neighbors; >1% versus exactly 1%; separate RGB/Y′/union counters; excluded border/active raster; unknown/inferred/full-range/HDR-like metadata; 10-bit source through 8-bit monitoring; legacy/current fixed GPU result shapes and raw-before-display shader order. CompositingWorkspaceQualification confirms the source-bound display and fail-closed unavailable state. Existing MediaScopeAnalysisTests and GpuScopesAndFrameComparisonTests protect histogram/Waveform/Parade/Vectorscope semantics, resource budgets and GPU/CPU fallback continuity.

Required Gates CI/Quality validate the software checks. Physical CUDA/D3D11, monitor calibration, native codec/storage-to-output and SDI legal-level measurements remain UNVERIFIED until explicit qualified hardware evidence exists. No monitoring wire contract change or persisted state migration is introduced.
