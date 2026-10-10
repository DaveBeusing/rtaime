// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfMetadataEncodingTests
{
    private static readonly byte[] Ul =
        [0x06,0x0e,0x2b,0x34,0x01,0x01,0x01,0x01,0x06,0x01,0x01,0x04,0x02,0x01,0x00,0x00];

    [Fact]
    public void Primer_roundtrips_unique_tag_and_property_mapping()
    {
        var bytes = MxfPrimerPack.Encode([new MxfPrimerPack.Entry(0x3c0a, Ul)]);
        var parsed = Assert.Single(MxfPrimerPack.Decode(bytes));
        Assert.Equal((ushort)0x3c0a, parsed.LocalTag);
        Assert.Equal(Ul, parsed.PropertyUl);
    }

    [Fact]
    public void Primer_rejects_duplicate_tags_and_truncation()
    {
        Assert.Throws<ArgumentException>(() => MxfPrimerPack.Encode([
            new MxfPrimerPack.Entry(1, Ul), new MxfPrimerPack.Entry(1, Ul)]));
        var payload = MxfPrimerPack.Encode([new MxfPrimerPack.Entry(1, Ul)]);
        Assert.Throws<InvalidDataException>(() => MxfPrimerPack.Decode(payload.AsSpan(0, payload.Length - 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(65536)]
    public void Local_set_roundtrips_canonical_BER_sizes(int size)
    {
        var value = new byte[size];
        Array.Fill(value, (byte)0x35);
        var bytes = MxfLocalSet.Encode([new MxfLocalSet.Property(0x3c0a, value)]);
        var property = Assert.Single(MxfLocalSet.Decode(bytes));
        Assert.Equal((ushort)0x3c0a, property.Tag);
        Assert.Equal(value, property.Value);
    }

    [Fact]
    public void Local_set_rejects_duplicate_property_or_corrupted_length()
    {
        Assert.Throws<ArgumentException>(() => MxfLocalSet.Encode([
            new MxfLocalSet.Property(1, [1]), new MxfLocalSet.Property(1, [2])]));
        Assert.Throws<InvalidDataException>(() => MxfLocalSet.Decode([0, 1, 0x82, 0x01]));
        Assert.Throws<InvalidDataException>(() => MxfLocalSet.Decode([0, 1, 0x81, 0x01, 0xaa]));
    }
}
