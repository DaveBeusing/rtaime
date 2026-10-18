# High-resolution cross-layer compatibility

Source inventory: `e86c7f46dfc61bb40871af393e8f19105290ef1e`.
All future columns describe proposals, not available features.
The [decision and approval gate](HighResolutionMediaDecision.md) applies.

## Format and consumer matrix

| Layer / source | Current HD behavior | 2160p25 / 50 / 59.94 SDR gap | Later 10-bit/HDR/alpha gap |
| --- | --- | --- | --- |
| `Media.Contracts/Contracts.cs`, `ColorContracts.cs` | Generic VideoFormat; only RGBA8/progressive pixels; named HD50/59.94 defaults carry UnknownRgba8 | Generic dimensions are representable but confer no output approval; define exact SDR color tuple and migration/identity policy | No P010/RGB10 plane contract, BT.2020/PQ/HLG or HDR metadata; numeric bit-depth alone is insufficient |
| `Provider.Contracts/Contracts.cs` | Exact VideoFormat equality, including color, in capability matching | All required consumers must negotiate exact resolution/rate/color; resource unit counts do not prove byte capacity | Negotiate layout, semantics, conversions and metadata support, not only codec name |
| `RuntimeHostProcess.cs` | CLI/env and Validate admit only HD50/59.94; default stays HD50 | All three UHD formats rejected today; 25 needs a new admitted cadence | Reject unsupported semantics before execution; no implicit tone map |
| `LocalMediaInputPolicy.cs`, Windows decoder | Bounded input envelope, normalized HD RGBA8 output; H.264 UHD25/30 can pass policy, UHD50/59.94 exceeds its macroblock rate; other codecs have separate actual decoder checks | Input resolution does not select Program format; qualify actual MFT output sizes/rates and A/B residency | Main10 SDR normalization loses native precision; signaled HDR/wide-gamut remains unsupported |
| `GpuProcessing.cs`, `V1RuntimeHostService.cs` | Advertised HD GPU capabilities; RGBA buffer arithmetic itself accepts other sizes; bounded host readback/shared monitoring | Neither buffer allocation nor a shader running proves a production profile; budget all live/cache/readback surfaces and transfers | Separate private multi-plane/device/fence proposal in [GPU surface design](GpuMediaSurfaceDesign.md); processing kernels and conversions need precision proof |
| RationalFrameSchedule / audio relationship | One rational Program epoch; 48-kHz audio follows exact video sample windows | 25: 40 ms / 1920 samples; 50: 20 ms / 960 samples; 60000/1001: 16.6833… ms / 800-or-801 samples (4004 per five frames) | No separate audio or color conversion clock; retain exact sequence/sample provenance |
| Monitoring contracts/wire, Client, WPF | Color metadata and optional shared GPU resource; independent lossy monitoring; CPU payload allocation and sRGB presentation fallback | Bound wire payload/pitch and display scaling; qualify source texture vs displayed proxy; monitor drop must not stall Program | HDR display pipeline, tone-map policy, transfer/range handling and actual display calibration are absent |
| `ProfessionalRecordingFormats.cs` | MP4 H.264/AAC, MOV 2vuy/PCM and opt-in MXF input-format lists contain only HD50/59.94 | New writer profile, bitrate/level, container fields, conversion/cadence and reopen tests per rate; old writer IDs must not silently acquire UHD | Existing 8-bit writers do not preserve native 10-bit/HDR/alpha; separate codecs, metadata and provider approval |
| ReplayCaptureEngine / RuntimeReplayService | Bounded queue of retained Program leases into encoded segments; storage/time bounds | Recording profile and queue-byte/storage bandwidth qualification required; encoded retention is not a raw UHD ring | Replay/reopen must preserve declared color and precision or explicitly reject |
| NDI / SRT providers, RuntimeNetworkOutputBridge | Both advertised SupportedFormats lists are HD50/59.94; bounded queues and existing host payload/audio transfer | Generic NetworkOutputConfiguration is not provider approval; qualify encode/transport, timestamps, receiver and overload isolation separately | Receiver/profile/color negotiation, alpha transport and metadata survival need explicit contracts |
| NativeMediaIoAdapter / AJA NTv2 adapter | Normalized HD RGBA8 only; native adapter guard checks 1920×1080 and 50/59.94; AJA uses RGBA frame buffer | New exact device/mode/link/reference/transfer validation, hardware loopback and sink byte sizing needed | Native V210 enum is not 10-bit Program; independent packing, key channel, sync and signal analysis required |
| Operator, control IPC and persistence | Operator intent; ControlHost commits; management IPC carries bounded control data | Format selection requires an approved profile, complete preflight and atomic activation; no full-frame management payload or second renderer | Versioned semantics and explicit unsupported-peer behavior; old checkpoints retain original interpretation |

