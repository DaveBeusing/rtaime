// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Bounded reference sequence source for the existing Runtime Preview/Program
/// graph. Control still owns source selection and Program Take. This component
/// never opens its own decoder, creates a clock or writes an output sink.
/// </summary>
public sealed partial class LocalMediaDeckRuntimeService
{
    private readonly object _sequenceOutputGate = new();
    private SequenceOutputSession? _sequenceOutput;
    internal const int MaximumSequenceOutputVideoLayers = 8;
    internal const int MaximumSequenceOutputAudioTracks = 8;

    private sealed class SequenceOutputSession
    {
        internal SequenceOutputSession(SequencePreparationReference reference,
            MediaSourceId sourceId, SequenceExecutionSnapshot snapshot,
            RuntimeProgramExecutionService runtime)
        {
            Reference = reference;
            Runtime = runtime;
            SourceId = sourceId;
            var audioClips = snapshot.Slices.SelectMany(slice => slice.Clips)
                .Where(clip => clip.Kind == SequenceMediaKind.Audio)
                .GroupBy(clip => clip.ClipId)
                .Select(group => group.First()).ToArray();
            if (audioClips.Length > MaximumSequenceOutputAudioTracks ||
                audioClips.Any(clip => clip.AudioBusId != AudioBusId.Program.Value) ||
                snapshot.Slices.Any(slice =>
                    slice.Clips.Count(clip => clip.Kind == SequenceMediaKind.Video) > MaximumSequenceOutputVideoLayers ||
                    slice.Clips.Count(clip => clip.Kind == SequenceMediaKind.Audio) > MaximumSequenceOutputAudioTracks ||
                    slice.Clips.Where(clip => clip.NativeSourceId is not null)
                        .Select(clip => clip.NativeSourceId).Distinct().Count() >
                        SequencePreparationContract.MaximumNativeOwners ||
                    slice.Clips.Any(clip => clip.Kind == SequenceMediaKind.Graphics)))
                throw new NotSupportedException(
                    "The admitted native playout supports at most eight video and eight audio layers; graphics require an output asset binding.");

            // The shared AudioProductionEngine requires a nonempty source
            // configuration. A video-only saved sequence has no decoded PCM
            // tracks; retain one silent, muted, never-supplied placeholder
            // channel rather than rejecting valid Preview video or inventing
            // synthetic audio samples.
            var sources = audioClips.Length == 0
                ? [new AudioProductionSourceConfiguration(
                    sourceId, 0, true, false, [AudioBusId.Program])]
                : audioClips.Select(clip =>
                    new AudioProductionSourceConfiguration(
                        new MediaSourceId(new Identity(clip.ClipId)), 1, false, false,
                        [AudioBusId.Program])).ToArray();
            var mixer = new AudioProductionEngine(new AudioProductionConfiguration(0,
                [new AudioProductionBusConfiguration(AudioBusId.Program, 1, false)],
                sources));
            Composer = new SequenceFrameComposition(runtime.SequenceCompositionGpu, mixer,
                runtime.Format, sourceId,
                MaximumSequenceOutputVideoLayers,
                MaximumSequenceOutputAudioTracks);
        }

