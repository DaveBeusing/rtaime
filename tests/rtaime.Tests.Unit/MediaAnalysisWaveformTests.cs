// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class MediaAnalysisWaveformTests
{
    private static AudioBufferDescriptor Audio(ulong position, uint count, int rate = 48000) =>
        new(MediaContractVersion.Current, new AudioStreamId(Identity.New()),
            AudioFormat.Stereo48kFloat32, Identity.New(),
            new AudioBufferTiming(position, count, checked((long)(position * 10_000_000UL / (ulong)rate)),
                new Timebase(1, 10_000_000)),
            new OpaqueAudioHandle("waveform.test", "samples"));

    [Fact]
    public void Known_impulse_and_silence_have_correct_per_channel_peaks_and_rms()
    {
        var waveform = new MediaAnalysisWaveformBuilder(0, TimeSpan.TicksPerSecond, 2, 48000, 2);
        var floats = new float[48000 * 2];
        floats[0] = 1f;
        floats[1] = -0.5f;
        waveform.Observe(Audio(0, 48000), MemoryMarshal.AsBytes(floats.AsSpan()).ToArray());
        waveform.Finish();
        var buckets = waveform.Build();
        Assert.Equal(2, buckets.Length);
        Assert.Equal(1f, buckets[0].Maximum[0]);
        Assert.Equal(-0.5f, buckets[0].Minimum[1]);
        Assert.Equal(0f, buckets[1].Maximum[0]);
        Assert.False(waveform.HasDiscontinuities);
        Assert.InRange(buckets[0].Rms![0], 0.006f, 0.007f);
        Assert.All(buckets, bucket => Assert.True(bucket.HasSamples));
    }

    [Fact]
    public void Missing_samples_and_packet_overlap_are_visible_not_fabricated()
    {
        var waveform = new MediaAnalysisWaveformBuilder(0, TimeSpan.TicksPerSecond, 4, 48000, 2);
        var packet = new float[4800 * 2];
        Array.Fill(packet, 0.25f);
        waveform.Observe(Audio(0, 4800), MemoryMarshal.AsBytes(packet.AsSpan()).ToArray());
        waveform.Observe(Audio(4800, 4800), MemoryMarshal.AsBytes(packet.AsSpan()).ToArray());
        waveform.Observe(Audio(4800, 4800), MemoryMarshal.AsBytes(packet.AsSpan()).ToArray());
        waveform.Observe(Audio(24000, 4800), MemoryMarshal.AsBytes(packet.AsSpan()).ToArray());
        waveform.Finish();
        var buckets = waveform.Build();
        Assert.Equal(9600u, buckets[0].SampleCount);
        Assert.True(waveform.HasDiscontinuities);
        Assert.True(buckets[1].Discontinuity);
        Assert.False(buckets[1].HasSamples);
        Assert.True(buckets[3].Discontinuity);
    }

    [Fact]
    public void Nonfinite_audio_is_not_accepted_as_measurement()
    {
        var waveform = new MediaAnalysisWaveformBuilder(0, 1_000_000, 1, 48000, 2);
        var packet = new float[] { float.NaN, 0 };
        Assert.Throws<InvalidDataException>(() =>
            waveform.Observe(Audio(0, 1), MemoryMarshal.AsBytes(packet.AsSpan()).ToArray()));
    }
}