2160p25, 2160p50 and 2160p60000/1001 are **all BLOCKED / physical UNVERIFIED**.
No subset of a row's existing generic APIs establishes support across this matrix.
Consumer failures must be attributable by provider/profile/reason, without falsely
marking the remaining Program path as ready.

## Arithmetic allocation and transfer envelope

Tightly packed RGBA8 is width × height × 4 bytes, excluding alignment, metadata,
audio, native allocations, encoder staging and transient duplication.
HD = 8,294,400 bytes; UHD = 33,177,600 bytes (31.640625 MiB), exactly four times
HD at the same rate. These are sizing calculations, not measurements or limits
approved for production.

| Hypothetical UHD item | Bytes / demand |
| --- | ---: |
| One full-frame directional transfer at 25 fps | 829,440,000 B/s |
| At 50 fps | 1,658,880,000 B/s |
| At 60000/1001 fps | approximately 1,988,667,333 B/s |
| Upload plus readback once per frame | twice the corresponding directional demand |
| Four retained host readback buffers (current GPU default count) | 132,710,400 bytes |
| Eight retained full-frame leases (replay default queue count) | 265,420,800 bytes |
| Eight cached buffers of this size (managed backend cache count limit) | 265,420,800 bytes |

Shared leases can reference one allocation; count unique allocations and retention
duration, not just references. Conversely worker/current-frame buffers can exist
outside queue capacity. Metadata histories (for example the virtual provider's
128 observations) are not 128 full-frame payloads. Recording/network/replay
consumers may share Program readback; measure actual ownership and copies before
summing them as separate frames.

The existing resident-next 32-MiB preparation budget is not a UHD guarantee:
one tight UHD frame nearly consumes it before audio, preroll, active worker
frames or decoder/native staging. Audit distinct host/device/pinned pools,
high-water bytes, per-format caches, pitch/alignment, decoder slots and generation
cleanup. Reject admission before resource exhaustion. No widening of a count or
budget is proposed here. Disk demand depends on input/recording codec and
concurrent reads/writes, not just raw RGBA bandwidth.

## Separate 10-bit HDR and alpha contract

Proposed semantics, with no public schema change:

- **Layout:** distinct P010 (4:2:0, 16-bit containers with defined valid-bit
  alignment and UV plane), packed RGB10 (explicit channel/alpha bit ordering),
  and RGBA8 identities. Declare per-plane offset, row pitch, sample geometry,
  chroma siting, byte order and checked total size. Reject odd/inconsistent
  subsampling geometry and undersized/overlapping planes.
- **Color:** primaries, transfer, matrix, range, valid bit depth and metadata
  authority must travel with the frame. Define Rec.709 SDR, BT.2020, PQ and HLG
  explicitly; absent or contradictory metadata is an error for a qualified HDR
  profile. Resolution, codec and a 10-bit container do not infer color.
- **Conversions:** default SDR-to-HDR policy is reject. An approved conversion
  must name source/destination semantics, reference white, peak/mastering
  assumptions, gamut mapping, rounding/dither and precision/error bounds.
  HDR-to-SDR needs its own approved tone-map policy. No automatic promotion,
  clipping or silent CPU/RGBA8 fallback.
- **Alpha:** distinguish opaque, straight and premultiplied alpha, channel
  precision and compositing domain. P010 has no implicit alpha. Define whether
  RGB10 supplies alpha and its precision; Fill/Key requires independent output
  channel pairing/reference and physical verification. Unknown alpha must not
  silently become a key signal.
- **Capabilities/ownership:** exact format/color/layout and conversion capability
  matching, source revision/generation/device epoch, completion fence before
  publication and bounded leases. Vendor handles/fences stay in private provider
  adapters; stable Control/Runtime contracts stay backend neutral.
- **Persistence/transport:** version the semantic payload and negotiation before
  adding unsupported enum values. Persist profile, color/alpha, conversion policy
  and metadata provenance through checkpoint/journal, IPC, prepared execution,
  recording/replay and reopen. Old 1.0 checkpoints retain their original HD
  meaning and checksum; no relabeling UnknownRgba8 as Rec.709 or HDR. Unsupported
  peers fail before activation, with atomic rollback and no saved-production loss.

Future tests must cover valid tuples and missing/contradictory metadata,
plane bounds/overflow, unsupported consumer rejection, SDR/HDR conversion
goldens, alpha edges, stale generation/fence completion, device loss, cancellation,
reopen and old-peer/checkpoint recovery. An enum addition alone satisfies none
of these requirements.
