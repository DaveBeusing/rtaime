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
remain owned by the existing `LocalMediaResourceLedger`. When an admitted profile
is used by `LocalMediaDeckRuntimeService`, each **new** single/group reservation
rechecks the Runtime-owned provider's current availability, exact decode/route
formats and reservable units, existing retained owner count, finite managed
allocation budget and observed host GC memory pressure. Unavailable/shrinking
provider inventory or uncertain pressure rejects new requests without revoking
the committed Program or previously retained leases. Default conservative and
opt-in-four profiles stay bounded by their established owner accounting; the
provider descriptor remains a capability upper bound, not proof of a physical
GPU/SSD/native memory reservation. Unknown native/VRAM/storage remains UNKNOWN.

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

The `RuntimeSoftwareOutputPublication` now provides a bounded software
publication owner for the already-mapped target groups. It stages one candidate
without changing visible output, checks the originating RuntimeHost epoch,
authoritative execution revision and monotonic Program frame slot, and swaps all
target frames atomically in one call made by the owning Program boundary.
A single group lease exposes every target from one published sequence, so readers
cannot observe different target generations within the same acquisition.
Retired GPU frame groups remain charged while active readers exist; a finite
old-generation ceiling rejects pressure rather than recycling live surfaces.
The final reader also defers GPU destruction to the management drain, so reader
release on a presentation thread cannot unexpectedly destroy mapped surfaces.
Released generations still consume the finite retirement budget until drained;
shutdown separately reclaims the last outstanding reader-owned frame.
Stale or rejected candidates preserve the last published group; cancellation
releases only staged frames. Closing the owner drains groups without destroying
frames held by outstanding leases. Integration tests cover successful two-target
pixels, revision/epoch rejection, retained lease pressure and shutdown.

This is an **in-process software publication primitive**, not a new Program
scheduler, external output-routing authority or physical scan-out provider.
A caller must still use the existing Program boundary and Control-approved route;
the current legacy Runtime mapping path does not invoke this owner yet.

The existing Runtime Program boundary now also sends the already-authorized
Program/Aux role frames and audio to **multiple distinct SRT/NDI provider
sessions** attached to that role. The startup output graph checks the observed
provider's exact formats and reserved slot count before creating any sink. All
target sessions must construct successfully; a mid-group construction failure
closes prior candidate sessions without changing Program. Frames are retained
independently per target and each session reports actual provider health plus
per-target admitted video and audio sample counters. Provider send failures
remain observational rather than changing authoritative routing.

This is **not** transactional hot-reconfiguration of physical output devices:
the configured network target graph is established at Runtime startup, not
reactivated from a new ControlHost-issued output graph revision. Independent
provider queues can diverge on post-commit transport failure; an already sent
packet cannot be atomically rolled back. The existing
`RuntimeProgramExecutionService.OutputMapping` still commits only one governed
Program canvas binding. The software mapped-frame publication primitive does
not automatically become the native SDI/NDI output plane. Complete per-target
hardware scan-out, per-device timing, measured SSD/native/GPU pressure,
control-authorized hot-swap and rollback of a *new* live output graph remain
required before hardware-qualified multi-output activation can be claimed.

Similarly, the platform's `VideoFormat` only defines currently implemented RGBA8
pixel format and progressive scanning. This profile model distinguishes exact
frame rate/color tuples and can reject unsupported UHD candidates; it is not
a new P010/HDR codec, SDI/NDI transport, color transform or GPU driver backend.
True professional output requires physical driver/SDK/display evidence and
measurements on the identified reference platform, with `HARDWARE UNVERIFIED`
until recorded separately.

## Signed physical target qualification

The exact per-target hardware proof and runtime gate are specified in [Hardware-qualified output profiles](HardwareQualifiedOutputProfiles.md). A self-asserted `hardwareQualified` flag or matching evidence ID is not accepted as physical permission. Every qualified target requires an independently verified ECDSA P-256 signed artifact, current source/device/driver/SDK/firmware identity, exact color/cadence format, finite measured budgets, and passing output scan-out, color, cadence, synchronization, recovery and soak evidence. Absent physical evidence remains `UNVERIFIED`, even when software tests are green.

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

- Extend the current per-reservation live decode/route and host-memory checks
  with measured native-decoder, VRAM, SSD and device/driver pressure telemetry
  before enabling higher physical owner counts; current provider inventory
  still describes an upper bound rather than a measured resource pressure stream.
- Complete ControlHost-revisioned live output graph prepare/commit/rollback
  and an actual SDI/NDI physical sink activation path. Multiple current NDI/SRT
  network sessions now use the existing Program cadence, but the software-frame
  owner and on-disk output canvas still need integrated multi-target authority,
  remote status negotiation and physical output validation.
- Establish a device-, driver-, output-, cadence- and color-specific qualification
  matrix (including UHD, HDR and professional I/O) with source-bound physical evidence.
- Maintain v1 readers and rollback while versioned peers opt in to additional
  capacity. Keep all unknown hardware profiles `UNAVAILABLE` or `UNVERIFIED`.
