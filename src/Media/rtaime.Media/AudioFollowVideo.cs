using System.Collections.ObjectModel;
using System.Numerics;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Media;

public readonly record struct AudioSampleWindow(
    ulong SamplePosition,
    uint SampleCount,
    long PresentationTimestamp,
    Timebase Timebase);

public static class AudioVideoTimingRelationship
{
    public static AudioSampleWindow GetSampleWindow(
        FrameRate videoFrameRate,
        uint audioSampleRate,
        ulong videoFrameSequence)
    {
        if (audioSampleRate == 0)
            throw new ArgumentOutOfRangeException(nameof(audioSampleRate));

        var denominator = new BigInteger(videoFrameRate.Numerator);
        var rateFactor = new BigInteger(audioSampleRate) * videoFrameRate.Denominator;
        var start = new BigInteger(videoFrameSequence) * rateFactor / denominator;
        var end = (new BigInteger(videoFrameSequence) + BigInteger.One) * rateFactor / denominator;
        var count = end - start;

        if (start < BigInteger.Zero || start > ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(videoFrameSequence), "Audio sample position exceeds UInt64 range.");
        if (start > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(videoFrameSequence), "Audio presentation timestamp exceeds Int64 range.");
        if (count <= BigInteger.Zero || count > uint.MaxValue)
            throw new InvalidOperationException("Audio samples per video boundary exceed the supported UInt32 range.");

        return new AudioSampleWindow(
            (ulong)start,
            (uint)count,
            (long)start,
            new Timebase(1, audioSampleRate));
    }
}

public readonly record struct AudioGain
{
    public AudioGain(double linear)
    {
        if (!double.IsFinite(linear) || linear < 0 || linear > 4)
            throw new ArgumentOutOfRangeException(nameof(linear), "Audio gain must be finite and in the inclusive range 0..4.");

        Linear = linear;
    }

    public double Linear { get; }
    public static AudioGain Unity => new(1);
    public static AudioGain Silence => new(0);
}

public sealed record AudioInputState(AudioStreamId StreamId, AudioGain Gain, bool Muted);

public enum AudioFollowVideoStatus
{
    Emitted = 1,
    Underrun = 2,
    RejectedBuffer = 3,
    RejectedVideoSource = 4,
    RejectedSequence = 5
}

public sealed record AudioFollowVideoResult(
    AudioFollowVideoStatus Status,
    MediaSourceId VideoSourceId,
    AudioStreamId? StreamId,
    ulong VideoFrameSequence,
    ulong? SamplePosition,
    uint SampleCount,
    AudioGain Gain,
    bool Muted,
    double PeakLevel,
    Failure? Failure)
{
    public bool Emitted => Status == AudioFollowVideoStatus.Emitted;
}

public readonly record struct AudioFollowVideoStatistics(
    ulong Boundaries,
    ulong Switches,
    ulong Emitted,
    ulong Underruns,
    ulong Rejected,
    double LastPeakLevel);

public sealed record AudioFollowVideoObservation(
    ulong Ordinal,
    string Code,
    ulong? VideoFrameSequence,
    AudioStreamId? StreamId,
    Failure? Failure);

public sealed class AudioFollowVideoEngine
{
    private readonly object _gate = new();
    private readonly FrameRate _videoFrameRate;
    private readonly Dictionary<MediaSourceId, AudioStreamDescriptor> _streamByVideoSource;
    private readonly Dictionary<AudioStreamId, AudioInputState> _inputStateByStream;
    private readonly List<AudioFollowVideoObservation> _observations = new();

    private MediaSourceId _activeVideoSourceId;
    private AudioStreamId _activeStreamId;
    private ulong _nextVideoFrameSequence;
    private ulong _boundaries;
    private ulong _switches;
    private ulong _emitted;
    private ulong _underruns;
    private ulong _rejected;
    private double _lastPeakLevel;
    private ulong _observationOrdinal;

