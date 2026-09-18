<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Release Packaging & Offline Deployment

## Purpose

The V1 offline deployment foundation converts a verified software-release evidence set into a self-describing Windows x64 deployment bundle that can be verified, preflighted and clean-installed without continuous Internet connectivity.

The deployment trust path is:

```text
Controlled Build
    ↓
Release Evidence
    ↓
Release Attestation / Release Record
    ↓
Offline Bundle Manifest
    ↓
Offline Bundle Attestation
    ↓
ZIP transport + SHA-256 sidecar
    ↓
Offline verification
    ↓
Offline preflight
    ↓
Verified clean installation staging
```

Software deployment remains distinct from Production Package activation.

## Bundle identity

The V1 package type is:

```text
bundleType          SOFTWARE_RELEASE
packageFormatVersion 1.0
osFamily            Windows
architecture        x64
rid                 win-x64
```

This package format is not the Production Package format described by the production architecture.

## Bundle contents

A generated bundle contains:

```text
bundle-manifest.json
bundle-attestation.json
OFFLINE-README.md
Start-rtaime-Showcase.cmd

product/
    rtaime.ControlHost/
    rtaime.RuntimeHost/
    rtaime.AIHost/
    rtaime.Operator/

release/
    artifact-manifest.json
    sbom.cdx.json
    compatibility-manifest.json
    release-evidence.json
    release-attestation.json
    release-record.json
    release-record.sha256

metadata/
    runtime-requirements.json

trust/
    trusted-release-keys.json

schemas/
    ... current repository schemas ...

docs/
    ... current repository documentation ...

tools/
    Test-OfflineReleaseBundle.ps1
    Invoke-OfflinePreflight.ps1
    Install-OfflineRelease.ps1
    Invoke-ManagedHostLifecycle.ps1
    Invoke-InvestorDemo.ps1
    Start-rtaime-Showcase.cmd
```

The ZIP transport is accompanied by a `.zip.sha256` sidecar.

### Canonical application entry point

The installed/generated bundle root exposes `rtaime.exe` as the canonical product entry point. The executable is the thin AppHost and is copied from the controlled `product/rtaime` release payload together with its framework-dependent runtime files.

Starting `rtaime.exe` does not collapse the internal topology. ControlHost, RuntimeHost, AIHost and Operator remain separate product artifacts below `product/`. AppHost starts or adopts ControlHost, waits for qualified readiness, and launches Operator only when the selected startup profile requires it.

The existing `tools/Invoke-ManagedHostLifecycle.ps1` remains the administrative lifecycle entry point, and `Start-rtaime-Showcase.cmd` remains the deterministic showcase compatibility entry point.

### Repository source paths vs bundle paths

The repository does **not** use a root `tools/` directory as a source location. Offline operational tools are maintained at their canonical repository paths under:

```text
build/release/
build/update/
build/state/
build/operations/
build/showcase/
```

`build/release/offline-bundle-policy.json` is the allowlist that maps those source files into the generated bundle's flat `tools/` directory. The bundle path `tools/...` therefore always refers to an installed/generated release bundle, not to a repository source directory.

## Self-description and integrity

`bundle-manifest.json` declares:

- product identity and version,
- release stage,
- source/build/build-run identity,
- target platform,
- contained Release Record identity,
- contained release signing-key fingerprint,
- runtime requirements path,
- every payload file,
- payload role,
- byte length,
- SHA-256.

`bundle-attestation.json` signs the exact bytes of `bundle-manifest.json` using the same V1 ECDSA P-256 / SHA-256 profile as the Release Attestation.

The bundle generator requires the same signing key fingerprint for:

```text
Release Attestation
=
Offline Bundle Attestation
```

This prevents an unrelated key from silently repackaging an otherwise valid signed release while preserving the release identity.

## Release trust remains transitive

Offline bundle verification does not stop at the package manifest.

It also verifies:

1. the Bundle Attestation,
2. the contained Release Attestation,
3. the content-addressed Release Record,
4. the Release Evidence references,
5. every signed product artifact from `artifact-manifest.json`,
6. the package payload inventory.

Therefore product binaries in the offline bundle must still be the exact binaries represented by the signed release evidence.

## Production trust

Cryptographic validity and production trust remain distinct.

A CI bundle signed with:

```text
TEST_EPHEMERAL
```

can prove the package mechanism and tamper detection but cannot establish Production Release Trust.

`-RequireTrustedProductionKey` requires:

```text
EXTERNAL_CONTROLLED signer
+
active SOFTWARE_RELEASE public-key fingerprint
```

in the packaged trust metadata.

The current production trust store remains intentionally empty until a separate controlled key-enrollment process is completed.

## Runtime requirements

The V1 bundle is framework-dependent.

`metadata/runtime-requirements.json` currently declares:

```text
Windows x64 / win-x64
Microsoft.NETCore.App       10.0 >= 10.0.0
Microsoft.WindowsDesktop.App 10.0 >= 10.0.0
continuousInternetRequired = false
```

The offline preflight checks installed runtimes locally.

It does not download or bootstrap missing runtimes from the Internet.

Self-contained and single-file publishing is supported as a developer/distribution build option and is documented in [BuildAndTest.md](BuildAndTest.md).

