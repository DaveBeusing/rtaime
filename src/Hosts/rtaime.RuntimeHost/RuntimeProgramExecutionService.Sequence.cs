// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

/// <summary>
/// A sequence is a source in the existing Preview/Program graph, not another
/// Program compositor or clock. Only a Control-authorized Runtime sequence
/// playout may call this adapter on the current media boundary.
/// </summary>
public sealed partial class RuntimeProgramExecutionService
{
    internal GpuProcessingProvider SequenceCompositionGpu => _gpu;

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
