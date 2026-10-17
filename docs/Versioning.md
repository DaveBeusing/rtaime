<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Product versioning

rtaime adopts the Unified Versioning Standard. All managed components share one product release stream and `vX.Y.Z` tags. The current release target is `0.5.39`; adoption does not declare a stable release or change persisted-state schemas.

`VersionPrefix` in `Directory.Build.props` is the planned SemVer target. Change it once when the release scope requires a new version. Never increment tracked PATCH per commit or commit generated build versions. Before 1.0, MINOR conventionally denotes notable features/contract changes and PATCH compatible fixes; SemVer does not guarantee a stable public API at 0.x.

The public contract includes externally consumed Control/Runtime/Media/Provider/AI contracts and IPC, CLI/configuration behavior, persisted production state, supported provider interactions, and offline installation/update behavior. Internal refactoring, tests and documentation do not automatically change product SemVer. Contract/protocol/schema versions and durable migration history remain governed separately by their existing policies and catalogs. Release notes must describe compatibility and migration consequences.

## Build identities

| Context | Identity | Distribution |
| --- | --- | --- |
| Local | `0.1.0-dev.g<full-sha>` | Development only |
| PR | `0.1.0-pr.<number>.<run-id>.<attempt>` | Validation artifacts only |
| Successful master integration gates | `0.1.0-preview.N` | Retained Actions candidate artifacts |
| Explicit candidate dispatch | `0.1.0-rc.N` | Retained candidate artifacts, readiness gates required |
| Annotated stable tag | `0.1.0` | Existing trusted stable publication path |

The Release Pipeline listens for successful **push** runs of Required gates on `master` in this repository. PR completions cannot enter allocation. PREVIEW/RC dispatch without a tag is allowed only from `master`. An explicitly supplied annotated tag must point to the exact checked-out commit and match its `VersionPrefix`; stable releases retain all existing production trust and Stable readiness gates. RC uses `RELEASE_CANDIDATE`, production signing trust and the existing explicit release-readiness PASS gate. RC is distributed through candidate artifacts; the automated update feeds remain Preview/Stable.

## Durable reservation and immutability

`build/release/Reserve-PrereleaseVersion.ps1` reads the remote annotated-tag registry for one base version and channel. It reserves the next positive integer by creating and pushing a new annotated tag. The tag contains a unique reservation identifier and the owning build ID; another CI run cannot resolve that reserved version for a rebuild. Simultaneous attempts cannot accidentally produce the same tag object. Git rejects an existing ref with another object. A collision fails the run without overwrite or automatic retry. The workflow serializes automatic previews separately from tagged releases, so a nightly build cannot cancel a pending stable publication. Each new base/channel starts at 1; failed builds consume reservations, so gaps are allowed.

Never delete/move reservation tags, including failed-build tags, or force-push tags. No commit count or generic CI run number allocates release versions. Release reruns are rejected: start a new run to allocate a fresh prerelease version. Once `vX.Y.Z` exists remotely, allocation for that base stops until the next target is selected. Stable and manually supplied release tags must be annotated and immutable; the existing publication step rejects an existing release and never replaces assets.

The automatic reservation uses the workflow token and does not recursively trigger another tag workflow. Its candidate is retained as an Actions artifact. Publishing to GitHub Releases remains the separate tagged-release path with an enrolled external production signing key. Rebuilding a retained automatic candidate for official publication is not supported; promote a newly identified candidate through the established process. Do not republish a different artifact under an existing version. Package feeds are not currently configured; any future feed must consume the verified candidate and honor the same reservation and collision rules.

## .NET mapping and provenance

| Property | Mapping |
| --- | --- |
| `Version` / `PackageVersion` | Resolved build SemVer |
| `InformationalVersion` | Build SemVer followed by `+g<full-source-sha>` |
| `AssemblyVersion` | Explicit compatibility identity `0.1.0.0`; change deliberately for assembly binding compatibility |
| `FileVersion` | Explicit numeric Windows resource identity `0.1.0.0`; each field must remain within 0–65535 |

The SDK's automatic source suffix is disabled to avoid duplicate hashes. Metadata also contains full source revision, normalized release stage and CI build ID. The Operator product label tooltip displays version/stage/short SHA. Host startup logs, support snapshots and support bundles retain full SHA/build ID.

The central release orchestrator passes the resolved product version/stage/build ID into compilation without rewriting tracked files. Evidence generation checks each host binary's embedded informational version against the requested SemVer and full SHA. The checksummed artifact manifest records base/build version, repository, source/build SHA, UTC timestamp, build ID, configuration, pinned SDK, target frameworks, Windows x64 identity and tree cleanliness. All publication paths retain existing checksum/attestation/offline verification. Actions candidate artifacts use the repository retention policy; published release assets and tags are immutable and retained as release history.

Run `build/release/Test-UnifiedVersioning.ps1` and `build/release/Test-ReleaseChannelPolicy.ps1` for versioning qualification, then the canonical managed and packaged Required Gates. A successful local check is not evidence that remote allocation/publication or hosted gates have run.
