<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Timed Text Interchange — EBU-TT-D and EBU-TT Live

## Scope and qualification

This release introduces a distinct, bounded Timed Text domain, an XML import/export **subset** and a source-bound **local Operator preview**. It does not establish complete EBU schema conformance, Program subtitle rendering, DVB accessibility, recording burn-in or SDI/ANC carriage.

| Capability | Current implementation | Residual qualification |
| --- | --- | --- |
| EBU-TT-D, Tech 3380 **v1.0.1** (2018) | Strict UTF-8 text subset import/export, source SHA-256 and exact profile signalling | Full EBU/IMSC XML Schema and external encoder interoperability UNVERIFIED |
| EBU-TT Live, Tech 3370 **v1.0** (2017) | Strict document subset, sequence identity/number, ordered-segment fail-closed store | Real Live WebSocket carriage, handover, broadcast clock and external interoperability UNVERIFIED |
| Exact local media frame cues | 25, 50 and 60000/1001 rational rates; confirmed media seek and pause | Runtime-confirmed Program overlay and downstream A/V sync UNAVAILABLE |
| Live clock timeBase | Parsed and retained, but shown as UNVERIFIED without authoritative origin | Runtime↔TAI/PTP mapping UNAVAILABLE |
| Caption cue identity | Separate from media marker cue points, EBUCore catalogue caption tags and editorial CG | Durable source-bound ControlHost cue store and governed approval UNAVAILABLE |
| Layout/font/glyph | Explicit regions, styles, languages, role, 5% product safe-area warnings, local font/glyph check | Qualified native renderer, shaping and physical fonts UNVERIFIED |
| Operator | Existing EDIT Timeline adds local XML import, text-only frame preview and PREVIEW ON/OFF | Program Captions ON/OFF, approved overlay, ANC provider UNAVAILABLE |

The exact profile designators are **urn:ebu:tt:distribution:2018-04** (EBU-TT-D 1.0.1) and **urn:ebu:tt:live:2017-05** (EBU-TT Live 1.0). References: [EBU Tech 3380](https://tech.ebu.ch/publications/tech3380), [EBU Tech 3370](https://tech.ebu.ch/publications/tech3370) and [TTML1](https://www.w3.org/TR/2018/REC-ttml1-20181108/). The existing [EBU-TT-D informative XSD](https://github.com/ebu/ebu-tt-d-xsd) covers 1.0, **not** the full 1.0.1 additions; it is not bundled or claimed as the normative conformance engine.

## Ownership and source of truth

- Media.Contracts TimedTextContracts.cs defines distinct cue IDs, language, role, region, style, time windows, source digest, format version and bounded warning model.
- Client TimedTextInterchangeCodec.cs parses/serializes only approved TTML structures; TimedTextCueStore.cs resolves cue time against exact rational frame rates and isolates Live discontinuities.
- Operator TimedTextReviewViewModel and the existing MediaTimelineControl bind local review to confirmed Media Deck source identity, exact frame rate, confirmed frame and import context epoch. Replaced/disconnected/uncertain sources cannot retain a previous caption preview.

The existing RuntimeHost GPU compositor and Production CG layer are **unchanged**. No caption bytes reach Program, recording or output providers; no ControlHost prepare/commit is bypassed. This is a safe partial deliverable, not a silently enabled broadcast feature.

## Supported secure XML subset

Documents use the TTML namespace **http://www.w3.org/ns/ttml**, UTF-8, explicit profile and timeBase. EBU-TT-D requires the v1.0.1 profile identifier in metadata and media timeBase. EBU-TT Live requires sequenceIdentifier, positive sequenceNumber and media/clock timeBase; local/utc clockMode is accepted for interchange but cannot be projected to Program without a confirmed runtime clock anchor.

The allowlisted structure is tt/head/metadata/styling/layout and body/div/p with span/br. Each document has at most **256 cues**, **16 regions**, **16 styles** and **256 KiB UTF-8 XML**. Cue IDs are unique, languages BCP-47-shaped, paragraphs no longer than 1024 characters or eight lines. Explicit region percent bounds, font family/size in px, color/background hex, text alignment and cue style/region IDs are validated. Unknown attributes, style inheritance, mixed inline styles, images, external fonts/URIs, animations, unconstrained timing and unsupported XML are rejected instead of silently discarding information. A text-only preview is not a geometrically or typographically exact output simulation.

Relative TTML begin/end/dur and second/millisecond/frame offsets use exact 100 ns ticks. Explicit document rates are 25, 50 or **60000/1001** (60 with frameRateMultiplier 1000 1001). No implicit drop-frame conversion is performed. Cue visibility is a half-open rational frame interval: **first frame at or after begin, excluding first frame at or after end**. Paused confirmed media holds its cue; seek/transport uncertainty, source replacement and Live sequence gaps revoke stale evidence. Live clock timeBase is UNVERIFIED until an authoritative Runtime-to-clock origin exists.

A 5% normalized product review-safe-area margin and same-language/same-region overlap produce warning records, **not** an EBU-prescribed placement or automatic correction. The WPF import path checks installed fonts and Unicode glyph maps and surfaces missing glyph warnings. This is local host evidence, not calibrated/portable production font equivalence.

## XML security and file lifecycle

The importer enforces strict UTF-8 and XmlReader character/document limits, prohibits DTD/external entities and uses XmlResolver=null. Unsupported format claims and any unrenderable semantic construct fail closed. The Operator file picker reads asynchronously with bounded file length **before** allocating the payload. Failed import discards previous local cues and exposes an error. No XML parsing, file I/O or font resolution happens on a Program/audio/render callback. Serialization generates bounded well-formed UTF-8 XML from validated document types and is a standalone metadata exporter, not an SDI ANC writer.

## Operator workflow

Open EDIT → Timeline with a confirmed media source, then choose IMPORT EBU-TT-D or IMPORT EBU-TT LIVE. The compact local review panel displays only currently active cue text and warnings at the **confirmed media frame**. PREVIEW ON/OFF has **zero Program effect**. Changing the loaded media, losing transport confirmation, leaving the workspace or completing a delayed import from an old source removes/invalidates its preview. The panel is not a multi-language authoring suite, Live receiver or caption approval interface.

The EBUCore Media Library caption metadata remains descriptive metadata; it cannot be mistaken for subtitle cue bodies or rendered output.

## Tests, CI and next integration boundary

- TimedTextContractTests: cue identity, role/language, region/style reference, safe area, overlap and invalid bounds.
- TimedTextInterchangeTests: UTF-8 XML import/export, profile/reject cases, XXE denial, missing images/resources, 25/50/59.94 exact timing, pause/seek/source/unknown state, Live clock/sequence duplicate/gap.
- TimedTextReviewTests: source-confirmed local review, rejected stale imports, text-only WPF binding and no Program mutation.
- Required Gates CI executes these targeted suites; Quality verifies XML security, source fences, Program isolation and support claims. Full CI, Quality, Security, Provider Smoke and Packaged E2E remain required for merger. Third-party EBU schema/conformance test suites and physical output are separately **UNVERIFIED**.

**Production dependency:** Add an independent governed caption-track service, ControlHost-authorized revision/admission and prepared Runtime caption layer distinct from editorial CG. Only with confirmed Runtime Program frames, approved Caption ON/OFF control, device/font/layout qualifications, A/V sync and recovery can real Program overlay be enabled. Actual SDI/ANC carriage requires a distinct provider qualification. Until then any real caption output remains **UNAVAILABLE**.
