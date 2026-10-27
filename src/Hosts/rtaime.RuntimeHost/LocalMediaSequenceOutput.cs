// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
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
        // At most one native loop repreroll is admitted per output session.
        // This is management work and never runs on the Program frame callback.
        internal Task? LoopPreroll { get; set; }
        internal bool EndReached { get; set; }
        internal ulong LoopReprerollCount { get; set; }
        internal TimeSpan LastLoopReprerollDuration { get; set; }
        internal TimeSpan LastComposeDuration { get; set; }
        internal TimeSpan MaximumComposeDuration { get; set; }
        internal ulong LateComposeBoundaries { get; set; }
    }


    /// <summary>Source-bound in-process qualification only; not receiver or physical I/O evidence.</summary>
    internal sealed record SequenceOutputPerformanceEvidence(
        string State, ulong PublishedBoundaries, ulong FailedBoundaries,
        ulong CompletedLoopPrerolls, TimeSpan LastLoopPreroll,
        TimeSpan LastBoundaryDuration, TimeSpan MaximumBoundaryDuration,
        ulong LateBoundaries, TimeSpan FrameBudget,
        int NativeOwners, int QueuedDecoderFrames, int MaximumDecoderQueueFrames,
        ulong DecoderStarvations, int ReservedOwners, long KnownManagedBytes,
        int QuarantinedOwners, long? NativeBytes, long? GpuBytes, string StoragePressure);

    internal SequenceOutputPerformanceEvidence? GetSequenceOutputPerformance(
        SequencePreparationReference reference)
    {
        reference.Validate();
        lock (_sequenceOutputGate)
        {
            var session = _sequenceOutput;
            if (session is null || session.Reference != reference) return null;
            LocalMediaDecodeSnapshot[] native;
            lock (_sequenceGate)
                native = _sequenceOwners is { } owners && owners.SequenceId == reference.SequenceId
                    ? owners.Owners.Select(owner => owner.Decode.Snapshot).ToArray()
                    : [];
            var capacity = _ledger.Snapshot;
            return new SequenceOutputPerformanceEvidence(session.OutputState,
                session.PublishedBoundaries, session.FailedBoundaries,
                session.LoopReprerollCount, session.LastLoopReprerollDuration,
                session.LastComposeDuration, session.MaximumComposeDuration,
                session.LateComposeBoundaries,
                TimeSpan.FromSeconds((double)session.Runtime.Format.FrameRate.Denominator /
                    session.Runtime.Format.FrameRate.Numerator),
                native.Length, native.Sum(snapshot => snapshot.QueuedFrames),
                native.Sum(snapshot => snapshot.Capacity),
                native.Aggregate(0UL, (sum, snapshot) => checked(sum + snapshot.Starvations)),
                capacity.ReservedOwners, capacity.KnownManagedBytes, capacity.QuarantinedOwners,
                capacity.NativeBytes, capacity.GpuBytes, capacity.StoragePressure);
        }
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
            if (session.EndReached)
            {
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }
            // A running native loop repreroll is never awaited by Program.
            if (session.LoopPreroll is { IsCompleted: false })
            {
                session.FailedBoundaries++;
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, "BUFFERING", "runtime.sequence.loop_preroll_pending",
                    session.LastPublishedSlot);
            }
            // An actual sink failure is terminal for this prepared owner.
            // A new native ingress cannot requalify a failed Program output
            // without an explicit cancellation and fresh preparation.
            if (session.OutputState is "FAILED" or "STALE")
            {
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }

            if (TryStartSequenceLoopRepreroll(session, programSlot))
            {
                session.FailedBoundaries++;
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }
            if (TryReachSequenceEnd(session, programSlot))
            {
                session.FailedBoundaries++;
                runtime.MarkSequenceInputUnavailable(session.SourceId);
                return (false, session.OutputState, session.FailureCode, session.LastPublishedSlot);
            }
            var count = 0;
            var ownerCount = 0;
            var composeStarted = Stopwatch.GetTimestamp();
            try
            {
                if (!TrySelectBoundSequenceBoundary(session.Reference, programSlot,
                    session.Clips, session.ClipRequests, session.OwnerRequests,
                    session.Frames, session.Indices,
                    out var position, out var sliceEndTick,
                    out count, out ownerCount, out var readFailure))
                {
                    // Real decoder exhaustion or a mismatched generation is
                    // terminal for this prepared source. Only an ordinary
                    // producer starvation may be retried at the next slot.
                    session.OutputState = IsTerminalSequenceFailure(readFailure?.Code)
                        ? "FAILED" : "BUFFERING";
                    session.FailureCode = readFailure?.Code;
                    session.FailedBoundaries++;
                    session.LastPreviewSlot = null;
                    session.LastProgramSlot = null;
                    session.ArmedTake = null;
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

                // The sample window belongs to authored source time, not the
                // absolute Program slot; 25/50/60000/1001 rates preserve their
                // exact 48-kHz cadence across Seek, Loop and non-frame cuts.
                var (audioPosition, sampleCount) =
                    SourcePcmWindow(position.Tick, runtime.Format.FrameRate);

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
                // The final authored slice can end mid-Program-frame. Silence
                // only its trailing source samples; never pad missing native
                // decoder audio and never shorten the Program PCM clock.
                if (audios > 0)
                    SilenceBeyondSourceSlice(slice, audioPosition, sampleCount, sliceEndTick);
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
                // Measure the complete admitted native selection + GPU/PCM
                // composition + Runtime ingress, including failed boundaries.
                session.LastComposeDuration = Stopwatch.GetElapsedTime(composeStarted);
                if (session.LastComposeDuration > session.MaximumComposeDuration)
                    session.MaximumComposeDuration = session.LastComposeDuration;
                var budget = TimeSpan.FromSeconds(
                    (double)runtime.Format.FrameRate.Denominator /
                    runtime.Format.FrameRate.Numerator);
                if (session.LastComposeDuration > budget)
                    session.LateComposeBoundaries++;
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
    /// The immutable Program position map advances into a new loop pass before
    /// the existing native decoder can be safely repositioned. The first
    /// boundary marks the old pass unavailable and schedules exactly one
    /// management-lane repreroll. Neither the native worker nor its cleanup
    /// ever executes under this Program boundary callback.
    /// Must be called with _sequenceOutputGate held.
    /// </summary>
    private bool TryReachSequenceEnd(SequenceOutputSession session, ulong programSlot)
    {
        lock (_sequenceGate)
        {
            var map = _sequenceShuttleMap;
            if (map is null || map.Loop ||
                map.State != SequenceRuntimeTransportState.Playing ||
                programSlot < map.AnchorProgramSlot ||
                map.TryMap(programSlot, out _))
                return false;
        }
        // End is a deliberately unavailable signal, not a fabricated held
        // native frame or an endlessly retrying decoder. A later explicit
        // Seek/Stop/Play must acquire a new generation before publication.
        session.EndReached = true;
        session.ArmedTake = null;
        session.LastPreviewSlot = null;
        session.LastProgramSlot = null;
        session.LastPublishedSlot = ulong.MaxValue;
        session.OutputState = "UNAVAILABLE";
        session.FailureCode = "runtime.sequence.end_reached";
        return true;
    }

    private bool TryStartSequenceLoopRepreroll(
        SequenceOutputSession session, ulong programSlot)
    {
        SequenceRuntimePositionMap? map;
        SequenceRuntimePosition next;
        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            map = _sequenceShuttleMap;
            if (prepared is null || map is null || !map.Loop ||
                map.State != SequenceRuntimeTransportState.Playing ||
                prepared.SequenceId != session.Reference.SequenceId ||
                prepared.AuthoringRevision != session.Reference.AuthoringRevision ||
                prepared.InitialGeneration != session.Reference.Generation ||
                !map.TryMap(programSlot, out next) ||
                next.Generation != prepared.Generation ||
                next.LoopPass <= prepared.LoopPass)
                return false;
        }

        session.ArmedTake = null;
        session.LastPreviewSlot = null;
        session.LastProgramSlot = null;
        session.LastPublishedSlot = ulong.MaxValue;
        session.OutputState = "BUFFERING";
        session.FailureCode = "runtime.sequence.loop_preroll_pending";
        // The asynchronous task captures only the original session and map.
        // Cancellation, replacement and authority changes are checked again
        // under the management owner before any native seek.
        session.LoopPreroll = Task.Run(() =>
            RunSequenceLoopRepreroll(session, map!, next));
        return true;
    }

    private void RunSequenceLoopRepreroll(SequenceOutputSession session,
        SequenceRuntimePositionMap map, SequenceRuntimePosition target)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            lock (_management)
            {
                lock (_sequenceOutputGate)
                    if (!ReferenceEquals(_sequenceOutput, session) ||
                        session.OutputState is "FAILED" or "STALE" ||
                        !session.Runtime.IsCommittedSequenceOutputSource(
                            session.SourceId, session.ProgramAuthorized, session.AuthorityRevision))
                        return;

                lock (_sequenceGate)
                {
                    if (!ReferenceEquals(_sequenceShuttleMap, map) ||
                        _sequenceOwners is not { } owners ||
                        owners.SequenceId != session.Reference.SequenceId ||
                        owners.Generation != target.Generation ||
                        owners.LoopPass >= target.LoopPass)
                        return; // Explicit Seek/Stop superseded this pending loop before native mutation.
                }
                // Reposition joins the old native worker generation; it retains
                // the same admitted decoder owners and resource-ledger charges.
                var receipt = RepositionSequenceMedia(
                    session.Reference.SequenceId, session.Reference.AuthoringRevision, target);
                if (!receipt.Prepared)
                    throw new IOException(receipt.Failure?.Code ??
                        "runtime.sequence.loop_preroll_failed");

                var slot = session.Runtime.NextSequenceOutputSlot;
                lock (_sequenceGate)
                {
                    if (!ReferenceEquals(_sequenceShuttleMap, map) ||
                        _sequenceOwners is not { } prepared ||
                        prepared.Generation != target.Generation ||
                        prepared.LoopPass != target.LoopPass)
                        throw new InvalidOperationException(
                            "Native loop owner changed before position publication.");
                    _sequenceShuttleMap = map.WithConfirmedLoopPrerollAnchor(slot, target);
                    _sequenceShuttleRevision = checked(_sequenceShuttleRevision + 1);
                }
                lock (_sequenceOutputGate)
                {
                    if (!ReferenceEquals(_sequenceOutput, session)) return;
                    session.LoopReprerollCount++;
                    session.LastLoopReprerollDuration = Stopwatch.GetElapsedTime(started);
                    session.FirstAuthorizedProgramSlot = slot;
                    session.OutputState = session.ProgramAuthorized
                        ? "TAKE_COMMITTED" : "UNAVAILABLE";
                    session.FailureCode = null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            ArgumentException or OverflowException or ObjectDisposedException)
        {
            lock (_sequenceOutputGate)
            {
                if (!ReferenceEquals(_sequenceOutput, session)) return;
                session.OutputState = "FAILED";
                session.FailureCode = "runtime.sequence.loop_preroll_failed";
                session.LastLoopReprerollDuration = Stopwatch.GetElapsedTime(started);
                session.Runtime.MarkSequenceInputUnavailable(session.SourceId);
            }
        }
        finally
        {
            lock (_sequenceOutputGate)
                if (ReferenceEquals(_sequenceOutput, session))
                    session.LoopPreroll = null;
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
    internal static bool IsTerminalSequenceFailure(string? code) => code is
        "runtime.sequence.ended" or
        "runtime.sequence.stale_sample" or
        "runtime.sequence.source_mismatch" or
        "runtime.sequence.boundary_missing" or
        "runtime.sequence.boundary_capacity" or
        "runtime.sequence.boundary_fence";

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
            session.EndReached = false;
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
                session.OutputState is "FAILED" or "STALE" or "BUFFERING" ||
                session.EndReached)
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

    /// <summary>
    /// One bounded native sequence PCM window at the original source's rational
    /// frame clock. The existing Program clock determines the presentation
    /// slot; this function never schedules one.
    /// </summary>
    internal static (ulong SamplePosition, uint SampleCount) SourcePcmWindow(
        long sequenceTick, FrameRate rate)
    {
        if (sequenceTick < 0 || rate.Numerator <= 0 || rate.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceTick));
        const int sourceRate = 240_000;
        const int authoredTicksPerPcmSample = 5;
        var nextTick = (Int128)sequenceTick +
            (Int128)sourceRate * rate.Denominator / rate.Numerator;
        var first = (Int128)sequenceTick / authoredTicksPerPcmSample;
        var end = nextTick / authoredTicksPerPcmSample;
        var count = end - first;
        if (first < 0 || first > ulong.MaxValue ||
            count < 1 || count > SequenceFrameComposition.MaximumAudioSamples)
            throw new InvalidOperationException(
                "Sequence frame rate requires a larger native PCM window than admitted.");
        return ((ulong)first, (uint)count);
    }

    /// <summary>
    /// Audio from a clip which ends inside this rational Program frame must not
    /// continue beyond its compiled source slice. No sample is synthesized to
    /// substitute an absent native PCM track.
    /// </summary>
    internal static int SilenceBeyondSourceSlice(Span<float> stereo,
        ulong sourceStartSample, uint frameSamples, long sliceEndTick)
    {
        if (sliceEndTick < 0 || frameSamples == 0 ||
            stereo.Length != checked((int)frameSamples * 2))
            throw new ArgumentException("Invalid bounded stereo source PCM slice.");
        var exclusiveEndSample = ((Int128)sliceEndTick + 4) / 5;
        var available = exclusiveEndSample - (Int128)sourceStartSample;
        if (available < 0) available = 0;
        if (available > frameSamples) available = frameSamples;
        var count = (int)frameSamples - (int)available;
        if (count > 0)
            stereo[((int)available * 2)..].Clear();
        return count;
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
