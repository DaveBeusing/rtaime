<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Media I/O Decision & Foundation

Status: **ACCEPTED for AP-32**  
Decision date: **2026-09-16**  
V1 reference adapter target: **AJA NTV2 SDK**  
Physical hardware qualification state: **UNVERIFIED**

## Decision

rtaime V1 will use the **AJA NTV2 SDK as the reference professional SDI media-I/O integration path**. The stable rtaime contracts remain vendor-neutral. AJA-specific types, headers and lifecycle rules are not permitted in `rtaime.Media.Contracts`, `rtaime.Provider.Contracts`, Control, Runtime or management IPC.

The implementation boundary is:

```text
Runtime / Media pipeline
	-> rtaime Media-I/O contracts and admission
	-> IMediaIoProviderAdapter
	-> rtaime_media_io_abi.h
	-> native AJA NTV2 adapter (AP-33)
	-> AJA SDK / driver / hardware
```

The AP-32 repository continues to contain exactly the approved 28 managed projects. No vendor SDK package and no native build project is introduced in this package. The first physical adapter is intentionally deferred to AP-33 so that the native dependency is justified by an explicit hardware boundary rather than added to the bootstrap graph speculatively.

## Why AJA NTV2 is the V1 reference path

The decision is based on the following V1 priorities:

1. **Auditable native boundary.** AJA exposes the core `libajantv2` SDK through an open-source path under the MIT license, allowing the provider boundary and SDK interaction to be reviewed without requiring vendor types in managed contracts.
2. **Professional capture/playback focus.** The AJA developer path targets KONA/Corvid class professional I/O hardware and provides a full developer-program path for supported OEM integration.
3. **C++ adapter fit.** NTV2 naturally fits the project technology baseline: managed C# owns platform behavior and orchestration; C++ is introduced only at the low-level hardware/SDK boundary.
4. **Build reproducibility.** The reference adapter can be isolated under `native/Providers` without making stable managed assemblies depend on proprietary binary packages.
5. **Future provider plurality.** The rtaime-facing ABI contains no AJA identifiers, allowing a DeckLink or other professional adapter to be added later without changing Control/Runtime contracts.

Blackmagic DeckLink remains a valid future adapter option. The current DeckLink SDK is cross-platform and its Windows API is COM-based; current documentation also describes NVIDIA GPUDirect support on Windows/Linux x86_64. It was not selected as the V1 reference adapter because AP-32 prioritizes a directly auditable/open core SDK and a narrow native C++ boundary. This is a reference-provider decision, not a claim that AJA hardware is universally superior.

## Contract model

### Stable media-I/O contract

`MediaIoContractVersion` is independently versioned at `1.0`. It introduces:

- `MediaIoPortId`
- input/output direction
- V1 SDI transport kind
- signal state
- transfer modes
- vendor-neutral native pixel encoding descriptors
- input frame descriptors that require explicit shared leases
- port health/status descriptors

This does **not** change `MediaContractVersion` or `ProviderContractVersion`.

### Logical vs native video format

The runtime-facing normalized V1 video formats remain:

- 1920x1080p50 RGBA8
- 1920x1080p59.94 RGBA8

Physical I/O adapters may capture/output a different native wire/storage encoding. AP-32 explicitly models the initial native encodings:

- RGBA8
- UYVY 8-bit 4:2:2
- V210 10-bit 4:2:2

Native encoding is a provider-boundary concern. Conversion into the normalized rtaime frame format belongs in the media-I/O adapter/conversion path and must not leak vendor SDK types into platform contracts.

## Buffer and lifetime model

Bulk media is never transported through management/control IPC and is never represented as managed byte arrays in the media-I/O contract.

The preferred transfer order is:

1. `DeviceDirectLease`
2. `SharedOpaqueHandle`
3. `PinnedHostLease`

A provider may advertise only the modes it actually supports. Admission fails closed if a requested mode is unavailable. No adapter may silently claim device-direct operation while copying through host memory.

Captured video returned to managed code must use:

- `SurfaceOwnership.SharedLease`
- an explicit lease identity
- an opaque surface handle
- explicit frame timing
- provider-owned release on lease disposal

This makes buffer lifetime observable and prevents accidental ownership transfer or unbounded copies.

## Native ABI

`native/Providers/MediaIo/rtaime_media_io_abi.h` is the only AP-32 native-facing ABI definition. It deliberately contains:

- opaque provider/session handles
- port enumeration
- session configuration
- signal status
- non-blocking input acquire/release
- non-blocking output submit
- opaque surface/audio handles
- lease identity and timing metadata

It deliberately does **not** contain:

- vendor SDK headers or types
- pixel/audio payload pointers intended for management serialization
- Control or Runtime authority structures
- host-to-host transport

The ABI is versioned independently as `1.0`.

## Admission and capability model

A media-I/O session is admitted only when all of the following are true:

- provider identity matches the selected provider profile
- provider is not unavailable
- port exists and direction matches
- normalized video format is declared
- transfer mode is declared
- requested embedded audio format is declared
- external reference is available when required
- the generic provider descriptor advertises the matching input/output capability

Physical ports map one-to-one to generic `ProviderResourceDescriptor` resources, preserving existing capability planning and reservation semantics.

## Audio

AP-32 preserves embedded audio as a handle/descriptor path using the existing `AudioBufferDescriptor`. No audio byte payload is added to Media I/O contracts. V1 logical audio remains compatible with the existing stereo 48 kHz model; native adapter conversion/interleave details stay behind the provider boundary.

## Timing and reference

The adapter must preserve capture/output sequence number, presentation timestamp and timebase. External reference support is declared per port and is an admission requirement when requested.

AP-32 does not claim genlock, reference-lock accuracy or end-to-end latency qualification. Those require physical evidence in AP-33/AP-34.

## Failure behavior

Media I/O is not production authority. Device/signal failure must be represented as provider/port status and may degrade execution, but it must not mutate authoritative Control state by itself.

Input acquisition and output submission are non-blocking seams. Backpressure, lost signal, device loss and queue exhaustion must be observable and bounded. AP-33 must not introduce synchronous vendor calls into Control management paths.

## AP-32 deliverables

AP-32 establishes:

- vendor-neutral media-I/O contracts
- provider port/profile contracts
- admission validation
- in-process adapter/session interfaces
- explicit input lease lifecycle
- vendor-neutral native ABI
- AJA NTV2 as the V1 reference adapter decision
- contract/unit/policy guardrails

## Deferred to AP-33

AP-33 will implement the first physical vertical slice. It must add evidence for:

- AJA device discovery
- two V1 video inputs
- one Program output
- embedded audio input/output path
- 1080p50 and 1080p59.94
- native-to-normalized conversion
- bounded capture/output queues
- device/signal loss and recovery
- surface lease release under failure
- actual transfer-mode reporting
- RuntimeHost composition through the provider seam

A specific AJA hardware SKU must be declared by the AP-33 qualification environment. AP-32 does not hard-code or falsely qualify a physical device.

## Source basis for the vendor decision

Current vendor documentation reviewed on 2026-09-16:

- AJA Developer platform: open-source `libajantv2` path under MIT plus a full NTV2 Developer Program for professional KONA/Corvid integrations.
- Blackmagic Design Desktop Video 16.0 SDK / DeckLink SDK Manual, released 2026-04-08: Windows/macOS/Linux support, COM-style Windows integration and documented GPUDirect capability on supported Windows/Linux x86_64 systems.

These vendor facts support only the reference-provider decision. Actual rtaime compatibility and performance remain evidence-driven and unverified until AP-33 hardware qualification.
