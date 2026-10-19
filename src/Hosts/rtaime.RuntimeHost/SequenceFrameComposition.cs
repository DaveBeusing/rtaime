// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// Ordering, opacity and lease index are compiled from an already validated
/// sequence revision. This internal description grants no production authority.
/// </summary>
internal readonly record struct SequenceVideoCompositionLayer(
    Guid ClipId, int OwnerIndex, int LayerOrder, byte Opacity);

/// <summary>
/// Logical audio track identity is distinct from the decoder owner: an audio
/// track may reuse the same native PCM lease as a video or another audio clip.
/// Transition times are 240000-tick authored sequence positions.
/// </summary>
internal readonly record struct SequenceAudioCompositionTrack(
    Guid ClipId, int OwnerIndex, MediaSourceId LogicalSourceId,
    long TransitionStartTicks = 0, long TransitionDurationTicks = 0, bool FadeIn = false);

/// <summary>
/// Isolated composition using the existing GPU provider and AudioProductionEngine.
/// Results are temporary evidence and cannot reach the Program sink without
/// a separately confirmed Control/Runtime execution binding. There is no new
/// clock, output, decoder admission or audio mixing algorithm here.
/// </summary>
internal sealed class SequenceFrameComposition
{
    internal const int MaximumAudioSamples = 2048;
    private const long SequenceTicksPerSecond = 240_000;
    private const long TicksPerAudioSample = 5;
    private readonly object _gate = new();
    private readonly GpuProcessingProvider _gpu;
    private readonly AudioProductionEngine _mixer;
    private readonly VideoFormat _format;
    private readonly MediaSourceId _outputSource;
    private readonly MediaSourceId _blackSource = MediaSourceId.New();
    private readonly RgbaFrameBuffer _black;
    private readonly RgbaFrameBuffer[] _videoScratch;
    private readonly float[][] _audioScratch;
    private readonly AudioProductionSourceBuffer[] _sourceBuffers;
    private readonly float[] _mixedAudio;

