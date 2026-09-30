<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Security Policy

This policy applies to **rtaime — Real Time AI Media Engine**, the **Production-grade real-time AI media platform.**

## Reporting a vulnerability

Please report suspected vulnerabilities privately to `david.beusing@gmail.com` with the subject prefix `[rtaime security]`.

Do not open a public GitHub issue for a vulnerability that has not already been disclosed publicly and remediated.

Include, when available:

- affected rtaime version or source commit;
- affected component, host, provider or deployment surface;
- reproduction steps or proof of concept;
- expected and observed security impact;
- whether exploitation is known or suspected in the wild;
- any proposed mitigation or remediation.

Do not include credentials, private keys, production media, personal data or other unrelated sensitive data in a report.

## Coordinated disclosure

rtaime uses coordinated vulnerability disclosure. Reports are triaged privately, linked to affected product/component identities, assigned an impact/severity assessment, and tracked through remediation and disclosure.

Public disclosure should occur only when users have an actionable remediation or mitigation, unless earlier disclosure is required by law or necessary to protect users.

## Security updates

Security fixes must remain traceable to the affected product version/source commit and release evidence. Security updates must use the normal signed release/update path; ad-hoc replacement binaries are not an accepted production remediation mechanism.

Where technically feasible, security remediation should be separable from unrelated functional change.

## Regulatory reporting

Regulatory notification is a separate manufacturer responsibility from public disclosure. A vulnerability record must never mark a regulatory report as completed without external evidence of the actual submission.

The repository policy models the EU Cyber Resilience Act reporting timelines that apply to actively exploited vulnerabilities and severe security incidents, but repository metadata alone is not legal evidence that a notification was submitted.

## Windows service deployment boundary

The supported persistent Windows engine can be registered through the packaged `Invoke-WindowsServiceLifecycle.ps1` administration tool.

The current default service registration uses the Windows `LocalSystem` account so the production engine can access its machine-level state root and qualified device/provider surfaces without an interactive user session. This is a privileged deployment identity, not a claim of least-privilege certification.

Production qualification must therefore verify the service account, filesystem ACLs, provider/device access, event-log visibility and operational access policy for the target machine. A deployment that requires a narrower service identity must validate that identity and its required ACL/device permissions before production use.

Operator remains an independent client process and does not acquire service-account privileges merely by connecting to the engine.


## Secure external control boundary

ControlHost can expose an optional gRPC control listener over TLS. The listener is disabled by default and binds to loopback by default. Enabling a non-loopback listener is an explicit deployment decision and does not expose RuntimeHost or AIHost directly.

The external boundary requires authenticated client identity and server-side authorization. Observer clients are read-only; Operator clients can execute normal production-control operations; administrative capability is granted only where an administrative operation is explicitly implemented. UI visibility is never treated as authorization.

TLS server identity can come from a PFX/PKCS#12 file, a PEM certificate/private-key pair or the Windows certificate store. Optional mTLS binds client certificates to configured identities. Bearer-token identities reference an environment-variable name; the token value itself is not source-controlled configuration.

The following material must never be committed or embedded in release artifacts:

- production private keys;
- client private keys;
- certificate passwords;
- bearer tokens or other external-control credentials.

Repository security gates reject tracked private-key-like files and PEM private-key material. Test certificates are generated at test runtime rather than committed.

The production client trust mode is normal platform validation or explicit server-certificate pinning. The `TestOnlyInsecure` trust mode is for isolated tests only and is not an accepted production deployment configuration.

Request/response sizes, connection count, per-client in-flight operations and request rate are bounded. Authentication failure, authorization denial, incompatible API versions, malformed requests, oversized messages and rate-limit violations fail closed without transferring Production Authority.

Public-Internet exposure, enterprise certificate lifecycle operations, external revocation infrastructure and WAN security/performance qualification remain unverified unless separate deployment evidence is recorded.

See `docs/ExternalControlDeployment.md`.

## Supported versions

The project has not yet established a production support-period declaration. Until such a declaration is approved and release evidence is updated, support-period status remains `UNVERIFIED`.