        internal SequencePreparationReference Reference { get; }
        internal RuntimeProgramExecutionService Runtime { get; }
        internal MediaSourceId SourceId { get; }
        internal SequenceFrameComposition Composer { get; }
        internal SequenceExecutionClip[] Clips { get; } =
            new SequenceExecutionClip[MaximumSequenceClipSelections];
        internal SequenceClipFrameRequest[] ClipRequests { get; } =
            new SequenceClipFrameRequest[MaximumSequenceClipSelections];
        internal SequenceMediaFrameRequest[] OwnerRequests { get; } =
            new SequenceMediaFrameRequest[SequencePreparationContract.MaximumNativeOwners];
        internal SequenceMediaFrameLease?[] Frames { get; } =
            new SequenceMediaFrameLease?[SequencePreparationContract.MaximumNativeOwners];
        internal int[] Indices { get; } = new int[MaximumSequenceClipSelections];
        internal SequenceVideoCompositionLayer[] Video { get; } =
            new SequenceVideoCompositionLayer[MaximumSequenceOutputVideoLayers];
        internal SequenceAudioCompositionTrack[] Audio { get; } =
            new SequenceAudioCompositionTrack[MaximumSequenceOutputAudioTracks];
        internal float[] Pcm { get; } = new float[SequenceFrameComposition.MaximumAudioSamples * 2];
        internal ulong LastPublishedSlot { get; set; } = ulong.MaxValue;
        internal ulong? LastPreviewSlot { get; set; }
        internal ulong? LastProgramSlot { get; set; }
        // First Program slot allowed to confirm the committed Take. Frames
        // completed on Preview before the commit can be observed afterward.
        internal ulong FirstAuthorizedProgramSlot { get; set; } = ulong.MaxValue;
        internal ulong PublishedBoundaries { get; set; }
        internal ulong FailedBoundaries { get; set; }
        internal string OutputState { get; set; } = "UNAVAILABLE";
        internal string? FailureCode { get; set; }
        internal bool ProgramAuthorized { get; set; }
        internal SequenceOutputCommand? ArmedTake { get; set; }
        internal ulong AuthorityRevision { get; set; }
    }

    /// <summary>
    /// Activate a native sequence only on the currently committed Preview
    /// source, never on Program. A separate Control permission/transaction is
    /// mandatory before this internal method may be invoked.
    /// </summary>
    internal void BindSequencePreviewOutput(SequencePreparationReference reference,
        MediaSourceId previewSource, Identity authorityStateId, Revision authorityRevision,
        RuntimeProgramExecutionService runtime)
    {
        reference.Validate();
        ArgumentNullException.ThrowIfNull(runtime);
        SequenceExecutionSnapshot snapshot;
        lock (_management)
        {
            lock (_sequenceGate)
            {
                var prepared = _sequenceOwners;
                if (prepared is null || prepared.ExecutionSnapshot is null ||
                    prepared.SequenceId != reference.SequenceId ||
                    prepared.AuthoringRevision != reference.AuthoringRevision ||
                    prepared.InitialGeneration != reference.Generation ||
                    prepared.PreparedExecution.ToString() != reference.PreparedExecutionId ||
                    prepared.ProtocolVersion != reference.Version ||
                    prepared.AuthorityStateId != authorityStateId ||
                    prepared.AuthorityRevision != authorityRevision.Value)
                    throw new InvalidOperationException(
                        "Sequence output requires an exact current Control-authorized native preparation.");
                snapshot = prepared.ExecutionSnapshot;
            }
            if (!runtime.IsCommittedSequencePreviewSource(previewSource))
                throw new InvalidOperationException(
                    "Sequence output may only replace the exact committed Preview source, never Program.");
            if (GetSnapshot(previewSource).State != MediaDeckState.Unloaded)
                throw new InvalidOperationException(
                    "Sequence output cannot overwrite an already loaded native media deck.");
            var session = new SequenceOutputSession(reference, previewSource, snapshot, runtime)
            {
                AuthorityRevision = authorityRevision.Value
            };
            lock (_sequenceOutputGate)
            {
                if (_sequenceOutput is { } current)
                {
                    if (current.Reference != reference || current.SourceId != previewSource)
                        throw new InvalidOperationException(
                            "Cancel the prior sequence playout before activating another owner.");
                    return; // Exact observed activation is idempotent.
                }
                _sequenceOutput = session;
            }
        }
    }

