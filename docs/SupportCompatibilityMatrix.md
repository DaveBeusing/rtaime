<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Support and Compatibility Matrix

> Generated from `docs/Governance/ProductSupportPolicy.json` and `docs/Governance/PlatformSupportMatrix.json`. Do not edit support states directly in this document.

## Version support

| Channel class | Support status | Support mode |
| --- | --- | --- |
| QUALIFICATION / DEV | NOT_APPLICABLE | Internal qualification; not a customer support commitment |
| PREVIEW | UNVERIFIED | UNVERIFIED |
| STABLE | UNVERIFIED | Approved duration required before support can become PASS |

No Stable version line is currently declared supported.

## Platform configurations

| Configuration | OS | Architecture | Runtime | GPU | Media I/O | Support status | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| windows-x64-reference-family | Windows — — | x64 | Microsoft.NETCore.App >= 10.0.0; Microsoft.WindowsDesktop.App >= 10.0.0 | NVIDIA professional GPU / driver — | AJA NTV2 / device — / driver — | UNVERIFIED | docs/qualification/ReferencePlatformQualification.md; docs/CudaReferenceHardwareQualification.md; docs/QualificationEvidenceProvenance.md |

## Compatibility rules

- Driver support policy: **EVIDENCE_BOUND**.
- Provider SDK policy: **PINNED_WHERE_REQUIRED**.
- Firmware policy: **EVIDENCE_BOUND**.
- A material driver/provider/firmware change requires **REQUALIFY**.
- A configuration without required qualification evidence must remain **UNVERIFIED** and cannot be projected as **SUPPORTED**.

## Support commitment boundary

Stable maintenance duration, security-fix duration, EOL notification lead time, security remediation targets and contract deprecation notice remain `UNVERIFIED` until explicitly approved in the machine-readable support policy.
