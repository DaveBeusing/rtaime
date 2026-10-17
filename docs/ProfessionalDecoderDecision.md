<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# ADR: professional local decoder dependency boundary

Date: 2026-10-17  
Status: PROPOSED; adoption and new input scope NOT APPROVED  
Source inventory: [capability matrix](LocalMediaDecoderCapabilities.md), baseline
`7bd13506272dec071bccda23beb767da44562fac`.

## Context and current decision

Keep the existing Windows Media Foundation provider and approved MP4/restricted
MOV path. ProRes, DNxHD/DNxHR, MXF input and production FFmpeg remain rejected.
The scope approvals recorded by existing local-media documentation cover MOV/H.264
AAC/PCM and two A/B file decks; they do not authorize these additional codecs.
No separate accepted professional-decoder approval is present in the inspected
repository. This record prepares a decision; it does not grant Product Owner or
architecture approval.

The capability gap is unavailable professional input functionality, not a measured
reason to replace Program rendering or its clock. No new project, runtime library,
plugin discovery, fallback during Program or persisted-state migration is proposed.

## Alternatives

| Concern | Existing Windows Media Foundation / narrowly approved additions | One optional native provider candidate: FFmpeg libraries |
| --- | --- | --- |
| Functionality | Reuse source reader and existing ownership; each proposed tuple still needs native evidence | Candidate for formats missing from the Windows path; individual profiles still require qualification |
| Redistribution | Existing path uses installed Windows components, not redistributed codec DLLs; extension/add-on distribution rights require separate review | Review exact configured build, every dependency, notices, corresponding source and redistribution obligations before adoption |
| License/patents | Windows availability is not a redistribution or codec patent clearance | Upstream generally LGPL 2.1+, optional GPL components change obligations; no patent clearance inferred from open source |
| Servicing | Windows/component updates can change available transforms; record build and actual selected binaries | Maintainer owns security triage, rebuild/requalification and offline update delivery |
| Deployment | Existing installed-host dependency; missing transform fails closed | Ship only an approved, immutable Windows x64 binary set in signed offline bundles; no PATH lookup, runtime download or arbitrary DLL search |
| Reproducibility | Per-host evidence needed; Source Reader currently does not expose selected MFT identity in outcomes | Pin source commit/archive hash, toolchain, configure flags, ABI, DLL hashes and transitive dependencies; version label alone is insufficient |
| Maintenance | Small existing C# interop surface; additional transforms may need owner/licensing support | Additional ABI wrapper, native ownership and vulnerability surface; must remain behind the existing probe/decoder seams |
| Current status | Existing path retained; no new format approved by this record | Evaluation candidate only; production version/build/hash intentionally NOT SELECTED |

A Windows extension is not presumed to supply ProRes/DNx/MXF. Microsoft's native
format list does not establish those tuples. A third-party MFT also constitutes
a dependency/scope decision and cannot bypass this gate.

The existing test-tool pin is FFmpeg 7.1.1 full-build archive, SHA-256
`D760E1B3574402ED18B4865851F87D87E73965A982E6453212DF8621FED1C508`,
verified by Install-ReferenceMediaEncoder.ps1. It is a test encoder/prober only.
Its libx264/full-build configuration, download location and hash are not a
production decoder selection or a license/security acceptance. A future candidate
must be assessed against current upstream security advisories and exact build flags;
this ADR does not recommend reusing that old test binary in Runtime.

## Required adoption record

Before implementation, obtain a Product Owner decision listing exact input tuples
(container, codec/profile, chroma/depth, alpha/field order/color, audio and timecode),
V1 inclusion, two-deck scale/resource limits and platform. Obtain architecture
acceptance of the provider/ABI/lifecycle design and an explicit dependency decision.

The dependency decision must attach the exact native pin/build recipe/hashes,
component and license inventory, redistribution/source/relinking obligations,
codec patent/licensing assessment, security owner/advisory review, approved update
and rollback path, supported Windows/driver matrix and packaging verification.
No such production pin or approvals are claimed here.

After approval, extend typed capabilities and fail-closed negotiation only at the
existing provider/probe seam. Keep decoded payloads Runtime-local, reuse bounded
preroll/leases and source timestamps, preserve seek/flush/generation fencing and
cleanup quarantine. Missing required audio, unsupported edits/scan/color and unavailable
native codec must be explicit failures. Never advertise a profile from extension,
enum membership or installed OS alone.

Acceptance requires deterministic generated plus independently checked assets,
native lifecycle/failure tests and exact-source Required Gates. Physical throughput,
latency, A/V and endurance require the separate reference-host acceptance procedure
in the capability inventory. Software success cannot close that hardware gate.

## Consequences

This preparation can be reviewed and integrated independently. It changes no current
input admission, public contracts, product version target (0.5.39) or saved production.
Professional codec implementation remains blocked on the approval record above;
existing packaged lifecycle and playback qualification failures remain separate
release blockers and cannot be waived by this ADR.

## Primary references

Reviewed 2026-10-17; these inform the alternatives, not a legal approval:

- [Microsoft native Media Foundation formats](https://learn.microsoft.com/en-us/windows/win32/medfound/supported-media-formats-in-media-foundation): distinguishes built-in sources/codecs and third-party extensions.
- [FFmpeg licensing and legal considerations](https://ffmpeg.org/legal.html): build-dependent LGPL/GPL obligations and patent considerations; review exact intended distribution.
- [FFmpeg security advisories](https://ffmpeg.org/security.html): assess the selected source/build against applicable fixes before pinning and again before release.
