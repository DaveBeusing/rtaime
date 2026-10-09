<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Advanced Audio Processing Qualification

## Purpose

This qualification turns the bounded Advanced Audio Production implementation into reproducible software evidence.

The qualified system remains one Runtime-owned 48 kHz stereo Float32 audio path:

```text
source gain/mute
  -> bounded per-source EQ
  -> crossfade / ducking contribution
  -> deterministic bus sum
  -> bus master gain/mute
  -> bounded compressor
  -> bounded sample-peak limiter
  -> final safety clamp
  -> final authoritative bus payload
  -> metering / recording / selected output role
```

ControlHost remains authoritative for configuration. RuntimeHost remains authoritative for audio execution state, sample processing and metering. Operator remains a synchronized presentation and mutation surface.

## Qualified matrix

| Area | Software evidence | Qualified claim |
| --- | --- | --- |
| Complete processing order | `AdvancedAudioProcessingQualificationTests.Complete_processing_order_matches_golden_vector` | Deterministic sample vector covers source gain, EQ, equal-power crossfade, ducking, bus master, compressor, sample-peak limiter and final safety clamp in canonical order. |
| Governed configuration | `AdvancedAudioQualificationIntegrationTests.Full_authoritative_configuration_crosses_Client_Control_and_Runtime` | Typed EQ, four buses, mixed routed/always-in-mix contribution, crossfade, ducking and bus dynamics cross Operator Client → ControlHost → RuntimeHost and are confirmed before becoming production truth. |
| Revision conflicts | The same integration qualification plus `ProductionIpcIntegrationTests` | Stale advanced-audio revisions fail closed with `control.audio.production.revision_conflict`; no blind retry becomes authoritative state. |
| 8-source / 4-bus load | `AdvancedAudioProcessingQualificationPerformanceTests.Full_eight_source_four_bus_processing_remains_bounded_allocation_free_and_finite` | Maximum bounded source/bus topology with active EQ, mixed contribution modes, crossfade, ducking and bus dynamics remains finite and allocation-free after warmup. |
| Exact 50 / 59.94 timing | `AudioFollowVideoPerformanceTests.Ten_minute_class_audio_follow_video_run_has_no_timing_discontinuities` | 30,000 boundaries at 50/1 finish at sample 28,800,000; 36,000 boundaries at 60000/1001 finish at sample 28,828,800 without timing discontinuity. |
| EQ semantics and restart state | `BoundedParametricEqualizerContractTests`, `BoundedParametricEqualizerProcessingTests`, `BoundedParametricEqualizerPerformanceTests` | Bounded typed parameters, finite coefficients, deterministic response/state isolation, block invariance, multi-bus replay and zero-allocation hot path. Reconstructed Runtime execution starts filter history from zero state. |
| Compressor / limiter semantics and restart state | `BoundedBusDynamicsContractTests`, `BoundedBusDynamicsProcessingTests`, `BoundedBusDynamicsPerformanceTests` | Bounded typed parameters, deterministic stereo-linked envelope behavior, sample-peak ceiling, state continuity/reset semantics, bus isolation and zero-allocation hot path. |
| Program recording parity | `ReferenceRecordingPayloadTests.Recording_persists_the_final_multi_source_Program_mix` | Recorded audio bytes are exactly the final dynamics-processed Program bus payload. |
| Selected output bus | `ProductionIpcIntegrationTests` plus Runtime final-bus materialization policy | Program/Aux roles carry confirmed `AudioBusId` selection; Runtime submits the selected already-materialized final bus payload and providers do not remix it. |
| Runtime restart / recovery | `ProductionIpcIntegrationTests.RuntimeHost_restart_resynchronizes_without_advancing_authoritative_revision` plus EQ/dynamics reconstruction tests | Authoritative configuration is restored after Runtime process replacement while filter/envelope history is deliberately reconstructed from safe initial execution state. No previous-process DSP history is fabricated. |
| Missing source / sidechain loss | `AudioProductionEngineTests` | Missing source contribution remains bounded silence/evidence; unavailable sidechain reports unavailable and releases ducking toward unity. |
| Invalid EQ / dynamics | `BoundedParametricEqualizerContractTests`, `BoundedBusDynamicsContractTests` | Non-finite and out-of-range processing configuration is rejected before execution. |
| Bus bounds / missing bus | `AudioProductionEngineTests` and `AudioProductionConfiguration` validation | Source and bus topology stays bounded at 8 × 4 and references to unconfigured buses fail closed. |
| Output backpressure | `NetworkOutputFoundationTests.Saturated_queue_drops_oldest_complete_sample_without_blocking_submitter` and `Transport_failure_is_observational_and_does_not_reject_future_submission_synchronously` | Output-side pressure/failure remains bounded and observational rather than redefining Program production authority. |
| Recording failure | `RecordingFailureIsolationTests.Recording_writer_failure_does_not_change_committed_runtime_or_stop_program_frames` | Recording-side failure does not change committed Runtime authority or stop Program frame execution. |
| Operator projection | `AudioMixerOperatorSurfaceTests` and Operator visual/DPI qualification | Mixer state is confirmed Client/Runtime evidence, uses bounded custom controls and does not create a WPF audio-analysis loop. |

