<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Media interchange metadata: EBUCore 1.10 mapping profile

## Status and authority

This is a **bounded, versioned, EBUCore 1.10-interoperable projection** attached to the existing persistent Media Library asset descriptor. It is not an EBUCore XML implementation, EBU certification, automatic source-tag extractor or decoder qualification.

- Canonical asset identity, source fingerprint and availability remain with ControlHost's existing `media.asset.catalog` document. No new SQLite schema/table or catalog identity is created.
- `MediaAssetDescriptor.InterchangeMetadata` is optional. Legacy `rtaime.media-asset-catalog.v1` payloads without the property remain readable and are not silently synthesized into new source evidence.
- The catalogue persists `interchangeJson` as an optional serialized field on each asset in the **same optimistic, checksummed document transaction**. The outer catalogue format remains `rtaime.media-asset-catalog.v1`.
- The interoperable JSON projection format is `rtaime.media-interchange.ebucore-1.10.v1`. Unsupported versions fail closed.
- Runtime probe information is copied **only** from actually admitted existing `MediaAssetDescriptor` technical fields. No embedded editorial, Timecode, Caption or HDR value is invented or inferred.
- ControlHost owns explicitly requested metadata authoring through `UpsertInterchangeAsync` / `ImportInterchangeJsonAsync`, requiring exact asset ID, catalogue revision and source SHA-256 fingerprint. Operator's existing Inspector displays the accepted projection read-only; the authoring API has no Operator editing controls yet.

## Fields and conservative EBUCore concept mapping

The following are exact **rtaime JSON field paths**, not standalone EBUCore XML element paths. Native XML mappings must be validated against the corresponding published EBUCore 1.10 XSD before adding an XML adapter.

| Projection path | Related EBUCore 1.10 concept | Present evidence and semantics |
| --- | --- | --- |
| `title`, `description`, `rights`, `language` | title, description, rights, language under core metadata | Editorial entries are explicit only; no filename-to-title inference |
| `content.sha256` | format hash/identifier | SHA-256 content digest must match the existing catalogue fingerprint |
| `video.0..7.codec`, `.width`, `.height` | videoFormat and technical attributes | Existing admitted provider result; never a broader codec claim |
| `video.0..7.frameRate` | rational frame rate | Positive exact `numerator/denominator`; no rounded decimal approximation |
| `video.0..7.timeScale` | time-base-related technical attribute | Positive clock scale, distinct from video frame rate |
| `video.0..7.transfer`, `.primaries`, `.hdr` | video colour/HDR metadata | Stored source tag, **not** proof of HDR decode/output/conversion |
| `audio.0..7.codec`, `.sampleRate`, `.channelCount` | audioFormat and track parameters | Probe-derived format evidence or explicitly authored/imported tag |
| `audio.0..7.language`, `.channelRole` | audio language / channel allocation | Language tag and descriptive role only; not a claimed EBU R 123 channel allocation |
| `caption.0..7.language`, `.format`, `.kind` | captioning/subtitling format | Descriptive metadata only; timed cue payloads stay with their owning subsystem |
| `timecode.start`, `timecode.dropFrame` | timecodeFormat / technical timecode | Explicit source timecode and DF/NDF semantics; no synthesized source timecode |
| `unmapped` dictionary | source-specific additional information | Original, bounded literal values remain visible; no speculative semantic mapping |

Known fields use `MediaInterchangeField` with `Path`, `Value`, `Source`, `Evidence` and an explicit UTC `ObservedAtUtc`. The source vocabulary is `ProviderProbe`, `Embedded`, `InterchangeImport`, `User` (in ascending overwrite precedence). Source evidence is not qualification: `implemented`, `software-qualified`, `hardware-qualified`, `unverified`, `unsupported` remain independently established.

There is no automatic embedded metadata reader. `ProviderProbe` initially populates only SHA-256, first probed video codec/width/height/exact frame rate and first audio codec/sample rate/channel count. Values such as HDR, rights, language, captions and start timecode stay absent until an identified source supplies them. Additional audio/video/caption tracks may be represented explicitly but do not imply those streams can be decoded.

## Timecode, interchange and conflict rules

