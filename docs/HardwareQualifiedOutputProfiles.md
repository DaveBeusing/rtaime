<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Hardware-qualified output profiles

## Operational boundary

Physical SDI, NDI, SRT and device-attached output is a product capability direction, not an implicit entitlement derived from software render tests or a provider descriptor. The existing Windows HD RGBA8 production formats remain the only currently admitted RuntimeHost Program formats. No physical UHD, P010, HDR, non-reference cadence, SDI scan-out or measured color accuracy is declared supported by adding these qualification contracts.

The source-bound evidence system under [Reference Platform Qualification](qualification/ReferencePlatformQualification.md) and [Qualification Evidence Provenance](QualificationEvidenceProvenance.md) remains the canonical source of physical measurement provenance. Runtime output admission adds a separate, fail-closed **signed target-level proof** that must be produced only after the relevant physical binding suites validate the exact device, target, format, native resources and measured long-running behavior.

## Signed target-level binding

The new versioned qualification payload schema is [physical-output-evidence 1.0](../schemas/qualification/v1/physical-output-evidence.schema.json). It is an out-of-band qualification artifact, not a Show, IPC, protobuf, license or release schema migration.

The UTF-8 JSON bytes are signed with ECDSA P-256 / SHA-256 using a DER-encoded signature. `RuntimePhysicalOutputEvidenceVerifier` independently checks the original unmodified byte sequence against a trusted, explicitly enrolled SubjectPublicKeyInfo public key. Do **not** embed qualification signing private keys in the application or repository, accept user-selected arbitrary public keys as production trust, or treat a JSON hash / profile string as an independent signature. The caller is responsible for supplying only the approved public key from the controlled trust policy.

Every signed PASS must bind:

- the exact 40-character source commit and the current running binary identity;
- provider ID, concrete device ID, observed driver, pinned provider SDK, firmware identity and physical output target;
- transport family (SDI, NDI or SRT) and the precise `VideoFormat`: resolution, rational cadence, pixel/scan format, primaries, transfer, matrix, nominal range, bit depth, alpha interpretation and authoritative metadata;
- finite measured managed/GPU/native budgets and maximum visible surfaces;
- UTC qualification and expiration dates, with a bounded validity interval;
- successfully qualified physical scan-out, color accuracy, frame cadence, audio/video synchronization, controlled recovery and long-soak stability.

Malformed/duplicate/missing required fields, blank or oversized device/driver/provider identities, unsigned or altered bytes, unknown contracts, incomplete color metadata, non-PASS evidence, stale dates and changed runtime identities fail closed. Signature validity never substitutes for the complete required evidence schema. Existing schema version 1.0 describes *currently representable* RGBA8 formats only. It does not silently add P010, RGBA16F, HDR metadata signaling, matrix conversion or frame-rate conversion. Those need separate real provider formats, conversion, pipeline qualification and compatibility decisions before they can appear as admitted tuples.

## Admission and authority

`RuntimeResourceCapabilityProfile` retains finite driver/provider budgets and exact per-target support; its historic `HardwareQualified` flag and `QualificationEvidence` string are **not trusted proof**. The active provider/runtime binding must additionally supply the observed `ActiveSourceCommit` and `ActiveDriverIdentity`, rather than importing these values from saved configuration.

`RuntimeResourceCapacityAdmission.Evaluate` retains the existing software-only signature and adds an optional list of `RuntimeVerifiedPhysicalOutputCapability` grants. A physical target is rejected unless its grant was minted by the independent verifier and matches its source, provider, device, driver, target, transport, color/cadence tuple, evidence ID, surface count, current expiry and finite native/GPU/managed resource estimates. An identical self-asserted string or an older grant is insufficient. The result is a **preflight decision**, not evidence that a provider has reserved native sink handles, committed a physical output or actually emitted frames.

For successful activation, a dedicated provider must still reserve every sink under ControlHost-owned authoritative revisions, activate all targets at the existing Runtime Program boundary, preserve the previous committed output if any preparation fails, and report measured per-target frames/audio, queue headroom, device health and recovery. The current `RuntimeSoftwareOutputPublication` is an in-process multi-target software primitive; it does not perform native scan-out.

## Qualification workflow

1. Execute the existing real reference-platform CUDA, professional Media I/O, timing/latency and recovery/soak suites on the designated self-hosted Windows hardware. Verify the resulting immutable bindings against the exact source SHA using the canonical qualification verifier. Treat missing evidence as `UNVERIFIED`.
2. Add a target-specific physical color/cadence/output measurement and prove all six required PASS dimensions at the declared format and driver. Capture finite native, GPU and managed measurements; do not infer memory budgets from device marketing specifications.
3. Construct the versioned output evidence payload from those independently verified measurements. Sign the exact serialized UTF-8 bytes with a controlled qualification private key outside the shipped product. Retain the signature, evidence payload, associated runner/source records and the approved signing public-key identity together.
4. On the actual target host, obtain the running source identity, active device and driver from trusted runtime/provider observations. Supply only the enrolled trust key to the verifier. No evidence means no physical output admission.
5. Match the verified grant against the current provider capability, revalidate frame/native budgets and perform transactional native sink reservation and Program-boundary activation. Requalify after material code, firmware, device, driver, color or transport changes.

Automated `RuntimePhysicalOutputEvidenceTests` use temporary in-memory signatures and synthetic device identities only. They verify the security and admission logic, not physical output or a valid production qualification. GitHub-hosted green tests do not promote any hardware status. Do not make a stable release or physical-device capability claim without an enrolled qualification key and retained exact-target measurements.

## Validation

After the Windows Release build, use:

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~RuntimePhysicalOutputEvidenceTests|FullyQualifiedName~RuntimeResourceCapacityAdmissionTests"
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Required Gates (CI, Quality, Security, Provider Smoke and Packaged E2E) must pass on the exact final source. Physical qualification is a separate self-hosted evidence obligation. A source-only PR cannot be marked hardware-PASS.