    public AudioFollowVideoEngine(
        IReadOnlyList<AudioStreamDescriptor> streams,
        FrameRate videoFrameRate,
        MediaSourceId initialVideoSourceId,
        ulong initialVideoFrameSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
            throw new ArgumentException("Audio Follow Video requires at least one audio stream.", nameof(streams));
        if (streams.Any(stream => stream is null))
            throw new ArgumentException("Audio stream descriptors must not contain null values.", nameof(streams));

        var streamIds = new HashSet<AudioStreamId>();
        var videoSourceIds = new HashSet<MediaSourceId>();
        foreach (var stream in streams)
        {
            if (!streamIds.Add(stream.StreamId))
                throw new ArgumentException("Audio stream identities must be unique.", nameof(streams));
            if (!videoSourceIds.Add(stream.FollowedVideoSourceId))
                throw new ArgumentException("Each video source may have only one FOLLOW_VIDEO audio stream in V1.", nameof(streams));
        }

        var referenceFormat = streams[0].Format;
        var referenceTimingDomain = streams[0].TimingDomainId;
        if (streams.Any(stream => stream.Format != referenceFormat))
            throw new ArgumentException("V1 Audio Follow Video requires a common audio format across followed inputs.", nameof(streams));
        if (streams.Any(stream => stream.TimingDomainId != referenceTimingDomain))
            throw new ArgumentException("V1 Audio Follow Video requires a common timing domain across followed inputs.", nameof(streams));

        _videoFrameRate = videoFrameRate;
        _streamByVideoSource = streams.ToDictionary(stream => stream.FollowedVideoSourceId);
        _inputStateByStream = streams.ToDictionary(
            stream => stream.StreamId,
            stream => new AudioInputState(stream.StreamId, AudioGain.Unity, false));

        if (!_streamByVideoSource.TryGetValue(initialVideoSourceId, out var initialStream))
            throw new ArgumentException("Initial video source has no FOLLOW_VIDEO audio stream.", nameof(initialVideoSourceId));

        _activeVideoSourceId = initialVideoSourceId;
        _activeStreamId = initialStream.StreamId;
        _nextVideoFrameSequence = initialVideoFrameSequence;
        Observe("audio.afv.initialized", initialVideoFrameSequence, _activeStreamId, null);
    }

    public MediaSourceId ActiveVideoSourceId
    {
        get
        {
            lock (_gate)
                return _activeVideoSourceId;
        }
    }

    public AudioStreamId ActiveStreamId
    {
        get
        {
            lock (_gate)
                return _activeStreamId;
        }
    }

