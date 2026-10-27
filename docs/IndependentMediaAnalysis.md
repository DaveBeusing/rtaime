# Independent Media Analysis

**Product:** rtaime · **Area:** Media / RuntimeHost / ControlHost / Client  
**Capability:** `rtaime.media-analysis.v1` · **Status:** Implementation in qualification (not a release-readiness claim)

## Responsibility and authority

The analysis API provides thumbnails at an actual decoded presentation timestamp and per-channel audio peaks for finite media intervals. It is not a second asset catalogue, preparation/readiness indicator, media clock, production deck, or timeline renderer.

```text
Operator -> rtaime.Client -> ControlHost catalogue authority
                                  |
                                  | Authorized asset ID, source path and file identity
                                  v
                        RuntimeHost analysis worker
                           |               |
                      shared global    separate optional
                      decoder ledger    Windows MF decoder
                           |
                   Read-only observations
                           |               |
                 bounded metadata     dedicated local named pipe
                 and PCM buckets       320 x 180 RGBA8 frame
```

The client requests `AssetId`, the opaque consumer name, monotonic `Generation`, analysis kind, time range and bounded geometry. It cannot supply or discover a local media path through the analysis request. ControlHost resolves the asset from the current authoritative catalogue and sends the identity and file location only to RuntimeHost. RuntimeHost reserves the **existing** `LocalMediaResourceLedger` before opening media.

## Versioned operations

`BeginMediaAnalysisAsync(request)` returns a job ID, Runtime epoch, request generation, disposition and optional observation-channel credentials. `GetMediaAnalysisAsync(job)` returns metadata and/or channel peaks. `CancelMediaAnalysisAsync(job)` requests cancellation, but `CancellationAcknowledged` becomes true only after confirmed native-owner cleanup (or before native admission). Job reads are fenced by asset, job, consumer, generation and Runtime epoch. A ControlHost restart or catalogue revision/identity change invalidates existing authorization.

The `MediaAnalysisState` values are `Pending`, `Running`, `Complete`, `Partial`, `Canceled`, `Stale`, `Unavailable` and `Failed`. Missing PCM samples are represented by bucket `HasSamples = false` and `Discontinuity = true`; silence with samples is a real zero-amplitude result, not a missing range.

The canonical external-control gRPC schema adds operation tags 98–100. These are additive operations and do not change the existing media, production, catalogue or storage protocol numbers.

## Bounded resources and latency

| Limit | Effective implementation |
|---|---|
| Observation frame | Up to 320 × 180, RGBA8, 230,400 bytes |
| Waveform request | Up to 120 seconds and 256 output buckets, level 0–8 |
| Consumers | Eight retained active consumer identities; completed identities can be recycled |
| Scheduling | At most one running analysis decode worker globally; at most one replaceable queued request per consumer |
| Per-request time budget | 45 seconds, with explicit native cleanup/Quarantine on uncertainty |
| Frame seek scan | 150 decoded frames maximum |
| Waveform decode scan | 7,200 decoded windows maximum |
| In-memory derived cache | 32 entries, 8 MiB |
| Persistent derived cache | 64 manifests, 32 MiB, 768 KiB per blob, checksum and atomic manifest commit |
| Observation pipe | Four concurrent read-only readers; five-second session timeout |
| Admission | Same Runtime ledger as resident Program and preflight; optional analysis can be rejected when pressure is unknown |

The runtime rejects optional media analysis beside resident production under the existing unknown storage-pressure policy. The analysis worker never seeks a resident Media Deck, cannot alter routes, and does not promise a frame when no decoder slot is admissible. Non-cooperative native readers remain charged and quarantined rather than falsely acknowledged.

The initial implementation verifies the catalogue fingerprint from a bounded, throttled read before requesting decode/cache publication. Fingerprint changes at the same path, file length, timestamp, decoder profile or analysis version invalidate the cache. Entries are **regenerable derived data** only, not a persisted asset authority or conditional readiness claim.

## Bounded thumbnail filmstrip preparation

