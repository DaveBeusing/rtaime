// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfUncompressedEssenceConverterTests
{
    [Fact]
    public void Converts_black_rgba_frame_to_expected_UYVY_size_and_levels()
    {
        var rgba = new byte[1920 * 1080 * 4];
        var uyvy = new byte[1920 * 1080 * 2];
        MxfUncompressedEssenceConverter.ConvertVideo(rgba, uyvy, 1920, 1080);
        Assert.Equal(uyvy.Length, 4_147_200);
        Assert.Equal((byte)128, uyvy[0]);
        Assert.Equal((byte)16, uyvy[1]);
        Assert.Equal((byte)128, uyvy[2]);
        Assert.Equal((byte)16, uyvy[3]);
    }

    [Fact]
    public void Rejects_unsupported_size_before_video_conversion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MxfUncompressedEssenceConverter.ConvertVideo(new byte[16], new byte[8], 2, 2));
    }

    [Fact]
    public void Converts_stereo_silence_into_pcm16()
    {
        var audio = new byte[16];
        var pcm = new byte[8];
        MxfUncompressedEssenceConverter.ConvertAudio(audio, pcm);
        Assert.All(pcm, sample => Assert.Equal((byte)0, sample));
    }

    [Fact]
    public void Rejects_odd_channel_or_wrong_pcm_length()
    {
        Assert.Throws<ArgumentException>(() =>
            MxfUncompressedEssenceConverter.ConvertAudio(new byte[4], new byte[2]));
        Assert.Throws<ArgumentException>(() =>
            MxfUncompressedEssenceConverter.ConvertAudio(new byte[8], new byte[1]));
    }
}
