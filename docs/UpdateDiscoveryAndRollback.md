<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Update Discovery, Verified Download & Rollback Foundation

## Purpose

Update Discovery & Rollback adds the first controlled software-update foundation on top of the Release Packaging & Offline Deployment offline package, Release Pipeline & Channels Release Candidate, and Release Publication & Discovery publication/discovery trust chain.

The production path is:

```text
Verified installed release
    ↓
GitHub Release discovery
    ↓
PREVIEW / STABLE publication metadata
    ↓
Candidate + Publication + Descriptor download
    ↓
Cross-hash and production-trust verification
    ↓
Update plan
    ↓
Explicit process-quiescence acknowledgement
    ↓
Side-by-side staging
    ↓
Pre-activation bundle verification
    ↓
Atomic software-root replacement
    ↓
Post-activation verification
    ↓
Retained rollback slot
```

The update mechanism is software deployment only. It is not Production Package activation.

## Update policy

The policy is version-controlled in:

```text
build/update/update-policy.json
```

Supported current channels:

```text
PREVIEW
STABLE
```

Allowed transitions:

```text
PREVIEW → PREVIEW
PREVIEW → STABLE
STABLE  → STABLE
```

The following are rejected:

```text
STABLE → PREVIEW
same-version reinstall
downgrade
draft release
QUALIFICATION as a production update source
unknown signing key
untrusted production bundle
second update while rollback slot exists
```

## Installed release inspection

`Get-InstalledReleaseState.ps1` verifies the current software root before update planning.

Production inspection requires:

```text
Test-OfflineReleaseBundle.ps1 -RequireTrustedProductionKey
```

The resulting state records:

- product version,
- release stage/channel,
- source/build commit,
- Release Record identity,
- release signing-key fingerprint,
- active `SOFTWARE_RELEASE` trust fingerprints from the installed trust store,
- platform identity,
- integrity verification status.

The installed trust store is important for update key continuity.

## Trust continuity and key rotation

A downloaded target may not authorize its own signing key.

The update planner therefore requires:

```text
target Candidate signing-key fingerprint
∈
currently installed ACTIVE SOFTWARE_RELEASE trust fingerprints
```

This is in addition to the target Candidate declaring:

```text
signerClass = EXTERNAL_CONTROLLED
productionTrust = PASS
publicationReadiness = PASS
```

This prevents a newly downloaded bundle from becoming trusted merely by shipping a trust store that contains its own signer.

Key rotation must therefore be prepared in an earlier trusted software release by enrolling the future key before releases signed exclusively by that key are offered as updates.

Update Discovery & Rollback does not implement cross-signing or emergency trust recovery.

## Online discovery

`Resolve-UpdateDiscovery.ps1` queries the GitHub Releases API for:

```text
DaveBeusing/rtaime
```

and selects either:

```text
PREVIEW → GitHub prerelease with vX.Y.Z-preview.N tag
STABLE  → non-prerelease with vX.Y.Z tag
```

Draft releases are ignored.

Discovery may be pinned to an exact version. Without a pin, the highest eligible semantic version newer than the installed version is selected.

A discovery result is not trust evidence. It identifies where verification material can be downloaded.

## Verified download

`Get-VerifiedUpdateCandidate.ps1` downloads only expected release assets from the approved GitHub release-download origin.

Downloaded material includes:

```text
release-candidate.json
release-candidate.sha256
rtaime-<version>-win-x64.zip
rtaime-<version>-win-x64.zip.sha256
release-publication.json
release-publication.sha256
release-notes.md
rtaime-channel-<channel>.json
rtaime-channel-<channel>.json.sha256
```

`Test-DownloadedRelease.ps1` then cross-checks:

- GitHub release tag/channel metadata,
- Candidate manifest and sidecar,
- Candidate content identity,
- Publication manifest and sidecar,
- Publication/Candidate identity binding,
- channel descriptor and descriptor sidecar,
- Candidate/Publication/Descriptor source/tag/version consistency,
- exact offline bundle hash,
- `EXTERNAL_CONTROLLED` signing,
- production-trust status,
- Candidate publication readiness,
- trusted production offline-bundle verification.

The channel descriptor remains `POINTER_ONLY` and is never treated as an independent trust anchor.

## Update plan

