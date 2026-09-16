<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

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

The product version and release stage remain source-controlled in:

```text
Directory.Build.props
```

The release pipeline does not silently override the source version through CI parameters.

## Channels

Channel policy is version-controlled in:

```text
build/release/release-channels.json
```

### QUALIFICATION

Required source state:

```text
RtaimeReleaseStage = DEV
RtaimeProductVersion = <major>.<minor>.<patch>-dev
```

Rules:

- no Git tag,
- `TEST_EPHEMERAL` signing only,
- no production-key trust claim,
- never publication eligible.

This is the channel used by the required `Packaged E2E` status check.

### PREVIEW

Required source state:

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
- tag version must exactly equal the source-controlled product version,
- tag commit must equal the checked-out source commit,
- `TEST_EPHEMERAL` may qualify the Preview mechanism,
- `EXTERNAL_CONTROLLED` signing is supported,
- Preview is publication-eligible only when Production Trust is `PASS`.

A Preview candidate signed by a CI test key remains useful qualification evidence but its publication readiness stays `UNVERIFIED`.

An externally controlled Preview Candidate whose key is active `SOFTWARE_RELEASE` trust may reach publication readiness `PASS` and can then cross the AP-22 publication boundary.

### STABLE

Required source state:

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
- Stable candidate publication readiness is `PASS` only after those gates pass.

A CI-generated `TEST_EPHEMERAL` key can never create a Stable candidate.

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
```

The `release-candidate` job owns Release Candidate creation.

For a tag push, AP-22 adds a separate `publish-release` job that consumes that Candidate artifact without rebuilding it.

For manual runs the operator selects:

```text
QUALIFICATION
PREVIEW
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

Only the downstream publication job receives:

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
