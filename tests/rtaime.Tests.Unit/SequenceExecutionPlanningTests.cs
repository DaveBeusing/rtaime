// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control;
using rtaime.Control.Contracts;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SequenceExecutionPlanningTests
{
    private static readonly string Hash = new('A', 64);
    private static readonly Guid MediaA = Guid.Parse("c1000000-0000-0000-0000-000000000001");
    private static readonly Guid MediaB = Guid.Parse("c1000000-0000-0000-0000-000000000002");
    private static readonly Guid MediaC = Guid.Parse("c1000000-0000-0000-0000-000000000003");
    private static readonly Guid MediaD = Guid.Parse("c1000000-0000-0000-0000-000000000004");

    private static SequenceDefinition FourOwnerSequence(SequenceRate rate)
    {
        var f = SequenceTime.FromFrames(1, rate);
        var v1 = new SequenceTrack(Guid.Parse("c2000000-0000-0000-0000-000000000001"), "Video source", SequenceMediaKind.Video, 1);
        var v2 = new SequenceTrack(Guid.Parse("c2000000-0000-0000-0000-000000000002"), "Overlay video", SequenceMediaKind.Video, 2);
        var a1 = new SequenceTrack(Guid.Parse("c2000000-0000-0000-0000-000000000003"), "Program A", SequenceMediaKind.Audio, 3, AudioBusId: "program");
        var a2 = new SequenceTrack(Guid.Parse("c2000000-0000-0000-0000-000000000004"), "Program B", SequenceMediaKind.Audio, 4, AudioBusId: "program");
        SequenceClip Clip(string id, SequenceTrack track, Guid asset, long start, long length) =>
            new(Guid.Parse(id), track.TrackId, new(asset, track.Kind, Hash), 0, length * f, start * f, length * f);
        var from = Clip("c3000000-0000-0000-0000-000000000001", v1, MediaA, 0, 5);
        var to = Clip("c3000000-0000-0000-0000-000000000002", v1, MediaB, 3, 7);
        var overlay = Clip("c3000000-0000-0000-0000-000000000003", v2, MediaC, 0, 10);
        var matchingAudio = Clip("c3000000-0000-0000-0000-000000000004", a1, MediaA, 0, 10);
        var audio = Clip("c3000000-0000-0000-0000-000000000005", a2, MediaD, 0, 10);
        // The incoming video starts from its own source IN at timeline frame 3.
        return new(SequenceContract.Version,
            Guid.Parse("c4000000-0000-0000-0000-000000000001"), "Prepared sequence", rate,
            [v1, v2, a1, a2], [from, to, overlay, matchingAudio, audio],
            [new(Guid.Parse("c5000000-0000-0000-0000-000000000001"),
                from.ClipId, to.ClipId, SequenceTransitionKind.Dissolve, 3 * f, 2 * f)]);
    }

    private static SequenceAsset[] Catalogue(long duration = 2400000) =>
    [
        new(MediaA, SequenceMediaKind.Video, duration, Hash),
        new(MediaA, SequenceMediaKind.Audio, duration, Hash),
        new(MediaB, SequenceMediaKind.Video, duration, Hash),
        new(MediaC, SequenceMediaKind.Video, duration, Hash),
        new(MediaD, SequenceMediaKind.Audio, duration, Hash)
    ];

    private static SequenceExecutionPlan Prepare(SequenceDefinition definition, int capacity = 4)
    {
        var compilation = SequenceExecutionCompiler.Compile(definition, 7, Catalogue(), ["program"], capacity);
        Assert.True(compilation.Succeeded,
            string.Join("; ", compilation.Issues.Select(issue => issue.Code + ": " + issue.Message)));
        return Assert.IsType<SequenceExecutionPlan>(compilation.Plan);
    }

    [Theory]
    [InlineData(50, 1, 4800)]
    [InlineData(60000, 1001, 4004)]
    public void Overlapped_video_tracks_and_two_audio_tracks_select_exact_source_and_weights(
        int numerator, int denominator, int frameTicks)
    {
        var plan = Prepare(FourOwnerSequence(new(numerator, denominator)));
        Assert.Equal(7UL, plan.AuthoringRevision);
        Assert.Equal(4, plan.PeakDecoderOwners);
        Assert.Equal(5, plan.MaximumSimultaneousClips);
        Assert.Equal(10 * frameTicks, plan.DurationTicks);
        var samples = new SequenceRenderSample[5];
        Assert.True(plan.TrySelectAtFrame(3, samples, out var count));
        Assert.Equal(5, count);
        Assert.Equal(3L * frameTicks, plan.TickAtFrame(3));
        Assert.Equal(2, samples[..count].ToArray().Count(sample => sample.Kind == SequenceMediaKind.Audio));
        Assert.Equal(3, samples[..count].ToArray().Count(sample => sample.Kind == SequenceMediaKind.Video));
        var video = samples[..count].ToArray().Where(sample => sample.Kind == SequenceMediaKind.Video).ToArray();
        Assert.Equal(new[] { 1, 1, 2 }, video.Select(v => v.LayerOrder).Order().ToArray());
        Assert.Equal(byte.MaxValue, video.Single(v => v.AssetId == MediaA).Opacity);
        Assert.Equal((byte)0, video.Single(v => v.AssetId == MediaB).Opacity);
        Assert.Equal(0, video.Single(v => v.AssetId == MediaB).SourcePositionTicks);
        Assert.True(plan.TrySelectAtFrame(4, samples, out count));
        var atLastOverlap = samples[..count].ToArray();
        Assert.Equal((byte)128, atLastOverlap.Single(v => v.AssetId == MediaA && v.Kind == SequenceMediaKind.Video).Opacity);
        Assert.Equal((byte)127, atLastOverlap.Single(v => v.AssetId == MediaB).Opacity);
        Assert.True(plan.TrySelectAtFrame(5, samples, out count));
        Assert.Equal(4, count);
        Assert.DoesNotContain(samples[..count].ToArray(), sample => sample.AssetId == MediaA && sample.Kind == SequenceMediaKind.Video);
        Assert.Equal(byte.MaxValue, samples[..count].ToArray().Single(v => v.AssetId == MediaB).Opacity);
        Assert.False(plan.TrySelectAtFrame(10, samples, out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Rational_framerate_to_48k_sample_windows_does_not_accumulate_rounding()
    {
        var plan = Prepare(FourOwnerSequence(new(60000, 1001)));
        ulong cursor = 0;
        for (ulong frame = 0; frame < 10; frame++)
        {
            Assert.True(plan.TryGetAudioWindow(frame, out var window));
            Assert.Equal(cursor, window.FirstSample);
            Assert.InRange(window.SampleCount, 800u, 801u);
            cursor += window.SampleCount;
        }
        Assert.Equal(8008UL, cursor);
        Assert.False(plan.TryGetAudioWindow(10, out _));
    }

    [Fact]
    public void Audio_crossfade_at_sample_ticks_is_monotonic_and_sum_is_constant()
    {
        var source = FourOwnerSequence(new(50, 1));
        var baseAudio = source.Clips[^1];
        var inbound = baseAudio with
        {
            ClipId = Guid.NewGuid(),
            TimelineStart = 6 * 4800,
            SourceOut = 4 * 4800,
            Duration = 4 * 4800,
            Media = baseAudio.Media with { AssetId = MediaC }
        };
        // The existing Audio track is adjusted so its overlap is exactly two frames.
        var outbound = baseAudio with { SourceOut = 8 * 4800, Duration = 8 * 4800 };
        var changed = source with
        {
            Clips = source.Clips[..^1].Append(outbound).Append(inbound).ToArray(),
            Transitions = source.Transitions.Append(new SequenceTransition(Guid.NewGuid(), outbound.ClipId, inbound.ClipId,
                SequenceTransitionKind.Crossfade, 6 * 4800, 2 * 4800)).ToArray()
        };
        var catalog = Catalogue().Append(new SequenceAsset(MediaC, SequenceMediaKind.Audio, 2400000, Hash)).ToArray();
        // Four owners are not enough if the video overlap and audio overlap coincide.
        // This boundary must fail before trying to open the fifth worker.
        var denied = SequenceExecutionCompiler.Compile(changed, 7, catalog, ["program"], 4);
        Assert.False(denied.Succeeded);
        Assert.Contains(denied.Issues, issue => issue.Code == "sequence.execution.overbudget");

        // A separate two-audio-track profile can be safely evaluated.
        var audioOnly = changed with
        {
            Tracks = changed.Tracks.Where(t => t.Kind == SequenceMediaKind.Audio).ToArray(),
            Clips = changed.Clips.Where(c => changed.Tracks.Single(t => t.TrackId == c.TrackId).Kind == SequenceMediaKind.Audio).ToArray(),
            Transitions = changed.Transitions.Where(t => t.Kind == SequenceTransitionKind.Crossfade).ToArray()
        };
        var ready = SequenceExecutionCompiler.Compile(audioOnly, 7, catalog, ["program"], 3);
        Assert.True(ready.Succeeded, string.Join("; ", ready.Issues.Select(issue => issue.Message)));
        var plan = ready.Plan!;
        var overlap = plan.Windows.Single(w => w.StartTick == 6 * 4800);
        var outgoing = overlap.Clips.Single(c => c.ClipId == outbound.ClipId);
        var incoming = overlap.Clips.Single(c => c.ClipId == inbound.ClipId);
        for (long tick = 6 * 4800; tick < 8 * 4800; tick += 5)
        {
            var a = SequenceExecutionPlan.Weight(outgoing, tick);
            var b = SequenceExecutionPlan.Weight(incoming, tick);
            Assert.Equal(255, (int)a + b);
        }
        Assert.Equal((byte)255, SequenceExecutionPlan.Weight(outgoing, 6 * 4800));
        Assert.Equal((byte)0, SequenceExecutionPlan.Weight(incoming, 6 * 4800));
    }

    [Fact]
    public void Missing_or_relinked_assets_and_underbudget_profiles_fail_closed()
    {
        var source = FourOwnerSequence(new(50, 1));
        var missing = Catalogue().Where(a => a.AssetId != MediaB).ToArray();
        var rejected = SequenceExecutionCompiler.Compile(source, 7, missing, ["program"], 4);
        Assert.Null(rejected.Plan);
        Assert.Contains(rejected.Issues, issue => issue.Code == "sequence.asset_reference");
        rejected = SequenceExecutionCompiler.Compile(source, 7, Catalogue(), ["program"], 3);
        Assert.Null(rejected.Plan);
        Assert.Contains(rejected.Issues, issue => issue.Code == "sequence.execution.overbudget");
        Assert.Contains("No reservation", rejected.Issues.Single(issue => issue.Code == "sequence.execution.overbudget").Message);
        rejected = SequenceExecutionCompiler.Compile(source, 7, Catalogue(), ["program"], 0);
        Assert.Null(rejected.Plan);
        Assert.Contains(rejected.Issues, issue => issue.Code == "sequence.execution.profile");
    }

    [Fact]
    public void Changing_draft_does_not_modify_compiled_revision_or_prepared_intervals()
    {
        var source = FourOwnerSequence(new(50, 1));
        var plan = Prepare(source);
        var before = plan.Windows.Select(w => (w.StartTick, w.EndTick, w.DecoderOwners)).ToArray();
        source.Clips[0] = source.Clips[0] with { TimelineStart = 2 * 4800 };
        source.Tracks[0] = source.Tracks[0] with { Name = "Edited" };
        Assert.Equal(7UL, plan.AuthoringRevision);
        Assert.Equal(before, plan.Windows.Select(w => (w.StartTick, w.EndTick, w.DecoderOwners)).ToArray());
        var samples = new SequenceRenderSample[5];
        Assert.True(plan.TrySelectAtFrame(0, samples, out var count));
        Assert.Contains(samples[..count].ToArray(), s => s.AssetId == MediaA && s.Kind == SequenceMediaKind.Video);
        Assert.Throws<ArgumentException>(() =>
        {
            var tooSmall = new SequenceRenderSample[1];
            plan.TrySelectAtFrame(3, tooSmall, out _);
        });
    }

    [Fact]
    public void Gap_and_end_define_black_silence_without_selection_side_effect()
    {
        var source = FourOwnerSequence(new(50, 1));
        source.Clips[0] = source.Clips[0] with { Enabled = false };
        source.Clips[1] = source.Clips[1] with { Enabled = false };
        source.Clips[2] = source.Clips[2] with { Enabled = false };
        source.Clips[3] = source.Clips[3] with { Enabled = false };
        source.Clips[4] = source.Clips[4] with { TimelineStart = 3 * 4800 };
        var plan = Prepare(source);
        var samples = new SequenceRenderSample[1];
        Assert.True(plan.TrySelectAtTick(0, samples, out var count));
        Assert.Equal(0, count);
        Assert.True(plan.TrySelectAtTick(3 * 4800, samples, out count));
        Assert.Equal(1, count);
        Assert.False(plan.TrySelectAtTick(plan.DurationTicks, samples, out count));
    }
}
