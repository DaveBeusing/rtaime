<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Product Scope and Capability Admission

**Status:** ACCEPTED PRODUCT DECISION — 2026-10-20  
**Product:** rtaime — Real Time AI Media Engine  
**Positioning:** Production-grade real-time AI media platform.  
**Scope authority:** This document is the current product-wide scope decision. Actual implementation and support remain evidence-bound.

## Decision: one complete product, no historical feature ceiling

rtaime is developed as a professional, extensible real-time media platform. The former V1 architectural-proof/product-phase definition is **superseded as a feature and product boundary**. It is not a reason to forbid additional sources, tracks, outputs, resolutions, codecs, effects, canvases, devices, workspaces or supported operating modes. Work may be delivered incrementally, but no implementation or roadmap item may treat a historical demonstration configuration as the maximum product.

The accepted 2026-10-17 [Operator scope decision](OperatorExperienceScope.md) remains applicable to its experience design and existing delivery ownership; this 2026-10-20 decision extends the removal of phase-based feature deferrals to the **entire product**. Historical technical design documents and accepted architectural decisions remain provenance, but any product-stage-only prohibition in them is superseded. This change does **not** implicitly rewrite published protocol formats, migration records, security policy, or resource safety invariants.

**Approved product scope is not evidence of an implemented capability.** Existing code, truthful provider descriptors, software qualification, physical validation and release approval remain separate. In particular, a native sequence reporting `PREPARED` or `PLAYING` is not proof of delivered Preview/Program pixels, committed Take or real sink audio. See [joint Operator integration evidence](OperatorExperienceIntegrationQualification.md).

## Complete media-server capability space

These are product areas, not an assertion that all combinations work today. Each feature becomes available only when its real owner, provider, admitted resources, tests and operator evidence support it.

| Domain | Product capability direction | Existing foundation / evidence owner |
| --- | --- | --- |
| Media inputs and asset library | Dynamic file and live-source acquisition; multiple concurrent sources; heterogeneous native codec/container support, metadata, analysis and independent scrubbing | [Local Media](LocalMediaFileSource.md), [Media Library](MediaLibraryAssetBrowser.md), [Independent Analysis](IndependentMediaAnalysis.md), [Media I/O](MediaIoDecisionAndFoundation.md) |
| Playback and sequencing | Multi-deck playback, native multi-source and multitrack timeline, cues, synchronized audio/video, replay and live transitions | [Deck Admission](FileDeckCapacityAdmission.md), [Sequence Playback](SequencePlayback.md), [Sequence Authoring](SequenceAuthoring.md) |
| Live production | Governed Preview/Program, scenes, CUT/DISSOLVE, prepared commits, cues, rundown and macros, compositing and CG | [Execution](TransactionalRuntimeCommit.md), [Live](LiveMultiviewAndSceneControl.md), [CG](ProductionCgTextRendering.md) |
| Outputs and mapping | Dynamic Program/Aux routing, independent provider targets, configurable canvases, surfaces, warp/transform/blend, monitoring and physical scan-out | [Canvas Mapping](OutputCanvasMapping.md), [Output Health](OutputRoutingHealth.md), [GPU Processing](GpuProcessingFoundation.md) |
| Video quality | Provider-admitted SD/HD/UHD and higher resolutions, exact cadence, pixel format, SDR/HDR/color pipeline and GPU-resident processing when demonstrated | [High-resolution Compatibility](HighResolutionMediaCompatibility.md), [Color Monitoring](ColorAccurateMediaMonitoring.md), [GPU Qualification](CudaReferenceHardwareQualification.md) |
| Audio | Multichannel/multibus composition, routing, mix, monitoring, Follow Video, signal generation and professional synchronization | [Audio Production](AdvancedAudioProduction.md), [Mixer](AudioMixerWorkspace.md), [A/V Sync](AvSyncDiagnostics.md) |
| Recording and replay | Program and selected streams, provider-specific recording formats, profiles, replay clips and failure-isolated storage | [Recording](RecordingFoundation.md), [Profiles](RecordingProfileCatalog.md), [Replay](ReplayClipProduction.md) |
| Transport and professional I/O | Local devices, networked input/output and interoperable NDI/SRT/SDI paths as each SDK, hardware and transport adapter is genuinely qualified | [NDI](NdiDiscoveryAndInput.md), [Network Output](NetworkOutputStreaming.md), [Media I/O](MediaIoVerticalSlice.md) |
| Processing and inference | Governed effects and inference, measurable latency/fallback, optional providers without giving inference Program authority | [AI](GovernedAIInference.md), [Operator Processing](AIProcessingWorkspace.md) |
| Operations and control | Remote/headless operation, authenticated APIs, external/automation integrations, diagnostics, supervision, recovery and performance qualification | [Remote APIs](ProductionIpcRemoteApi.md), [Health](HealthCenter.md), [Deployment](ProductionDeploymentBaseline.md) |
| Operator experience | Responsive workspaces, high-fidelity monitoring within actual viewport bounds, accessible editing, live-safe production UX and honest state | [Experience Design](OperatorExperienceDesign.md), [Qualification](OperatorExperienceIntegrationQualification.md) |

