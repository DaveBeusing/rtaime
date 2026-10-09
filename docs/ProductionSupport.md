<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Production Support and Stable Readiness

## Purpose

This document describes the source-controlled operational support and Stable-readiness framework for rtaime.

It deliberately separates five different concepts:

1. **policy commitment** — what the organization has explicitly agreed to support;
2. **implementation capability** — what the software and tooling can do;
3. **qualification evidence** — what has been measured or verified for an exact source/configuration;
4. **release readiness** — whether a specific candidate satisfies the required gates;
5. **legal/regulatory conformity** — an external determination that is never inferred from repository policy.

A policy file can define the rules for support without proving a hardware configuration, production signing key, certification, or Stable release.

## Sources of truth

Machine-readable policy:

- docs/Governance/ProductSupportPolicy.json;
- docs/Governance/PlatformSupportMatrix.json;
- docs/Governance/ProductionDeploymentBaseline.json;
- build/release/release-channels.json;
- build/update/update-policy.json;
- build/update/coordinated-upgrade-policy.json.

Generated human-readable projection:

- docs/SupportCompatibilityMatrix.md.

The projection is verified in CI and must not be edited as a second source of support truth.

## Current support status

The policy framework is implemented, but the current support commitments are intentionally not promoted beyond their evidence:

| Area | Current status | Reason |
| --- | --- | --- |
| Stable support duration | UNVERIFIED | no approved maintenance/security duration exists |
| Preview support commitment | UNVERIFIED | no approved maintenance/security commitment exists |
| Security remediation targets/SLA | UNVERIFIED | no approved severity deadlines exist |
| Contract deprecation notice period | UNVERIFIED | no approved minimum notice duration exists |
| Supported Stable version lines | none | the source remains DEV |
| Supported platform tuple | none | no evidence-backed Windows/GPU/Media-I/O tuple is declared SUPPORTED |
| Production signing trust | UNVERIFIED | requires externally controlled active production trust |
| Physical qualification | UNVERIFIED unless exact source-bound evidence exists | hosted CI is not physical qualification |

No month/day value is implied by these UNVERIFIED states.

## Supported versions and EOL

ProductSupportPolicy.json is the support-version source of truth.

A Stable version line may be declared SUPPORTED only when its support start, maintenance end, security end and EOL date are explicit and valid. The policy verifier rejects reversed or incomplete dates.

After EOL, routine maintenance is not promised by the repository policy. Security handling is case-by-case until an approved exception policy or legal obligation is separately established.

Preview support remains distinct from Stable support. Qualification/DEV builds are engineering evidence and are not customer support commitments.

## Security support

Vulnerability intake and coordinated disclosure remain defined by SECURITY.md and docs/Governance/ProductSecurityPolicy.json.

Security fixes must use the normal trusted release/update path. An emergency does not authorize an unsigned replacement binary, embedded test key, or bypass of release evidence.

Severity-specific remediation durations remain UNVERIFIED until explicitly approved. This repository does not convert engineering policy into a legal service-level guarantee.

## Upgrade and deprecation

The support policy reuses the existing update architecture rather than defining a second mechanism:

- direct channel transitions come from build/update/update-policy.json;
- persistent-state migration comes from build/update/coordinated-upgrade-policy.json;
- production-trusted published candidates remain mandatory;
- downgrade and same-version reinstall remain blocked by the update policy;
- persistent-state migration remains coordinated-only;
- rollback remains evidence-bound.

Contract/API removal requires migration evidence and release-note disclosure. A numeric minimum deprecation notice period is not yet approved and therefore remains UNVERIFIED.

## Operational support model

Incident categories are product/operations categories, not promises of response time.

- **SEV1 — Critical production outage:** Program continuity, authoritative control or recovery unavailable without an acceptable workaround.
- **SEV2 — Major production degradation:** material production degradation or repeated recovery while Program remains available.
- **SEV3 — Functional defect:** bounded functional failure without immediate Program-continuity risk.
- **SEV4 — Question or enhancement:** usage, integration, documentation or enhancement request.

Ownership categories include Control Authority, Runtime/Media, Audio, GPU, Media I/O, Recording, Network Output, Integrations, Operator UI, Deployment/Update and Security.