    public AudioFollowVideoStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new AudioFollowVideoStatistics(
                    _boundaries,
                    _switches,
                    _emitted,
                    _underruns,
                    _rejected,
                    _lastPeakLevel);
            }
        }
    }

    public IReadOnlyList<AudioFollowVideoObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<AudioFollowVideoObservation>(_observations.ToArray());
        }
    }

    public AudioInputState GetInputState(AudioStreamId streamId)
    {
        lock (_gate)
        {
            if (!_inputStateByStream.TryGetValue(streamId, out var state))
                throw new KeyNotFoundException($"Unknown audio stream '{streamId}'.");

            return state;
        }
    }

    public void SetInputState(AudioStreamId streamId, AudioGain gain, bool muted)
    {
        lock (_gate)
        {
            if (!_inputStateByStream.ContainsKey(streamId))
                throw new KeyNotFoundException($"Unknown audio stream '{streamId}'.");

            _inputStateByStream[streamId] = new AudioInputState(streamId, gain, muted);
            Observe("audio.input.state_changed", null, streamId, null);
        }
    }

    public AudioFollowVideoResult ProcessBoundary(
        MediaSourceId committedVideoSourceId,
        ulong videoFrameSequence,
        AudioBufferDescriptor? buffer,
        double observedPeakLevel)
    {
        lock (_gate)
        {
            if (videoFrameSequence != _nextVideoFrameSequence)
            {
                _rejected++;
                var failure = new Failure(
                    "audio.afv.sequence_mismatch",
                    $"Expected video frame sequence '{_nextVideoFrameSequence}', received '{videoFrameSequence}'.");
                Observe("audio.afv.sequence_rejected", videoFrameSequence, null, failure);
                return Rejected(
                    AudioFollowVideoStatus.RejectedSequence,
                    committedVideoSourceId,
                    videoFrameSequence,
                    failure);
            }

            if (!_streamByVideoSource.TryGetValue(committedVideoSourceId, out var expectedStream))
            {
                _rejected++;
                var failure = new Failure(
                    "audio.afv.video_source_unmapped",
                    $"Video source '{committedVideoSourceId}' has no FOLLOW_VIDEO audio stream.");
                Observe("audio.afv.video_source_rejected", videoFrameSequence, null, failure);
                return Rejected(
                    AudioFollowVideoStatus.RejectedVideoSource,
                    committedVideoSourceId,
                    videoFrameSequence,
                    failure);
            }

            if (committedVideoSourceId != _activeVideoSourceId)
            {
                _activeVideoSourceId = committedVideoSourceId;
                _activeStreamId = expectedStream.StreamId;
                _switches++;
                Observe("audio.afv.switched", videoFrameSequence, _activeStreamId, null);
            }

            var inputState = _inputStateByStream[_activeStreamId];
            var expectedWindow = AudioVideoTimingRelationship.GetSampleWindow(
                _videoFrameRate,
                expectedStream.Format.SampleRate,
                videoFrameSequence);

            _boundaries++;
            AdvanceSequence();

            if (buffer is null)
            {
                _underruns++;
                _lastPeakLevel = 0;
                var failure = new Failure(
                    "audio.afv.underrun",
                    $"No audio buffer was available for video frame sequence '{videoFrameSequence}'.");
                Observe("audio.afv.underrun", videoFrameSequence, _activeStreamId, failure);
                return new AudioFollowVideoResult(
                    AudioFollowVideoStatus.Underrun,
                    committedVideoSourceId,
                    _activeStreamId,
                    videoFrameSequence,
                    expectedWindow.SamplePosition,
                    expectedWindow.SampleCount,
                    inputState.Gain,
                    inputState.Muted,
                    0,
                    failure);
            }

            var validationFailure = ValidateBuffer(expectedStream, expectedWindow, buffer);
            if (validationFailure is not null)
            {
                _rejected++;
                _lastPeakLevel = 0;
                Observe("audio.afv.buffer_rejected", videoFrameSequence, _activeStreamId, validationFailure);
                return new AudioFollowVideoResult(
                    AudioFollowVideoStatus.RejectedBuffer,
                    committedVideoSourceId,
                    _activeStreamId,
                    videoFrameSequence,
                    expectedWindow.SamplePosition,
                    expectedWindow.SampleCount,
                    inputState.Gain,
                    inputState.Muted,
                    0,
                    validationFailure);
            }

            if (!double.IsFinite(observedPeakLevel) || observedPeakLevel < 0 || observedPeakLevel > 1)
                throw new ArgumentOutOfRangeException(nameof(observedPeakLevel), "Observed peak level must be finite and in the inclusive range 0..1.");

            var effectivePeak = inputState.Muted
                ? 0
                : Math.Min(1, observedPeakLevel * inputState.Gain.Linear);

            _emitted++;
            _lastPeakLevel = effectivePeak;
            Observe("audio.afv.emitted", videoFrameSequence, _activeStreamId, null);

            return new AudioFollowVideoResult(
                AudioFollowVideoStatus.Emitted,
                committedVideoSourceId,
                _activeStreamId,
                videoFrameSequence,
                expectedWindow.SamplePosition,
                expectedWindow.SampleCount,
                inputState.Gain,
                inputState.Muted,
                effectivePeak,
                null);
        }
    }

    private static Failure? ValidateBuffer(
        AudioStreamDescriptor expectedStream,
        AudioSampleWindow expectedWindow,
        AudioBufferDescriptor buffer)
    {
        if (buffer.StreamId != expectedStream.StreamId)
            return new Failure("audio.afv.stream_mismatch", "Audio buffer does not belong to the active FOLLOW_VIDEO stream.");
        if (buffer.Format != expectedStream.Format)
            return new Failure("audio.afv.format_mismatch", "Audio buffer format does not match the active stream format.");
        if (buffer.TimingDomainId != expectedStream.TimingDomainId)
            return new Failure("audio.afv.timing_domain_mismatch", "Audio buffer timing domain does not match the active stream.");
        if (buffer.Timing.SamplePosition != expectedWindow.SamplePosition ||
            buffer.Timing.SampleCount != expectedWindow.SampleCount ||
            buffer.Timing.PresentationTimestamp != expectedWindow.PresentationTimestamp ||
            buffer.Timing.Timebase != expectedWindow.Timebase)
        {
            return new Failure(
                "audio.afv.timing_mismatch",
                "Audio buffer timing does not match the expected video/audio timing relationship.");
        }

        return null;
    }

    private AudioFollowVideoResult Rejected(
        AudioFollowVideoStatus status,
        MediaSourceId sourceId,
        ulong videoFrameSequence,
        Failure failure)
    {
        var state = _inputStateByStream[_activeStreamId];
        return new AudioFollowVideoResult(
            status,
            sourceId,
            null,
            videoFrameSequence,
            null,
            0,
            state.Gain,
            state.Muted,
            0,
            failure);
    }

    private void AdvanceSequence()
    {
        if (_nextVideoFrameSequence == ulong.MaxValue)
            throw new InvalidOperationException("Audio Follow Video sequence cannot advance beyond UInt64.MaxValue.");

        _nextVideoFrameSequence++;
    }

    private void Observe(
        string code,
        ulong? videoFrameSequence,
        AudioStreamId? streamId,
        Failure? failure) =>
        _observations.Add(new AudioFollowVideoObservation(
            _observationOrdinal++,
            code,
            videoFrameSequence,
            streamId,
            failure));
}
