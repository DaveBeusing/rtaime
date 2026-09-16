<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Media I/O Vertical Slice

## Scope

AP-33 turns the AP-32 Media I/O boundary into the first executable professional SDI path without changing production authority. ControlHost remains authoritative, RuntimeHost remains the execution owner, and media bytes remain outside management/control IPC.

The V1 reference topology is deliberately small:

- SDI input 1 -> runtime source A;
- SDI input 2 -> runtime source B;
- one Program SDI output on the third video channel;
- 1920x1080 progressive at 50 fps or 60000/1001 fps;
- RGBA8 as the normalized rtaime working format;
- embedded stereo 48 kHz audio at the managed Media I/O boundary;
- bounded, non-blocking capture/output behavior.

## Reference adapter

The native reference adapter targets the supported `aja-video/libajantv2` SDK line. Hardware qualification must use an explicitly identified release checkout or commit through `RTAIME_AJA_NTV2_ROOT` and `RTAIME_AJA_SDK_REVISION`. The deprecated predecessor repository is not the AP-33 dependency.

The adapter is built with CMake under `native/Providers/AjaNtv2`. No Visual C++ project is added to the managed solution, and the approved 28 managed-project graph is unchanged.

## Transfer mode

AP-33 intentionally qualifies `PinnedHostLease` first.

The path is:

1. AJA AutoCirculate transfers one captured frame into a page-aligned host buffer.
2. The stable ABI exposes only an opaque process-local handle plus lease identity and metadata.
3. `NativeMediaIoProviderAdapter` materializes that as `SharedLease`.
4. `MediaIoVerticalSlice` copies the current leased RGBA frame into the runtime working frame while the lease is valid.
5. The capture lease is released before another frame can be acquired.
6. Program output is pinned only for the synchronous `TrySubmit` call and passed back to AutoCirculate.

There is no unbounded managed media queue. A capture session permits at most one outstanding lease. Output uses `CanAcceptMoreOutputFrames`; lack of room becomes explicit backpressure rather than hidden buffering.

`DeviceDirectLease` is not advertised by the AP-33 AJA adapter. AJA/CUDA direct DMA or RDMA remains a future optimization and must not be claimed until measured on qualified hardware.

## Video

AJA frame stores are configured for `NTV2_FBF_RGBA`. SDI input is routed through the AJA color-space converter into the RGB frame store; Program output routes the RGB frame store through the color-space converter to SDI.

Both V1 rates are represented explicitly:

- 1080p50;
- 1080p59.94 (60000/1001).

Input format detection fails closed if the SDI signal is absent or does not match the requested V1 rate.

## Audio

When embedded audio is enabled, the native adapter configures 48 kHz capture/playout and normalizes the AP-33 managed boundary to stereo Float32. The native side converts between AJA 32-bit PCM samples and normalized Float32 samples.

The managed Program submission can apply the existing AFV gain/mute result before pinning the output audio buffer. The stable ABI 1.1 therefore carries output audio sample count and channel count in addition to the opaque audio handle.

## Signal and device loss

Media I/O status remains observational and non-authoritative.

Input status maps detected SDI state to `Locked`, `Unstable`, or `Lost`. Device readiness failure maps to `Faulted`. Output status reflects the AutoCirculate state. No hardware failure mutates Control authority and there is no automatic provider fallback when native Media I/O is explicitly selected.

Full reference recovery, genlock behavior and long-running timing stability are intentionally part of AP-34.

## Native ABI

The native ABI remains vendor-neutral even though the AP-33 implementation uses AJA NTV2. ABI 1.1 adds only information required by the physical vertical slice:

- embedded output audio sample/channel metadata;
- read-only provider identity metadata for qualification evidence.

No AJA type crosses the ABI. No raw media array is added to a control or management contract.

## Verification layers

Normal Required Gates verify:

- managed bridge compilation;
- vendor-neutral orchestration with an in-memory provider;
- capture lease release;
- bounded output/backpressure behavior;
- architecture and vendor-boundary policy;
- no accidental generic-CI hardware claim.

The dedicated reference-hardware workflow additionally builds `rtaime_media_io.dll` against the pinned `libajantv2` checkout and exercises the physical SDI path.

## Physical hardware qualification state

**UNVERIFIED**

Repository implementation, tests and normal GitHub-hosted CI are not physical AJA evidence. AP-33 may only be described as physically qualified after the self-hosted Media I/O reference workflow produces retained `PASSED` evidence for the declared AJA adapter, driver and SDK revision.

## Deferred to AP-34

AP-34 owns the broader timing/reference qualification:

- external reference/genlock qualification;
- reference-loss and re-lock behavior;
- end-to-end latency and jitter thresholds;
- sustained frame/audio continuity;
- long soak and recovery testing;
- final reference-hardware timing evidence.
