<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Product Security Assessment & Release Evidence Binding

Status: AP-37 IMPLEMENTATION FOUNDATION

## Purpose

AP-37 adds a fail-closed bridge between a completed product-security review and the `SECURITY` domain in release evidence.

The assessment is engineering evidence for one exact source and build identity. It does not assert CRA conformity, CE readiness, legal product classification, regulatory submission, production support-period fulfillment, or production signing trust.

## Default state

A normal release-evidence generation without a supplied product-security assessment continues to produce:

```text
SECURITY = UNVERIFIED
```

No repository policy, successful unit test, empty vulnerability list, SBOM generation, or successful signing operation may silently promote that state.

## Assessment contract

The canonical contract is:

```text
schemas/security/v1/product-security-assessment.schema.json
```

A product-security assessment records:

- `assessmentId`;
- product name and version;
- exact source commit;
- exact build commit;
- exact build identity;
- overall `PASS`, `FAIL`, or `UNVERIFIED` status;
- source review status;
- dependency review status;
- vulnerability-triage status;
- security-update-path status;
- security findings and dispositions;
- reviewer identity and role;
- assessment timestamp.

For `PASS`, every required check must be `PASS`, and no `BLOCKER`, `CRITICAL`, or `HIGH` finding may remain in a state other than `CLOSED`.

## Exact source binding

Release promotion is bound to the exact source identity of the release-evidence subject.

The following values must match exactly:

```text
product.version
source.sourceCommit
source.buildCommit
source.buildId
```

An assessment for another branch head, merge tree, build, or product version is rejected. Evidence is never inherited from a nearby commit or assumed equivalent because a diff appears small.

## Binding order

The binding order is mandatory:

```text
Build and test exact candidate
    ↓
Generate release evidence
    ↓
Optional product-security assessment binding
    ↓
Verify release evidence and assessment hash/provenance
    ↓
Create release attestation
    ↓
Create release record
    ↓
Generate offline bundle
```

The assessment must be bound before release attestation. `Bind-ProductSecurityAssessment.ps1` rejects evidence directories that already contain a release attestation or release record.

This prevents a post-signing mutation from changing the `SECURITY` claim without invalidating the release trust path.

## Binding mechanics

The binder copies the accepted assessment to the canonical release-evidence path:

```text
security-assessment.json
```

It then records a SHA-256 reference and assessment ID in `release-evidence.json` and promotes only the `SECURITY` domain from `UNVERIFIED` to `PASS`.

`Test-ProductSecurityAssessmentBinding.ps1` verifies:

- canonical path;
- SHA-256 integrity;
- assessment ID;
- assessment `PASS` state;
- required check states;
- high-severity finding closure;
- exact product version;
- exact source commit;
- exact build commit;
- exact build identity;
- `SECURITY` domain source and state.

`Test-ReleaseEvidence.ps1` invokes this binding verifier on every release-evidence verification. A tampered assessment therefore fails verification even after it was originally accepted.

## Release pipeline input

The authoritative pipeline exposes an optional explicit input:

```powershell
./build/release/Invoke-ReleasePipeline.ps1 `
    -Channel QUALIFICATION `
    -SignerClass TEST_EPHEMERAL `
    -SignerId "qualification" `
    -SecurityAssessmentPath "<path-to-assessment.json>"
```

Omitting `-SecurityAssessmentPath` does not fail a DEV/QUALIFICATION evidence build; it keeps `SECURITY=UNVERIFIED`.

Supplying the parameter is not a request to trust the file. The binder validates the file against the exact release identity before the evidence subject is signed.

## Failure qualification

`build/security/Test-ProductSecurityAssessmentFailureCases.ps1` qualifies the fail-closed behavior for at least:

- valid exact-source binding;
- source-commit mismatch;
- tampering after binding;
- an `UNVERIFIED` assessment attempting promotion;
- a nominal `PASS` assessment with an unresolved `HIGH` finding;
- a nominal `PASS` assessment with a failed dependency review.

These cases run through the repository Security gate via the product-security policy verifier.

## Evidence boundary

A product-security assessment `PASS` means only that the defined engineering review contract has been completed for the exact release identity and passed its configured checks.

It does not assert CRA conformity. It also does not establish:

- formal conformity assessment;
- CE marking authorization;
- completed ENISA or CSIRT reporting;
- vulnerability absence;
- production signing-key trust;
- support-period fulfillment;
- hardware qualification;
- `STABLE`, `VALIDATED`, or `CERTIFIED` release readiness.

Those remain separate evidence domains and proof obligations. Missing evidence remains `UNVERIFIED`.