`New-UpdatePlan.ps1` is the decision boundary between downloaded release material and activation.

It rejects activation unless:

```text
current installation integrity = PASS
target channel transition allowed
target version > current version
target source commit != current source commit
target signer = EXTERNAL_CONTROLLED
target production trust = PASS
target publication readiness = PASS
target key enrolled by current installation
```

The resulting `update-plan.json` binds the exact target Candidate id and bundle SHA-256.

## Process quiescence

Update Discovery & Rollback does not automatically stop ControlHost, RuntimeHost, AIHost, Operator, Windows services, or third-party provider processes.

Production update invocation requires explicit:

```powershell
-AcknowledgeProcessesStopped
```

This makes process quiescence a deliberate operational boundary rather than silently replacing binaries that may still be in use.

Automated service/process coordination belongs to a later operational deployment package.

## Atomic software replacement

`Invoke-AtomicSoftwareReplacement.ps1` performs:

```text
1. verify current installation
2. copy incoming ZIP to a local immutable snapshot
3. verify ZIP before extraction
4. extract to sibling staging directory
5. verify staged software bundle
6. run offline preflight
7. rename current installation → <InstallPath>.rollback
8. rename stage → active InstallPath
9. verify active installation
```

A successful activation retains exactly one rollback slot:

```text
<InstallPath>.rollback
```

If that slot already exists, another managed update fails closed.

This prevents silently destroying the only software rollback snapshot.

## Automatic rollback on activation failure

If an exception occurs after the current installation has been moved into the rollback slot, the engine:

```text
removes failed new active tree
→ restores rollback tree to active path
→ verifies restored bundle
→ compares restored bundle-manifest hash with pre-update state
→ rethrows the original update failure
```

The failed update therefore remains a failure even when automatic restoration succeeds.

Rollback success is not used to rewrite the update result to PASS.

## Manual rollback

`Invoke-SoftwareRollback.ps1` requires process-quiescence acknowledgement and verifies both active and rollback trees before switching them.

It uses a sibling swap directory so that, after a successful rollback:

```text
active path   = previous version
rollback slot = version that was active immediately before rollback
```

This allows an operator to reverse the rollback if required while preserving exactly one rollback slot.

## Persistent application state boundary

Update Discovery & Rollback does **not** migrate, back up, restore, or claim successful protection of persisted operational/application data.

Policy explicitly records:

```text
persistentStateMigration = NOT_IMPLEMENTED
```

The atomic mechanism covers the software installation root only.

Before a future package permits state/schema migrations, persistent data locations, backup semantics, restore validation, migration ordering, and compatibility rules must be explicit.

## Qualification strategy

The real production update path requires a published PREVIEW/STABLE Candidate with active production trust. Such a release is not fabricated in CI.

Instead `Test-UpdateFoundation.ps1` uses the existing QUALIFICATION offline bundle only to test the transport-independent replacement mechanics:

- update tools are packaged,
- clean installation works,
- incoming ZIP is verified before extraction,
- simulated post-commit failure automatically restores current software,
- successful replacement retains rollback slot,
- second replacement is blocked while rollback exists,
- manual rollback swap succeeds,
- same-version reinstall is rejected,
- STABLE → PREVIEW is rejected,
- a target key not enrolled by the current installation is rejected.

This qualifies mechanics without promoting TEST_EPHEMERAL trust to production trust.

## Production command

From an installed bundle carrying the Update Discovery & Rollback tools:

```powershell
./tools/Invoke-VerifiedUpdate.ps1 `
    -InstallPath C:\rtaime `
    -Channel STABLE `
    -AcknowledgeProcessesStopped
```

Optional exact-version pinning:

```powershell
-PinnedVersion 1.2.3
```

The update remains operator-initiated.

## Explicit non-claims

Update Discovery & Rollback does not implement or claim:

- background or scheduled auto-update,
- automatic host/service quiescence,
- persistent database/state backup,
- schema migration,
- persistent-state rollback,
- Production Package activation,
- downgrade support,
- emergency release-key recovery,
- CDN/mirror failover,
- delta/patch updates,
- bandwidth optimization,
- fleet orchestration,
- Reference Platform hardware qualification,
- VALIDATED or CERTIFIED product status,
- formal CRA conformity.

`UNVERIFIED` remains distinct from `PASS`.
