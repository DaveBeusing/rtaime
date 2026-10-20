<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Provider-qualified resource admission

## Scope and implemented behavior

The existing Runtime resource ledger now supports a finite provider/device-specific
capacity envelope in addition to the historical conservative and four-owner constructors.
The immutable `RuntimeResourceCapabilityProfile` and
`RuntimeResourceCapacityAdmission` perform side-effect-free, all-or-none planning across
decoder owners, sequence owners, exact `VideoFormat` identities and multiple named output
targets. A plan is not a reservation, prepared execution, confirmed Program sink or
physical output certification.

Profiles explicitly identify the provider and device, immutable admitted format/target
sets, finite per-domain concurrency, mapped surfaces per output and managed/GPU/native
budgets. Planning includes the existing 11 RGBA-frame-equivalent decoder payloads and
bounded audio buffers, plus output-frame and GPU staging estimates. The runtime ledger
refuses an owner count or managed-memory requirement outside the admitted envelope.
Actual owner tickets, retained pool leases, all-or-none group rollback and quarantine
remain owned by the existing `LocalMediaResourceLedger`. It does not turn unknown
native/VRAM/storage allocations into zero evidence.

The default RuntimeHost startup continues to use its current conservative and explicit
four-owner profiles. No new production host CLI setting promotes unmeasured native
concurrency. Provider inventory, native workers, actual SSD throughput, scheduling and
resource qualification must be implemented before a larger profile can be enabled in a
production host. The synthetic six-owner tests establish only ledger correctness under
a declared software profile.

## Versioned native sequence admission

The `rtaime.sequence-preparation.v1` contract and its four-owner limit remain
supported unchanged. An explicitly requested
`rtaime.sequence-preparation.v2` selects a separate bounded protocol envelope
(up to 64 owners as a defensive protocol ceiling, **not** hardware capacity).
ControlHost compiles against the selected version and still requires real advertised
`media.route` resources for **every** native owner; RuntimeHost additionally
checks the actual Runtime ledger capacity before opening any decoder.
A successful preparation preserves the exact negotiated version through
get/cancel, the Runtime lifetime fence, authoring revision and plan generation.
A `PREPARED` status still gives no Preview/Program picture or Take authority.
Older v1 requests, stored Shows and clients retain their old behavior. No
older endpoint or protobuf tag was changed.

## Multi-output and video format admission

Multiple software output candidates can be planned together with distinct target IDs,
per-target visible surface budgets and exact admitted formats. Repeated targets,
unknown target IDs, unsupported colors/cadence/resolutions, oversized surface graphs
and exceeding managed or GPU budgets fail before allocation. Physical targets fail
closed unless source-bound hardware evidence and finite native/GPU budgets are available.

The `RuntimeMappedOutputComposer` can now produce **multiple independent RGBA8
software GPU mapped frames** from the same retained Program frame and declared
target list. Every target geometry and capacity is validated before mapping. Each
successful mapped target is retained in one disposable group; a later failed GPU
target releases earlier new frames while leaving the source Program frame unchanged.
Regression cases verify different target pixels, deterministic lease disposal,
unknown targets, malformed quads and mid-group failure rollback.

This **does not** yet activate multiple governed output sinks. The current
`V1RuntimeHostService.OutputMapping` still commits only the single authorised
Program mapping and the existing Aux/NDI/SRT routes keep their current contracts.
The software frame group is a reusable mapping execution primitive, not a
transactionally activated multi-output provider or physical scan-out claim.
Binding it to working outputs requires the owning output/provider path to reserve
real sink resources, commit all targets on the Program boundary, report per-target
evidence, keep the old Program unchanged on failure, and qualify pixels/audio and
frame cadence.

Similarly, the platform's `VideoFormat` only defines currently implemented RGBA8
pixel format and progressive scanning. This profile model distinguishes exact
frame rate/color tuples and can reject unsupported UHD candidates; it is not
a new P010/HDR codec, SDI/NDI transport, color transform or GPU driver backend.
True professional output requires physical driver/SDK/display evidence and
measurements on the identified reference platform, with `HARDWARE UNVERIFIED`
until recorded separately.

## Validation and evidence

Run the focused contract and integration suites after a Release build:

```powershell
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj -c Release --no-build --filter "FullyQualifiedName~SequencePreparationContractTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj -c Release --no-build --filter "FullyQualifiedName~RuntimeResourceCapacityAdmissionTests|FullyQualifiedName~RuntimeMappedOutputComposerTests|FullyQualifiedName~LocalMediaResourceLedgerTests|FullyQualifiedName~Real_native_sequence_prepare_get_cancel"
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

The five Required Gates must pass on the exact final PR head. Tests on synthetic
resource descriptors do not establish actual GPU/native memory consumption,
hardware-qualified color fidelity, long-soak latency, multiple physical scan-out
or release readiness.

## Follow-on integration

- Bind real provider capability/resource descriptors and measured/native pressure
  into the RuntimeHost capacity profile, without trusting arbitrary CLI claims.
- Add an authoritative multi-output preparation transaction, per-target GPU/sink
  leases, format conversion and strict boundary commit/rollback.
- Establish a device-, driver-, output-, cadence- and color-specific qualification
  matrix (including UHD, HDR and professional I/O) with source-bound physical evidence.
- Maintain v1 readers and rollback while versioned peers opt in to additional
  capacity. Keep all unknown hardware profiles `UNAVAILABLE` or `UNVERIFIED`.
