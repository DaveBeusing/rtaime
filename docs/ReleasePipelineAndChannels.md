<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Single Release Pipeline & Release Channels

## Purpose

The software release lifecycle has one authoritative implementation:

```text
Source identity
    ↓
Restore
    ↓
Release build
    ↓
Exact build tests
    ↓
Release Evidence
    ↓
Release Attestation
    ↓
Offline Software Bundle
    ↓
Offline verification / failure qualification
    ↓
Clean install / post-install verification
    ↓
Release Candidate manifest
```

The authoritative implementation is:

```text
build/release/Invoke-ReleasePipeline.ps1
```

GitHub workflows may trigger it, but they must not reimplement the release path.

Developer build, single-file publish and targeted test commands are documented centrally in [BuildAndTest.md](BuildAndTest.md). Those commands are development conveniences and do not replace the authoritative release pipeline or its evidence gates.

## Source identity is immutable

The release pipeline requires:

```text
checked-out HEAD
=
SourceCommit
=
BuildCommit
```

Tagged channels additionally require:

```text
Git tag commit
=
SourceCommit
```

The pipeline therefore rejects a build where the claimed source commit differs from the code actually checked out and built.

## Version source

The target base version and default development stage are source-controlled in:

```text
Directory.Build.props
```

`VersionPrefix` is changed for release scope, never per commit. The release pipeline resolves the build SemVer/stage from validation context or an immutable annotated tag and passes it to compilation and evidence without editing source. See [Versioning.md](Versioning.md) for allocation, PR isolation, .NET mappings and provenance.

## Channels

Channel policy is version-controlled in:

```text
build/release/release-channels.json
```

### QUALIFICATION

Default development state (the build label also includes source revision or PR identity):

```text
RtaimeReleaseStage = DEV
RtaimeProductVersion = $(VersionPrefix)-dev
```

Rules:

- no Git tag,
- `TEST_EPHEMERAL` signing only,
- no production-key trust claim,
- never publication eligible.

This is the channel used by the required `Packaged E2E` status check.

### PREVIEW

Resolved build state, passed by the release pipeline:

```text
RtaimeReleaseStage = PREVIEW
RtaimeProductVersion = <major>.<minor>.<patch>-preview.<n>
```

Required tag:

```text
v<major>.<minor>.<patch>-preview.<n>
```

Rules:

- tag must exist,
- tag base version must equal the source-controlled `VersionPrefix`, and its full version becomes the build product version,
- tag commit must equal the checked-out source commit,
- `TEST_EPHEMERAL` may qualify the Preview mechanism,
- `EXTERNAL_CONTROLLED` signing is supported,
- Preview is publication-eligible only when Production Trust is `PASS`.

A Preview candidate signed by a CI test key remains useful qualification evidence but its publication readiness stays `UNVERIFIED`.

An externally controlled Preview Candidate whose key is active `SOFTWARE_RELEASE` trust may reach publication readiness `PASS` and can then cross the Release Publication & Discovery publication boundary.

### RC

An explicit RC dispatch reserves `vX.Y.Z-rc.N`, resolves `RELEASE_CANDIDATE`, and requires production signing trust and the existing explicit release-readiness PASS gate. RC output is retained as candidate artifacts; it does not enter automated Preview/Stable update discovery.

### RC

An explicit RC dispatch reserves vX.Y.Z-rc.N and uses RELEASE_CANDIDATE stage. It requires production signing trust and the existing explicit release-readiness PASS gate. RC output is retained as candidate artifacts and does not enter Preview/Stable update discovery.

### STABLE

Resolved build state, passed by the release pipeline:

```text
RtaimeReleaseStage = STABLE
RtaimeProductVersion = <major>.<minor>.<patch>
```

Required tag:

```text
v<major>.<minor>.<patch>
```

Rules:

- exact tag/version equality,
- exact tag/source commit equality,
- `EXTERNAL_CONTROLLED` signing only,
- signing-key fingerprint must be active `SOFTWARE_RELEASE` trust,
- offline bundle verification must pass with `-RequireTrustedProductionKey`,
- Stable candidate publication readiness is `PASS` only after those gates pass,
- the Stable-readiness verifier must independently return `PASS`,
- Stable readiness must bind the repository HEAD, supplied release evidence and candidate Release Evidence SHA-256 to the same exact source/build identity.

A CI-generated `TEST_EPHEMERAL` key can never create a Stable candidate. Candidate trust from one release cannot be combined with qualification or known-issues evidence from another release bundle.

## Release Candidate artifact

The final candidate directory contains exactly:

```text
release-candidate.json
release-candidate.sha256
rtaime-<version>-win-x64.zip
rtaime-<version>-win-x64.zip.sha256
```

The candidate manifest binds:

