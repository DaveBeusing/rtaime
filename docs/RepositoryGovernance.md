<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Repository Governance & Required Gates

## Purpose

Repository governance is an explicit, version-controlled contract for the `master` integration branch.

The intended enforcement model is:

```text
Pull Request
    ↓
CI
Quality
Security
Packaged E2E
Provider Smoke
    ↓
all required checks PASS
    ↓
master merge allowed
```

Direct changes to `master`, force pushes and deletion of `master` are prohibited by the desired GitHub ruleset.

## Required check contexts

The required checks are intentionally stable and exact:

```text
CI
Quality
Security
Packaged E2E
Provider Smoke
```

The names are duplicated in:

- `governance/repository-policy.json`,
- `.github/rulesets/master.ruleset.json`,
- `.github/workflows/required-gates.yml`.

`build/governance/Test-RepositoryGovernance.ps1` fails if these sources drift.

## Gate responsibilities

### CI

`CI` is the broad managed correctness gate:

```text
restore
build Release
complete solution test run
```

The equivalent developer commands and targeted test-suite commands are maintained in [BuildAndTest.md](BuildAndTest.md). Local execution is useful pre-PR evidence but does not replace the required GitHub checks.

### Quality

`Quality` checks:

```text
repository governance intent
release channel / single-pipeline policy
Release build
Architecture tests
Contract tests
Unit tests
```

`build/release/Test-ReleaseChannelPolicy.ps1` also verifies that release workflows call only the authoritative release orchestrator rather than duplicating low-level evidence/signing/packaging logic.

### Security

`Security` is repository security governance, not a claim of complete product-security certification.

It currently fails closed on:

- tracked private-key file extensions,
- PEM private-key material,
- committed generated release artifacts,
- structurally invalid trusted release-key metadata,
- broad write permissions in the Required Gates workflow.

This gate does not convert the broader `SECURITY` Release Evidence domain from `UNVERIFIED` to `PASS`.

### Provider Smoke

`Provider Smoke` rebuilds the approved graph and runs the Integration test project.

This provides a stable provider/host composition smoke gate for the currently implemented VirtualMedia, GPU and inference integration paths.

It does not qualify unavailable physical GPU or professional media-I/O hardware.

### Packaged E2E

`Packaged E2E` checks out the exact pull-request head and calls:

```text
build/release/Invoke-ReleasePipeline.ps1
    -Channel QUALIFICATION
    -SignerClass TEST_EPHEMERAL
```

The orchestrator owns:

```text
restore
build
complete tests
Release Evidence
Release Attestation
signing failure qualification
offline bundle
offline verification and preflight
offline failure qualification
clean install / post-install verification
Release Candidate manifest
candidate failure qualification
```

This proves the complete packaged path without pretending that a CI test key is production signing trust.

## Single release implementation

Release implementation is centralized in:

```text
build/release/Invoke-ReleasePipeline.ps1
```

The Required Gates workflow and dedicated Release Pipeline workflow both invoke this script.

The legacy `.github/workflows/bootstrap-validation.yml` was removed after the single-pipeline consolidation because its managed validation is covered by `CI` and its packaged path is covered by `Packaged E2E`.

## Primary branch rules

The desired `master` ruleset is stored at:

```text
.github/rulesets/master.ruleset.json
```

It requires:

- pull-request based changes,
- all five required status checks,
- strict/up-to-date required checks,
- resolved review threads,
- no force push,
- no branch deletion.

It deliberately does **not** impose:

- a mandatory approving-review count,
- mandatory CODEOWNERS approval,
- mandatory approval by a person different from the last pusher.

The project uses PR review and evidence without inventing a foreign-review requirement that is not part of the accepted governance model.

## Administrative enforcement evidence

Repository files cannot by themselves prove that GitHub is enforcing the ruleset.

Therefore `governance/repository-policy.json` records administrative enforcement as:

```text
UNVERIFIED
```

until live repository settings provide direct evidence.

At the most recent repository check before Release Pipeline & Channels implementation, GitHub still reported:

```text
master protected = false
repository rulesets = []
```

That state must not be represented as governance PASS.

The connected repository automation can read rulesets but does not expose administrative ruleset mutation. Live activation remains an explicit repository-administration action and evidence obligation.

## Required live GitHub state

Administrative enforcement becomes PASS only when live repository evidence shows an active branch ruleset for `refs/heads/master` equivalent to the version-controlled specification and requiring exactly:

```text
CI
Quality
Security
Packaged E2E
Provider Smoke
```

with pull requests required, force push blocked and deletion blocked.

If live state differs from the version-controlled specification, live enforcement status is not PASS.

## Workflow permissions

`.github/workflows/required-gates.yml` defaults to:

```text
contents: read
```

`.github/workflows/release-pipeline.yml` also remains read-only during Release Pipeline & Channels.

Release Candidate creation therefore does not imply GitHub Release publication authority.

Future publication workflows may legitimately need narrower write permissions, but those permissions belong to their dedicated publication boundary and must not broaden validation workflows.

## Evidence boundary

Repository-side PASS can cover:

- policy consistency,
- stable required check names,
- managed build/test execution,
- architecture/contract/unit quality tests,
- release-channel policy consistency,
- repository secret/private-key guardrails,
- provider integration smoke,
- authoritative packaged end-to-end qualification.

The following remains separate:

- live GitHub ruleset activation,
- production signing-key enrollment,
- release publication/distribution,
- hardware qualification,
- formal compliance determination.

`UNVERIFIED` is never converted into `PASS` by documentation or intent alone.
