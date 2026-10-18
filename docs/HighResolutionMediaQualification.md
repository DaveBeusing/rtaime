# High-resolution qualification and evidence plan

Status: **BLOCKED** for UHD output implementation; physical qualification
**UNVERIFIED**. See [approval record](HighResolutionMediaDecision.md) and
[source inventory](HighResolutionMediaCompatibility.md). This plan adds no
supported UHD profile or test result.

## Current deterministic guard coverage

`HighResolutionOutputAdmissionTests` exercises both existing HD tuples and
all three proposed UHD rates across Runtime validation, virtual timing admission,
GPU advertised capabilities and recording input-format lists. It verifies
command-line/environment rejection, exact color identity and the separation of
UHD input policy from Program output. Frame-size checks use arithmetic only;
they allocate no UHD pixel buffer and prove no throughput.

Required Gates runs this subset after the canonical Release build, before broad
suites. The full solution, ownership/architecture/failure/performance suites,
Provider Smoke and Packaged E2E retain their existing thresholds. Final revision
results and retained run links belong in the corresponding PR; older green
results do not qualify a new revision.

## Future deterministic native-resolution references

After scope approval, add separate, explicitly named 3840×2160 SDR25, SDR50 and
SDR60000/1001 references to the existing reference-media generator/manifest path.
Generate at native resolution, without upscaling an HD or 640×360 canvas.
Use fixed seeds, documented generator/version/command, exact rational PTS,
known 48-kHz audio windows and stable expected frame counts.

Include one-pixel checker/edge patterns, corner coordinates, ramps, limited/full
range checks, color bars, transparent edges only where alpha is separately
approved, sequence markers and audio channel/sample markers. Record source hash,
dimensions, rate, color metadata, codec/profile/bitrate, audio layout, source
generation and expected output transform. Keep input file identity distinct from
Program resolution and displayed monitoring proxy identity.

Decode representative sources through the actual selected provider. Verify
post-composite output size, pixel/color tolerances, sequence/PTS/audio continuity,
end/seek/loop behavior and recording/replay reopen. Test negative format/provider
negotiation without touching committed Program. Cover A/B prepared handoff,
stale revision/generation, decoder timeout/failure, overload, shutdown and device
recovery while retaining existing HD pixel/authority regressions.
The present [reference-media corpus](ReferenceMediaRegression.md) is HD; passing
its manifest or Main10-to-RGBA8 cases is not UHD/native-10-bit evidence.

## Physical acceptance sequence

1. Establish exact-source HD stability first on the intended reference machine.
   Existing opt-in workflows are `cuda-reference-qualification.yml`
   (`rtaime-cuda-reference`, expected device name), `ssd-playback-qualification.yml`
   (`rtaime-ssd`, real A/B files, format and elapsed-duration profile), and
   `media-io-reference-qualification.yml` (`rtaime-media-io-reference`, expected AJA
   adapter, pinned SDK, format and external-reference requirement).
   They currently qualify HD formats; none is a runnable UHD acceptance procedure.
2. Accept the profile-specific GPU/transfer and hardware/codec budgets. Extend
   those same qualification mechanisms for the approved UHD tuple before
   dispatch; do not pass invented UHD options to existing scripts.
3. Retain machine/OS/CPU/RAM/GPU/VRAM/driver/decoder identity, SSD model/interface/
   filesystem/free space, source hashes and I/O/receiver/encoder configuration.
   Use matched HD/UHD runs at exact source revision with concurrent A/B,
   effects, monitoring, recording/replay and each approved output consumer.
4. Measure real elapsed Program deadlines and missed/late slots, P50/P95/P99/max
   stages, A/V drift, decoded/output PTS, stalls/underruns, per-provider transfers,
   upload/readback bytes and durations, host/private/pinned/device high water,
   pool/lease occupancy, SSD queue/read latency and writer/receiver drops.
   Include cold preparation, warm steady state, sustained load and recovery.
5. Predeclare per-profile latency/resource/soak limits on that hardware; preserve
   existing HD acceptance and negative tests. Fail unsupported fallback,
   unbounded growth, ownership leaks and concealed Program gaps. Software run
   completion is not timing acceptance. Retain failed evidence and reason codes.
6. Bind raw results, summaries, reference manifests and artifact digests to the
   tested SHA/run/attempt with the existing qualification binding/verifier path.
   Product Owner accepts only the exact proven consumer/profile set. Native
   10-bit/HDR/Fill-Key requires another approval and signal-analysis procedure.

No identified physical machine/device/media set was provided for this design
change. Consequently no physical run or measured UHD memory/latency improvement
is claimed. An approved profile, physical HD stability and accepted transfer
budget are the next dependencies; defaults remain HD.