SEV1 and SEV2 investigations require a support bundle unless collection itself is unavailable or unsafe.

## Support bundle contract

The existing Operator support-bundle implementation is the canonical collection path.

It is on-demand, bounded and redacted:

- maximum source diagnostic content: **256 MiB**;
- required metadata: Manifest.json and health/OperatorHealth.json;
- lifecycle and current correlated session logs are included when available;
- structured metadata is sanitized through rtaime.Core.DiagnosticRedactor;
- private keys, credentials, bearer tokens, media payloads, monitoring images and GPU surface contents are excluded by policy;
- executable regression tests cover both redaction and bounded collection.

Support data is diagnostic evidence, not Production Authority.

## Platform and driver compatibility

A platform tuple may be called SUPPORTED only when it is concrete and evidence-backed.

The tuple can include:

- Windows edition/version/build family;
- x64 architecture and package RID;
- runtime requirements;
- GPU class and tested driver range;
- Media I/O provider/device/driver/firmware;
- optional network/output providers;
- exact qualification evidence references;
- known limitations.

Material GPU driver, Media-I/O driver, provider SDK or firmware changes invalidate compatibility evidence where the change can affect the qualified behavior. The affected tuple returns to UNVERIFIED until requalification.

Pinned provider SDKs remain governed by their existing source-controlled pin where applicable.

## Release notes and known issues

From RELEASE_CANDIDATE stage onward, the support policy requires an explicit known-issues assessment and release-note disclosure.

A missing assessment remains UNVERIFIED; an empty issue list does not become PASS merely because no issue was entered. Stable readiness consumes the KNOWN_ISSUES release-evidence domain and cannot reach PASS while that domain is missing, failed or unverified.

Release notes must identify the exact product/release identity they describe and must not convert unresolved hardware, signing, support-policy or conformity gaps into supported claims.

## Known-issues assessment binding

A future release candidate can bind an exact-source known-issues assessment through build/release/Bind-KnownIssuesAssessment.ps1.

The assessment is bound before release attestation and is accepted as PASS only when:

- product version, release stage, source commit, build commit and build id match the release evidence;
- unresolved BLOCKER or CRITICAL issues are absent;
- every unresolved accepted/open issue is explicitly marked for release-note disclosure;
- the assessment hash is bound into release evidence;
- the projected release knownIssues list matches the unresolved assessment issues.

Tampering, source mismatch or missing disclosure fails closed.

## Stable readiness

build/release/Test-StableReadiness.ps1 produces a machine-readable readiness result without changing product version or release stage.

It evaluates:

- exact repository source identity;
- support-period policy;
- supported-version policy;
- platform compatibility policy;
- product-security policy;
- security support commitments;
- upgrade/deprecation policy;
- deployment policy;
- update/rollback policy;
- exact-source packaged coordinated state-upgrade qualification evidence;
- exact release evidence;
- known-issues evidence;
- required hardware evidence;
- production signing trust;
- release-candidate/release-evidence correlation;
- current source release stage.

The release evidence must match the exact repository source commit. When a release candidate is supplied, its product/source/build identity and bound Release Evidence SHA-256 must match the supplied release-evidence bundle; trust from one candidate cannot be combined with hardware/evidence from another bundle.

Allowed states are PASS, FAIL, UNVERIFIED and NOT_APPLICABLE.

Any FAIL makes the aggregate FAIL. A missing required proof remains UNVERIFIED. Aggregate PASS is possible only for a source that already declares STABLE and for which every required readiness domain is satisfied.

The verifier reports the exact source commit and releasePromotionPerformed = false. It is a verifier, not a promotion tool.

## Current source boundary

The current repository identity remains 0.1.0-dev in release stage DEV.

Production support governance does not change that identity.

A future Stable publication still requires the normal release-channel rules, production signing trust, exact release evidence, known-issues evidence, exact-source coordinated state-upgrade qualification evidence, required physical qualification, approved support commitments and deployment readiness. Missing state-upgrade qualification remains `UNVERIFIED`; inconsistent or source-mismatched evidence is `FAIL`.
