// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfRandomIndexPackWriterTests
{
    [Fact]
    public void Writes_rip_offsets_and_total_klv_length()
    {
        using var stream = new MemoryStream();
        MxfRandomIndexPackWriter.Write(stream, [(0U, 0UL), (1U, 4096UL), (0U, 8192UL)]);
        var element = Assert.Single(MxfKlvReader.Scan(stream));
        Assert.Equal(MxfRandomIndexPackWriter.UniversalLabel, element.Key);
        Assert.Equal(40L, element.Length);
        stream.Position = element.ValueOffset;
        Span<byte> item = stackalloc byte[12];
        stream.ReadExactly(item);
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32BigEndian(item));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(item[4..]));
        stream.ReadExactly(item);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32BigEndian(item));
        Assert.Equal(4096UL, BinaryPrimitives.ReadUInt64BigEndian(item[4..]));
        stream.ReadExactly(item);
        Assert.Equal(8192UL, BinaryPrimitives.ReadUInt64BigEndian(item[4..]));
        Span<byte> total = stackalloc byte[4];
        stream.ReadExactly(total);
        Assert.Equal((uint)stream.Length, BinaryPrimitives.ReadUInt32BigEndian(total));
    }

    [Fact]
    public void Rejects_duplicate_unsorted_and_nonzero_initial_offsets()
    {
        using var stream = new MemoryStream();
        Assert.Throws<InvalidDataException>(() =>
            MxfRandomIndexPackWriter.Write(stream, [(0U, 0UL), (1U, 0UL)]));
        Assert.Throws<InvalidDataException>(() =>
            MxfRandomIndexPackWriter.Write(stream, [(0U, 100UL), (1U, 200UL)]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MxfRandomIndexPackWriter.Write(stream, [(0U, 0UL)]));
    }
}
