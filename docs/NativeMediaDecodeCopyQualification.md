<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Native Media Decode Copy and Managed Allocation Qualification

## Scope and authority

This qualification concerns only the existing Windows Media Foundation RGB32 / PCM16
source reader and its single background `LocalMediaDecodeWorker`. The Runtime
admission buffer, GPU upload, native decoder ownership, Program clock, output
mapping and recording/monitoring consumers are unchanged. GPU-resident decoding
remains **PROPOSED / NO-GO**, not an implemented output capability.

Source starting point: `master`
`d5ac98225fb978534a5c6c1ec3c0bd57928650b5` (2026-10-21);
initial attribution head: `40c2138ef9f16d21d87ecd09422ae3bc547d4dce`.
Exact final PR head, check statuses and source-correlated evidence must be
recorded in the pull request after Windows qualification. These SHAs do
not stand in for a completed before/after measurement.

## Actual ownership and transfers

| Boundary | Lifetime and amount | Change |
| --- | --- | --- |
| Media Foundation RGB32 → decoder RGBA scratch | One 1920×1080 RGBA8 host payload: 8,294,400 bytes; pitch and visible rows validated | Positive tightly packed rows use one full-frame `Marshal.Copy` rather than 1,080 row calls; padded/negative-pitch still use validated row calls |
| In-place channel normalization | Reads and writes the same RGBA scratch; opaque alpha | Unchanged swizzle and colors; no new source buffer |
| Native PCM16 → decoder-local packet scratch | One admitted packet, up to 192,000 bytes; native length and capacity checked | Reused bounded scratch replaces per-packet managed array; no packet references escape the decoder |
| PCM16 → returned Float32 audio window | Frame-aligned stereo 48-kHz Float32, exact owned output bytes | Conversion/timestamp semantics and independent per-frame output allocation unchanged |
| Decoder RGBA/Float32 → worker slot | Ten exact-size session-owned slots, queue capacity eight; per-lease retention | Full-frame and PCM copies remain, source generation/PTS fences unchanged |
| Worker lease → Runtime source RGBA | Existing Runtime framebuffer, one full RGBA payload when admitted | Unchanged; isolated diagnostic separately reproduces `RgbaFrameBuffer.CopyPixelsFrom`, not actual Runtime transaction timing |
| GPU upload / Program readback | Existing provider ownership / bounded leases | Unchanged; no new DXGI/D3D11 import or zero-copy claim |

At 50 fps, one 8,294,400-byte RGBA transfer represents 414.720 decimal MB/s
of **logical payload requests**; at 60000/1001 fps it is about
497.167 MB/s. These arithmetic rates are not measured DRAM/PCIe throughput,
and the optimized contiguous copy still transfers the same pixels.
A decoded frame may not be admitted or uploaded, so do not multiply
these values into actual device statistics without correlated counts.

## Optional constant-space software attribution

Set `RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS=1` before opening the source/worker.
When off, `CopyDiagnosticsEnabled=false` and zero copy counters mean
**UNMEASURED**, never measured zero. The existing `LocalMediaReadMeasurements`
exponential fixed-bin histograms expose cumulative CPU-wall P50/P95/P99
upper bounds with their sampling method. The new `LocalMediaCopyMeasurements`
records an operation count and logical payload bytes (not total memory traffic).
Separate stages are:

- RGB32 lock/copy/unlock into decoder scratch, then independent RGBA swizzle;
- native PCM16 packet copy, then each PCM16-to-Float32 chunk conversion;
- worker video-slot copy and worker PCM-slot copy;
- isolated test-only Runtime framebuffer copy, separately labeled as such.

The RGB32 copy span includes buffer lock/unlock overhead and cannot by itself
distinguish native MFT decode or driver waiting; these belong to existing
`NativeVideoRead` measurements. The PCM conversion histogram samples *chunks*,
not necessarily one operation per Program boundary.

The isolated native reader test runs 1080p50 H.264/AAC with eight warm-up
and 32 measured frames on one caller thread. It reports
`GC.GetAllocatedBytesForCurrentThread` alongside an approximate
`GC.GetTotalAllocatedBytes(false)`, Gen0/1/2 count deltas, cumulative GC
pause, last-GC heap/LOH/POH indicators, per-stage CPU-wall histograms,
and source identity. Its reproduced Runtime source-copy span is not a
live host/output measurement.

`LocalMediaDecoderSafetyTests.Native_RGB32_bulk_copy_matches_row_reference_at_both_production_rates`
compares the old row-by-row algorithm and the new bulk path on the same
1080p source-sized buffer for both 50 and 60000/1001 rate sample counts,
with identical bytes, CPU-wall P50/P95/P99 and thread allocations. This
is an in-process **synthetic copy microbenchmark**, not native codec throughput.
The same test suite compares the old fresh PCM array method against
decoder-local scratch for identical native bytes and checks no repeated
managed allocation after warmup.

## Correctness and failure coverage

