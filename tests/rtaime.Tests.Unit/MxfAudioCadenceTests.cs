// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfAudioCadenceTests
{
    [Fact]
    public void Fifty_fps_has_exactly_960_stereo_sample_frames_per_picture()
    {
        for (var i = 0; i < 5000; i++)
            Assert.Equal(960, MxfAudioCadence.SamplesForFrame(i, 50, 1));
        Assert.Equal(4_800_000L, MxfAudioCadence.SampleBoundary(5000, 50, 1));
    }

    [Fact]
    public void Fractional_edit_rate_uses_no_cumulative_rounding()
    {
        long sum = 0;
        for (var i = 0; i < 60000; i++)
        {
            var samples = MxfAudioCadence.SamplesForFrame(i, 60000, 1001);
            Assert.True(samples is 800 or 801);
            sum += samples;
        }
        Assert.Equal(48_048_000L, sum);
        Assert.Equal(sum, MxfAudioCadence.SampleBoundary(60000, 60000, 1001));
    }

    [Fact]
    public void Unsupported_rates_and_negative_frame_indices_fail_closed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MxfAudioCadence.SamplesForFrame(-1, 50, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MxfAudioCadence.SamplesForFrame(0, 25, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MxfAudioCadence.SampleBoundary(0, 60000, 1000));
    }
}