    internal SequenceFrameComposition(
        GpuProcessingProvider gpu, AudioProductionEngine mixer,
        VideoFormat format, MediaSourceId outputSource,
        int maximumVideoLayers = GpuCompositeLimits.MaxActiveLayers,
        int maximumAudioTracks = AudioProductionLimits.MaximumSources)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        if (format.PixelFormat != PixelFormat.Rgba8 ||
            maximumVideoLayers is < 1 or > GpuCompositeLimits.MaxActiveLayers ||
            maximumAudioTracks is < 1 or > AudioProductionLimits.MaximumSources ||
            outputSource.Value.IsEmpty)
            throw new ArgumentException("Sequence composition requires finite RGBA and audio profiles.");
        _format = format;
        _outputSource = outputSource;
        _black = RgbaFrameBuffer.Solid(format, 0, 0, 0);
        _videoScratch = Enumerable.Range(0, maximumVideoLayers)
            .Select(_ => RgbaFrameBuffer.Solid(format, 0, 0, 0, 0)).ToArray();
        _audioScratch = Enumerable.Range(0, maximumAudioTracks)
            .Select(_ => new float[MaximumAudioSamples * 2]).ToArray();
        _sourceBuffers = new AudioProductionSourceBuffer[maximumAudioTracks];
        _mixedAudio = new float[MaximumAudioSamples * 2];
    }

    /// <summary>
    /// Renders real decoded RGBA layers and mixes real 48-kHz Float32 stereo
    /// through the retained production-class GPU and audio implementations.
    /// The caller owns every native frame lease and the returned GPU frame.
    /// On rejection output audio is cleared and no GPU output escapes.
    /// An eventual Program sink must repeat the current-generation check.
    /// </summary>
    internal bool TryCompose(
        LocalMediaDeckRuntimeService decoder,
        Guid sequenceId, ulong authoringRevision, SequenceRuntimePosition position,
        ReadOnlySpan<SequenceMediaFrameLease?> nativeFrames,
        ReadOnlySpan<SequenceVideoCompositionLayer> videoLayers,
        ReadOnlySpan<SequenceAudioCompositionTrack> audioTracks,
        FrameTiming timing,
        ulong audioSamplePosition, uint audioSampleCount,
        Span<float> audioDestination,
        out GpuFrame? video,
        out AudioProductionBlockResult? audio,
        out Failure? failure)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        video = null;
        audio = null;
        failure = null;
        audioDestination.Clear();
        if (sequenceId == Guid.Empty || authoringRevision == 0 ||
            nativeFrames.Length is < 1 or > 4 ||
            videoLayers.Length > _videoScratch.Length ||
            audioTracks.Length > _audioScratch.Length ||
            audioSampleCount == 0 || audioSampleCount > MaximumAudioSamples ||
            audioDestination.Length != checked((int)audioSampleCount * 2) ||
            position.Tick < 0 || (Int128)audioSamplePosition * TicksPerAudioSample >
                (Int128)SequenceTicksPerSecond * 24 * 3600 ||
            (ulong)(position.Tick / TicksPerAudioSample) != audioSamplePosition)
        {
            failure = new("runtime.sequence.composition_bounds",
                "Sequence, layer capacity or audio sample window is not admitted.");
            return false;
        }

        lock (_gate)
        {
            for (var index = 0; index < nativeFrames.Length; index++)
            {
                var frame = nativeFrames[index];
                if (frame is null || frame.SequenceId != sequenceId ||
                    frame.AuthoringRevision != authoringRevision ||
                    frame.Position != position || !decoder.IsCurrentSequenceFrame(frame))
                {
                    failure = new("runtime.sequence.composition_stale",
                        "Native frame generation is no longer eligible for rendering.");
                    return false;
                }
            }

            var configured = _mixer.Configuration;
            if (!ValidateLayers(videoLayers, audioTracks, nativeFrames, configured,
                audioSampleCount, out failure))
                return false;

            // These are already-allocated scratch buffers. All nontrivial mixing
            // is delegated to the existing AudioProductionEngine.
            if (audioTracks.Length != 0)
            {
                for (var index = 0; index < audioTracks.Length; index++)
                {
                    var track = audioTracks[index];
                    var selected = nativeFrames[track.OwnerIndex]!.Frame;
                    var source = MemoryMarshal.Cast<byte, float>(selected.AudioPayload.Span);
                    var samples = _audioScratch[index].AsSpan(0, audioDestination.Length);
                    for (var sample = 0; sample < audioSampleCount; sample++)
                    {
                        var sampleTick = checked((long)((Int128)(audioSamplePosition + (ulong)sample) *
                            TicksPerAudioSample));
                        var weight = TransitionWeight(track, sampleTick) / 255f;
                        var offset = checked((int)sample * 2);
                        samples[offset] = source[offset] * weight;
                        samples[offset + 1] = source[offset + 1] * weight;
                    }
                    _sourceBuffers[index] = new(track.LogicalSourceId,
                        _audioScratch[index].AsMemory(0, audioDestination.Length));
                }
                audio = _mixer.ProcessBus(AudioBusId.Program,
                    audioSamplePosition, audioSampleCount,
                    audioTracks[0].LogicalSourceId,
                    _sourceBuffers.AsSpan(0, audioTracks.Length),
                    _mixedAudio.AsSpan(0, audioDestination.Length));
            }
            else
                _mixedAudio.AsSpan(0, audioDestination.Length).Clear();

            // Every GPU surface is temporary and disposed on rejection.
            // A failing or stopped GPU provider never publishes a partial output.
            GpuFrame? black = null;
            var uploaded = new GpuFrame[videoLayers.Length];
            GpuFrame? output = null;
            try
            {
                black = _gpu.Upload(_blackSource, _black, timing,
                    Generation.Initial, "sequence-background");
                var layers = new GpuKeyLayer[videoLayers.Length];
                for (var index = 0; index < videoLayers.Length; index++)
                {
                    var layer = videoLayers[index];
                    var frame = nativeFrames[layer.OwnerIndex]!.Frame;
                    var scratch = _videoScratch[index];
                    scratch.CopyPixelsFrom(frame.RgbaPixels.Span);
                    uploaded[index] = _gpu.Upload(frame.Video.SourceId, scratch,
                        timing, new Generation(position.Generation), "sequence-video");
                    layers[index] = new(uploaded[index], layer.Opacity, visible: true);
                }

                var render = _gpu.Composite(GpuCompositeRequest.WithLayers(
                    _outputSource, black, black, GpuTransition.CutToA, layers));
                if (!render.Succeeded || render.Frame is null)
                {
                    failure = render.Failure ?? new Failure("runtime.sequence.composition_gpu",
                        "The existing GPU compositor did not return a frame.");
                    return false;
                }
                output = render.Frame;

                // A native Seek, replacement, cancellation or Runtime recovery
                // during composition invalidates the whole group before output.
                foreach (var native in nativeFrames)
                {
                    if (decoder.IsCurrentSequenceFrame(native!)) continue;
                    failure = new("runtime.sequence.composition_stale",
                        "Sequence generation changed while the composite was being prepared.");
                    return false;
                }

                _mixedAudio.AsSpan(0, audioDestination.Length).CopyTo(audioDestination);
                video = output;
                output = null;
                failure = null;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or
                InvalidOperationException or NotSupportedException)
            {
                failure = new("runtime.sequence.composition_gpu",
                    "GPU composition failed without publishing output: " +
                    exception.GetType().Name);
                return false;
            }
            finally
            {
                if (video is null)
                    audio = null;
                output?.Dispose();
                foreach (var frame in uploaded)
                    frame?.Dispose();
                black?.Dispose();
            }
        }
    }

    private bool ValidateLayers(
        ReadOnlySpan<SequenceVideoCompositionLayer> videos,
        ReadOnlySpan<SequenceAudioCompositionTrack> audios,
        ReadOnlySpan<SequenceMediaFrameLease?> frames,
        AudioProductionConfiguration configuration,
        uint audioSampleCount, out Failure? failure)
    {
        for (var index = 0; index < videos.Length; index++)
        {
            var layer = videos[index];
            if (layer.ClipId == Guid.Empty || layer.OwnerIndex < 0 ||
                layer.OwnerIndex >= frames.Length ||
                (index > 0 && (layer.LayerOrder < videos[index - 1].LayerOrder ||
                  layer.LayerOrder == videos[index - 1].LayerOrder &&
                  layer.ClipId.CompareTo(videos[index - 1].ClipId) <= 0)) ||
                frames[layer.OwnerIndex]!.Frame.Video.Surface.Format != _format ||
                frames[layer.OwnerIndex]!.Frame.RgbaPixels.Length !=
                    RgbaFrameBuffer.RequiredByteLength(_format))
            {
                failure = new("runtime.sequence.composition_video",
                    "Video layers require a sorted, genuine, exact-format RGBA source.");
                return false;
            }
            for (var earlier = 0; earlier < index; earlier++)
                if (videos[earlier].ClipId == layer.ClipId)
                {
                    failure = new("runtime.sequence.composition_duplicate",
                        "Clip identities must be unique within a sequence frame.");
                    return false;
                }
        }

        for (var index = 0; index < audios.Length; index++)
        {
            var track = audios[index];
            if (track.ClipId == Guid.Empty || track.OwnerIndex < 0 ||
                track.OwnerIndex >= frames.Length || track.LogicalSourceId.Value.IsEmpty ||
                track.TransitionStartTicks < 0 || track.TransitionDurationTicks < 0 ||
                (track.TransitionDurationTicks > 0 &&
                    (Int128)track.TransitionStartTicks + track.TransitionDurationTicks >
                    SequenceTicksPerSecond * 24L * 3600) ||
                frames[track.OwnerIndex]!.Frame.Audio is not { } nativeAudio ||
                nativeAudio.Format != AudioFormat.Stereo48kFloat32 ||
                nativeAudio.Timing.SampleCount != audioSampleCount ||
                frames[track.OwnerIndex]!.Frame.AudioPayload.Length !=
                    checked((int)audioSampleCount * 2 * sizeof(float)) ||
                !configuration.Sources.Any(source =>
                    source.SourceId == track.LogicalSourceId &&
                    source.IsAssignedTo(AudioBusId.Program) && !source.FollowRoutedSource))
            {
                failure = new("runtime.sequence.composition_audio",
                    "Audio tracks require a genuine 48-kHz stereo source and a configured mixer channel.");
                return false;
            }
            for (var earlier = 0; earlier < index; earlier++)
                if (audios[earlier].ClipId == track.ClipId ||
                    audios[earlier].LogicalSourceId == track.LogicalSourceId)
                {
                    failure = new("runtime.sequence.composition_duplicate",
                        "Audio tracks require unique clip and mixer-source identities.");
                    return false;
                }
            for (var earlier = 0; earlier < videos.Length; earlier++)
                if (videos[earlier].ClipId == track.ClipId)
                {
                    failure = new("runtime.sequence.composition_duplicate",
                        "A single clip identity cannot be both Video and Audio.");
                    return false;
                }
        }

        failure = null;
        return true;
    }

    internal static byte TransitionWeight(SequenceAudioCompositionTrack track, long tick)
    {
        if (track.TransitionDurationTicks == 0 ||
            tick < track.TransitionStartTicks ||
            (Int128)tick >= (Int128)track.TransitionStartTicks + track.TransitionDurationTicks)
            return byte.MaxValue;
        var elapsed = tick - track.TransitionStartTicks;
        var incoming = (byte)(((Int128)elapsed * byte.MaxValue) /
            track.TransitionDurationTicks);
        return track.FadeIn ? incoming : (byte)(byte.MaxValue - incoming);
    }
}
