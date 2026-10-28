<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Offline Media Quality Control – EBU QC 2026-04 profile

## Architecture and source of authority

**Status: implemented (managed file checks); physical and external-reference qualification: UNVERIFIED.**

The QC scheduler is a bounded **Operator/Client-side read-only file worker**, not a
new Program decoder, ControlHost authority, RuntimeHost callback or media mutation.
A user explicitly selects an **online, catalogued Clip** and presses
**START OFFLINE QC**. The worker consumes the existing immutable
`MediaAssetDescriptor` and catalogue revision. It never changes
`media.asset.catalog`, Control's production state, video/audio samples,
decoder admission, memory reservation, source routing or Preview/Program.

The operator can **CANCEL QC**, review all per-check results, export a validated
EBU QC XML report or import a compatible report bound to the selected asset.
Import does **not** accept a foreign report without the strict rtaime evidence
extension. Importing an unrelated document, an earlier model revision,
mismatched content hash or catalogue revision fails closed.

## Pinned EBU schema and licensing

This implementation pins the **published EBU QC 2026-04** release rather than
adapting an older data model.

| Resource | Exact target namespace / version |
| --- | --- |
| `schemas/quality-control/ebu/2026-04/qc.xsd` | `tag:qc.ebu.ch,2026-04` |
| `schemas/quality-control/ebu/2026-04/TimingExtensionMediaPlaybackEditUnits.xsd` | `tag:qc.ebu.ch,2026-04:extensions:timing` |
| `schemas/quality-control/ebu/2026-04/qc-catalogue-api-schema.xsd` | `tag:catalogue.qc.ebu.ch,2026-04`, catalogue API **v3** |
| `tests/fixtures/ebu-qc/2026-04/qc-report-generic-sample.xml` | published sample report, validation fixture |

