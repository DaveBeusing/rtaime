<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Release Evidence Foundation

## Purpose

The release-evidence foundation turns a successful managed validation run into an offline-verifiable evidence bundle without claiming a release level that has not been earned.

The current trust path is:

```text
Source identity
    ↓
Controlled managed build/test
    ↓
Collected host artifacts
    ↓
SHA-256 Artifact Manifest
    ↓
CycloneDX SBOM
    ↓
Source-bound Qualification Evidence Manifest
    ↓
Explicit Compatibility Manifest
    ↓
Release Evidence Manifest
    ↓
Release Attestation
    ↓
Content-addressed Release Record
    ↓
Offline verification
```

Release evidence generation and release signing remain separate trust steps. The evidence manifest is complete before it is signed; signing therefore binds the exact evidence bytes instead of rewriting the signed subject afterward.

Production key trust and immutable external publication remain `UNVERIFIED` until their dedicated controlled infrastructure and evidence exist.

## Product identity

The canonical product identity is defined in [ProductIdentity.md](ProductIdentity.md):

```text
Name        rtaime
Pronounced  realtime
Expansion   Real Time AI Media Engine
Slogan      Production-grade real-time AI media platform.
```

The current development version/release identity is declared centrally in `Directory.Build.props`:

```text
RtaimeProductVersion = 0.1.0-dev
RtaimeReleaseStage   = DEV
```

The release lifecycle values represented by the manifest contract are:

```text
DEV
PREVIEW
RELEASE_CANDIDATE
STABLE
VALIDATED
CERTIFIED
```

The current product remains `DEV`.

## Source commit vs build commit

Pull-request CI builds GitHub's tested merge ref rather than the branch head in isolation. The evidence bundle therefore records both identities:

- `sourceCommit`: the proposed source branch commit,
- `buildCommit`: the exact Git object used for the CI build.

On a normal `master` build these values are expected to be the same. Keeping them separate prevents an evidence record from claiming that bytes produced from a tested merge tree came from the unmerged branch tree alone.

Physical qualification evidence is stricter: a qualification binding must name the exact `sourceCommit` it qualified. Evidence from another source commit is rejected rather than inherited implicitly.

## Generated evidence bundle

`build/release/New-ReleaseEvidence.ps1` first creates the release evidence and then applies any valid source-bound physical qualification bindings:

```text
artifacts/release-evidence/
├─ product/
│  ├─ rtaime/
│  ├─ rtaime.ControlHost/
│  ├─ rtaime.RuntimeHost/
│  ├─ rtaime.AIHost/
│  └─ rtaime.Operator/
├─ qualification/
│  ├─ bindings/
│  ├─ payloads/
│  ├─ supported-performance.json
│  └─ supported-performance.md
├─ artifact-manifest.json
├─ sbom.cdx.json
├─ compatibility-manifest.json
├─ qualification-evidence-manifest.json
└─ release-evidence.json
```

The `qualification/` directories can be empty when no physical evidence exists. In that case all physical requirements remain `UNVERIFIED`.

The signing stage can then add:

```text
release-attestation.json
release-record.json
release-record.sha256
```

The `product/` directory is a release-evidence payload assembled from the already-built Release outputs. It is not an installer, deployment package or claim of offline deployment acceptance.

PDB files are excluded from this evidence payload. All included product files are recorded with byte length and SHA-256.

## Artifact Manifest

`artifact-manifest.json` records:

- product and release-stage identity,
- source and build commits,
- build identity,
- SHA-256 as the integrity algorithm,
- every collected product artifact,
- exact byte length and digest.

The verifier recalculates every digest from the actual bundle. A missing, truncated or modified recorded artifact fails verification.

## Software Bill of Materials

`sbom.cdx.json` uses CycloneDX `1.6`.

The generator derives the production dependency inventory from resolved `project.assets.json` files below `src/`. Test-only projects are not used as the production software-composition source.

The SBOM proves the resolved NuGet component inventory captured by this build. It does not by itself prove:

- vulnerability absence,
- license-policy acceptance,
- dependency security review,
- formal CRA conformity.

Those claims require separate evidence.

## Qualification Evidence Manifest

`qualification-evidence-manifest.json` is the release-local bridge between dedicated physical qualification workflows and compatibility evidence.

The static release policy never grants physical hardware `PASS`. Its five V1 requirements remain configured as `UNVERIFIED`:

```text
REFERENCE_GPU
PROFESSIONAL_MEDIA_IO
GENLOCK
PHYSICAL_END_TO_END_LATENCY
LONG_SOAK
```

A requirement becomes `PASSED` in the qualification manifest only when a repository-authorized binding exists for the same source commit and the exact binding/payload bytes pass their fail-closed verifier. The manifest also carries a `supportedPerformance` reference containing status, path and SHA-256 for machine-readable performance evidence derived exclusively from verified physical payloads. The accepted binding, payload and supported-performance bytes are copied into the release-evidence bundle and hashed again after the copy.

