// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Runtime;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SequenceRuntimePositionMapTests
{
    private static readonly Guid SequenceId = Guid.Parse("c6000000-0000-0000-0000-000000000001");

    [Theory]
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
}
