// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// A sequence is a source in the existing Preview/Program graph, not another
/// Program compositor or clock. Only a Control-authorized Runtime sequence
/// playout may call this adapter on the current media boundary.
/// </summary>
public sealed partial class RuntimeProgramExecutionService
{
    /// <summary>
    /// The one existing Program boundary mutex spans both the normal
    /// transactional Runtime Commit and promotion of the already armed
    /// native sequence source. No Program frame may interleave between
    /// those steps and no second scheduler or renderer is introduced.
    /// </summary>
    internal RuntimeHostApplyResult ApplyArmedSequenceTake(
        PreparedExecutionContract prepared, MediaSinkId programSink,
        RuntimeProgramTransitionIntent? transition, Action confirmNativeSource)
    {
        ArgumentNullException.ThrowIfNull(confirmNativeSource);
        lock (_boundaryExecutionGate)
        {
            var applied = ApplyExecution(prepared, programSink, transition);
            if (applied.Committed)
                confirmNativeSource();
            return applied;
        }
    }

    internal GpuProcessingProvider SequenceCompositionGpu => _gpu;
    internal ulong NextSequenceOutputSlot
    {
        get { lock (_gate) return _nextSequenceNumber; }
    }

    internal bool IsCommittedSequencePreviewSource(MediaSourceId sourceId) =>
        IsCommittedSequenceOutputSource(sourceId, allowProgram: false);

    internal bool IsCommittedSequenceOutputSource(MediaSourceId sourceId, bool allowProgram)
    {
        lock (_gate)
        {
            var active = _runtime.ActiveExecution?.PreparedExecution;
            if (active is null || !_backgrounds.ContainsKey(sourceId))
                return false;
            var preview = active.Bindings.SingleOrDefault(binding =>
                binding.OutputRoleId is null &&
                binding.MediaSourceId is not null &&
                binding.MediaSinkId is not null);
            var program = active.Bindings.SingleOrDefault(binding =>
                binding.MediaSinkId == _programSinkId);
            if (program is null || preview is null)
                return false;
            return (preview.MediaSourceId == sourceId &&
                    (allowProgram || program.MediaSourceId != sourceId)) ||
                   (allowProgram && program.MediaSourceId == sourceId);
        }
    }

    internal void PresentSequenceInput(
        MediaSourceId sourceId, GpuFrame composedVideo,
        ReadOnlySpan<float> composedStereo48k)
    {
        ArgumentNullException.ThrowIfNull(composedVideo);
        if (composedVideo.Descriptor.Surface.Format != _format ||
            composedStereo48k.Length == 0 ||
            composedStereo48k.Length > _maximumAudioValuesPerBoundary ||
            (composedStereo48k.Length & 1) != 0)
            throw new InvalidDataException("Sequence input must match the Program video and 48-kHz stereo window.");

        // The existing GPU provider owns the readback lease. The established
        // source buffers, audio production, Preview monitoring, transitions,
        // recording, replay, Aux and Program sinks remain authoritative.
        using var readback = _gpu.RentReadback(composedVideo);
        SetExternalInputContent(sourceId, readback.Memory.Span, RuntimeInputSignalState.Valid);
        SetExternalAudioInput(sourceId, composedStereo48k);
    }

    internal void MarkSequenceInputUnavailable(MediaSourceId sourceId)
    {
        SetInputSignalState(sourceId, RuntimeInputSignalState.Lost);
        ClearExternalAudioMeter(sourceId);
    }
}
