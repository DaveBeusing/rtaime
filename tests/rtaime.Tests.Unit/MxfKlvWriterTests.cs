// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfKlvWriterTests
{
    private static readonly byte[] Label = [0x06, 0x0e, 0x2b, 0x34, 0x02, 0x05, 0x01, 0x01, 0x0d, 0x01, 0x02, 0x01, 0x01, 0x02, 0x04, 0x00];

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65536)]
    public void KLV_roundtrips_canonical_BER_length(int size)
    {
        var payload = new byte[size];
        Array.Fill(payload, (byte)0xa5);
        using var stream = new MemoryStream();
        MxfKlvWriter.Write(stream, Label, payload);
        var scanned = Assert.Single(MxfKlvReader.Scan(stream));
        Assert.Equal(size, scanned.Length);
        Assert.Equal(stream.Length, scanned.EndOffset);
        stream.Position = scanned.ValueOffset;
        var actual = new byte[size];
        stream.ReadExactly(actual);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void Writer_rejects_non_SMPTE_label()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => MxfKlvWriter.Write(stream, new byte[16], [1]));
        Assert.Equal(0, stream.Length);
    }
}
