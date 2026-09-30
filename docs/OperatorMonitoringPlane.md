<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Operator Monitoring Plane

## Scope

Operator Monitoring Plane provides non-authoritative visual Preview and Program monitoring to the V1 Operator. The monitoring plane is deliberately separate from the versioned ControlHost management path. It carries observation only and cannot mutate production state, commit Runtime execution, change routing, control recording or influence Program continuity.

The authoritative control path remains:

`Operator -> rtaime.Client -> ControlHost`

The visual monitoring path is independent:

`RuntimeHost committed media -> monitoring tap -> bounded subscriber -> dedicated monitoring Named Pipe -> rtaime.Client monitoring transport -> RtaimeMonitorPresentation`

When the qualified Windows graphics path is available, `RtaimeMonitorPresentation` prefers a read-only GPU-resident shared resource. The existing frozen WPF bitmap remains the deterministic fallback for Combined presentation and the bounded source for the legacy 1×1 inspector, CPU scopes and Difference analysis. Full-frame channel isolation and ROI statistics use the shared GPU resource instead of materializing another CPU frame.

## Monitoring contract

`rtaime.Media.Contracts` defines `MonitoringContractVersion` and `MonitoringFrameDescriptor`. Operator Monitoring Plane uses monitoring contract version `1.3` independently from the primary Media contract version.

Each frame declares:

- stream kind (`Source` or `Program`);
- source identity;
- bounded fallback monitoring width and height;
- RGBA8 pixel format;
- production frame sequence and presentation timing;
- explicit color metadata;
- shared-resource capability state distinct from current-frame resource availability;
- an optional read-only `MonitoringSharedResourceDescriptor` with resource/provider/surface identity, full video format, storage domain and generation/lifetime;
- narrow Windows graphics presentation metadata when the resource can be opened by the Operator;
- an exact RGBA fallback payload length, or zero payload bytes only for consumers that explicitly do not require the CPU fallback.

The stable contract does not expose CUDA device pointers, CUDA types or provider-internal opaque surface handles. Windows graphics interop data is presentation metadata only and never grants mutation or production authority.

## Runtime capture boundary

The RuntimeHost monitoring tap is fed from committed execution after timed input processing. Source A and Source B retain the bounded 320×180 observational payload. The source selected by authoritative Preview routing may additionally be materialized through the already selected GPU provider so the sampled Preview observation can carry a shared GPU resource.

Program monitoring is tied to the actual post-composite output frame. RuntimeHost continues to use the existing Program readback required by the managed V1 reference pipeline; GPU monitor export does not add another Program `Readback` or `ReadbackInto`.

The GPU presentation export is sampled at the same monitoring boundary. On the qualified CUDA/Windows backend, the provider performs a GPU-resident copy into a shareable D3D11 texture and publishes only its bounded read-only lease metadata. This is not a second decoder, compositor or Program renderer.

Asynchronous ownership remains explicit. Replacing a pending sample releases its Program readback retention and shared Preview/Program resources. Publishing a newer sampled observation releases the previously retained presentation resources. Subscriber disconnect, Runtime shutdown and provider stop release remaining resources deterministically.

## GPU-resident Operator presentation

The Operator owns one presentation abstraction: `RtaimeMonitorPresentation`. Its template contains both:

- `GpuMonitorPresentationSurface`, the preferred Windows/D3D11 presentation surface;
- the existing high-quality WPF `Image`, retained as deterministic fallback.

The GPU surface opens only `WindowsGraphicsSharedHandle` resources whose adapter LUID matches its D3D11 device. It opens the texture read-only for shader-resource presentation, never as Runtime or Program authority.

Fit, Fill, Pixel Perfect, free zoom and pan continue to use `MediaPresentationGeometry`. The viewport is measured in device-independent units and converted to physical pixels using the active WPF DPI scale. The D3D surface is sized to the physical viewport and inverse-scaled only for WPF placement, avoiding a second media scaling stage.

Sampling is deliberate:

- normal fractional scaling uses linear sampling;
- integral/pixel-inspection scaling uses point sampling;
- 100% Pixel Perfect maps one full-resolution shared-resource pixel to one physical display pixel;
- the source-aligned pixel grid remains a separate overlay and does not resample media.

The GPU shader applies the same qualified source-to-sRGB transfer/range intent as `MonitoringDisplayTransform`. Incomplete color metadata remains passthrough rather than guessed. Presentation changes do not modify Runtime or Program pixels.

The same presentation surface also owns viewer-local full-frame channel inspection. Combined, Red, Green, Blue, Alpha and Luma are selected in the D3D11 pixel shader without changing the shared texture. Alpha is rendered as opaque grayscale; Luma uses the existing Rec.709 display-code weighting. If the GPU resource cannot be presented, non-Combined channel views become explicitly unavailable instead of synthesizing a CPU full-frame channel image.

Bounded ROI statistics run in a D3D11 compute shader against the opened read-only monitoring texture. The ROI is supplied in source-pixel coordinates. Analysis is rate-limited to 200 ms and bounded to 262,144 samples; larger regions use a deterministic sample stride. The compute shader reduces count, mean, minimum and maximum values into a 16-element uint buffer. Only that reduced numeric buffer is copied to CPU-visible staging memory.

