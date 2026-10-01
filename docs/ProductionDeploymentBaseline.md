<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Production Deployment Baseline

## Purpose

This checklist is the human-readable projection of the deployment controls in docs/Governance/ProductionDeploymentBaseline.json.

Completing the checklist records deployment evidence. It does not create hardware qualification, production signing trust, certification, legal conformity, or Stable release promotion.

## Required deployment checks

| Check | Required deployment evidence |
| --- | --- |
| Windows platform | Windows edition/version/build, x64 architecture, matching support-matrix configuration id |
| Service account | selected persistent-engine identity and verified access requirements |
| Filesystem ACLs | reviewed ACLs for application, state, logs, certificates and update locations |
| Storage | declared application/state/log/recording locations plus capacity and recovery expectations |
| GPU/driver | GPU identity, driver version and matching qualification/support evidence |
| Media I/O | provider, device, driver, firmware and matching qualification/support evidence |
| Network/firewall | listener inventory and explicit firewall/public-exposure review |
| Certificate/trust | external-control and release/update trust identities without private-key disclosure |
| Diagnostics/event log | structured diagnostics path, support-bundle export and event-log visibility |
| Update source | governed published release source with production-trust enforcement |
| Rollback/state | rollback slot and state-maintenance prerequisites |
| Time/reference | reference/timing configuration and evidence when required, otherwise explicit NOT_APPLICABLE rationale |

## Evidence rules

A deployment checklist item may be complete while the underlying product-support or hardware-qualification status remains UNVERIFIED.

In particular:

- a Windows machine is not SUPPORTED merely because rtaime starts on it;
- a GPU is not SUPPORTED merely because CUDA loads;
- a Media-I/O device is not SUPPORTED merely because the provider enumerates it;
- test certificates or ephemeral release keys cannot satisfy production trust;
- hosted CI cannot substitute for required physical qualification;
- successful deployment does not imply legal/regulatory conformity.

## Service identity and ACL boundary

The packaged Windows lifecycle currently supports the documented service deployment path. The selected service account and all required filesystem/device permissions must be verified on the actual target machine.

A narrower service identity may be used only when its required ACL and device/provider access are explicitly validated.

## Update and rollback

Production updates must continue through the governed published-release discovery and verification flow.

Before replacement:

1. verify exact installed identity;
2. verify candidate production trust and publication readiness;
3. establish required process quiescence;
4. satisfy coordinated state-maintenance prerequisites;
5. confirm rollback/state recovery prerequisites.

A failed or uncertain update remains failed. The deployment process must not silently reinterpret failure as readiness.

## Diagnostics

Before production use verify that:

- AppHost/ControlHost/RuntimeHost/AIHost/Operator diagnostics can be correlated;
- support-bundle export succeeds;
- redacted lifecycle and health data are present;
- the bundle does not contain secrets or media payloads;
- relevant Windows event-log visibility is available for the deployment.

## Final deployment record

A production deployment record should identify the exact product/release candidate, selected support-matrix configuration, evidence references, deployment-check results and known deviations.

Deviations remain explicit. They do not automatically become accepted supported configurations.
