<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

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

The current development identity is declared centrally in `Directory.Build.props`:

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

## Generated evidence bundle

`build/release/New-ReleaseEvidence.ps1` first creates:

```text
artifacts/release-evidence/
├─ product/
│  ├─ rtaime.ControlHost/
│  ├─ rtaime.RuntimeHost/
│  ├─ rtaime.AIHost/
│  └─ rtaime.Operator/
├─ artifact-manifest.json
├─ sbom.cdx.json
├─ compatibility-manifest.json
└─ release-evidence.json
```

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

Reference GPU, professional media I/O and genlock qualification remain `UNVERIFIED`. Virtual or managed tests must not turn those hardware claims into `PASS`.

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
Verify Release Evidence
Generate ephemeral CI signing key outside repository
Sign Release Evidence
Verify Signature + Release Record
Run negative signing qualification
Delete ephemeral private key
Upload Evidence Bundle
```

The uploaded GitHub Actions artifact is evidence for the specific build run. A test-ephemeral signature is not an official production signature and the Actions artifact is not automatically an immutable official release record store.

## Security boundary

Private production signing material must not be stored in the repository, release policy, test fixtures, generated evidence bundle or logs.

Only public release-key trust metadata may be version-controlled.

The CI signing key is purpose-built test material generated outside the repository for one qualification run and deleted after use. It cannot establish production signing trust.

## Scope boundary

The release evidence and signing foundations do not claim:

- `STABLE`, `VALIDATED` or `CERTIFIED` product status,
- production key provisioning or HSM/KMS qualification,
- immutable external release-record retention,
- formal CRA compliance or conformity,
- vulnerability-management completion,
- license-compliance approval,
- professional GPU qualification,
- professional media-I/O qualification,
- genlock qualification,
- installer/deployment acceptance,
- byte-for-byte reproducible builds across independent environments.

Those remain separate proof obligations and must receive their own evidence before their status can change.