## Golden-vector semantics

The complete-order unit vector intentionally activates every bounded stage that can influence the Program sample path in one deterministic block:

- source A is the ducking sidechain and equal-power crossfade origin;
- source B passes through all three typed EQ bands and is the ducking target/crossfade destination;
- source contribution is accumulated into Program;
- Program master gain is applied before dynamics;
- compressor gain reduction is active;
- the sample-peak limiter clamps the instantaneous peak to its configured ceiling;
- final safety clipping remains unused for the qualified vector.

The expected samples are retained as constants in the test rather than being calculated by a second copy of the implementation under test.

## Sustained bounded-load semantics

The combined performance qualification uses:

- 8 admitted sources;
- 4 buses: `program`, `aux`, `clean`, `iso`;
- mixed routed-only and always-in-mix contribution modes;
- all three source EQ bands active;
- equal-power crossfade and ducking active on Program;
- compressor and sample-peak limiter active on all four buses;
- 960-frame blocks at the 48 kHz / 50 fps reference cadence;
- 64 warmup blocks followed by 500 measured blocks per bus.

After warmup, the AudioProductionEngine must allocate zero bytes on the measured thread. Every final sample must remain finite and inside [-1, +1]. The hosted 30-second elapsed-time guard is a regression guard only; it is not a physical audio-latency claim.

## Timing evidence

Exact sample windows remain derived from rational video/audio timing rather than accumulated rounded frame durations.

The retained long-run evidence covers:

```text
50/1:
30,000 video boundaries
-> 28,800,000 audio samples

60000/1001:
36,000 video boundaries
-> 28,828,800 audio samples
```

This proves software sample-position continuity for the reference timing model. It does not prove external hardware clock lock.

## Recovery semantics

Runtime restart restores authoritative advanced-audio configuration through the established Control/Runtime recovery path.

Configuration survives according to durable/authoritative state. DSP execution history does not:

- source EQ filter delay/history starts from zero state;
- bus compressor envelope starts from unity;
- bus sample-peak limiter envelope starts from unity;
- new Runtime timing resumes from the Runtime lifecycle model;
- no stale previous-process filter or envelope value is reported as if execution history survived process loss.

This is intentional safe reconstruction, not continuity of analog/DSP memory across process failure.

## Required software gates

The Advanced Audio Production quality policy requires this qualification evidence together with the existing specialized tests.

The normal qualification sequence is:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release
dotnet test tests/rtaime.Tests.Behavioral/rtaime.Tests.Behavioral.csproj --configuration Release
dotnet test tests/rtaime.Tests.Failure/rtaime.Tests.Failure.csproj --configuration Release
dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
```

Required Gates remain the final repository merge criterion.

## Evidence boundary

The retained evidence is software qualification.

The following remain **UNVERIFIED** until dedicated reference-platform or physical-I/O evidence exists:

- physical embedded-audio continuity;
- DMA/device-driver latency;
- external hardware buffer latency;
- hardware clock/genlock continuity;
- analog audio performance;
- certified loudness compliance;
- oversampled true-peak compliance;
- external console interoperability.

A green hosted test or Required Gates run must not be described as proof of any of those physical or standards-qualified properties.