- channel,
- product version,
- release stage,
- Git tag where applicable,
- source commit,
- build commit,
- build id,
- Release Record identity,
- Release Evidence SHA-256,
- signing-key fingerprint,
- production trust status,
- offline bundle name, size and SHA-256,
- publication readiness.

Its `candidateId` is content-addressed from the release identity, Release Record, signing key and bundle hash.

## Candidate verification

Run:

```powershell
./build/release/Test-ReleaseCandidate.ps1 \
    -CandidatePath <candidate-directory>
```

Verification includes:

- candidate manifest sidecar,
- candidate content id,
- bundle size and hash,
- bundle sidecar,
- channel-specific version/tag rules,
- source/build commit equality,
- Preview/Stable publication-readiness trust rules,
- complete offline bundle verification,
- no undeclared files in the candidate directory.

When a Candidate declares Production Trust `PASS`, the bundle verifier is also required to validate the active trusted production key.

## Failure qualification

The single pipeline checks negative cases for the final candidate layer:

```text
tampered bundle
→ reject

tampered candidate manifest
→ reject

undeclared candidate file
→ reject

Stable candidate with TEST_EPHEMERAL signer
→ reject
```

These complement the lower-level release-attestation and offline-bundle failure suites.

## GitHub Required Gates

`Packaged E2E` calls:

```text
Invoke-ReleasePipeline.ps1 -Channel QUALIFICATION
```

against the exact pull-request head commit.

The other required status checks remain independent:

```text
CI
Quality
Security
Provider Smoke
```

`Quality` runs `Test-ReleaseChannelPolicy.ps1`, which also validates the separate publication policy and permission boundary.

## GitHub Release Pipeline workflow

`.github/workflows/release-pipeline.yml` supports:

```text
push tag v*
workflow_dispatch
successful master-push Required gates completion
```

The `release-candidate` job owns Release Candidate creation.

For a tag push, Release Publication & Discovery adds a separate `publish-release` job that consumes that Candidate artifact without rebuilding it.

For manual runs the operator selects:

```text
QUALIFICATION
PREVIEW
RC
STABLE
```

Manual runs qualify Candidates but do not publish GitHub Releases because official publication is tied to a tag-push event.

## Signing input

The GitHub workflow can consume externally provisioned values:

```text
secret: RTAIME_RELEASE_SIGNING_KEY_PEM
variable: RTAIME_RELEASE_SIGNER_ID
```

The private key is materialized only in the Candidate job's runner temporary directory and deleted in `finally`.

It is never transferred to the publication job, repository or release assets.

## Publication boundary

Candidate creation defaults to read-only repository permission.

The version reservation job and downstream publication job receive isolated grants of:

```text
contents: write
```

Publication is defined in `docs/ReleasePublicationAndDiscovery.md` and is required to consume the already-built Candidate rather than invoking the release pipeline again.

This preserves:

```text
Release Candidate creation
≠
Release publication / distribution
```

while allowing trusted PREVIEW and STABLE Candidates to be published without a rebuild.

## Legacy workflow consolidation

The historical `.github/workflows/bootstrap-validation.yml` remains removed.

Managed correctness is covered by `CI`; packaged release correctness is covered by `Packaged E2E` through the authoritative release orchestrator.

There is no second release-evidence/signing/packaging implementation in CI or publication.

## Non-claims

The release pipeline and publication foundation do not claim:

- update-service delivery,
- autonomous client update selection,
- immutable external/WORM release retention,
- production signing-key enrollment completion,
- HSM/KMS integration,
- Reference Platform hardware qualification,
- STABLE status for the current `0.1.0-dev` source,
- VALIDATED or CERTIFIED product status,
- formal CRA conformity.

`UNVERIFIED` remains distinct from `PASS`.

## Known-issues input for release candidates

The authoritative release orchestrator accepts an optional KnownIssuesAssessmentPath beside the existing product-security assessment input.

The known-issues assessment is exact-source/build-bound and is applied before release attestation. A STABLE candidate cannot reach Stable-readiness PASS while KNOWN_ISSUES remains UNVERIFIED.

This input is evidence, not a source-code declaration of quality. The assessment must independently pass its verifier and cannot hide unresolved BLOCKER/CRITICAL issues.

## Stable support/readiness boundary

The STABLE channel remains a release-channel mechanism, not an automatic commercial support promise.

A future Stable publication must continue to satisfy the existing exact version/tag/source identity and production-signing requirements. In addition, `build/release/Test-StableReadiness.ps1` provides a separate fail-closed operational/commercial readiness view over:

- approved support lifecycle and supported-version policy;
- evidence-backed platform compatibility;
- security/support commitments;
- exact release and known-issues evidence;
- required physical qualification;
- production signing trust;
- deployment/update readiness.

The readiness verifier never changes release stage or channel policy. Current `0.1.0-dev / DEV` source remains non-Stable even though the readiness framework exists.

