<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Broadcast-facing security assessment — EBU R 143 v3.0

**Baseline:** EBU Recommendation R 143, version 3.0, March 2025 ([official publication](https://tech.ebu.ch/publications/r143)). This is a scoped engineering assessment of selected applicable **Annex B** P1/P2 controls, not a declaration that rtaime complies with all EBU R 143 requirements. The separately published EBU control-assertion workbook is a procurement aid, not a signed software qualification.

**Assessed software interfaces:** optional ControlHost gRPC/HTTPS external control, IntegrationHost upstream gRPC client, OSC UDP and Companion HTTP/WebSocket adapters, configuration/identities, repository security policy and release evidence. MIDI/virtual GPIO are local provider inputs; physical devices and their upstream network access require individual assessment. Annex A vendor-wide ISMS, organizational training and incident response require independent organizational evidence. Annex C SaaS is not applicable to the currently implemented standalone product, and would require a separate assessment if SaaS were introduced.

**Source of truth:** [machine-readable control matrix](../qualification/security/r143-v3-control-matrix.json). Each control includes EBU identifier and exact P1/P2 priority, VERIFIED/PARTIAL/MISSING/NOT_APPLICABLE, severity, observed source/test paths, a finding and required remediation. The Security Required Gate runs `build/security/Test-BroadcastSecurityEvidence.ps1` against the exact checked-out commit and retains `artifacts/qualification/broadcast-security/r143-v3-evidence.json`. The generated artifact reports the source SHA and listed test names, **not** an unmeasured penetration-test or certification PASS. CI separately runs the named negative regressions on that same source.

## Security invariants and implemented mitigations

1. **No new Production Authority:** all external commands still reach ControlHost's existing authenticated/authorized command dispatch. IntegrationHost cannot directly command RuntimeHost, AIHost or media providers. Preview/Program changes retain stable identities, optimistic revisions and prepare/commit sequencing.
2. **Unencrypted adapters do not listen on a remote interface:** OSC UDP and Companion HTTP/WebSocket now reject all non-IPv4-loopback listener addresses, including wildcard binding. OSC without an explicit source allowlist accepts only loopback sender addresses. Constructors validate options even when called outside the normal configuration loader. External systems must use a separately secured, monitored local bridge/tunnel; encryption, authentication, exposure and routing through that bridge are **UNVERIFIED** until tested.
3. **Only the secure ControlHost gateway may serve remote production control:** external gRPC remains disabled by default, bound to loopback by default and requires TLS 1.2/1.3 when enabled. Remote binding is an explicit operator action, not evidence of an approved WAN or internet-facing deployment.
4. **Identity cannot be relabeled through credential reuse:** certificate thumbprints and bearer-secret environment references cannot be shared across different roles. mTLS identities cannot fall back to another client's bearer identity when their presented certificate disagrees with the declared client ID. Client IDs, secret environment reference names and certificate thumbprints have bounded formats; operational security records strip control characters and enforce length limits.
5. **Untrusted payload admission is bounded:** after client authentication, the request quota is consumed before control payload JSON parsing; in-flight operations remain bounded, and oversized requests continue to fail at gRPC message limits. A rejected operation does not bypass authoritative ControlHost revision or idempotency rules.
6. **Test TLS bypass is not a deployment configuration:** the client transport rejects insecure certificate validation at non-loopback endpoints. IntegrationHost additionally requires `testMode=true`, `dryRun=true` and a loopback HTTPS origin. Both upstream and client control endpoints reject embedded URL credentials, arbitrary paths, queries and fragments.
7. **Diagnostic boundaries do not become secret disclosure paths:** startup health snapshots give generic failures instead of propagating raw certificate-path and configuration exception text. Bounded audit records do not contain bearer-token values, certificate passwords or private keys. Deployment personnel retain responsibility for protected host/OS diagnostics.

## Selected Annex B control coverage and unresolved findings

The authoritative machine-readable matrix contains **15 individually assessed controls**, including:

| Controls | EBU priority | Evidence-bound assessment |
| --- | --- | --- |
| M-AA-02 / M-AA-03 / M-AA-04 | P2 | PARTIAL — request deadlines and ControlHost RBAC exist; session/individual-user accountability remains incomplete for adapters |
| M-AA-06 | P1 | NOT_APPLICABLE only to the current **non-internet-facing** deployment profile; do not enable public-facing services without qualified MFA |
| M-AA-07 | P1 | PARTIAL — bounded in-memory security events exist; durable AAA audit with correlation and retention remains open |
| M-EN-03 | P1 | PARTIAL — gRPC is encrypted; insecure adapters are local-only, while remote secure bridge operation is not qualified |
| M-EN-04 | P2 | PARTIAL — expiry and pin checks exist; certificate renewal and revocation evidence is missing |
| M-EN-05 | P1 | PARTIAL — secret references and certificates exist; operational key lifecycle/rotation evidence is absent |
| M-BA-01 | P1 | PARTIAL — logs are bounded/redacted; reliable encrypted acknowledged forwarding and tamper-resistant retention remain missing |
| M-BA-03 / M-BA-05 | P1 / P2 | PARTIAL — unnecessary external listeners default off, health projection exists; host OS hardening and security telemetry remain unqualified |
| M-BA-06 | P1 | PARTIAL — existing backup/restore workflows exist, but physical deployment qualification is not implied |
| M-NE-01 | P1 | MISSING — deployment-level management/media network isolation and VLAN/router segmentation proof |
| M-NE-02 | P2 | PARTIAL — offline operation is possible; customer egress/jump-host rules are unverified |
| M-AP-01 | P1 | PARTIAL — policy and negative regressions exist, not an independent secure-development audit |

These are requirements from the normative EBU document. P1 refers to the EBU recommended priority, not a software defect severity or product certification. High-severity unresolved findings are retained even when they cannot be remediated without customer-network, PKI, SIEM or organizational evidence.

## Threat model and deployment constraints

| Trust boundary | Attacker opportunity | Current mitigation / remaining risk |
| --- | --- | --- |
| ControlHost remote TLS listener | forged identity, certificate expiry, cross-role replay, malformed/rate-burst requests | TLS, mapped identity, server-side RBAC, per-client quotas, process-local request cache; revocation, WAN DoS and retained AAA logs remain unverified |
| Local OSC UDP ingress | unauthenticated commands and replay/spoofed messages from local host | loopback-only bind and sender check; local processes with access can still inject events and must be isolated via OS permissions and mapping admission |
| Companion HTTP/WebSocket | bearer interception, replay, excessive remote feedback subscriptions | loopback-only HTTP, bearer auth, limited Kestrel connections/upgrades and bounded gateway admission; local trust and request lifecycle limitations remain |
| Gateway upstream client | hostile URLs, false TLS identity, accidental disablement of TLS verification | HTTPS-origin validation, system/pinned trust by default, dry-run-only local bypass; renewal/revocation is an operator responsibility |
| Host diagnostics and release evidence | credentials in logs, unsupported compliance promotion | output redaction, security path verification, source-bound controls; persistent centralized logs and independent certification not established |

### Operator requirements before an external deployment

- Document the exact interfaces and firewall rules, certificate issuers, trust chains, identities, credential rotation periods, token withdrawal procedures, service account ACLs and allowed management network.
- Use platform-validated or explicitly pinned TLS trust for gateway clients. Do not rely on test-only bypass, shared bearer credentials across roles, plaintext remote OSC or plaintext remote Companion HTTP.
- Use an authenticated encrypted bridge for OSC/Companion external equipment **only after** independent testing. Do not assume a reverse proxy alone proves source authentication, transport encryption or replay prevention. Prefer controlling remote production directly through the existing gRPC/TLS boundary.
- Before marking M-BA-01 or M-AA-07 VERIFIED, implement and demonstrate reliable encrypted centralized log forwarding, acknowledgement, retention, time correlation, access controls and failure response. The in-memory ring is not a forensic evidence archive.
- Before marking M-NE-01 VERIFIED, test real routed/VLAN separation for management and media, denial of unauthorized flows and recovery after topology changes on identified target hardware.
- Any internet-facing service requires a new security review and qualified MFA (M-AA-06). Current implementation has no approved public-internet exposure profile.
- An optional NMOS IS-04 Query observer and **loopback-only virtual IS-05/registration test profile** are implemented, with bounded HTTP, pinned API versions and no media/Production mutation. Public NMOS management, ST 2110/RTP activation, NMOS Security BCP authorization, PKI lifecycle, DNS-SD/mDNS and web-graphics adapters remain **UNVERIFIED/UNAVAILABLE**. Future real NMOS endpoints must default disabled and require independent interface admission, AuthN/Z, operational network-policy validation and qualified physical tests. See [NmosDiscoveryAndConnectionManagement.md](NmosDiscoveryAndConnectionManagement.md). Their standards conformance must never be inferred from this assessment.

## CI, product security and release evidence

The normal Required Gates continue to own **CI**, **Quality**, **Security**, **Provider Smoke** and **Packaged E2E**. Broadcast negative tests run in CI for `BroadcastGatewaySecurityTests` and `BroadcastControlSecurityTests`; the Security job verifies matrix provenance and creates an artifact bound to the exact checkout SHA. The evidence artifact explicitly reports `testExecutionStatus=UNVERIFIED`, `operationalAndHardwareQualification=UNVERIFIED` and `securityCertification=UNVERIFIED` because its source-path verifier does not itself execute or authorize these proofs. The authoritative release pipeline's existing product-security assessment and signed evidence binding remain unchanged. **A green Security job is not an EBU R 143 or CRA compliance certificate.**

Only a separately reviewed, exact-source product-security assessment can elevate the existing release security domain; control-matrix completeness cannot do so. No software, compliance, hardware or support maturity claim is made for unexecuted qualification.

## Compatibility and migration

The integration-gateway schema remains version `1.0`; no new persisted contracts or DB migrations are introduced. Existing **non-loopback** OSC/Companion listener configurations now fail closed rather than exposing unencrypted ingress. Existing `testMode=true` insecure upstream trust also fails until it is restricted to `dryRun=true` with a loopback HTTPS endpoint. Deployment owners must reconfigure to loopback and use a separately qualified secure connection before restoring remote traffic.

## Next actions (unresolved evidence)

1. **P1 / HIGH:** Build controlled encrypted, acknowledged centralized AAA/log export with retention, access control and fail-safe outage handling (M-AA-07 and M-BA-01).
2. **P1 / HIGH:** Establish deployment network-isolation evidence and management/media segmentation profiles (M-NE-01).
3. **P1 / HIGH:** Qualify authenticated encrypted remote transport/bridges and key lifecycle/rotation, including mTLS rejection and disallowed internet exposure (M-EN-03/M-EN-05).
4. **P2:** Add certificate revocation/rotation and individual operator-account audit semantics, plus relevant performance/security operational measurements.

The unresolved items stay **PARTIAL**, **MISSING** or **UNVERIFIED** until evidenced, even if all repository CI gates pass.