The current qualification mapping is documented in `docs/QualificationEvidenceProvenance.md`.

## Compatibility Manifest

Compatibility is explicit rather than inferred from version equality.

`build/release/release-policy.json` currently declares exact support for:

```text
Control contract   1.0
Runtime contract   1.0
Media contract     1.0
AI contract        1.0
Provider contract  1.0
IPC protocol       1.0
IPC schema set     schemas/ipc/v1
```

The generated manifest uses `EXACT_DECLARED` compatibility policy. The offline verifier compares the generated manifest back to the controlled policy and fails on undeclared, missing or changed compatibility entries.

Physical compatibility status is derived from the source-bound qualification evidence manifest. `UNVERIFIED` remains `UNVERIFIED`; only a verified manifest `PASSED` entry becomes compatibility `PASS`.

Normal managed or virtual tests must not turn hardware claims into `PASS`.

## Release Evidence Manifest

`release-evidence.json` records the controlled evidence domains:

```text
ARCHITECTURE
CONTRACTS
BEHAVIOR
FAILURE
PERFORMANCE
COMPATIBILITY
SECURITY
SUPPLY_CHAIN
DOCUMENTATION
COMPLIANCE
KNOWN_ISSUES
```

Allowed states are exactly:

```text
PASS
FAIL
NOT_APPLICABLE
UNVERIFIED
```

Allowed severities are:

```text
BLOCKER
CRITICAL
MAJOR
MINOR
INFORMATIONAL
```

A CI invocation may mark the managed test-backed domains `PASS` only because evidence generation happens after the complete `dotnet test rtaime.slnx --configuration Release --no-build` command has succeeded.

The current DEV evidence intentionally keeps:

- `SECURITY = UNVERIFIED`,
- `COMPLIANCE = UNVERIFIED`,
- `KNOWN_ISSUES = UNVERIFIED`,
- production signing trust `UNVERIFIED`,
- overall release readiness `UNVERIFIED`.

Physical hardware requirements also remain `UNVERIFIED` unless same-source physical bindings are actually supplied to that release build.

A later cryptographic attestation does not rewrite those claims. It proves the integrity/provenance relationship of the evidence subject; it does not magically satisfy every release domain.

## Evidence verification

Run after a Release build and evidence generation:

```powershell
./build/release/Test-ReleaseEvidence.ps1
```

It validates at minimum:

- product/source/build identities across manifests,
- artifact existence, byte length and SHA-256,
- manifest-to-manifest hash references,
- CycloneDX format and production component inventory,
- explicit contract and IPC compatibility against release policy,
- source-bound physical qualification provenance,
- byte-identical copied qualification binding and payload hashes,
- qualification workflow run/attempt provenance,
- evidence status and severity allowlists,
- required evidence-domain presence,
- fail-closed handling of any `FAIL` evidence,
- release-stage promotion requiring an explicit readiness `PASS`.

After release attestation is created, also run:

```powershell
./build/release/Test-ReleaseAttestation.ps1
```

The signing profile, trust-store policy, tamper qualification and content-addressed record are documented in `docs/ReleaseSigningAndAttestation.md`.

## CI integration

The pull-request validation workflow executes in this order:

```text
Restore
Build
Test
Generate Release Evidence
Bind same-source physical qualification evidence if present
Verify Release Evidence
Generate ephemeral CI signing key outside repository
Sign Release Evidence
Verify Signature + Release Record
Run negative signing qualification
Delete ephemeral private key
Upload Evidence Bundle
```

The uploaded GitHub Actions artifact is evidence for the specific build run. A test-ephemeral signature is not an official production signature and the Actions artifact is not automatically an immutable official release record store.

Normal GitHub-hosted Required Gates do not contain reference hardware artifacts, so their qualification manifest remains `UNVERIFIED`. That behavior is intentional and is separately regression-tested.

## Security boundary

Private production signing material must not be stored in the repository, release policy, test fixtures, generated evidence bundle or logs.

Only public release-key trust metadata may be version-controlled.

The CI signing key is purpose-built test material generated outside the repository for one qualification run and deleted after use. It cannot establish production signing trust.

Qualification bindings contain provenance and hashes only. They do not carry private signing material.

## Scope boundary

The release evidence and signing foundations do not claim:

- `STABLE`, `VALIDATED` or `CERTIFIED` product status,
- production key provisioning or HSM/KMS qualification,
- immutable external release-record retention,
- formal CRA compliance or conformity,
- vulnerability-management completion,
- license-compliance approval,
- professional GPU qualification without retained same-source physical evidence,
- professional media-I/O qualification without retained same-source physical evidence,
- genlock qualification without retained same-source physical evidence,
- physical end-to-end latency qualification without retained same-source physical evidence,
- long-soak qualification without retained same-source physical evidence,
- installer/deployment acceptance,
- byte-for-byte reproducible builds across independent environments.

Those remain separate proof obligations and must receive their own evidence before their status can change.
