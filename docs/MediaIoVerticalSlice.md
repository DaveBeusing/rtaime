<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Media I/O Vertical Slice

## Scope

Media I/O Vertical Slice turns the Media I/O Decision & Foundation Media I/O boundary into the first executable professional SDI path without changing production authority. ControlHost remains authoritative, RuntimeHost remains the execution owner, and media bytes remain outside management/control IPC.

The V1 reference topology is deliberately small:

- SDI input 1 -> runtime source A;
- SDI input 2 -> runtime source B;
- one Program SDI output on the third video channel;
- 1920x1080 progressive at 50 fps or 60000/1001 fps;
- RGBA8 as the normalized rtaime working format;
- embedded stereo 48 kHz audio at the managed Media I/O boundary;
- bounded, non-blocking capture/output behavior.

## Reference adapter

The native reference adapter targets the supported `aja-video/libajantv2` SDK line. The Media I/O Vertical Slice reference source is pinned in `native/Providers/AjaNtv2/libajantv2.version.json` to the exact `release`-line commit `aa4d482a47fdd9fd9f2883163e286206ac0d7ae7`. Both generic native compile evidence and physical qualification must resolve this exact commit before building the adapter. A moving branch tip is never accepted as qualification evidence.

The adapter is built with CMake under `native/Providers/AjaNtv2`. No Visual C++ project is added to the managed solution, and the approved 28 managed-project graph is unchanged.

## RuntimeHost selection

RuntimeHost defaults to the existing virtual Media I/O path. Physical Media I/O is selected only through the explicit `native` mode:

- command line: `--media-io=native`;
- environment: `RTAIME_RUNTIME_MEDIA_IO=native`.

`virtual` and `native` are the only accepted values. When `native` is requested, RuntimeHost constructs `NativeMediaIoProviderAdapter`, opens the two input sessions and Program output through `RuntimeMediaIoVerticalSlice`, and starts only if the complete native path is available. Missing DLLs, missing devices, unsupported formats, unavailable ports or rejected sessions fail startup. There is no automatic fallback to the virtual provider.

External reference may be requested only for native mode through `--require-external-reference=true` or `RTAIME_RUNTIME_REQUIRE_EXTERNAL_REFERENCE=true`. This is an admission requirement; Timing/Reference/Latency/Soak Qualification owns the measured timing/reference qualification.

The physical bridge wraps the existing committed execution boundary:

1. physical inputs are pumped into the current source-A/source-B working content;
2. Control/Runtime routing and transition authority remain unchanged;
3. the existing compositor executes CUT/DISSOLVE/layer logic;
4. the already existing Program GPU readback is reused for monitoring and physical Program output;
5. the followed capture audio window is submitted with the existing AFV gain/mute result.

No additional Program GPU readback is introduced by Media I/O Vertical Slice.

## Transfer mode

Media I/O Vertical Slice intentionally enables only `PinnedHostLease` in the AJA reference adapter. Repository tests and native compiler evidence verify that implementation boundary; physical qualification of the transfer path remains **UNVERIFIED** until the dedicated self-hosted workflow produces retained `PASSED` evidence.

The path is:

1. AJA AutoCirculate transfers one captured frame into a page-aligned host buffer.
2. The stable ABI exposes only an opaque process-local handle plus lease identity and metadata.
3. `NativeMediaIoProviderAdapter` materializes that as `SharedLease`.
4. `MediaIoVerticalSlice` copies the current leased RGBA frame into the runtime working frame while the lease is valid.
5. The capture lease is released before another frame can be acquired.
6. Program output is pinned only for the synchronous `TrySubmit` call and passed back to AutoCirculate.

There is no unbounded managed media queue. A capture session permits at most one outstanding lease. Output uses `CanAcceptMoreOutputFrames`; lack of room becomes explicit backpressure rather than hidden buffering.

`DeviceDirectLease` is not advertised by the Media I/O Vertical Slice AJA adapter. AJA/CUDA direct DMA or RDMA remains a future optimization and must not be claimed until measured on qualified hardware.

## Video

