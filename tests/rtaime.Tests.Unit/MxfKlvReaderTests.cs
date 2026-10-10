// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfKlvReaderTests
{
    private static readonly byte[] Key = [0x06, 0x0e, 0x2b, 0x34, 0x02, 0x05, 0x01, 0x01, 0x0d, 0x01, 0x02, 0x01, 0x01, 0x02, 0x04, 0x00];

    [Fact]
    public void Scan_reads_definite_length_key_value()
    {
        using var stream = new MemoryStream();
        stream.Write(Key);
        stream.WriteByte(3);
        stream.Write([1, 2, 3]);
        var elements = MxfKlvReader.Scan(stream);
        var element = Assert.Single(elements);
        Assert.Equal(0L, element.Offset);
        Assert.Equal(17L, element.ValueOffset);
        Assert.Equal(3L, element.Length);
        Assert.Equal(20L, element.EndOffset);
    }

    [Fact]
    public void Scan_rejects_value_length_past_end()
    {
        using var stream = new MemoryStream();
        stream.Write(Key);
        stream.WriteByte(12);
        stream.WriteByte(1);
        Assert.Throws<InvalidDataException>(() => MxfKlvReader.Scan(stream));
    }

    [Fact]
    public void Scan_rejects_indefinite_ber_length()
    {
        using var stream = new MemoryStream();
        stream.Write(Key);
        stream.WriteByte(0x80);
        Assert.Throws<InvalidDataException>(() => MxfKlvReader.Scan(stream));
    }

    [Fact]
    public void Scan_rejects_noncanonical_long_form()
    {
        using var stream = new MemoryStream();
        stream.Write(Key);
        stream.Write([0x81, 0x01, 0xff]);
        Assert.Throws<InvalidDataException>(() => MxfKlvReader.Scan(stream));
    }

    [Fact]
    public void Scan_enforces_element_limit()
    {
        using var stream = new MemoryStream();
        for (var index = 0; index < 2; index++)
        {
            stream.Write(Key);
            stream.WriteByte(0);
        }
        Assert.Throws<InvalidDataException>(() => MxfKlvReader.Scan(stream, maximumElements: 1));
    }
}
