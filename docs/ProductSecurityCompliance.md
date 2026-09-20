<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Product Security & Compliance Evidence Foundation

Status: IMPLEMENTATION FOUNDATION

This document defines the repository-level product-security evidence model for rtaime. It is an engineering control and evidence framework. It does not assert legal conformity, CE readiness, notified-body status, product classification, or market-placement readiness.

## Objectives

The V1 repository must be able to demonstrate, without silently promoting missing evidence to PASS:

- a private coordinated-vulnerability-disclosure path;
- traceability from a vulnerability to affected product versions and source commits;
- explicit exploitation state and remediation state;
- machine-readable vulnerability records;
- software bill of materials generation in the release-evidence path;
- regular security-policy verification in Required Gates;
- signed release/update handling for security fixes;
- explicit CRA reporting assessment with externally verifiable submission evidence;
- release security status that remains UNVERIFIED when required security assessment is missing.

## Regulatory baseline

The repository tracks Regulation (EU) 2024/2847 (Cyber Resilience Act) as a compliance input.

Current regulatory dates encoded by policy:

- Article 14 reporting obligations apply from 2026-09-11.
- Full CRA application begins 2027-12-11.

The engineering mapping includes the following Article 14 timing model:

- actively exploited vulnerability: early warning within 24 hours of awareness;
- actively exploited vulnerability: follow-up notification within 72 hours of awareness;
- actively exploited vulnerability: final report no later than 14 days after corrective or mitigating measures become available;
- severe security incident: early warning within 24 hours of awareness;
- severe security incident: follow-up notification within 72 hours of awareness;
- severe security incident: final report within one month after the follow-up notification.

Repository records may calculate or preserve deadlines, but must not claim that a regulator notification occurred unless external submission evidence is attached or referenced.

## CRA Annex I vulnerability-handling mapping

The foundation maps these engineering controls:

| CRA-oriented requirement | rtaime evidence/control | Current state |
| --- | --- | --- |
| Identify/document components and vulnerabilities | CycloneDX release SBOM + vulnerability record schema | FOUNDATION |
| Address/remediate vulnerabilities without delay | vulnerability lifecycle + signed release/update path | FOUNDATION |
| Regular security testing/review | Required Gates Security job + repository security tests | ACTIVE / EXPANDING |
| Coordinated vulnerability disclosure | `SECURITY.md` | ACTIVE |
| Vulnerability reporting contact | `SECURITY.md` + product security policy | ACTIVE |
| Secure update distribution | signed release/update pipeline | ACTIVE FOUNDATION |
| Support-period vulnerability handling | support-period policy | UNVERIFIED |
| Formal conformity assessment | external legal/conformity evidence | UNVERIFIED |

## Vulnerability record model

Canonical vulnerability records use `schemas/security/v1/vulnerability-record.schema.json`.

Important fail-closed rules:

1. `ACTIVELY_EXPLOITED` is an evidence-bearing state; it must never be inferred from severity alone.
2. CRA reporting assessment and actual submission status are separate fields.
3. `EXTERNALLY_VERIFIED` regulatory submission status requires evidence outside the repository-generated assertion itself.
4. A fix is not considered production-remediated merely because a source commit exists; the fixed version must pass the normal release/signing/update evidence path.
5. Security status does not become PASS merely because no known vulnerabilities are currently recorded.

## Release evidence integration

Product Security & Compliance establishes policy and schema first. The release pipeline must subsequently bind a product-security assessment to the exact source commit and product identity before the `SECURITY` release-evidence domain may move from `UNVERIFIED` to `PASS`.

Until that binding exists, the current release-evidence generator must continue to report product security as `UNVERIFIED`.

## Support period

A production support period has not yet been approved. The repository therefore records support-period status as `UNVERIFIED`.

A future support-period decision must define at least:

- product/version scope;
- start and end conditions;
- security-update availability expectations;
- end-of-support communication;
- relationship to maintained release channels;
- evidence retention requirements.

## Evidence boundaries

The following are explicitly outside the claim made by this foundation:

- legal advice or legal interpretation;
- final CRA product classification;
- completed conformity assessment;
- CE marking authorization;
- proof of ENISA/CSIRT submission;
- proof of production support-period fulfillment;
- proof that every third-party component is vulnerability-free.

Missing evidence remains `UNVERIFIED`.
