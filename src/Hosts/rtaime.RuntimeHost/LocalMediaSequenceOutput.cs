// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime;

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
            SourceId = sourceId;
            var audioClips = snapshot.Slices.SelectMany(slice => slice.Clips)
                .Where(clip => clip.Kind == SequenceMediaKind.Audio)
                .GroupBy(clip => clip.ClipId)
                .Select(group => group.First()).ToArray();
            if (audioClips.Length > MaximumSequenceOutputAudioTracks ||
                snapshot.Slices.Any(slice =>
                    slice.Clips.Count(clip => clip.Kind == SequenceMediaKind.Video) > MaximumSequenceOutputVideoLayers ||
                    slice.Clips.Count(clip => clip.Kind == SequenceMediaKind.Audio) > MaximumSequenceOutputAudioTracks ||
                    slice.Clips.Any(clip => clip.Kind == SequenceMediaKind.Graphics)))
                throw new NotSupportedException(
                    "The admitted native playout supports at most eight video and eight audio layers; graphics require an output asset binding.");

            var sources = audioClips.Select(clip =>
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
        internal ulong PublishedBoundaries { get; set; }
        internal ulong FailedBoundaries { get; set; }
        internal string OutputState { get; set; } = "UNAVAILABLE";
        internal string? FailureCode { get; set; }
        internal bool ProgramAuthorized { get; set; }
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
                    "Sequence output may only replace the exact dedicated committed Preview source, never Program.");
            var session = new SequenceOutputSession(reference, previewSource, snapshot, runtime)
            {
                AuthorityRevision = authorityRevision.Value
            };
            lock (_sequenceOutputGate)
            {
                if (_sequenceOutput is { } current && current.Reference != reference)
                    throw new InvalidOperationException("Cancel the prior sequence playout before activating another owner.");
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
                session.SourceId, session.ProgramAuthorized))
            {
                session.OutputState = "STALE";
                session.FailureCode = "runtime.sequence.route_changed";
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
                var nextTick = checked((long)((Int128)(programSlot + 1) *
                    SequenceContract.TicksPerSecond * runtime.Format.FrameRate.Denominator /
                    runtime.Format.FrameRate.Numerator));
                var currentTick = checked((long)((Int128)programSlot *
                    SequenceContract.TicksPerSecond * runtime.Format.FrameRate.Denominator /
                    runtime.Format.FrameRate.Numerator));
                var sampleCount = checked((uint)(nextTick / 5 - currentTick / 5));
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
                session.OutputState = "PRESENTED";
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
