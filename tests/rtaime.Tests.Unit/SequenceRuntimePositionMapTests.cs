// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Runtime;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SequenceRuntimePositionMapTests
{
    private static readonly Guid SequenceId = Guid.Parse("c6000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(25, 1, 9600)]
    [InlineData(50, 1, 4800)]
    [InlineData(60000, 1001, 4004)]
    public void Existing_program_slots_define_rational_sequence_positions_without_a_second_timer(
        long numerator, long denominator, long frameTicks)
    {
        var prepared = SequenceRuntimePositionMap.Prepare(SequenceId, 5,
            new FrameRate(numerator, denominator), 10 * frameTicks, 100, 1);
        Assert.Equal(SequenceRuntimeTransportState.Stopped, prepared.State);
        Assert.False(prepared.TryMap(100, out _));
        var playing = prepared.Play(100);
        for (ulong index = 0; index < 10; index++)
        {
            Assert.True(playing.TryMap(100 + index, out var position));
            Assert.Equal((long)index * frameTicks, position.Tick);
            Assert.Equal(2UL, position.Generation);
            Assert.Equal(0UL, position.LoopPass);
        }
        Assert.False(playing.TryMap(110, out _));
        Assert.False(playing.TryMap(99, out _));
    }

    [Fact]
    public void Seek_pause_resume_and_stop_reanchor_and_fence_old_frame_samples()
    {
        var map = SequenceRuntimePositionMap.Prepare(SequenceId, 3, FrameRate.Fps50, 48000, 100, 9).Play(100);
        Assert.True(map.TryMap(102, out var initial));
        Assert.Equal(9600, initial.Tick);
        var paused = map.Pause(102);
        Assert.Equal(SequenceRuntimeTransportState.Paused, paused.State);
        Assert.Equal(11UL, paused.Generation);
        Assert.True(paused.TryMap(10000, out var held));
        Assert.Equal(initial.Tick, held.Tick);
        Assert.NotEqual(initial.Generation, held.Generation);
        var sought = paused.Seek(10001, 14400);
        Assert.Equal(12UL, sought.Generation);
        Assert.True(sought.TryMap(10001, out var position));
        Assert.Equal(14400, position.Tick);
        var resumed = sought.Play(10002);
        Assert.Equal(13UL, resumed.Generation);
        Assert.True(resumed.TryMap(10003, out position));
        Assert.Equal(19200, position.Tick);
        var stopped = resumed.Stop(10004);
        Assert.Equal(SequenceRuntimeTransportState.Stopped, stopped.State);
        Assert.False(stopped.TryMap(10005, out _));
        var restarted = stopped.Play(10005);
        Assert.True(restarted.TryMap(10005, out position));
        Assert.Equal(0, position.Tick);
        Assert.Equal(15UL, position.Generation);
    }

    [Fact]
    public void Every_loop_pass_has_a_distinct_queue_fence_without_resetting_program_slot()
    {
        var map = SequenceRuntimePositionMap.Prepare(SequenceId, 7,
            FrameRate.Fps59_94, 5 * 4004, 300, 3).Play(300).SetLoop(300, true);
        Assert.True(map.TryMap(304, out var last));
        Assert.Equal(16016, last.Tick);
        Assert.Equal(0UL, last.LoopPass);
        Assert.True(map.TryMap(305, out var first));
        Assert.Equal(0, first.Tick);
        Assert.Equal(1UL, first.LoopPass);
        Assert.Equal(last.Generation, first.Generation);
        Assert.NotEqual((last.Generation, last.LoopPass), (first.Generation, first.LoopPass));
        Assert.True(map.TryMap(310, out var nextPass));
        Assert.Equal(2UL, nextPass.LoopPass);
        var stopped = map.Stop(310);
        Assert.False(stopped.TryMap(311, out _));
        Assert.Equal(map.Generation + 1, stopped.Generation);
    }

    [Theory]
    [InlineData(25, 1, 9600)]
    [InlineData(50, 1, 4800)]
    [InlineData(60000, 1001, 4004)]
    public void Native_loop_preroll_reanchors_to_existing_clock_without_reusing_old_pass(
        long numerator, long denominator, long frameTicks)
    {
        var rate = new FrameRate(numerator, denominator);
        var map = SequenceRuntimePositionMap.Prepare(
            SequenceId, 7, rate, 5 * frameTicks, 100, 3).Play(100).SetLoop(100, true);
        Assert.True(map.TryMap(105, out var firstNewPass));
        Assert.Equal(1UL, firstNewPass.LoopPass);
        Assert.Equal(0, firstNewPass.Tick);
        // Repreroll is performed outside Program frame cadence. Three
        // Program slots pass while native workers are resetting.
        var confirmed = map.WithConfirmedLoopPrerollAnchor(108, firstNewPass);
        Assert.Equal(map.Generation, confirmed.Generation);
        Assert.Equal(1UL, confirmed.LoopPassBase);
        Assert.Equal(108UL, confirmed.AnchorProgramSlot);
        Assert.True(confirmed.TryMap(108, out var first));
        Assert.Equal(firstNewPass, first);
        Assert.True(confirmed.TryMap(112, out var last));
        Assert.Equal(4 * frameTicks, last.Tick);
        Assert.Equal(1UL, last.LoopPass);
        Assert.True(confirmed.TryMap(113, out var secondPass));
        Assert.Equal(2UL, secondPass.LoopPass);
        Assert.Equal(0, secondPass.Tick);
        Assert.Throws<InvalidOperationException>(() =>
            confirmed.WithConfirmedLoopPrerollAnchor(109, firstNewPass));
        var sought = confirmed.Seek(109, frameTicks);
        Assert.Equal(0UL, sought.LoopPassBase);
        Assert.True(sought.TryMap(109, out var newGeneration));
        Assert.Equal(0UL, newGeneration.LoopPass);
        Assert.NotEqual(firstNewPass.Generation, newGeneration.Generation);
    }

    [Fact]
    public void Enabling_loop_after_finished_playback_cannot_restart_media_implicitly()
    {
        var map = SequenceRuntimePositionMap.Prepare(SequenceId, 4,
            FrameRate.Fps50, 2 * 4800, 20, 7).Play(20);
        Assert.False(map.TryMap(22, out _));
        Assert.Throws<InvalidOperationException>(() => map.SetLoop(22, true));
        var stopped = map.Stop(22);
        var loop = stopped.SetLoop(22, true).Play(23);
        Assert.True(loop.TryMap(23, out var selected));
        Assert.Equal(0, selected.Tick);
        Assert.True(loop.TryMap(25, out selected));
        Assert.Equal(1UL, selected.LoopPass);
    }

    [Fact]
    public void Illegal_rate_revision_seek_and_backward_program_slot_fail_closed()
    {
        Assert.Throws<ArgumentException>(() =>
            SequenceRuntimePositionMap.Prepare(Guid.Empty, 1, FrameRate.Fps50, 40000, 0, 1));
        Assert.Throws<ArgumentException>(() =>
            SequenceRuntimePositionMap.Prepare(SequenceId, 0, FrameRate.Fps50, 40000, 0, 1));
        Assert.Throws<ArgumentException>(() =>
            SequenceRuntimePositionMap.Prepare(SequenceId, 1, new FrameRate(1000, 1), 40000, 0, 1));
        var prepared = SequenceRuntimePositionMap.Prepare(SequenceId, 1, FrameRate.Fps50, 40000, 500, 1).Play(500);
        Assert.Throws<ArgumentOutOfRangeException>(() => prepared.Seek(501, 40000));
        Assert.Throws<ArgumentOutOfRangeException>(() => prepared.Seek(501, -1));
        Assert.Throws<ArgumentException>(() => prepared.Seek(499, 0));
        Assert.Throws<ArgumentException>(() => prepared.Stop(499));
    }

    [Theory]
    [InlineData(25, 1, 9600)]
    [InlineData(50, 1, 4800)]
    [InlineData(60000, 1001, 4004)]
    public void Prepared_native_cue_step_and_clock_reanchor_retain_exact_rational_generation(
        long numerator, long denominator, long frameTicks)
    {
        var map = SequenceRuntimePositionMap.Prepare(SequenceId, 12,
            new FrameRate(numerator, denominator), frameTicks * 20, 400, 2);
        var cued = map.Cue(401, 3 * frameTicks);
        Assert.Equal(SequenceRuntimeTransportState.Paused, cued.State);
        Assert.Equal(3UL, cued.Generation);
        Assert.True(cued.TryMap(402, out var held));
        Assert.Equal(3L * frameTicks, held.Tick);
        var backward = cued.Step(402, -1);
        Assert.Equal(2L * frameTicks, backward.StartTick);
        var forward = backward.Step(403, 1);
        Assert.Equal(3L * frameTicks, forward.StartTick);
        var playing = forward.Play(404);
        Assert.True(playing.TryMap(405, out var frameBeforePreroll));
        Assert.Equal(4L * frameTicks, frameBeforePreroll.Tick);
        var confirmed = playing.WithConfirmedPrerollAnchor(420);
        Assert.Equal(playing.Generation, confirmed.Generation);
        Assert.Equal(3L * frameTicks, confirmed.StartTick);
        Assert.True(confirmed.TryMap(420, out var atPreroll));
        Assert.Equal(3L * frameTicks, atPreroll.Tick);
        Assert.True(confirmed.TryMap(421, out var next));
        Assert.Equal(4L * frameTicks, next.Tick);
        Assert.Throws<InvalidOperationException>(() => playing.Step(406, -1));
        Assert.Throws<InvalidOperationException>(() => playing.Cue(406, frameTicks));
        Assert.Throws<ArgumentException>(() => playing.WithConfirmedPrerollAnchor(403));
    }
}