AJA frame stores are configured for `NTV2_FBF_RGBA`. SDI input is routed through the AJA color-space converter into the RGB frame store; Program output routes the RGB frame store through the color-space converter to SDI.

Both V1 rates are represented explicitly:

- 1080p50;
- 1080p59.94 (60000/1001).

Input format detection fails closed if the SDI signal is absent or does not match the requested V1 rate.

## Audio

When embedded audio is enabled, the native adapter configures 48 kHz capture/playout and normalizes the Media I/O Vertical Slice managed boundary to stereo Float32. The native side converts between AJA 32-bit PCM samples and normalized Float32 samples.

The managed Program submission can apply the existing AFV gain/mute result before pinning the output audio buffer. The stable ABI 1.1 therefore carries output audio sample count and channel count in addition to the opaque audio handle.

## Signal and device loss

Media I/O status remains observational and non-authoritative.

Input status maps detected SDI state to `Locked`, `Unstable`, or `Lost`. Device readiness failure maps to `Faulted`. Output status reflects the AutoCirculate state. No hardware failure mutates Control authority and there is no automatic provider fallback when native Media I/O is explicitly selected.

Full reference recovery, genlock behavior and long-running timing stability are intentionally part of Timing/Reference/Latency/Soak Qualification.

## Native ABI

The native ABI remains vendor-neutral even though the Media I/O Vertical Slice implementation uses AJA NTV2. ABI 1.1 adds only information required by the physical vertical slice:

- embedded output audio sample/channel metadata;
- read-only provider identity metadata for qualification evidence.

No AJA type crosses the ABI. No raw media array is added to a control or management contract.

## Verification layers

Normal Required Gates verify:

- managed bridge compilation;
- vendor-neutral orchestration with an in-memory provider;
- capture lease release;
- bounded output/backpressure behavior;
- explicit RuntimeHost `virtual|native` selection and fail-closed native startup;
- architecture and vendor-boundary policy;
- no accidental generic-CI hardware claim.

Provider Smoke additionally clones the repository-pinned `libajantv2` source, verifies the exact commit, configures the real AJA native target with CMake, compiles `rtaime_media_io.dll`, requires exactly one resulting provider DLL, and then runs the managed provider integration smoke tests. This is native compiler evidence only; it is not physical AJA hardware evidence.

The dedicated `.github/workflows/media-io-reference-qualification.yml` workflow runs only on the self-hosted `rtaime-media-io-reference` Windows x64 runner. It:

1. reads the same repository SDK pin used by Provider Smoke;
2. resolves and verifies the exact pinned `libajantv2` commit;
3. builds `rtaime_media_io.dll` from that exact source through CMake;
4. exposes only that built native provider to the test process;
5. runs `HardwareMediaIoQualificationTests.Reference_hardware_profile_must_pass_when_explicitly_enabled`;
6. requires two locked SDI inputs, minimum capture counts, accepted Program output and zero hard capture/output failures;
7. retains `artifacts/qualification/media-io-reference.json` as immutable workflow evidence.

The evidence records source commit, expected/detected AJA adapter, driver version, exact AJA SDK revision, ABI version, managed Media I/O contract version, format, transfer mode, reference requirement, signal states and frame/backpressure counters.

## Physical hardware qualification state

**UNVERIFIED**

Repository implementation, native compiler evidence, tests and normal GitHub-hosted CI are not physical AJA evidence. Media I/O Vertical Slice may only be described as physically qualified after the self-hosted Media I/O reference workflow produces retained `PASSED` evidence for the declared AJA adapter, driver and pinned SDK revision.

A successful normal Required-Gates run therefore proves the implementation and qualification mechanism, not the presence or behavior of physical AJA hardware.

## Deferred to Timing/Reference/Latency/Soak Qualification

Timing/Reference/Latency/Soak Qualification owns the broader timing/reference qualification:

- external reference/genlock qualification;
- reference-loss and re-lock behavior;
- end-to-end latency and jitter thresholds;
- sustained frame/audio continuity;
- long soak and recovery testing;
- final reference-hardware timing evidence.