A product area absent from today's provider matrix is **planned/unsupported**, never permanently "out of scope because V1". No table entry grants support for a specific GPU, codec, driver, display, SDK or deployment environment.

## Capability contract, finite resources and admission

1. **Advertise real capabilities.** Resolve provider/device identity, availability, supported format/color/cadence/route combinations, resource profile and qualification evidence. Do not infer support from a UI dropdown or a model's intended feature set.
2. **Plan and validate.** ControlHost owns authoritative production state and transaction authorization. Build candidate execution requirements and an explicit plan from provider snapshots and request parameters. User selection, saved configuration and `PREPARED` are not committed output.
3. **Reserve before execution.** Admit the complete requested graph against finite budgets: decoder/native owner sessions, CPU/RAM and VRAM, frame/texture size, GPU/CPU execution time, handles, queues, recording/transport throughput, device availability and relevant source identities. If a value is unknown, treat it as unverified rather than an unlimited budget.
4. **Commit atomically at the correct owner.** RuntimeHost retains the single committed Program cadence and resource ownership. Prepare/commit must not produce partial multi-output or partial multi-source changes; failed candidates release provisional reservations without mutating committed Program or valid active outputs.
5. **Fail clearly.** Unsupported profile, incompatible provider, missing qualification, exhausted budget, stale authority and unsafe rollback receive distinct actionable rejections and preserved prior state. Optional work yields before live Program.
6. **Qualify scale per profile.** More sources, tracks, outputs, surfaces and larger frames require targeted boundary/concurrency/recovery tests, measurement on the selected provider and physical evidence where applicable. Broad global max-number increases are not admission.

Current 1/2/4 decoder admission, bounded source registry, sequence owner cap, one-target output mapping, eight mapped surfaces, 16-MiB buffer threshold and 1080p reference format set are **current implementation constraints** under [Legacy and Capability Migration](LegacyCompatibilityAndCapabilityMigration.md); they are not global product strategy. Genuine allocation, protocol, security or real-time bounds remain finite until a safe implementation is demonstrated.

## Status language and evidence

| Label | Required meaning |
| --- | --- |
| SCOPE APPROVED | Feature belongs to the product strategy; does not mean it is implemented |
| IMPLEMENTED | Actual owners, runtime paths, contracts and controlled failure/recovery exist |
| SOFTWARE QUALIFIED | Named software tests passed at a recorded exact SHA and runtime configuration |
| HARDWARE UNVERIFIED | Physical driver/device/performance/latency/color/scan-out evidence not established |
| UNAVAILABLE / UNSUPPORTED | Real provider, format, policy or resource admission cannot execute the request |
| RELEASE READY | All required code, CI, security, compatibility, packaged and release evidence passed for the exact source and approved artifact |

The absence of proof is not `READY` or `PASS`. The [five Required Gates](Governance/RepositoryGovernance.md) remain CI, Quality, Security, Provider Smoke and Packaged E2E. Additional physical, legal, codec, performance and release qualification is independent of green managed tests.

## Versioned compatibility is not product branding

Historical `v1` protocol namespaces, ABI labels, endpoint identities, JSON/Proto documents, durable Show formats, revisions and event histories remain readable under an explicit compatibility contract. Keep Source IDs, command identity, revision/generation fences, old layout/checkpoints and migrations stable. Versioned adapters and round-trip fixtures protect them. A historical wire format may be called `v1` **inside its compatibility scope**, but user-facing product scope and new unversioned services do not inherit that name.

Any intentionally breaking wire/persistence/ABI change requires a separate decision, version negotiation or migration procedure, compatibility proof and rollback path. See [migration inventory](LegacyCompatibilityAndCapabilityMigration.md) and [Versioning](Versioning.md).

## Decisions still requiring dedicated approval

This product decision authorizes scope, **not** unbounded allocation; unsupported CUDA/SDI/HDR/NDI claims; purchase of commercial SDKs or hardware; external data transmission; changed RBAC, security, compliance or licensing; or irreversible wire/persistence changes. Those require their own approved change and evidence. Prior per-profile high-resolution approval requirements must be interpreted as implementation/qualification reviews, **not** a historical V1 prohibition: the [high-resolution decision](HighResolutionMediaDecision.md) is to be reconciled through its own owner before enabling a new format.

## Delivery discipline

Work from current `master`, inspect pending and historical PRs, preserve authority and existing real resource owners, and migrate legacy semantics deliberately. Maintain truthful README/Operator/help/diagnostics and measurable acceptance in the owning subsystem. The canonical implementation and compatibility ledger is [Legacy Compatibility and Capability Migration](LegacyCompatibilityAndCapabilityMigration.md); product-stage language in older historical or archived evidence is not an operative restriction.
