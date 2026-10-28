<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# UHD SDR pipeline capacity admission

**Status:** exact-format candidates and software-only capacity preflight IMPLEMENTED; production UHD activation **UNSUPPORTED**; physical decoder, transfer, recording, monitor and output qualification **UNVERIFIED**. No existing HD release behavior changes.

## Exact candidate identities

`UhdSdrVideoProfiles` in `rtaime.Media.Contracts` defines exactly three **candidate**, not supported, Program formats:

| Candidate | Rate | Color and layout | RGBA frame bytes | One directional transfer, ceiling B/s | 48-kHz audio |
| --- | --- | --- | ---: | ---: | --- |
| 2160p25 | 25/1 | 3840x2160, progressive, BT.709 primaries + transfer, identity RGB matrix, full range, RGBA8 straight alpha, authoritative | 33,177,600 | 829,440,000 | 1,920 samples/frame |
| 2160p50 | 50/1 | same exact tuple | 33,177,600 | 1,658,880,000 | 960 samples/frame |
| 2160p59.94 | 60000/1001 | same exact tuple | 33,177,600 | 1,988,667,333 | 800/801 samples; exactly 4,004 samples/5 frames |

The tuples do **not** reinterpret legacy `UnknownRgba8`. Source-declared BT.709 YCbCr limited-range and 8-bit chroma subsampling require a **separate** converter, plane layout, range/chroma handling and color validation before they can feed these RGB candidates; no implicit upqualification is allowed. Other rates, HDR, 10-bit, BT.2020 and precision/alpha changes fail closed. One-directional throughput figures are payload arithmetic only, not measured transfer bandwidth or PCIe/DRAM utilization.

`UhdSdrVideoProfiles.AudioSamplesBeforeFrame` derives absolute 48-kHz sample boundaries from the exact frame index. `FrameDeadlineTicks` supplies rational single-slot deadline budgets: 400,000, 200,000 and 166,834 TimeSpan ticks respectively. These are mathematical budgets, not observed render latency.

## Fail-closed non-authoritative preflight

`UhdSdrPipelineCapacityAdmission.Evaluate` in RuntimeHost accepts a **provider-supplied** read-only candidate description and deliberately does not mutate Program or allocate memory. Each active consumer must have its own unique provider identity, exact `VideoFormat` (including color), positive per-frame payload and directional throughput allowance, and availability evidence.

Required roles are **Decoder, Gpu, Preview, Program, Monitoring**. Active optional **Recording, Replay, Ndi, Srt, Mxo** roles are never inferred from Program success. The active list must match the advertised capability list exactly. Unsupported/missing/duplicate consumer, color mismatch, unavailable provider, incomplete per-consumer rate, overflow, and insufficient managed/GPU/native reservations produce distinct deterministic rejection codes.

The capacity envelope uses conservative **unique allocation upper bounds**, not lease/reference counts:

- Managed: `(decoder frames + live frames + cached frames + readback frames + sink frames) * 33,177,600` bytes.
- GPU: `GPU frame/surface slots * 33,177,600` bytes.
- Native decoder: `decoder frame slots * separately declared native bytes per frame`.
- Decoder frame slots must include the existing ten frame-pool slots plus one scratch slot **per admitted owner**.
- Readback slots must cover the actual Runtime composition minimum of `RuntimeProgramExecutionService.ProgramReadbackBufferCapacity` (currently 67), in addition to suitably accounted active network/replay/recording ownership. If optional queues retain independent allocations, include them in sink slots; if leases share a pool, count the unique allocation only once. The declaring owner must demonstrate this distinction and account for actual concurrent owner counts.

A synthetic 11-decode / 2-live / 8-cache / 67-readback / 12-sink = 100-frame managed envelope needs 3,317,760,000 managed payload bytes, plus 6 GPU frames = 199,065,600 bytes and a separately bounded native decoder pool. This fixture exists **only** in software tests; it is not a recommended or approved device budget.

`CapacityFits=true` establishes **planning arithmetic and exact candidate capability matching only**. It is not an activation credential, output authorization, measured real-time deadline, hardware acceptance, or released provider claim. Physical output continues to require an independently verifier-issued exact-target grant through the existing `RuntimeResourceCapacityAdmission` boundary. The current `RuntimeHostProcessOptions.Validate`, `VirtualMediaReferenceProvider`, GPU providers, recording profiles, network providers and native I/O continue to reject UHD Program. No CLI alias or feature toggle is added.

## Required qualification before any enablement

1. Obtain actual provider/device/driver/SDK identities, decoder outputs and color/range conversion proof. Bind sources to an exact binary SHA, codec profile and test-media hashes.
2. Trace and budget peak **native decode + all live workers/cache + GPU allocations + shared readback + monitoring + recording/replay + each active network/physical sink** with high-water observations, no unmeasured or unbounded budget.
3. Validate complete Preview/Program frames and measured GPU-frame deadline distribution for each exact profile; preserve single authoritative Runtime cadence, prepared execution, revision/generation fencing and unchanged committed HD when any consumer fails.
4. Run **30–120 minute** native-resolution soak per profile on a named hardware/driver configuration with concurrent A/B decode, GPU, recording/reopen, monitoring, output receivers, dropout/recovery/overflow and 48-kHz continuity. Preserve P50/P95/P99/max stage times, missed slots, peak RAM/VRAM/native bytes, sustained throughput, resource plateau, codec and content hashes. Tests must assert no Program callback blocking I/O or unbounded allocations.
5. Treat NDI, SRT, MXO, recording and replay as **independent exact-format sink qualifications**. A missing one is excluded/rejected explicitly, never silently downgraded. Any HD fallback is a new, visible Control-authorized choice, not an automatic renderer reinterpretation.
6. Record all source-bound results and their provenance with separate `IMPLEMENTED`, `SOFTWARE QUALIFIED`, `HARDWARE UNVERIFIED`, `UNSUPPORTED` and `RELEASE READY` statuses. Without measured evidence, retain **NO-GO / UNVERIFIED**.

No new codec SDK, HDR/color pipeline, persistence migration, public IPC, schema, hardware driver or version bump is part of this change.

## Targeted regressions and execution

```powershell
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release --no-restore --filter "FullyQualifiedName~UhdSdrVideoProfilesTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-restore --filter "FullyQualifiedName~UhdSdrPipelineCapacityAdmissionTests|FullyQualifiedName~HighResolutionOutputAdmissionTests|FullyQualifiedName~RuntimeResourceCapacityAdmissionTests"
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Windows x64 and the repository-pinned .NET SDK 10.0.401 apply. The Required Gates additionally require CI, Quality, Security, Provider Smoke and Packaged E2E on the exact final SHA. Hosted software gates cannot substitute for physical qualification; historical, incomplete or unrelated failure evidence must not be presented as a new UHD PASS.

Related: [high-resolution decision](HighResolutionMediaDecision.md), [cross-layer gaps](HighResolutionMediaCompatibility.md), [qualification procedure](HighResolutionMediaQualification.md), [GPU residency audit](GpuResidentMediaEvaluation.md), [provider capacity](ProviderQualifiedCapabilityAdmission.md), and [source-bound evidence](QualificationEvidenceProvenance.md).
