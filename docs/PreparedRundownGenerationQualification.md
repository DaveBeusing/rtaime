<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Prepared Rundown Decoder Generation Qualification

## Scope and source authority

This qualification covers the resident A/B local-media decoder during the existing
PREPARE → GO CUT/DISSOLVE sequence, and specifically the final MediaPlay action
after authoritative Program TAKE. It does not introduce a second decoder, alter
ControlHost authority, accept a stale preparation ticket or change the Frame/PCM
ownership, clock, GPU, recording or wire-contract boundaries.

Investigation base: `master`
`f22345f802c2c8c3515a0cc4ad068618d9d8798f` (2026-10-21);
pinned .NET SDK `10.0.401`, Windows x64. Final PR HEAD and exact-source
qualification evidence must be recorded in the PR, not assumed from this base.

## Historic failure and diagnostic boundary

[Issue #315](https://github.com/DaveBeusing/rtaime/issues/315) records
[Required Gates run 39510511166](https://github.com/DaveBeusing/rtaime/actions/runs/39510511166),
CI job `118930062322`, step **Resident next preparation and authoritative
handoff regressions**, source
`983105eef92f5cbe4bf3bdae617048e76b8a0ecd`.
The 50-fps CUT/`invalidate=none` test observed a post-GO decoder generation
of **2** after PREPARE recorded **1**. In the same targeted execution
the DISSOLVE/`invalidate=none` and invalidation variants passed.
The log does not include a stage-by-stage native worker event trace, so the
precise historical seek origin is not directly measured.

Source inspection identifies a concrete unauthorized replay path:

1. `LocalMediaDeckSlot.PrepareNext` seeks the resident worker to effective IN.
   `LocalMediaDecodeWorker.Reposition` increments the generation when an
   actual seek occurs. `WithPreparedNext` verifies that exact generation,
   source, asset, owner and guarded PreparedExecution before Runtime TAKE.
2. `ObserveProgramSource` can auto-start the committed source when Program
   changes. The regular authored cue list also sends **MediaPlay after TAKE**.
3. The transport controller treats `Play` on **Ended** as a new seek to frame
   zero, incrementing decode generation; `Play` on **Playing** rejects as an
   invalid transition. A one-second bundled 1080p50 reference clip makes the
   end-before-final-cue ordering plausible under a slow Windows CI runner.

The combination is a real duplicate-activation hazard. Its deterministic
regression proves the control-flow path, without claiming the historical
worker/runner trace itself has been recovered or that every generation change
in unrelated playback is erroneous.

## Corrective ownership and lifecycle

The resident slot now owns one `_preparedNextPlayPending` acknowledgement.
It becomes armed **only after** `WithPreparedNext` has verified current
evidence and its authorized Runtime commit reports success. PREPARE alone
cannot arm it, and a validation-only or rejected commit cannot arm it.

The first subsequent scoped MediaPlay:

- uses ordinary playback when autoplay has not yet started;
- acknowledges already **Playing** or **Ended** Program-edge autoplay without
  replaying, reopening or seeking the prepared owner;
- consumes that one-shot acknowledgement. A later manual `Play` on **Ended**
  again performs the ordinary explicit replay and increments generation;
- rejects invalid transport states normally and does not change authority,
  prepared-ticket, catalogue, revision, expiration or preroll checks.

Stop, seek, configuration changes, jumps, steps, close, replacement and a
subsequent observed exit from Program clear any pending cue acknowledgement and
preserve existing prepared-ticket invalidation semantics. Observations of the
old Program source before the prepared target first enters Program do not revoke
the acknowledgement armed by an authorized TAKE. Prepared playback's early-EOF guard remains active until a
deliberate later transport operation, and failed owner cleanup remains
quarantined. No retries, additional worker, polling or per-frame trace
were introduced.

## Deterministic regression matrix

`PreparedMediaHandoffIntegrationTests` now drives direct resident ownership
through preload, verified PREPARE, guarded TAKE, Program-edge autoplay and
final MediaPlay in two orderings: **while Playing** and **after normal OUT**.
Both must keep the same decoder generation and resource reservation.
The after-OUT variant then performs a distinct manual replay, which **must**
increment generation exactly once.

The bundled Windows 1080p50 H.264/AAC clip runs by default. Generated
1080p59.94 H.264/AAC variants run when the reference-media regression
environment is enabled. The generated 59.94-fps late-cue case uses an
explicitly authored short OUT at frame 5 so the successful HoldLastFrame
handoff is isolated from the separate full-duration native-EOF boundary.
Initial reference run `39651717289`, job `119401479172`, exposed an
`Error` instead of `Ended` when that generated six-second clip was driven
to native EOF; this is not treated as a passing EOF qualification and
requires separate source-bound diagnosis. The existing end-to-end Runtime/Control A → B
CUT/DISSOLVE integration retains its exact generation assertion and now
includes final-state/owner diagnostics on failure. Existing seek, cancel,
close, ticket, epoch, catalogue, authority, HOLD, monotonic-expiry,
audio immutability, early-EOF and worker-lifetime tests remain required.

### Targeted Windows tests

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --filter "FullyQualifiedName~PreparedMediaHandoffIntegrationTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --filter "FullyQualifiedName~MultiDeckLocalMediaIntegrationTests|FullyQualifiedName~AsynchronousLocalMediaDecodeTests"
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

All five Required Gates (`CI`, `Quality`, `Security`, `Provider Smoke`,
`Packaged E2E`) must be verified separately for the final PR HEAD. A queued,
running, cancelled, skipped, unreported or failed check is not PASS.
Native hardware behavior, physical A/V continuity and gapless output remain
**UNVERIFIED** without corresponding exact-source evidence.

Do not close Issue #315 or merge on the strength of a plausible diagnosis
alone. Confirm deterministic Windows regression PASS, stable CUT/DISSOLVE
through required Integration coverage, and source-bound Required Gates.