`LocalMediaDecoderSafetyTests` validates positive contiguous, padded and
bottom-up negative pitch, opaque RGBA channels, strict native buffer capacity,
maximum PCM16 packet extent, unchanged Float32 payload ownership across later
real H.264/AAC frames, seek-to-zero pixel/audio hashes at 50 and 59.94 and
native open/seek/close recovery. `ReferenceMediaPlaybackAllocationDiagnosticsTests`
covers isolated source profiling and worker-slot copy observation with a
retained old frame lease; `AsynchronousLocalMediaDecodeTests`,
`LocalMediaResourceLedgerTests`, `MultiDeckLocalMediaIntegrationTests`
and the reference-media regression matrix remain required.

Known native packet bounds, cancellation, EOS, clock domain, seek/loop, worker
slot-count/queue/backpressure and quarantine must not be relaxed.
No forced `GC.Collect`, unbounded pools, background alloc logs or artificial
`NoGCRegion` are part of the measured path.

## Regression of microbenchmark allocation windows (2026-10-21)

The merged native-copy optimization was followed by two intermittent Windows
Integration failures on later PR HEADs. [Run 39603350045](https://github.com/DaveBeusing/rtaime/actions/runs/39603350045),
Provider Smoke job `119233184694`, observed **7,352 bytes** against the
unchanged **4,096-byte** budget in the *row-reference* copy measurement at 50/1,
not in the optimized-copy assertion. [Run 39611347595](https://github.com/DaveBeusing/rtaime/actions/runs/39611347595),
Provider Smoke job `119260855815`, observed **7,664 bytes** against the same
budget in the combined RGB32 copy/swizzle test. Neither failure by itself
proves a decoder leak, incorrect pixels, or a measured native throughput loss.

Previously, `GC.GetAllocatedBytesForCurrentThread` covered the test's
`Stopwatch.GetTimestamp` / `Stopwatch.GetElapsedTime` observation as well
as the actual copy operations, after only four untimed warm-up iterations.
These counters could not attribute sporadic managed allocations to the
copy operations rather than lazy runtime/JIT/interop or observer work.
The exact byte-level origin of the historical spikes remains **UNVERIFIED**
without source-correlated allocation attribution.

The corrected synthetic qualification now:

- Warms the existing production bulk-copy and reference-row paths, the
  combined copy/swizzle path, and timing APIs with 64 iterations *before*
  either strict managed-allocation sample.
- Measures **the same number of complete frame copies** without a timer inside
  each strict allocation window; the optimized copy, row-reference copy and
  production copy+swizzle path each retain the original **4,096-byte** ceiling.
  No results or `referenceAllocations` assertions are omitted.
- Separately samples copy/row-reference and copy+swizzle CPU-wall times,
  including P50/P95/P99 where applicable. Reports observer-inclusive
  allocation deltas independently so stopwatch/JIT noise remains diagnosable
  without being misidentified as a managed copy-buffer allocation.
- Compares 1920x1080 pixel bytes before and after in-place RGB32 swizzle
  at 50/1 and 60000/1001, with tightly packed and padded positive/negative
  pitches. The 2D scanline and contiguous-buffer entry points must agree.
  Ownership, PCM leases, packet bounds, native sessions and decoder semantics
  are unchanged.

This corrective change is limited to the synthetic Integration tests and
their qualification documentation. No extra full-frame transfer, renderer,
worker, diagnostic poller, or production buffer allocation is introduced.
The regression branch starts from `master`
`35f05b73604683697af45a53c87c8cfbd83a6a0c`; its final head,
Windows runner, observed copy-only and timed-window allocation deltas,
native 50/59.94 cases, and all five Required Gates require independently
verified PR/Actions evidence. A successful synthetic test cannot establish
hardware memory traffic, end-to-end Program latency, or physical GPU behavior.

## Exact-source results and acceptance

| Evidence | Current state |
| --- | --- |
| Code-level copy call count for positive tight 1080p | 1 `Marshal.Copy` per frame instead of 1,080; bytes per frame unchanged |
| Native PCM scratch allocation policy | After warmup reused per decoder; maximum retained packet scratch 192,000 bytes; Float32 frame output still allocated |
| Synthetic identical-run allocation and CPU-wall comparison | Implemented as tests; PASS and observed numbers require exact-run logs |
| 50/59.94 true H.264/AAC, PCM-ownership and seek/hash qualification | Implemented as Windows tests; PASS requires exact-run logs |
| Full Runtime A/B, recording/replay, MXF/MOV, GC pause, P99 comparison | **UNVERIFIED** until source-bound Windows integration evidence |
| Hardware decoder choice, NVIDIA GPU, physical SDI/NDI output, SSD/DRAM/PCIe latency | **UNVERIFIED** and outside this copy refinement |

Run the canonical Release build and focused Windows Integration tests first,
then full solution tests and all five Required Gates: `CI`, `Quality`,
`Security`, `Provider Smoke`, `Packaged E2E`.
Reference-media regression is a separate additional qualification.
Check the final source SHA, runner, SDK 10.0.401, actual test logs and
artifacts after each subsequent commit. Running, cancelled, failed, skipped
or unavailable checks cannot count as PASS.

Until comparable real-source pre/post profile numbers and the complete gates
are available, describe speedup, total allocation reduction, hardware latency,
GC pause or release readiness as **UNVERIFIED** rather than extrapolating
from source code or synthetic microbenchmarks.