- All strings are Unicode/UTF-8 JSON. A single projection is limited to 2,048 encoded bytes, 24 known fields and eight bounded opaque unmapped fields. This keeps existing paged catalogue transport below its 1-MiB budget without raw media IPC.
- Start timecode must be `HH:MM:SS:FF` (NDF) or `HH:MM:SS;FF` (DF). `timecode.dropFrame` is mandatory when a start timecode is supplied. DF is accepted only with the exact `30000/1001` (two dropped labels) or `60000/1001` (four labels) frame rate, including nominal frame-number and non-tenth-minute skip checks.
- Language values are validated to a BCP-47-shaped tag; this does not prove full registry correctness. All unknown but material source metadata must be retained as bounded `unmapped` text instead of silently discarded.
- A metadata update rejects a stale catalogue revision or mismatched source fingerprint. User-authored values cannot be replaced by conflicting lower-precedence embedded/provider values; conflicting unmapped values are rejected.
- Same-hash relink and availability refresh retain the metadata; rejected relink or failed persistence leaves the existing document unchanged. Remove from Library removes the metadata with the catalogue entry, never the file.
- JSON import/export only accepts the documented `rtaime.media-interchange.ebucore-1.10.v1` profile; it rejects foreign top-level properties, oversized payloads, contradictory timecode, duplicate/invalid paths and invalid provenance.
- The existing catalogue document reader supports missing optional `interchangeJson`. Reopening a new document with a **downgraded** older writer can drop optional data: make an integrity-preserving backup before downgrading, and do not claim bidirectional writer compatibility.

## Explicit UNMAPPED and unsupported interchange

General EBUCore 1.10 XML import/export, XSD validation, controlled vocabulary URI resolution, RDF/METS, ADM/audio objects, editorial roles and people, rights enforcement, complete per-stream MXF track identity, arbitrary multi-track provider detection, timed-text cue bodies, mastering-display/color-volume validation and HDR playout qualification are **not implemented by this profile**. No third-party XSD or transformation library is bundled.

## Validation

Targeted suites:

```powershell
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release --no-build --filter FullyQualifiedName~MediaInterchangeMetadataContractTests
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter FullyQualifiedName~MediaAssetCatalogIntegrationTests
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Build with `./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release` before running `--no-build` tests. Check Required Gates **CI**, **Quality**, **Security**, **Provider Smoke**, **Packaged E2E** on the exact PR head. Software test success does not prove external EBUCore XSD compliance or physical provider interoperability.

## References

- [EBU Tech 3293, EBUCore Metadata Set v1.10](https://tech.ebu.ch/publications/tech3293)
- [Published EBUCore schema maintenance repository](https://github.com/ebu/ebucore)
- [EBU R 123, Audio Track Allocation](https://tech.ebu.ch/publications/r123)
- [EBU R 154, UHD/HDR Exchange](https://tech.ebu.ch/publications/r154)
- [Media Library](MediaLibraryAssetBrowser.md)
- [Durable Persistence](DurablePersistenceAndJournal.md)


## Timed Text cue ownership

The optional descriptive Media Library fields for caption language, format and kind remain plain EBUCore-oriented **metadata**. They are not subtitle cue streams, rendered overlays or proof of caption accessibility. The separate [Timed Text Interchange](TimedTextInterchange.md) implementation owns timecoded cue identities, source-bound local XML import/export and read-only preview. It does not write or migrate the durable asset catalogue. Production caption overlays and file/ANC carriage remain UNAVAILABLE pending independent provider and ControlHost admission.

## Optional ADM track and object interchange (separate sidecar)

The version-one EBUCore 1.10 metadata projection remains unchanged, including
its 2-KiB JSON budget, provenance precedence and bounded audio field paths.
A selected catalogue clip can now carry a **separate optional ADM source
XML sidecar** with the original bytes intact; it is not flattened into
`audio.0.*` fields. The Inspector presents EBUCore scalar values and
the imported ADM Track UIDs, roles, languages, object relationships and
start/end times **side by side**. Neither projection infers media-channel
decoding or grants multichannel/immersive playback.

The admitted profile uses ITU-R BS.2076-3 XML guided by **EBU Tech
3393 (7 October 2025)**. Physical track allocation is optional and must
be expressly declared under the subset of legacy **EBU R 123 (2009)**
profiles `4b`, `8a` or `8b`. Unassigned stereo and six-channel
5.1 metadata are not silently relabeled as R 123 8a/8b, and no
downmix is created. The imported XML's unknown data is preserved
byte-exactly rather than dropped in the limited EBUCore mapping.
No new SQLite/catalogue schema or IPC payload is created.

See [ADM Audio Track Metadata](AdmAudioTrackMetadata.md) for exact
subset, validation, source binding, limits, test fixtures, export and
the explicit `UNSUPPORTED` immersive output state.
