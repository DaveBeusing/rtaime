<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Repository Governance & Required Gates

## Purpose

AP-20 turns repository governance into an explicit, version-controlled contract for the `master` integration branch.

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

`build/governance/Test-RepositoryGovernance.ps1` fails if these three sources drift.

## Gate responsibilities

### CI

`CI` is the broad managed correctness gate:

```text
restore
build Release
complete solution test run
```

It is the primary regression gate for the managed solution.

### Quality

`Quality` checks repository-governance intent and the high-value architecture/contract/unit suites:

```text
repository governance intent
Release build
Architecture tests
Contract tests
Unit tests
```

The gate is deliberately separate from `CI` so architecture and contract regressions are visible as a dedicated required status.

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

`Packaged E2E` rebuilds and retests the exact candidate it packages, then executes:

```text
Release Evidence generation
Release Evidence verification
TEST_EPHEMERAL signing
Release Attestation verification
signing failure qualification
offline bundle generation
ZIP verification
offline preflight
offline failure qualification
clean installation
post-install verification
```

This gate proves the managed package path without pretending a CI test key is production signing trust.

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

At AP-20 implementation start, the live repository reported:

```text
master protected = false
repository rulesets = []
```

That state must not be represented as governance PASS.

The connected repository automation available during AP-20 can read rulesets but does not expose administrative ruleset mutation. Consequently the package can implement and validate the repository-side contract, while live activation remains an explicit repository-administration action and evidence obligation.

## Required live GitHub state

For AP-20 administrative enforcement to become PASS, live repository evidence must show an active branch ruleset for `refs/heads/master` equivalent to the version-controlled specification and requiring exactly:

```text
CI
Quality
Security
Packaged E2E
Provider Smoke
```

with pull requests required, force push blocked and deletion blocked.

If live state differs from the version-controlled specification, the live enforcement status is not PASS.

## Workflow permissions

`.github/workflows/required-gates.yml` defaults to:

```text
contents: read
```

The Required Gates workflow must not need repository write authority.

Future publishing workflows may legitimately need narrower write permissions, but those permissions belong to their dedicated workflow and do not broaden the required validation gates.

## Existing validation workflow

`bootstrap-validation.yml` remains present during AP-20 for continuity.

AP-20 adds stable required gates without claiming that the entire repository already has one final release pipeline. Consolidation into a single trusted Source → Evidence → Package → Release pipeline belongs to the next release-pipeline hardening package.

## Evidence boundary

Repository-side PASS can cover:

- policy consistency,
- stable required check names,
- managed build/test execution,
- architecture/contract/unit quality tests,
- repository secret/private-key guardrails,
- provider integration smoke,
- packaged end-to-end qualification.

The following remains separate:

- live GitHub ruleset activation,
- production signing-key enrollment,
- hardware qualification,
- formal compliance determination.

`UNVERIFIED` is never converted into `PASS` by documentation or intent alone.
