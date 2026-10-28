<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# ADM audio-track metadata and EBU R 123 interchange

**Product state:** metadata-only implementation pending exact-head CI.
**Immersive playback/render/publish:** UNSUPPORTED.
**Complete EBU Tech 3393 / BS.2076-3 conformance:** UNVERIFIED.
**Project:** rtaime · Real Time AI Media Engine.

## Source scope and normative provenance

This feature is deliberately a **bounded metadata interchange subset** of
the Audio Definition Model (ADM), not a purported complete ADM renderer or
full vendor-independent schema validator.

- **EBU Tech 3393**, *EBU Audio Definition Model (ADM) production profile*,
  7 October 2025, is the active production-profile guidance:
  https://tech.ebu.ch/publications/tech3393
- The XML model version admitted by this implementation is **ITU-R BS.2076-3
  (February 2025)**, represented by the explicit
  `audioFormatExtended version="ITU-R_BS.2076-3"` attribute.
  Other versions are not silently upgraded.
- **EBU R 123**, *Audio Track Allocation for International File Exchange*,
  9 July 2009, Table 1, defines legacy file exchange track arrangement
  identifiers, maintained as-is for legacy SD/HD:
  https://tech.ebu.ch/publications/r123
- **EBU Tech 3293 v1.10**, *EBUCore Metadata Set*, 16 April 2020:
  https://tech.ebu.ch/publications/tech3293

The supported importer accepts either a standalone `audioFormatExtended`
element or the same ADM element embedded beneath an EBUCore
`ebuCoreMain` wrapper using the
`urn:ebu:metadata-schema:ebuCore_2014` namespace. The recognized ADM
subset is limited to the following direct children:

`audioProgramme`, `audioContent`, `audioObject`, `audioPackFormat`,
`audioChannelFormat`, `audioStreamFormat`, `audioTrackFormat`,
`audioTrackUID` and their known `*IDRef` links.

At least one programme, content, object and track UID are needed.
Links are validated for uniqueness, referenced-target existence and
non-cyclic object/pack relationships. Supported `audioObject`
`start` and `duration` use `HH:mm:ss.fffff` with nonnegative start
and positive duration. Parsed end time is derived only for this
specific supported syntax; arbitrary time formats are not
reinterpreted. Other ADM XML constructs, block-format position
extensions and vendor elements remain **unaltered in retained XML**,
but their full acoustic semantics are not qualified.

`SourceXmlBase64` stores the **original bytes** (including BOM,
comments, whitespace, namespaces, unknown nodes, original lexical
times and attribute order). XML export returns those bytes exactly,
not a reserialized `XDocument` approximation. There is no
potentially lossy conversion into EBUCore scalar fields.

## Interchange and source identity

The independent version-one bundle format is
`rtaime.media-adm.tech3393-2025.v1`.
It contains:

- stable `MediaAssetId`, full catalogue SHA-256 source fingerprint;
- admitted source ADM model version and EBU 3393 guidance reference;
- byte-preserved ADM XML, imported-at UTC timestamp;
- optional R 123 allocation code with explicit numbered physical track
  mappings to ADM `ATU_*` track UIDs, channel role and optional language.

No asset file or decoded audio frame is modified. User-selected XML
is independent editorial metadata: importing it does **not** prove
that the original file contains matching PCM/ADM. The catalogued
fingerprint binds the sidecar to the same previously admitted asset
content; it is **not** an independent rehash on every UI access.
On import of an exported JSON sidecar, its asset ID and SHA-256 must
exactly match the selected catalogue entry. If the asset changes or
is no longer available, previous metadata is not presented as
current source evidence.

The sidecar is stored at
`%LOCALAPPDATA%/rtaime/audio-adm/<asset-id>.json` through
a validated, cancellable, **atomic file rename** and may be restored
after an Operator restart. It is not part of the ControlHost's
`media.asset.catalog` optimistic-concurrency document or SQLite
schema. The existing 2-KiB `MediaInterchangeMetadata` / EBUCore
1.10 fields and their source precedence remain intact and continue
to appear in the same Inspector. The ADM XML is never copied
into the normal 1-MiB management IPC snapshot.

On a changed catalogue fingerprint or asset ID, load/import is
rejected. In-flight UI imports recheck selection, catalogue revision
and source hash before sidecar publication. The read-only
Inspector projects at most 16 track UIDs, physical assignment if
explicit, language, role, object links and supported time intervals.
Unknown physical channel order is displayed as **UNMAPPED**.

## R 123 allocation and channel meaning

The explicit, locally admitted Table 1 arrangements are:

| R 123 reference | Physical track numbers | Semantic metadata meaning |
| --- | --- | --- |
| `4b` | 1–4 | Stereo mix L/R followed by IT or effects L/R |
| `8a` | 1–8 | Stereo L/R, then discrete MCA L, R, C, LFE, Left Surround, Right Surround |
| `8b` | 1–8 | Discrete MCA L, R, C, LFE, Left Surround, Right Surround, then Stereo L/R |

Code `null` is *unassigned*. Six channel-roles in a file may be
described as **5.1 metadata only**, but they are **not** automatically
labeled R 123 `8a`/`8b`: those allocations require all eight
explicit physical track mappings. No physical track number is
deduced from XML element order. Explicit assignments must use
unique UIDs and consecutive track positions, and the profile checks
the declared role per position. It does **not** claim that a physical
media track was decoded and matched to the ADM channel. Such a
mapping is labeled `DECLARED · SOURCE CHANNEL ASSIGNMENT UNVERIFIED`.

Other R 123 profiles, arbitrary international track arrangements
and extended Atmos/HOA/S-ADM channel mappings are outside this
version-one subset and fail closed during profile import. No
silent automatic downmix to stereo is provided.

## Capability states

Keep these four states distinct:

| Question | State |
| --- | --- |
| XML subset parsed and validated? | `PARSED` (interchange implemented; full Tech 3393 conformance UNVERIFIED) |
| Can rtaime decode the described asset tracks? | `UNVERIFIED` – handled separately by the real media admission path |
| Can the stereo mixer render arbitrary 5.1 / object / HOA tracks? | `UNSUPPORTED` – no multichannel/immersive DSP |
| Can a provider publish an immersive signal? | `UNSUPPORTED` – no such qualified output provider |

The existing `AudioProductionEngine` continues processing **48 kHz
Float32 stereo** from its already admitted sources and buses.
The metadata reader has no reference to `AudioProductionEngine`,
`RuntimeProgramExecutionService`, `ControlHostService`,
Prepare/Commit, network output, recording or the audio hot path.
The Media Library actions remain optional. Imported `audioObject`
metadata never produces Program audio, gains, new speakers or an
implicit stereo downmix; silence and channel-role meaning are not
fabricated.

## Resource and security bounds

The importer reads no more than **192 KiB of source XML**; the
interchange JSON is bounded to **320 KiB**. XML parsing disables DTD
and external resolution, limits input characters and enforces
a maximum of **2,048 XML elements, 4,096 attributes, depth 24,
64 objects, 8 programmes and 16 unique track UIDs**.
Unsupported or malformed ADM versions and dangling/duplicated
IDs, invalid physical track claims and cyclic references reject
without saving. The original byte buffer is retained only on
explicit file import or selected-sidecar restore, not per Program
frame or during unrelated catalogue operations.

A source-assignment mismatch or invalid sidecar is reported to the
Operator but does not block existing playback. Explicit import/export
uses local file dialogs; no external dependency, decoder, license
or DTD schema fetch is introduced. Full Tech 3393 normative profile
validation against all schema types, common definitions, BW64/CHNA
track binding, BWF/MXF embedded ADM extraction, S-ADM live metadata,
codec payload analysis, Dolby/Atmos tooling, object renderer, HOA
processing, immersive monitor/output and hardware test remain
**UNVERIFIED / UNSUPPORTED** until individually implemented and
qualified. No Tech 3392 compliance is claimed.

## Tests and acceptance

Test fixtures under `tests/fixtures/adm/2025/` cover standalone
BS.2076-3 stereo, six-channel 5.1, explicit eight-track R 123
`8a` mapping and a source-preserved moving/object metadata
example with an unknown vendor extension.

Use canonical repo SDK 10.0.401 and Windows-x64 build:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~AdmAudio"
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter "FullyQualifiedName~AdmAudio"
dotnet test tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj --configuration Release --no-build --filter "FullyQualifiedName~AdmMetadata"
```

Unit regressions require exact XML and JSON roundtrips (including BOM
and unknown fields), strict graph and R 123 mapping integrity,
unsupported layout rejection, source hash/asset mismatch, safe DTD/
oversize/depth failures, atomic persistence/restart, and **bit-identical**
stereo mix output with and without ADM metadata. Operator and
architecture tests guard no production dispatch and bounded read-only
display.

Required Gates: CI, Quality, Security, Provider Smoke and Packaged E2E.
The precise PR-head build/CI result and any hardware profile must be
checked before merge. Managed tests do not certify EBU Tech 3393
conformance or physical immersive broadcast output.
