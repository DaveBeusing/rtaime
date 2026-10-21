<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Local Media Read Histogram Allocation Qualification

## Scope

This qualification covers only the value-based `LocalMediaReadMeasurements.Observe`
histogram, with its 17 inline exponential bins. It does not measure native
Media Foundation decoding, native RGB32/PCM16 copies, frame leases, GPU
transfers or end-to-end Program cadence. Decoder and Runtime authority,
contracts, persistence and all resource ownership remain unchanged.

## Historical regression and attribution boundary

Required Gates run
[39625156828](https://github.com/DaveBeusing/rtaime/actions/runs/39625156828),
CI job `119308345826`, recorded one failure in
`Long_read_observation_has_zero_steady_state_managed_allocation_and_saturates_counts`:
**408 caller-thread managed bytes** during 100,000 observations, with an
expected value of zero. The original test warmed only 100 calls before
the measurement. Other invocations passed.

The old failure log exposes a single window delta, not allocation events,
allocation stacks or a tiered-JIT transition. It therefore cannot prove
whether the 408 bytes came from `Observe`, runtime initialization or test
instrumentation. The `InlineArray(17)` production path does not explicitly
allocate or retain per-sample objects; source inspection is not a substitute
for a measured workload. Attribution remains **UNVERIFIED** until an exact-run
profile or controlled reproduction identifies the allocator.

## Measurement protocol

The Unit regression now runs on a dedicated managed thread, rather than an
xUnit runner worker, and observes the saturated public `Calls` and
`TotalMilliseconds` counters. The Performance qualification runs both
forward and reverse latency sample orders. Each test:

1. Starts and finishes its entire worker inside one named, dedicated managed
   thread; thread creation and test-runner setup occur outside the window.
2. Warms the **same non-inlined observation method** for 100,000 calls before
   reading allocation counters, to minimize cold JIT and tier-promotion noise.
3. Reads `GC.GetAllocatedBytesForCurrentThread()` twice around an empty
   observer control; the control must itself be zero, without subtraction.
4. Reads the same per-thread counter immediately before and after an actual
   100,000-call observation loop. **Each workload window must equal 0 bytes.**
5. In Performance, repeats the control/workload pair for **three independent
   100,000-call windows**, alternating latency order, without allocating a
   report, logging or reading quantiles inside the measured windows.
6. Validates thread identity, cumulative count, capacity and expected
   P50/P95/P99/overflow behavior **after** all allocation windows.

The performance workload alternates equally among 0, 0.1, 1 and 9000 ms.
Each 400,000-sample run (100,000 warmup plus three measured windows)
must produce P50 = 0.125 ms, P95 = P99 = 9000 ms and capacity = 17.
The separate Unit cases continue to cover value equality/hash semantics,
finite-input rejection, extreme finite total saturation, empty quantiles and
parallel snapshot reading **outside** the observation window.

A nonzero delta fails; there is **no 4-KB budget**, GC intervention, byte
subtraction, retry-to-pass rule, global allocation total in place of the
worker count, or changed production sampling contract. Repeated windows
improve reproducibility but do not establish a proof for every supported
runtime or hardware platform.

## Evidence identity

On a failed Performance assertion, the test includes the window index,
observed bytes, empty-control bytes, sample direction, process and thread IDs,
CPU identifier when provided by Windows, architecture, processor count, OS,
managed runtime description, assembly informational version and
`GITHUB_SHA` environment value (which can identify the synthetic PR merge
commit instead of the actual head). Reported values are emitted through
xUnit output **after** measurement. Do not treat missing CPU metadata as
measured hardware evidence.

The .NET SDK pin is `global.json` (`10.0.401` at introduction); the
effective SDK must be checked against the corresponding Actions
`Setup .NET` and `Qualify SDK` steps. Record the actual PR **head SHA**,
workflow run/attempt and job IDs separately. `GITHUB_SHA` is not by itself
a substitute for source provenance.

## Reproduction

From a fresh `master`-derived checkout on the pinned Windows x64 .NET SDK:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~LocalMediaPlaybackMeasurementsTests" --logger "console;verbosity=detailed"
dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release --filter "FullyQualifiedName~LocalMediaReadAllocation" --logger "console;verbosity=detailed"
```

Run the two filters in both orders and repeat the long-window Performance
filter in separate test processes. A one-off passing run cannot be
retrospectively credited to another source SHA or CI job. When an allocation
reappears, preserve the failing process/test output and collect allocation
events/stacks with a supported profiler **outside the measurement window**
before changing the product code.

Then run the canonical full qualification:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

The exact final PR head must also pass **CI**, **Quality**, **Security**,
**Provider Smoke** and **Packaged E2E**, including required jobs and artifacts.
Queued, skipped, cancelled, failed or unavailable checks mean **not PASS**.
A Windows Actions run cannot qualify physical reference SSD, CPU model,
NVIDIA GPU or hardware output without separate source-bound evidence.

## Result register

| Qualification | Status |
| --- | --- |
| Historical 408-byte failure reproduced from CI log | CONFIRMED, historical source only |
| 17-bin value-only product path retained | Source inspection; no product behavior changed |
| Dedicated-thread warmup and three strict zero-byte windows | Implemented as tests; PASS requires exact-head execution |
| Observer control and forward/reverse window order | Implemented as tests; PASS requires exact-head execution |
| Saturation, quantiles, invalid input and parallel immutable reads | Implemented as tests; PASS requires exact-head execution |
| Root cause of 408-byte historical outlier | UNVERIFIED without allocation stacks or controlled attribution |
| Native decoding and real hardware performance | OUT OF SCOPE / UNVERIFIED |
| Five Required Gates on final PR head | UNVERIFIED until evaluated |

Update this register only from source-bound execution evidence. The remaining
allocation-attribution uncertainty must not be represented as a resolved
production defect or as proven tiered-JIT behavior.
