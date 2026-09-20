<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Release Publication, Distribution & Channel Discovery

## Purpose

Release Publication & Discovery adds the publication boundary after Release Pipeline & Channels Release Candidate creation.

The authoritative lifecycle is now:

```text
Source
    ↓
Authoritative Release Pipeline
    ↓
Verified Release Candidate
    ↓
Publication verification
    ↓
Immutable GitHub Release
    ↓
Preview / Stable discovery descriptor
```

Publication does not rebuild, retest, resign or repackage product payloads.

## Trust boundary

The Release Candidate remains the authoritative product trust source.

Publication metadata and channel descriptors bind to the Candidate, but they do not replace:

- Release Attestation,
- Release Record,
- Bundle Attestation,
- Bundle Manifest,
- signed product artifact hashes.

The channel descriptor explicitly declares:

```text
discoveryTrust.status = POINTER_ONLY
```

A consumer must verify the Release Candidate and contained trust chain before trusting downloaded payloads.

## Publication channels

### QUALIFICATION

Publication is prohibited.

A QUALIFICATION candidate exists only as CI evidence and cannot be converted into an official GitHub Release.

### PREVIEW

Preview publication requires:

```text
channel = PREVIEW
signerClass = EXTERNAL_CONTROLLED
productionTrust = PASS
matching version/tag/source commit
```

The GitHub Release is created as a prerelease and must not become the latest Stable release.

Release Pipeline & Channels still permits `TEST_EPHEMERAL` Preview mechanism qualification. Release Publication & Discovery deliberately rejects such a Candidate for public distribution.

### STABLE

Stable publication requires:

```text
channel = STABLE
signerClass = EXTERNAL_CONTROLLED
productionTrust = PASS
candidate publicationReadiness = PASS
matching version/tag/source commit
```

The GitHub Release is non-prerelease and becomes the latest release.

## Immutability

Publication policy is fail-closed:

```text
existing GitHub Release
→ FAIL

asset overwrite
→ forbidden

rebuild during publication
→ forbidden
```

The `Publish Release` job only downloads the Candidate artifact produced by the preceding `Release Candidate` job.

It must not invoke:

- `dotnet build`,
- `dotnet test`,
- `Invoke-ReleasePipeline.ps1`,
- Release Evidence generation,
- Release Attestation generation,
- Offline Bundle generation.

## GitHub permissions

The workflow defaults to:

```yaml
permissions:
  contents: read
```

Only the `publish-release` job receives:

```yaml
permissions:
  contents: write
```

This separates Candidate creation authority from repository publication authority.

## Published assets

An official GitHub Release contains the exact Candidate assets:

```text
release-candidate.json
release-candidate.sha256
rtaime-<version>-win-x64.zip
rtaime-<version>-win-x64.zip.sha256
```

and publication/discovery metadata:

```text
release-publication.json
release-publication.sha256
release-notes.md
rtaime-channel-preview.json
rtaime-channel-preview.json.sha256
```

or for Stable:

```text
rtaime-channel-stable.json
rtaime-channel-stable.json.sha256
```

No generated asset may silently overwrite an existing release asset.

## Publication manifest

`release-publication.json` binds:

- repository identity,
- Preview/Stable channel,
- product version and stage,
- Git tag,
- source commit,
- Release Candidate id and manifest hash,
- offline bundle hash,
- signing-key fingerprint,
- production trust status,
- expected GitHub Release URL,
- prerelease/latest behavior,
- immutability rules,
- release-notes hash.

Its `publicationId` is content-addressed from repository, channel, version, tag, source commit, Candidate id, Candidate manifest hash and bundle hash.

## Channel discovery

Each published release carries one channel descriptor:

```text
PREVIEW → rtaime-channel-preview.json
STABLE  → rtaime-channel-stable.json
```

The descriptor points to:

- GitHub Release tag and URL,
- product version,
- Candidate id,
- Candidate manifest SHA-256,
- source commit,
- Publication id,
- Publication manifest SHA-256,
- bundle filename and SHA-256.

### Stable discovery

GitHub's latest non-prerelease endpoint can identify the current Stable GitHub Release.

The attached `rtaime-channel-stable.json` then provides the rtaime-specific discovery pointer and Candidate bindings.

### Preview discovery

Preview is represented by GitHub prereleases. A discovery client may enumerate releases and select the newest Preview release according to an explicit version/channel policy, then verify `rtaime-channel-preview.json` and finally the Candidate trust chain.

Release Publication & Discovery does not implement a client-side updater or autonomous version selection engine.

## Publication verification

Before GitHub mutation, the workflow executes:

```powershell
./build/release/New-ReleasePublication.ps1 ...
./build/release/Test-ReleasePublication.ps1 ...
```

The verifier rechecks:

- the complete Release Candidate,
- Preview/Stable publication policy,
- external signer requirement,
- Production Trust PASS,
- Candidate/source/tag binding,
- publication content id,
- Candidate and bundle hashes,
- release-notes hash,
- channel descriptor binding,
- channel descriptor sidecar,
- immutable/no-rebuild policy.

## Post-publication verification

After `gh release create`, the workflow reads the GitHub Release back and verifies:

- tag,
- draft state,
- prerelease state,
- all expected asset names.

The job fails if the published GitHub Release does not match the prepared publication.

## Current evidence boundary

Repository/CI qualification can prove:

- publication policy consistency,
- workflow permission separation,
- no-rebuild publication design,
- publication metadata generation/verifier code,
- fail-closed channel policy.

Until the first real trusted Preview or Stable tag is published, actual GitHub Release creation remains `UNVERIFIED`.

There were no GitHub Releases in the repository when Release Publication & Discovery implementation began.

## Non-claims

Release Publication & Discovery does not implement or claim:

- automatic client updates,
- background update download,
- update installation or rollback,
- CDN/mirror distribution,
- package-manager feeds,
- mutable channel aliases outside GitHub Releases,
- cryptographic signing of discovery metadata itself,
- transparency-log or WORM retention,
- HSM/KMS key custody,
- hardware qualification,
- VALIDATED or CERTIFIED product status,
- formal CRA conformity.

`UNVERIFIED` remains distinct from `PASS`.