The Operator now exposes \`MediaAssetFilmstripService\` as a reusable read-only
library/sequence preview consumer. A \`MediaFilmstripRequest\` binds the catalogue
asset ID, current SHA-256 fingerprint, length/update identity, duration, exact
source-tick range, rational nominal frame rate, requested dimensions,
sample count and compact/detailed quality. This is a client composition of
existing versioned **Frame** analysis requests, not a new media contract,
codec provider, bulk management IPC, production deck seek or second index.

The source-aligned plan takes up to **eight** distinct representative
frame positions in a visible range. Compact requests are at most 80×45,
detailed at most 160×90; one strip contains no more than **460,800** RGBA
source bytes before frozen WPF presentation. Each requested position is
matched against the actual decoded PTS returned by Runtime; nominal
25/50/60000/1001 timebases determine *request positions*, never fabricate
VFR presentation timestamps. Only successful, verified local observations
become thumbnails. Failed, timed-out, offline or unavailable positions remain
explicitly absent; a partial strip cannot claim COMPLETE.

Identical in-flight strip requests share one cancellable operation. A
150-ms replacement debounce and one active consumer bound scroll/scrub
pressure; when the final waiter cancels, an admitted job is explicitly
canceled after Begin returns. Per-frame deadlines are eight seconds and the
whole optional strip has a 45-second budget. Progress reports completed
observations, not an assumed decoder or Program readiness. Frozen images are
cached in the Operator for at most 16 strips / 8 MiB / ten seconds. A current
catalogue read precedes *every* cache use; changed fingerprint or offline
identity evicts old images. Runtime's existing content-addressed, SHA-verified
single-frame disk cache remains authoritative for reusable decoded evidence.

The persistent derived cache now serializes same-instance reads/writes,
validates waveform range boundaries before reuse, and counts *manifests plus
blobs* toward its existing 32-MiB/64-entry disk quota. Temporary files use
the existing atomic-manifest-last failure boundary. No original media,
full-size frame series or PCM is persisted. Consumers still must refresh
catalogue availability when external media changes; a locally cached image
is not independent physical-file or current Program evidence.

The filmstrip API is ready for subsequent Library/sequence visual bindings;
existing single-position Library hover and decoded-PCM timeline waveforms
continue as their current presentation paths. Do not claim an installed
filmstrip glyph or an expanded audio-only provider based on this foundation.

## Observation-only transport

All frame pixels travel on `<runtime-endpoint>.analysis`, **not** in the existing management envelopes, root dispatch bulk payload or Program monitoring stream. The local client receives an opaque 256-bit per-job token from the authorized Control begin response and opens the Runtime-owned observations-only named pipe. The pipe authenticates the token, current job generation and completion state; replies are fixed-length bounded raw RGBA8 or a negative acknowledgement. Existing Operator pipe ACLs or the current-user pipe restriction apply.

The gRPC management API exposes lifecycle metadata and waveform buckets. It does **not** claim successful transfer of raw frame pixels to remote gRPC clients: remote frame data requires a separate qualified authenticated observations transport. Local Operator named-pipe consumers can hydrate frame pixels through the dedicated pipe.

## Waveform fidelity and known qualification limits

The decoder supplies PCM Float32 from its existing audio stream and exact audio sample positions. Buckets store minimum, maximum, optional RMS and count per channel; rational sample-to-time conversion avoids accumulated float frame drift. Native GOP seeks and actual decoded video PTS are used for position images.

The current Windows Media Foundation file provider still requires a supported video stream for opening media; an **audio-only asset** therefore reports `Unavailable` until that provider gains an audio-only open path. The existing finite codec, format, GPU and input support boundaries remain unchanged. VFR/GOP, 25/50/59.94, interrupted callback/COM cleanup, 1,000 rapid replacements, real client/host path, file change and native-pressure qualifications are release gates, not presumed passes.

## Recovery and diagnostics

Cancellation before decoder admission is acknowledged immediately; during native work it is only requested until decoder disposal and ticket release finish. Shutdown drains the optional worker, then quarantines unconfirmed native ownership instead of silently returning capacity to Program. A host restart changes `RuntimeEpoch` and invalidates former jobs. A partial waveform remains distinguishable from a validated complete result.

Raw source media, credentials, full local file paths, original PCM and native decoded full-size frames are not written into manifests, diagnostics, CI artifacts or Management IPC. The derived cache is stored under the process user's local app data directory.

## Test and CI instructions

On a configured Windows SDK 10.0.401 workstation:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release --no-build --filter "FullyQualifiedName~MediaAnalysisContractTests"
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~MediaAnalysisWaveformTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~IndependentMediaAnalysisIntegrationTests|FullyQualifiedName~LocalMediaResourceLedgerTests"
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Review **CI**, **Quality**, **Security**, **Provider Smoke**, and **Packaged E2E** on the same final commit SHA. Queued, skipped, missing, or non-Windows-only synthetic tests are not evidence of passing actual Windows decoder/Program isolation.

Related boundaries: [LocalMediaFileSource.md](LocalMediaFileSource.md), [MediaLibraryAssetBrowser.md](MediaLibraryAssetBrowser.md), [OperatorMonitoringPlane.md](OperatorMonitoringPlane.md), [OperatorExperienceScope.md](OperatorExperienceScope.md), [BuildAndTest.md](BuildAndTest.md).