## Fallback and recovery

GPU presentation fails open to the existing WPF path. Fallback remains available when:

- the provider reports no shared-resource capability;
- a sampled frame has no shareable resource;
- the shared handle cannot be opened;
- the graphics device is unavailable or recreated;
- the resource belongs to a different adapter;
- a newer monitoring generation replaces the current resource;
- Runtime/provider restart invalidates the previous provider generation.

`OperatorMonitoringViewModel` accepts only newer resources within a provider generation and replaces the GPU projection from the ordered monitoring stream. `GpuMonitorPresentationSurface` releases opened D3D resources on frame replacement and graphics-surface unload. A failed open or draw leaves the CPU/WPF image visible; a later valid frame can retry the GPU path without affecting command/control operation.

The diagnostics HUD reports the active presentation path from actual surface state rather than assuming GPU success from capability alone.

## Bounded and loss-tolerant behavior

Monitoring is subordinate to Program continuity:

- monitoring is sampled once every four production boundaries;
- the CPU fallback/analysis image remains 320×180 RGBA8;
- the Runtime tap retains at most one pending sampled boundary;
- a newer sample replaces older pending work under pressure;
- subscriber queues are bounded and drop old observations rather than block Runtime;
- shared Preview and Program leases are bounded by the GPU provider;
- no subscriber causes the monitoring tap to stop retaining observational resources;
- monitoring reconnects independently from ControlHost synchronization.

A slow, disconnected or failed Operator may therefore observe dropped or stale monitoring frames. That state never delays committed Program execution.

## Dedicated transport

RuntimeHost exposes a dedicated output-only Named Pipe at:

`<runtime-endpoint>.monitor`

For the default V1 endpoint this is:

`rtaime.v1.runtime.default.monitor`

The Operator can override it with `RTAIME_MONITOR_ENDPOINT`. Otherwise it derives the endpoint from `RTAIME_RUNTIME_ENDPOINT`.

The transport carries frame observations only. It exposes no Set Preview, CUT, DISSOLVE or other production mutation.

## Preview and Program behavior

**Preview** selects source observations by the authoritative Preview routing received from ControlHost. When the active sampled Preview source has a presentable shared resource, the existing monitor uses that GPU resource; otherwise the cached WPF source image remains available.

**Program** consumes the post-composite Runtime monitoring observation and prefers its GPU resource when presentable. Program presentation never reconstructs transitions, graphics or output state in the Operator.

Monitoring health remains separate from ControlHost connection health. Loss of the monitoring pipe can mark monitoring `STALE` without changing authoritative control state.

## Clean Program

Program Output / Clean Feed reuses the same `OperatorMonitoringViewModel.ProgramGpuFrame` and `ProgramImage` as the in-workspace Program monitor. It does not create a second monitoring subscriber, decoder, compositor or frame transport.

The clean window contains only the Program presentation surfaces. It does not add diagnostics HUD, guides, safe area, center marks, pixel grid, scopes, comparison or inspection overlays. If the shared GPU resource cannot be presented, the same Program WPF fallback remains visible.

## Pixel inspection, scopes and comparison

Pixel inspection keeps the full-resolution GPU presentation coordinate as its source coordinate. The existing 1×1 inspection sampler remains intentionally bounded to the already-present 320×180 CPU monitoring bitmap. The full-resolution coordinate is deterministically mapped to its bounded monitoring sample and is reported as display-code evidence, not fabricated source-code evidence.

Full-frame channel inspection and ROI statistics are separate GPU-resident presentation capabilities. ROI analysis returns only reduced numeric evidence; it does not transfer ROI pixels or a full-resolution frame to the CPU. Technical scopes and Difference analysis continue to consume the bounded CPU fallback at their existing limited cadence. None of these tools creates another decoder, full-resolution Program readback or production renderer.

## Verification

`build/quality/Test-OperatorMonitoringPolicy.ps1` checks authority separation, bounded/loss-tolerant monitoring, GPU-resident presentation, retained WPF fallback, Clean Program reuse and the absence of GPU-to-CPU copy operations in the CUDA/D3D11 export path.

Automated coverage qualifies:

- monitoring contract and Windows graphics interop round trips;
- bounded shared-resource ownership and provider restart identity;
- Preview/Program resource publication and release;
- Fit/Fill/Pixel Perfect physical geometry over qualified DPI scales;
- full-resolution source-coordinate mapping to the bounded 1×1 inspection payload;
- source-space ROI clamping and stable projection through Fit/Fill/Pixel Perfect/zoom/pan/DPI changes;
- GPU channel-mode shader semantics and bounded 16-value ROI reduction;
- one GPU monitor surface plus one WPF fallback surface without nested Viewbox scaling;
- Clean Program reuse of `ProgramGpuFrame` with no Operator overlay layer;
- absence of CPU bitmap materialization inside the active GPU presentation control.

Physical NVIDIA/CUDA/D3D11 interop, per-monitor device-loss behavior and final color/visual equivalence still require reference-hardware qualification. Repository Required Gates remain authoritative for CI, Quality, Security, Provider Smoke and Packaged E2E.