Pinned upstream source: [EBU QC 2026-04 release](https://github.com/ebu/qc/releases/tag/2026-04).
Schemas and sample are reproduced without edits under the upstream
**Creative Commons Attribution 4.0 International (CC BY 4.0)** licence:
[licence](https://github.com/ebu/qc/blob/2026-04/LICENSE.md) and
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
Credit: European Broadcasting Union / EBU Quality Control project,
version 2026-04, released 12 July 2026. No EBU logo, certification or
endorsement is asserted.

Only the versioned **Report XML** is currently exported/imported. The
Catalogue API schema is pinned for compatibility and future work; rtaime
does **not** call, impersonate or publish a Catalogue API v3 service.

## Internal checks and EBU mapping

The application has one versioned local profile
`b65f72d6-a8f0-4bbd-9d31-9e8abed70a05`, `1.0`. Check IDs prefixed
`rtaime-` are **local definitions, not catalogue-registered EBU QC IDs**.
The 2026-04 XML `ItemDefinition.CheckResultDefinition` is used for check
semantics, rather than an obsolete output named `CheckResult`.

| Check | Evidence and actual scope | Outcome limitations |
| --- | --- | --- |
| `rtaime-container` | Complete source SHA-256 plus ISO BMFF/QuickTime top-level `ftyp/moov/mdat` structure | Does not prove decoder support |
| `rtaime-truncation` | Declared box and nested box boundary checks on the fully hashed file | Cannot detect content corruption inside valid-sized media payloads |
| `rtaime-streams` | Observed `mdia/hdlr` video/audio handlers versus catalogued expected audio presence | Does not fully decode or validate every elementary stream |
| `rtaime-timestamp` | Actual `stts` decode-time sample/delta table check | CTTS/edit-list and presentation-timestamp monotonicity remain unverified |
| `rtaime-metadata` | Movie header and observed track sample-table presence | Not a complete editorial/EBUCore/rights/HDR metadata validator |
| `rtaime-loudness` | None | **NOT_EXECUTED** until a separate offline R 128 adapter exists |
| `rtaime-range` | None | **NOT_EXECUTED** until a qualified offline R 103 adapter exists |

The already implemented live Program loudness or Color Review measurements
must **not** be reused to report a media file as compliant. Unsupported
sources/checks are never replaced with a synthetic `PASS`.

### Extension via separately measured checks

`IMediaQcCheckAdapter` is the managed, **trusted built-in registration**
contract for adding independently implemented offline checks later. Version
and check-definition equality are enforced before a worker starts. Only
the two currently unmeasured profile slots (`rtaime-loudness` and
`rtaime-range`) can be registered, with at most two adapters. The
registry rejects duplicates and foreign check IDs; it cannot replace
the built-in container probe. Implementations receive the original
catalogued asset, immutable SHA-256, length and catalogue revision and
run sequentially in the existing single worker. An adapter's own result
must validate against the exact declared check definition and report the
identical full-file SHA-256 source identifier. The worker rehashes the file
after adapter execution, so a modifying adapter cannot inherit earlier
verification. Unexpected adapter failure produces no completed report.
An explicitly unsupported adapter may return `UNSUPPORTED`, never a
placeholder `PASS`. Synthetic adapter values are used only in tests.

Internal states have exactly the following semantics:

- `PASS` – the named check was actually executed and its declared,
  **narrow** check predicate passed.
- `FAILED` – the named check executed and the predicate failed, with
  observed detail and source. This is **not** a Runtime execution failure.
- `UNSUPPORTED` – file/profile/check capability cannot support the
  stated observation. It does not count as PASS.
- `NOT_EXECUTED` – intentionally unmeasured, or an adapter is not yet
  installed. It does not count as PASS.

The EBU model's `ExecutionStatus` only permits `complete` and `error`;
those are **not** internal QC check result states. The XML report uses
`ExecutionStatus=complete` for a finished QC job. Only independently
executed PASS/FAILED checks emit `ItemResult.CheckResult=true/false`.
For `UNSUPPORTED`/`NOT_EXECUTED`, the item is present in the Profile
but **has no measured ItemResult**. The report-level `CheckResult` is
omitted unless **all** checks have measured outcomes. The application
preserves all internal states in a tightly checked foreign-namespace
`rqc:Binding/rqc:Outcome` extension under Report.ExtensionProperties.

The extension also binds `MediaAssetId`, full SHA-256, captured catalogue
revision, file size, source last-modified UTC, report creation UTC, check
implementation version, observed source and optional 0..1 confidence,
nonnegative severity, track identifier and edit-unit range.
The EBU `Report.LastModifiedDateTime` is the QC report timestamp; the
**source-file modification time** is separately preserved in
`rqc:Binding/@sourceModifiedUtc`. XML import requires matching source UUID
and SHA-256 in both Report and Profile identifiers and cross-checks
outcome versions, tool/time provenance, confidence, severity and range
against the actual measured EBU ItemResults. Stored reports are
canonicalized before atomic publication rather than retaining arbitrary
foreign extension elements.
When a result has a frame range, the EBU `Locator.Start/End` values are
paired with the published
`TimingExtensionMediaPlaybackEditUnits/EditRate` rational; a single
edit rate is required throughout the report. No frame/time range is
fabricated for a container-level check.

## Worker budget, cancellation and recovery

- Exactly **one** off-hotpath worker; at most **four** queued jobs and
  **five** in-flight jobs including the running job.
- Default per-file admission maximum **1 GiB**, never above 32 GiB.
  Larger files are rejected as unqualified by this implementation, not
  pronounced clean. Maximum structural traversal: 20,000 boxes, depth 8,
  and 65,536 `stts` entries per inspected table.
- SHA-256 is calculated from every byte using a 64 KiB read buffer.
  There is an async 125-ms yield after each 1 MiB hashed, a nominal
  I/O admission of approximately 8 MiB/s on an uncongested disk.
  This is a throttle policy, **not** proof of physical SSD isolation.
- Check work is not dispatched from media, audio, Render or Program
  callbacks, and no separate 200-ms QC polling loop is created.
- Start and end file length/timestamps and the full SHA-256 must agree
  with the imported immutable asset identity. Source replacement or
  changed bytes invalidates the job; no partial QC result is retained.
- A cancelled job does not produce an XML report. Completed reports
  are validated before an atomic rename to
  `%LOCALAPPDATA%/rtaime/qc/<asset-id>.xml`. The existing report
  remains unchanged after a cancelled/failed replacement.
- On Operator restart the Media Library can reopen prior completed
  reports, but explicitly labels them **RETAINED / HISTORICAL**.
  They do not establish current file readiness without a new full hash.
  A changed catalogue revision, asset identity, fingerprint, or
  source length rejects the retained report and requires re-run.
- If the catalogue changes while inspection is pending, the worker is
  cancelled and prior results are visibly **STALE**. Neither a partial
  evaluation nor an old report is silently promoted to current PASS.

The original catalogue persistence schema and IPC are unchanged.
Third-party XML import/export is bounded to 1 MiB, external entities and
DTDs are forbidden, and every report is schema-validated against the
embedded pinned 2026-04 XSD. No external network lookup or
runtime-installed XML schema is required.

## Verification and remaining qualification

Managed verification includes the pinned upstream sample, schema
roundtrips and invalid namespace/version/definition/CheckResult cases,
rational timing extension, real ISO BMFF fixture checks, truncated files,
missing audio tracks, zero decode deltas, changed source SHA-256,
cancellation/quota behaviour and atomic retained-result recovery.

Canonical commands from the current repository:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~MediaQc"
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter "FullyQualifiedName~MediaQc"
dotnet test tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj --configuration Release --no-build --filter "FullyQualifiedName~MediaQc"
```

On the exact PR head, inspect CI, Quality, Security, Provider Smoke
and Packaged E2E. **A passing managed test run does not qualify**
physical Program continuity, codec bitstream validity, actual PTS
ordering, multi-hour large-file I/O/resource budgets, R 128, R 103,
MXF/IMF or the complete EBU QC catalogue. Those remain
`UNVERIFIED` or `UNSUPPORTED` until separately proven.
