using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class AudioFollowVideoTests
{
    private static readonly MediaSourceId SourceA =
        new(Identity.Parse("92000000-0000-0000-0000-000000000001"));
    private static readonly MediaSourceId SourceB =
        new(Identity.Parse("92000000-0000-0000-0000-000000000002"));
    private static readonly AudioStreamId StreamA =
        new(Identity.Parse("92000000-0000-0000-0000-000000000003"));
    private static readonly AudioStreamId StreamB =
        new(Identity.Parse("92000000-0000-0000-0000-000000000004"));
    private static readonly Identity TimingDomain =
        Identity.Parse("92000000-0000-0000-0000-000000000005");

    [Fact]
    public void Fps50_maps_exactly_to_960_audio_samples_per_video_boundary()
    {
        for (ulong sequence = 0; sequence < 100; sequence++)
        {
            var window = AudioVideoTimingRelationship.GetSampleWindow(FrameRate.Fps50, 48_000, sequence);

            Assert.Equal(sequence * 960, window.SamplePosition);
            Assert.Equal((uint)960, window.SampleCount);
            Assert.Equal((long)(sequence * 960), window.PresentationTimestamp);
            Assert.Equal(new Timebase(1, 48_000), window.Timebase);
        }
    }

    [Fact]
    public void Fps5994_uses_exact_800_801_sample_cadence_without_rounding_to_fixed_count()
    {
        var counts = Enumerable.Range(0, 10)
            .Select(sequence => AudioVideoTimingRelationship.GetSampleWindow(
                FrameRate.Fps59_94,
                48_000,
                (ulong)sequence).SampleCount)
            .ToArray();

        Assert.Equal(new uint[] { 800, 801, 801, 801, 801, 800, 801, 801, 801, 801 }, counts);
        Assert.Equal((ulong)8008, AudioVideoTimingRelationship.GetSampleWindow(FrameRate.Fps59_94, 48_000, 10).SamplePosition);
    }

    [Fact]
    public void Cut_switches_followed_audio_on_the_same_video_boundary()
    {
        var engine = CreateEngine(FrameRate.Fps50);

        var first = engine.ProcessBoundary(SourceA, 0, Buffer(StreamA, FrameRate.Fps50, 0), 0.25);
        var cut = engine.ProcessBoundary(SourceB, 1, Buffer(StreamB, FrameRate.Fps50, 1), 0.75);

        Assert.True(first.Emitted);
        Assert.Equal(StreamA, first.StreamId);
        Assert.True(cut.Emitted);
        Assert.Equal(StreamB, cut.StreamId);
        Assert.Equal(SourceB, engine.ActiveVideoSourceId);
        Assert.Equal(StreamB, engine.ActiveStreamId);
        Assert.Equal((ulong)1, engine.Statistics.Switches);
        Assert.Contains(engine.Observations, observation =>
            observation.Code == "audio.afv.switched" && observation.VideoFrameSequence == 1);
    }


    [Fact]
    public void Breakaway_pins_audio_across_video_cuts_until_explicit_return_to_follow_video()
    {
        var engine = CreateEngine(FrameRate.Fps50);
        var routing = engine.SetRouting(AudioRoutingMode.Breakaway, SourceA);

        var first = engine.ProcessBoundary(SourceA, 0, Buffer(StreamA, FrameRate.Fps50, 0), 0.25);
        var cut = engine.ProcessBoundary(SourceB, 1, Buffer(StreamA, FrameRate.Fps50, 1), 0.75);

        Assert.Equal(AudioRoutingMode.Breakaway, routing.Mode);
        Assert.Equal(SourceA, cut.AudioSourceId);
        Assert.Equal(SourceB, cut.VideoSourceId);
        Assert.Equal(StreamA, cut.StreamId);
        Assert.Equal(StreamA, engine.ActiveStreamId);
        Assert.Equal(SourceA, engine.ActiveAudioSourceId);

        var follow = engine.SetRouting(AudioRoutingMode.FollowVideo);
        var returned = engine.ProcessBoundary(SourceB, 2, Buffer(StreamB, FrameRate.Fps50, 2), 0.5);

        Assert.Equal(AudioRoutingMode.FollowVideo, follow.Mode);
        Assert.Equal(SourceB, returned.AudioSourceId);
        Assert.Equal(StreamB, returned.StreamId);
        Assert.True(returned.RoutingRevision > cut.RoutingRevision);
    }

    [Fact]
    public void Breakaway_rejects_unknown_source_without_changing_confirmed_routing()
    {
        var engine = CreateEngine(FrameRate.Fps50);
        var initial = engine.RoutingState;

        Assert.Throws<KeyNotFoundException>(() =>
            engine.SetRouting(AudioRoutingMode.Breakaway, MediaSourceId.New()));

        Assert.Equal(initial, engine.RoutingState);
    }

    [Fact]
    public void Gain_and_mute_are_applied_per_input_without_changing_follow_policy()
    {
        var engine = CreateEngine(FrameRate.Fps50);
        engine.SetInputState(StreamA, new AudioGain(0.5), muted: false);

        var gained = engine.ProcessBoundary(SourceA, 0, Buffer(StreamA, FrameRate.Fps50, 0), 0.8);
        Assert.Equal(0.4, gained.PeakLevel, 6);
        Assert.Equal(0.5, gained.Gain.Linear, 6);
        Assert.False(gained.Muted);

        engine.SetInputState(StreamB, AudioGain.Unity, muted: true);
        var cutMuted = engine.ProcessBoundary(SourceB, 1, Buffer(StreamB, FrameRate.Fps50, 1), 0.9);
        Assert.True(cutMuted.Emitted);
        Assert.True(cutMuted.Muted);
        Assert.Equal(0, cutMuted.PeakLevel);
        Assert.Equal(StreamB, engine.ActiveStreamId);
    }

    [Fact]
    public void Stereo_meter_preserves_channels_applies_gain_and_reports_clipping()
    {
        var engine = CreateEngine(FrameRate.Fps50);
        engine.SetInputState(StreamA, new AudioGain(2.0), muted: false);

        var result = engine.ProcessBoundary(
            SourceA,
            0,
            Buffer(StreamA, FrameRate.Fps50, 0),
            new AudioStereoMeter(0.6, 0.3));

        Assert.True(result.Emitted);
        Assert.True(result.Clipping);
        Assert.Equal(1.0, result.LeftPeakLevel, 6);
        Assert.Equal(0.6, result.RightPeakLevel, 6);
        Assert.Equal(1.0, result.PeakLevel, 6);
        Assert.Contains(engine.Observations, observation => observation.Code == "audio.afv.clipping");
    }

    [Fact]
    public void Allocation_free_stereo_metering_measures_real_interleaved_samples()
    {
        var samples = new float[]
        {
            0.10f, -0.20f,
            -0.70f, 0.45f,
            0.30f, -0.90f
        };

        var meter = AudioMetering.MeasureInterleavedStereoFloat32(samples);

        Assert.Equal(0.70, meter.LeftPeakLevel, 5);
        Assert.Equal(0.90, meter.RightPeakLevel, 5);
        Assert.Equal(0.90, meter.PeakLevel, 5);
        Assert.Equal(new AudioStereoMeter(0, 0), AudioMetering.MeasureInterleavedStereoFloat32(ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void Sequence_mismatch_fails_closed_without_consuming_expected_boundary()
    {
        var engine = CreateEngine(FrameRate.Fps50);

        var rejected = engine.ProcessBoundary(SourceA, 1, Buffer(StreamA, FrameRate.Fps50, 1), 0.25);
        var recovered = engine.ProcessBoundary(SourceA, 0, Buffer(StreamA, FrameRate.Fps50, 0), 0.25);

        Assert.Equal(AudioFollowVideoStatus.RejectedSequence, rejected.Status);
        Assert.Equal("audio.afv.sequence_mismatch", rejected.Failure!.Value.Code);
        Assert.True(recovered.Emitted);
        Assert.Equal((ulong)1, engine.Statistics.Rejected);
        Assert.Equal((ulong)1, engine.Statistics.Boundaries);
    }

    [Fact]
    public void Buffer_from_non_followed_stream_is_rejected_for_that_boundary()
    {
        var engine = CreateEngine(FrameRate.Fps50);

        var result = engine.ProcessBoundary(SourceA, 0, Buffer(StreamB, FrameRate.Fps50, 0), 0.75);

        Assert.Equal(AudioFollowVideoStatus.RejectedBuffer, result.Status);
        Assert.Equal("audio.afv.stream_mismatch", result.Failure!.Value.Code);
        Assert.Equal(StreamA, engine.ActiveStreamId);
        Assert.Equal((ulong)1, engine.Statistics.Rejected);
    }

    [Fact]
    public void Incorrect_audio_timing_is_rejected_instead_of_silently_resampled()
    {
        var engine = CreateEngine(FrameRate.Fps59_94);
        var correct = Buffer(StreamA, FrameRate.Fps59_94, 0);
        var wrong = new AudioBufferDescriptor(
            correct.Version,
            correct.StreamId,
            correct.Format,
            correct.TimingDomainId,
            new AudioBufferTiming(
                correct.Timing.SamplePosition,
                correct.Timing.SampleCount + 1,
                correct.Timing.PresentationTimestamp,
                correct.Timing.Timebase),
            correct.Handle);

        var result = engine.ProcessBoundary(SourceA, 0, wrong, 0.25);

        Assert.Equal(AudioFollowVideoStatus.RejectedBuffer, result.Status);
        Assert.Equal("audio.afv.timing_mismatch", result.Failure!.Value.Code);
    }

    [Fact]
    public void Unmapped_video_source_is_rejected_without_redefining_active_audio()
    {
        var engine = CreateEngine(FrameRate.Fps50);
        var unknown = MediaSourceId.New();

        var result = engine.ProcessBoundary(unknown, 0, null, 0);

        Assert.Equal(AudioFollowVideoStatus.RejectedVideoSource, result.Status);
        Assert.Equal(SourceA, engine.ActiveVideoSourceId);
        Assert.Equal(StreamA, engine.ActiveStreamId);
        Assert.Equal("audio.afv.video_source_unmapped", result.Failure!.Value.Code);
    }

    [Fact]
    public void Duplicate_follow_mapping_is_rejected_at_configuration_time()
    {
        var first = Stream(StreamA, SourceA);
        var duplicate = Stream(StreamB, SourceA);

        Assert.Throws<ArgumentException>(() => new AudioFollowVideoEngine(
            new[] { first, duplicate },
            FrameRate.Fps50,
            SourceA));
    }

    [Fact]
    public void Gain_rejects_non_finite_negative_and_excessive_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioGain(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioGain(-0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioGain(4.01));
        Assert.Equal(1, AudioGain.Unity.Linear);
    }

    private static AudioFollowVideoEngine CreateEngine(FrameRate frameRate) => new(
        new[] { Stream(StreamA, SourceA), Stream(StreamB, SourceB) },
        frameRate,
        SourceA);

    private static AudioStreamDescriptor Stream(AudioStreamId streamId, MediaSourceId sourceId) => new(
        MediaContractVersion.Current,
        streamId,
        sourceId,
        AudioFormat.Stereo48kFloat32,
        TimingDomain);

    private static AudioBufferDescriptor Buffer(AudioStreamId streamId, FrameRate frameRate, ulong sequence)
    {
        var window = AudioVideoTimingRelationship.GetSampleWindow(frameRate, 48_000, sequence);
        return new AudioBufferDescriptor(
            MediaContractVersion.Current,
            streamId,
            AudioFormat.Stereo48kFloat32,
            TimingDomain,
            new AudioBufferTiming(
                window.SamplePosition,
                window.SampleCount,
                window.PresentationTimestamp,
                window.Timebase),
            new OpaqueAudioHandle("unit.audio", $"{streamId}:{sequence}"));
    }
}
