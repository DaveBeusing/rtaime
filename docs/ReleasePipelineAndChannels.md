<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Single Release Pipeline & Release Channels

## Purpose

AP-21 consolidates the software release lifecycle into one authoritative implementation:

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

This ensures that the version represented by the release candidate was already part of the source commit being released.

## Channels

Channel policy is version-controlled in:

```text
build/release/release-channels.json
```

### QUALIFICATION

Purpose:

```text
pull-request / master packaged E2E qualification
```

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
- `EXTERNAL_CONTROLLED` signing is also supported,
- Preview does not claim Stable publication readiness.

A Preview candidate signed by a CI test key remains cryptographically useful evidence but production trust is `UNVERIFIED`.

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
- Stable signer/trust requirements,
- complete offline bundle verification,
- no undeclared files in the candidate directory.

## Failure qualification

The single pipeline also checks negative cases for the final candidate layer:

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

`Packaged E2E` no longer reimplements release evidence, signing and packaging steps.

Instead it calls:

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

`Quality` also runs `Test-ReleaseChannelPolicy.ps1`, which fails if workflows bypass the authoritative orchestrator or the channel trust rules drift.

## GitHub Release Pipeline workflow

`.github/workflows/release-pipeline.yml` supports:

```text
push tag v*
workflow_dispatch
```

For tag pushes the channel is resolved automatically from the source-controlled release stage.

For manual runs the operator selects:

```text
QUALIFICATION
PREVIEW
STABLE
```

PREVIEW/STABLE manual runs must provide an existing matching Git tag.

## Signing input

The GitHub workflow can consume externally provisioned values:

```text
secret: RTAIME_RELEASE_SIGNING_KEY_PEM
variable: RTAIME_RELEASE_SIGNER_ID
```

The private key is materialized only in the runner temporary directory and deleted in `finally`.

It is never written into the repository or uploaded release candidate.

This is an integration seam, not a claim that production key provisioning is currently complete.

## Publication boundary

AP-21 deliberately keeps:

```text
permissions:
  contents: read
```

for the release workflow.

Therefore AP-21 creates and uploads a verified Release Candidate artifact but does **not** create a GitHub Release, update channel index or mutate repository release state.

This separates:

```text
Release Candidate creation
≠
Release publication / distribution
```

Publication is a later work package with its own authorization, retention and discovery evidence.

## Legacy workflow consolidation

The historical:

```text
.github/workflows/bootstrap-validation.yml
```

is removed by AP-21.

Its managed correctness responsibilities are covered by `CI` and its packaged release responsibilities are covered by `Packaged E2E` through the authoritative release orchestrator.

There is no longer a second release-evidence/signing/packaging implementation in CI.

## Non-claims

AP-21 does not claim:

- GitHub Release publication,
- release-channel index publication,
- update-service delivery,
- immutable external/WORM release retention,
- production signing-key enrollment completion,
- HSM/KMS integration,
- Reference Platform hardware qualification,
- STABLE status for the current `0.1.0-dev` source,
- VALIDATED or CERTIFIED product status,
- formal CRA conformity.

`UNVERIFIED` remains distinct from `PASS`.