The **authoritative V1 offline release bundle remains framework-dependent** until self-contained/single-file artifacts are explicitly added to the release pipeline, artifact inventory, runtime policy, offline preflight and release evidence. A developer single-file publish therefore must not be represented as a qualified PREVIEW/STABLE release artifact merely because it executes successfully.

A future qualified self-contained distribution must have its own size, servicing, licensing, extraction, integrity and security evidence.

## Offline verification

For an unpacked bundle:

```powershell
./tools/Test-OfflineReleaseBundle.ps1 -BundlePath .
```

For a ZIP:

```powershell
./Test-OfflineReleaseBundle.ps1 -BundlePath .\rtaime-<version>-win-x64.zip
```

Verification is local-only and checks at minimum:

- archive path traversal rejection,
- duplicate archive file rejection,
- symbolic-link rejection,
- bundle signature,
- bundle manifest hash,
- complete payload allowlist,
- payload sizes and SHA-256,
- no private-key-like files,
- runtime metadata identity,
- Release Attestation,
- Release Record identity and content address,
- Release Evidence references,
- signed product artifact hashes,
- trust-store policy.

Unknown or injected files fail closed.

## Offline preflight

Run:

```powershell
./tools/Invoke-OfflinePreflight.ps1 \
    -BundlePath . \
    -InstallPath C:\rtaime
```

Preflight performs:

```text
bundle verification
Windows qualification check
x64 architecture check
required .NET runtime discovery
continuous-Internet requirement check
install-volume free-space check
```

The free-space guard reserves room for staging plus installation rather than assuming a direct in-place copy.

## Clean installation

Run:

```powershell
./tools/Install-OfflineRelease.ps1 \
    -BundlePath . \
    -InstallPath C:\rtaime
```

The V1 foundation supports only a new or empty destination directory.

The install sequence is:

```text
source bundle snapshot / copy
        ↓
verification + preflight
        ↓
stage on destination volume
        ↓
verify staged bundle again
        ↓
move staged directory into final path
        ↓
verify installed directory
```

A non-empty destination fails before modification.

The installer does not overwrite or partially update an existing installation.

## Failure qualification

CI explicitly qualifies:

```text
tampered product payload
→ reject

tampered signed bundle manifest
→ reject

unlisted injected payload
→ reject

archive path traversal
→ reject before extraction

truncated ZIP
→ reject

non-empty installation target
→ reject + preserve existing content

valid clean installation
→ install + post-install verification PASS
```

## Funding showcase entry point

The installed bundle root contains `Start-rtaime-Showcase.cmd` for the controlled funding-showcase workflow.

The entry point does not create a parallel service manager. It delegates to the packaged showcase launcher, which reuses `Invoke-ManagedHostLifecycle.ps1`. ControlHost remains the top-level managed service process and retains RuntimeHost/AIHost supervision.

On a clean showcase start, the launcher:

```text
verify/start managed lifecycle
        ↓
runtime readiness PASS
        ↓
launch Operator with exact lifecycle endpoints
        ↓
interactive showcase
        ↓
Operator closes
        ↓
gracefully stop only the lifecycle started by the launcher
```

The entry point requires PowerShell 7 (`pwsh.exe`) because the existing offline lifecycle tooling is implemented in PowerShell. The entry point checks that prerequisite before changing lifecycle state. The presenter does not enter terminal commands or edit JSON/environment configuration.

See `InvestorDemoScenario.md` for the continuous demonstration sequence and acceptance boundary.

## Deployment boundary

The clean installer places the verified bundle tree at the requested location.

It does not:

- register Windows services,
- write machine-wide environment variables,
- configure firewall rules,
- install GPU/media-I/O drivers,
- install missing .NET runtimes,
- activate a Production Package,
- migrate persisted production state,
- start production execution.

Those operations require separate operational authority and evidence.

## Upgrade and rollback boundary

Release Packaging & Offline Deployment deliberately does not implement in-place update or rollback.

Existing installations are protected by a fail-closed rule:

```text
non-empty target
→ installation rejected
→ existing content unchanged
```

A later update/rollback package must define:

- installation discovery,
- supported source versions,
- compatibility gates,
- durable-state backup requirements,
- process/service quiescence,
- staged replacement,
- rollback triggers,
- migration ordering,
- post-update health validation.

None of those semantics are invented by this foundation package.

## Offline diagnostics

The bundle itself contains the documentation, schemas, trust metadata and verification/preflight tools required for basic diagnostics.

Basic diagnosis can therefore distinguish at least:

```text
invalid/tampered bundle
unsupported platform
missing runtime
insufficient disk space
non-empty install target
valid bundle ready for clean installation
```

Continuous access to GitHub, package feeds or cloud services is not required for these checks.

## Non-claims

This package does not claim:

- MSI/MSIX installer qualification,
- Windows service lifecycle installation,
- automatic update,
- in-place upgrade,
- rollback,
- driver installation,
- self-contained .NET runtime distribution,
- Production Package activation,
- production signing-key enrollment,
- immutable external/WORM publication,
- STABLE / VALIDATED / CERTIFIED product status,
- formal CRA conformity.

Those remain separate proof obligations.
