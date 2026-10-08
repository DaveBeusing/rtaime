# Compositor and Processing Pipeline Optimization

## Status

- implementation baseline: master at `587146aeca98efb2deecf2cedd331fae7d33106e`
- .NET SDK: 10.0.401
- stable contract version change: none
- compositor authority change: none
- backend interface change: none
- fused multi-layer CUDA kernel: not introduced
- runtime shader compilation: none
- physical CUDA P95/P99 performance comparison for this source: UNVERIFIED

This document records the compositor pass model, the exact no-op pass reduction implemented from source semantics, the software profiling surface, and the evidence required before any fused multi-layer CUDA kernel is permitted.

## Authority and semantic baseline

ControlHost remains production authority. RuntimeHost owns committed execution, timing and the single Program renderer/compositor. Operator remains observational.

The authoritative V1 order remains:

```text
Background A/B transition
→ ordered RGBA layers
→ final Program surface
→ one Program readback
→ monitoring / recording / replay / network consumers
```

The optimization does not create a second compositor, renderer, timing authority, state store or downstream pixel path.

All requested layers are still validated for ownership, lifetime, format, timing and uniqueness before backend processing. A semantically empty layer is skipped only after the request has passed the same validation as before.

## Ordered straight-alpha semantics

The integer transition remains:

```text
(A * (255 - weight) + B * weight + 127) / 255
```

For each visible layer, effective alpha remains:

```text
(sourceAlpha * opacity + 127) / 255
```

RGB composition remains:

```text
(background * (255 - alpha) + foreground * alpha + 127) / 255
```

Output alpha remains:

```text
foregroundAlpha + (backgroundAlpha * (255 - foregroundAlpha) + 127) / 255
```

Layer order remains authoritative. Later contributing layers remain visually above earlier contributing layers. No floating-point blending or tolerance-based semantic change is introduced.

## Pass model

Before this change the provider submitted:

- 0 requested layers: 1 backend pass for the A/B transition;
- N requested layers where N > 0: N backend passes.

The first layer pass resolves the A/B transition and that layer in one backend operation. Each later layer pass uses the prior intermediate surface as its base and applies the next ordered layer.

The safe optimization classifies a layer as contributing only when:

```text
Visible == true && Opacity != 0
```

A hidden layer or an opacity-zero layer is an exact no-op for every RGBA input because it cannot change RGB or alpha. Those passes are therefore not submitted to the backend.

The resulting backend-pass count is:

```text
max(1, contributingLayerCount)
```

Requested layer count, ordering metadata and validation behavior remain unchanged. This is not a heuristic and does not inspect vendor state or raw pixels.

## Logical frame-byte traffic model

For RGBA8 1920x1080 one full frame is 8,294,400 bytes.

With the current primitive:

- a transition-only pass logically reads two full frames and writes one: `3 × frameBytes`;
- a pass with a layer logically reads Background A, Background B and the layer and writes one output: `4 × frameBytes`.

For fully contributing layer counts this gives the following source-level logical access volume per request:

| Contributing layers | Backend passes | Logical frame-byte accesses |
| ---: | ---: | ---: |
| 0 | 1 | 24,883,200 bytes |
| 1 | 1 | 33,177,600 bytes |
| 2 | 2 | 66,355,200 bytes |
| 4 | 4 | 132,710,400 bytes |
| 8 | 8 | 265,420,800 bytes |

For later passes the current backend receives the same prior intermediate surface as both background inputs with CUT-to-A semantics. The CUDA kernel still contains both background loads. That is a source-level optimization candidate, not proof of physical DRAM traffic because GPU caches, compiler behavior and driver execution are hardware-dependent.

Skipping explicit no-op layers reduces submitted backend passes and their logical full-frame accesses exactly. Physical GPU memory bandwidth, kernel time and PCIe behavior remain UNVERIFIED until measured on reference hardware.

## Profiling and tail evidence

`GpuCompositorPerformanceProfileTests` exercises:

- 0 / 1 / 2 / 4 / 8 requested layers;
- all-contributing and no-op-heavy alpha/visibility patterns;
- actual backend pass counts through a counting backend around the managed semantic reference;
- calculated logical frame-byte traffic;
- P95 and P99 managed-reference wall-clock samples.

The existing 1080p managed-reference performance tests continue to cover 1080p50 and 1080p59.94 scaling. These tests are software regression evidence only and must not be described as CUDA throughput or production GPU latency.

The provider also emits one bounded observation per successful composite with the actual backend pass count and the number of skipped no-op layers.

## Golden parity coverage

`GpuCompositorOptimizationTests` uses an independent test-side implementation of the declared integer blend equations and validates byte-exact output for:

- layer counts 0 / 1 / 2 / 4 / 8;
- odd 3x3 RGBA8 dimensions;
- transparent source-alpha boundaries;
- opacity 0 and 255;
- hidden layers;
- CUT/DISSOLVE-compatible integer behavior;
- the reduced-pass path.

Existing `GraphicsOverlayIntegrationTests` remain authoritative for deterministic bitmap rotation, anchor and crop behavior and for Color Grade execution before GPU composition. Those operations continue to materialize into the established Runtime-owned dynamic layer surfaces.

No tolerance is required for the declared integer GPU composite operations.

## Static graphics reuse

Static and dynamic graphics reuse remains keyed by the existing provider-internal source identity, format, generation, RGBA buffer identity and content version rules from the GPU memory-transfer work.

This optimization does not add a second cache. Transform, crop, rotation or Color Grade mutations continue to update the Runtime-owned dynamic layer content/generation and therefore invalidate reusable GPU upload retention deterministically.

## Shader and kernel policy

The CUDA backend continues to use the existing embedded `CompositeKernelPtx`. The module is loaded during backend startup with `cuModuleLoadData`.

No NVRTC dependency, runtime source compilation, per-frame module build or arbitrary shader graph is introduced. Therefore this change adds no runtime shader compilation spike.

## Why a fused multi-layer CUDA kernel is not introduced

A fused 2/4/8-layer kernel could theoretically reduce kernel launches and intermediate full-frame surfaces. It would also change register pressure, memory-access patterns, argument layout, error/recovery behavior and qualification scope.

The available hosted software evidence cannot prove that this complexity improves the complete production boundary on the reference NVIDIA GPU.

A fused implementation is therefore blocked until exact-SHA reference-hardware comparison demonstrates all of the following:

1. identical output pixels for the full golden matrix;
2. lower P99 complete composite/readback latency or another explicitly approved production metric;
3. no P95/P99 regression for 0- and 1-layer cases;
4. bounded intermediate/in-flight resources;
5. unchanged stop/recovery behavior;
6. no monitoring or recording pixel divergence;
7. no runtime shader compilation or qualification fallback.

The physical matrix must include 1080p50 and 1080p59.94, CUT and DISSOLVE, 0/1/2/4/8 layers, alpha/opacity extremes, warm-up and steady-state samples.

Until that evidence exists, fused-kernel performance benefit remains **UNVERIFIED**.

## Program consumer parity

RuntimeHost still obtains the final Program payload from the same post-composite `_gpu.RentReadback(output)`.

That one owned payload is then used or retained by:

- Program monitoring;
- recording;
- replay;
- Program network output;
- physical Program output through the existing host path.

GPU monitoring may additionally export the same final output surface through the existing bounded shared-resource path. None of these consumers render Program independently.

## Evidence boundary

Software CI can prove exact integer semantics, pass-count reduction for explicit no-op layers, bounded ownership, no runtime compilation path, transform/crop/Color Grade regressions and single-renderer architecture.

It cannot prove physical GPU memory bandwidth, occupancy, cache behavior, kernel P95/P99 or the value of a fused kernel.

Those hardware-only claims remain **UNVERIFIED** without exact-source reference-hardware evidence.