    internal (bool Published, string State, string? FailureCode, ulong LastSlot)
        PresentSequenceBoundary(RuntimeProgramExecutionService runtime, ulong programSlot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null)
                return (false, "UNAVAILABLE", null, ulong.MaxValue);
            if (!runtime.IsCommittedSequenceOutputSource(
                session.SourceId, session.ProgramAuthorized, session.AuthorityRevision))
            {
                session.OutputState = "STALE";
                session.FailureCode = "runtime.sequence.route_changed";
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }
            // An actual sink failure is terminal for this prepared owner.
            // A new native ingress cannot requalify a failed Program output
            // without an explicit cancellation and fresh preparation.
            if (session.OutputState == "FAILED")
            {
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }

            var count = 0;
            var ownerCount = 0;
            try
            {
                if (!TrySelectBoundSequenceBoundary(session.Reference, programSlot,
                    session.Clips, session.ClipRequests, session.OwnerRequests,
                    session.Frames, session.Indices,
                    out var position, out count, out ownerCount, out var readFailure))
                {
                    session.OutputState = "BUFFERING";
                    session.FailureCode = readFailure?.Code;
                    session.FailedBoundaries++;
                    runtime.MarkSequenceInputUnavailable(session.SourceId);
                    return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
                }

                var videos = 0;
                var audios = 0;
                for (var index = 0; index < count; index++)
                {
                    var clip = session.Clips[index];
                    if (clip.Kind == SequenceMediaKind.Video)
                    {
                        if (videos >= session.Video.Length)
                            throw new InvalidOperationException("Sequence frame exceeded admitted video layer count.");
                        session.Video[videos++] = new SequenceVideoCompositionLayer(
                            clip.ClipId, session.Indices[index], clip.LayerOrder,
                            Weight(clip, position.Tick));
                    }
                    else if (clip.Kind == SequenceMediaKind.Audio)
                    {
                        if (audios >= session.Audio.Length)
                            throw new InvalidOperationException("Sequence frame exceeded admitted audio track count.");
                        session.Audio[audios++] = new SequenceAudioCompositionTrack(
                            clip.ClipId, session.Indices[index],
                            new MediaSourceId(new Identity(clip.ClipId)),
                            clip.TransitionStartTicks, clip.TransitionDurationTicks, clip.FadeIn);
                    }
                }

                var audioPosition = checked((ulong)(position.Tick / 5));
                // Decoder samples are positioned in the authored sequence
                // timeline, not the absolute Program frame number. A prepared
                // seek or Preroll reanchors the sequence at a later Program
                // slot without advancing the source PCM clock.
                var nextSourceTick = checked((long)((Int128)position.Tick +
                    (Int128)SequenceContract.TicksPerSecond *
                    runtime.Format.FrameRate.Denominator /
                    runtime.Format.FrameRate.Numerator));
                var sampleCount = checked((uint)(
                    nextSourceTick / 5 - position.Tick / 5));
                if (sampleCount == 0 || sampleCount > SequenceFrameComposition.MaximumAudioSamples)
                    throw new InvalidOperationException("Sequence Program PCM window exceeds bounded stereo sample capacity.");

                var slice = session.Pcm.AsSpan(0, checked((int)sampleCount * 2));
                var composed = session.Composer.TryCompose(this, session.Reference.SequenceId,
                    session.Reference.AuthoringRevision, position,
                    session.Frames.AsSpan(0, ownerCount), session.Video.AsSpan(0, videos),
                    session.Audio.AsSpan(0, audios),
                    new FrameTiming(programSlot, checked((long)programSlot),
                        new Timebase(runtime.Format.FrameRate.Denominator,
                            runtime.Format.FrameRate.Numerator)),
                    audioPosition, sampleCount, slice,
                    out var frame, out _, out var failure);
                if (!composed || frame is null)
                {
                    session.OutputState = "BUFFERING";
                    session.FailureCode = failure?.Code ?? "runtime.sequence.composition_unavailable";
                    session.FailedBoundaries++;
                    runtime.MarkSequenceInputUnavailable(session.SourceId);
                    return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
                }
                using (frame)
                    runtime.PresentSequenceInput(session.SourceId, frame, slice);

                session.LastPublishedSlot = programSlot;
                session.PublishedBoundaries++;
                // A previously observed physical ProgramBoundaryResult
                // remains source-bound evidence while the next composed
                // input travels through the same sink. Do not erase that
                // real confirmation on every 20 ms Preview/Program ingress;
                // buffering, stale authority and failure still invalidate it.
                session.OutputState = session.ProgramAuthorized
                    ? session.LastProgramSlot is null ? "TAKE_COMMITTED" : "PROGRAM_CONFIRMED"
                    : session.ArmedTake is not null ? "TAKE_PENDING"
                    : HasRecentConfirmedPreview(session) ? "PREVIEW_READY" : "UNAVAILABLE";
                session.FailureCode = null;
                return (true, session.OutputState, null, programSlot);
            }
            catch (Exception error) when (error is InvalidOperationException or
                ArgumentException or NotSupportedException or OverflowException)
            {
                session.FailedBoundaries++;
                session.OutputState = "FAILED";
                session.FailureCode = "runtime.sequence.output_" + error.GetType().Name;
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }
            finally
            {
                // Lease cleanup never depends on successful Program submission.
                for (var index = 0; index < session.Frames.Length; index++)
                {
                    session.Frames[index]?.Dispose();
                    session.Frames[index] = null;
                }
            }
        }
    }

    /// <summary>
    /// Native cleanup immediately withdraws the published source signal and
    /// clears the session before a future media owner can reuse its slot.
    /// Cancellation of a taken Program sequence must never leave a falsely
    /// healthy frozen frame or silently rebind the previous source.
    /// </summary>
    private void RetireSequenceOutput()
    {
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null) return;
            _sequenceOutput = null;
            try { session.Runtime.MarkSequenceInputUnavailable(session.SourceId); }
            catch (ObjectDisposedException) { } // A completed Runtime shutdown owns final teardown.
        }
    }

    // A previously confirmed Preview frame remains eligible while the next
    // source frame is still passing through the existing Program boundary.
    // A two-frame gap, source failure or stale generation is not Take-ready.
    private static bool HasRecentConfirmedPreview(SequenceOutputSession session) =>
        session.LastPreviewSlot is { } confirmed &&
        session.LastPublishedSlot >= confirmed &&
        session.LastPublishedSlot - confirmed <= 1;

    /// <summary>
    /// Arm a previously proven Preview sequence without touching committed
    /// Program. This owner reservation is checked again by Runtime's
    /// existing PreparedExecution commit under its single boundary gate.
    /// </summary>
    internal SequenceOutputObservation ArmSequenceTake(
        SequenceOutputCommand command, Identity authorityId, Revision revision,
        RuntimeProgramExecutionService runtime, string hostEpoch)
    {
        command.Validate();
        lock (_management)
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null || session.Reference != command.Preparation ||
                session.SourceId.ToString() != command.PreviewSourceId ||
                session.AuthorityRevision != command.ExpectedProductionRevision ||
                session.AuthorityRevision != revision.Value ||
                session.ProgramAuthorized ||
                session.OutputState is not ("PREVIEW_READY" or "TAKE_PENDING") ||
                !HasRecentConfirmedPreview(session) ||
                session.LastPublishedSlot + 1 < runtime.NextSequenceOutputSlot ||
                !runtime.IsCommittedSequencePreviewSource(session.SourceId))
                throw new InvalidOperationException("Only confirmed native Preview pixels and PCM may arm Program Take.");
            lock (_sequenceGate)
            {
                if (_sequenceOwners is not { } prepared ||
                    prepared.PreparedExecution.ToString() != command.Preparation.PreparedExecutionId ||
                    prepared.AuthorityStateId != authorityId ||
                    prepared.AuthorityRevision != revision.Value ||
                    prepared.Generation != _sequenceShuttleMap?.Generation)
                    throw new InvalidOperationException("Native Sequence Take owner/generation is no longer authoritative.");
            }

            if (session.ArmedTake is not null && session.ArmedTake != command)
                throw new InvalidOperationException("Another sequence Take is already armed.");
            session.ArmedTake = command;
            session.OutputState = "TAKE_PENDING";
            return GetSequenceOutput(command.Preparation, hostEpoch);
        }
    }

    internal void DisarmSequenceTake(SequenceOutputCommand command)
    {
        command.Validate();
        lock (_sequenceOutputGate)
        {
            if (_sequenceOutput is not { } session || session.Reference != command.Preparation ||
                session.ArmedTake != command || session.ProgramAuthorized)
                return;
            session.ArmedTake = null;
            session.OutputState = session.LastPreviewSlot == session.LastPublishedSlot
                ? "PREVIEW_READY" : "BUFFERING";
        }
    }

    /// <summary>
    /// A transport mutation invalidates previously sampled Preview evidence
    /// and any armed Take before native seek/repreroll can begin. This runs
    /// only on the management path; the existing Program cadence is not held
    /// across native worker operations.
    /// </summary>
    internal void InvalidateSequenceOutputOnShuttleMutation(SequencePreparationReference reference)
    {
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null || session.Reference != reference)
                return;
            // A terminal sink failure or stale authority cannot be
            // requalified by a transport command.
            if (session.OutputState is "FAILED" or "STALE")
            {
                session.Runtime.MarkSequenceInputUnavailable(session.SourceId);
                return;
            }
            session.ArmedTake = null;
            session.LastPreviewSlot = null;
            session.LastProgramSlot = null;
            session.LastPublishedSlot = ulong.MaxValue;
            session.OutputState = session.ProgramAuthorized
                ? "TAKE_COMMITTED" : "UNAVAILABLE";
            session.FailureCode = null;
            session.Runtime.MarkSequenceInputUnavailable(session.SourceId);
        }
    }

    /// <summary>
    /// A bound sequence source is reserved for explicit, source-fenced Take.
    /// A generic source CUT cannot accidentally activate staged decoder data.
    /// </summary>
    internal bool IsBoundSequenceProgramTarget(MediaSourceId? targetSource)
    {
        lock (_sequenceOutputGate)
            return targetSource is { } source &&
                _sequenceOutput is { ProgramAuthorized: false } current &&
                current.SourceId == source;
    }

    internal SequenceOutputCommand RequireArmedSequenceTake(
        PreparedExecutionContract candidate, RuntimeProgramTransitionIntent? transition)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput ?? throw new InvalidOperationException(
                "Program attempted to commit an unavailable sequence source.");
            var command = session.ArmedTake ?? throw new InvalidOperationException(
                "Sequence Program requires a separately armed, revision-bound Take.");
            if (session.ProgramAuthorized ||
                session.OutputState != "TAKE_PENDING" ||
                !HasRecentConfirmedPreview(session) ||
                candidate.AuthoritySnapshot.Revision.Value !=
                    checked(command.ExpectedProductionRevision + 1) ||
                transition?.Kind != RuntimeProgramTransitionKind.Cut ||
                transition.ToSourceId != session.SourceId)
                throw new InvalidOperationException(
                    "Program successor, transition or Preview source changed since Take was armed.");
            return command;
        }
    }

    /// <summary>
    /// Hold the native owner lifecycle and output-session fences across the
    /// existing Runtime transaction and native promotion. Without this scope
    /// Cancel/Seek could retire the armed workers after verification but
    /// before the Runtime Program commit, leaving ambiguous cross-host state.
    /// Program frame cadence itself is still owned by Runtime.
    /// </summary>
    internal RuntimeHostApplyResult CommitArmedSequenceTake(
        SequenceOutputCommand command, PreparedExecutionContract candidate,
        Func<RuntimeHostApplyResult> commit)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(commit);
        command.Validate();
        lock (_management)
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null ||
                session.Reference != command.Preparation ||
                session.ArmedTake != command ||
                session.ProgramAuthorized ||
                session.SourceId.ToString() != command.PreviewSourceId ||
                session.AuthorityRevision != command.ExpectedProductionRevision ||
                candidate.AuthoritySnapshot.Revision.Value !=
                    checked(command.ExpectedProductionRevision + 1))
                throw new InvalidOperationException(
                    "Sequence Take was invalidated before the Program transaction.");
            lock (_sequenceGate)
            {
                var prepared = _sequenceOwners;
                if (prepared is null ||
                    prepared.PreparedExecution.ToString() != command.Preparation.PreparedExecutionId ||
                    prepared.SequenceId != command.Preparation.SequenceId ||
                    prepared.AuthoringRevision != command.Preparation.AuthoringRevision ||
                    prepared.InitialGeneration != command.Preparation.Generation ||
                    prepared.AuthorityStateId != candidate.AuthoritySnapshot.StateId ||
                    prepared.AuthorityRevision != command.ExpectedProductionRevision ||
                    prepared.Generation != _sequenceShuttleMap?.Generation)
                    throw new InvalidOperationException(
                        "Prepared native owner or shuttle generation changed before Program commit.");
            }
            // AuthorizeSequenceProgram executes synchronously, under these
            // same re-entrant management/session locks, after Runtime Commit.
            return commit();
        }
    }

    /// <summary>
    /// A confirmed ControlHost production commit may promote the *same*
    /// prepared native owner into Program. No implicit take or new decoder
    /// reservation occurs. A stale or reordered revision is refused.
    /// </summary>
    internal void AuthorizeSequenceProgram(
        SequencePreparationReference reference,
        MediaSourceId sequenceSource,
        Identity stateId,
        Revision previousRevision,
        Revision committedRevision,
        RuntimeProgramExecutionService runtime)
    {
        reference.Validate();
        if (committedRevision.Value != checked(previousRevision.Value + 1) ||
            runtime.CommittedProgramSourceId != sequenceSource)
            throw new InvalidOperationException("Sequence Take has no exact committed Program successor.");

        lock (_management)
        {
            lock (_sequenceOutputGate)
            {
                var session = _sequenceOutput;
                if (session is null || session.Reference != reference ||
                    session.SourceId != sequenceSource)
                    throw new InvalidOperationException("Sequence Take owner identity changed.");
                if (session.ProgramAuthorized)
                {
                    // Recovery may observe this exact already-confirmed Take.
                    // Never commit Program again or increment its revision.
                    lock (_sequenceGate)
                    {
                        if (session.AuthorityRevision == committedRevision.Value &&
                            _sequenceOwners is { } confirmed &&
                            confirmed.AuthorityRevision == committedRevision.Value &&
                            confirmed.PreparedExecution.ToString() == reference.PreparedExecutionId)
                            return;
                    }
                    throw new InvalidOperationException("An unrelated sequence Take is already active.");
                }
                if (session.AuthorityRevision != previousRevision.Value)
                    throw new InvalidOperationException("Sequence Take authority is stale.");
                lock (_sequenceGate)
                {
                    var prepared = _sequenceOwners;
                    if (prepared is null || prepared.PreparedExecution.ToString() != reference.PreparedExecutionId ||
                        prepared.SequenceId != reference.SequenceId ||
                        prepared.AuthoringRevision != reference.AuthoringRevision ||
                        prepared.AuthorityStateId != stateId ||
                        prepared.AuthorityRevision != previousRevision.Value)
                        throw new InvalidOperationException("Sequence Take cannot rebind an unrelated native owner.");
                    _sequenceOwners = prepared with { AuthorityRevision = committedRevision.Value };
                }
                session.AuthorityRevision = committedRevision.Value;
                session.FirstAuthorizedProgramSlot = runtime.NextSequenceOutputSlot;
                session.ProgramAuthorized = true;
                session.OutputState = "TAKE_COMMITTED";
                session.FailureCode = null;
            }
        }
    }

    /// <summary>
    /// Only an actual completed ProgramBoundaryResult containing provider
    /// readback pixels and an emitted PCM payload can confirm this Take.
    /// </summary>
    internal void ObserveSequenceProgramBoundary(ProgramBoundaryResult boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null || session.LastPublishedSlot != boundary.SequenceNumber ||
                session.OutputState is "FAILED" or "STALE")
                return; // Late feedback cannot revive terminal owner/authority loss.
            if (session.ProgramAuthorized)
            {
                // The boundary may have finished before Take but be reported
                // after its authoritative commit. It cannot confirm or fail the
                // newly selected Program; only post-commit slots qualify.
                if (boundary.SequenceNumber < session.FirstAuthorizedProgramSlot)
                    return;
                if (boundary.CommittedProgramSourceId == session.SourceId &&
                    boundary.ProgramFrame.SourceId == session.SourceId &&
                    boundary.ProgramPixels.Length > 0 &&
                    boundary.ProgramAudioPayload.Length > 0)
                {
                    session.LastProgramSlot = boundary.SequenceNumber;
                    session.OutputState = "PROGRAM_CONFIRMED";
                    session.FailureCode = null;
                }
                else
                {
                    session.OutputState = "FAILED";
                    session.FailureCode = "runtime.sequence.program_sink_unconfirmed";
                    session.Runtime.MarkSequenceInputUnavailable(session.SourceId);
                }
            }
            else if (session.OutputState is "UNAVAILABLE" or "PREVIEW_READY" or "TAKE_PENDING")
            {
                // Native composition is only input readiness. Preview is
                // observable after the corresponding Runtime boundary completes.
                session.LastPreviewSlot = boundary.SequenceNumber;
                if (session.OutputState == "UNAVAILABLE")
                    session.OutputState = "PREVIEW_READY";
            }
        }
    }

    internal SequenceOutputObservation GetSequenceOutput(
        SequencePreparationReference reference,
        string runtimeHostInstanceId)
    {
        reference.Validate();
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null || session.Reference != reference ||
                reference.RuntimeHostInstanceId != runtimeHostInstanceId)
                throw new InvalidOperationException("No matching authoritative sequence output session exists.");
            var result = new SequenceOutputObservation(
                SequenceOutputContract.Version, reference, session.SourceId.ToString(),
                session.OutputState, session.PublishedBoundaries, session.FailedBoundaries,
                session.LastPublishedSlot == ulong.MaxValue ? null : session.LastPublishedSlot,
                session.LastProgramSlot, session.AuthorityRevision, session.FailureCode,
                session.OutputState switch
                {
                    "PROGRAM_CONFIRMED" => "Committed Program frame and PCM payload were observed at the existing output sink.",
                    "PREVIEW_READY" => "Decoded sequence pixels and PCM reached the current Preview source through the existing compositor.",
                    "TAKE_COMMITTED" => "Control committed Program routing; real sink frame/audio confirmation is pending.",
                    _ => "Sequence output is not fully qualified for the current Program or Preview boundary."
                });
            result.Validate();
            return result;
        }
    }

    private static byte Weight(SequenceExecutionClip clip, long tick)
    {
        if (clip.TransitionKind is null || clip.TransitionDurationTicks == 0 ||
            tick < clip.TransitionStartTicks ||
            (Int128)tick >= (Int128)clip.TransitionStartTicks + clip.TransitionDurationTicks)
            return byte.MaxValue;
        var elapsed = tick - clip.TransitionStartTicks;
        var incoming = checked((byte)(((Int128)elapsed * byte.MaxValue) /
            clip.TransitionDurationTicks));
        return clip.FadeIn ? incoming : checked((byte)(byte.MaxValue - incoming));
    }
}
