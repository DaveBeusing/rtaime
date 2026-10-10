// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfPartitionPackWriterTests
{
    private static readonly byte[] Op1a =
        [0x06,0x0e,0x2b,0x34,0x04,0x01,0x01,0x01,0x0d,0x01,0x02,0x01,0x01,0x01,0x09,0x00];
    private static readonly byte[] Container =
        [0x06,0x0e,0x2b,0x34,0x04,0x01,0x01,0x01,0x0d,0x01,0x03,0x01,0x02,0x01,0x00,0x00];

    [Fact]
    public void Partition_encoding_roundtrips_exact_offsets_and_essence_batch()
    {
        var expected = new MxfPartitionPackReader.Partition(
            1, 3, 512, 4096, 2048, 8192, 1024, 128, 2, 0, 1,
            Op1a, [Container]);
        var bytes = MxfPartitionPackWriter.Encode(expected);
        var actual = MxfPartitionPackReader.Parse(bytes, expected.ThisPartition);
        Assert.Equal(expected.MajorVersion, actual.MajorVersion);
        Assert.Equal(expected.MinorVersion, actual.MinorVersion);
        Assert.Equal(expected.KagSize, actual.KagSize);
        Assert.Equal(expected.ThisPartition, actual.ThisPartition);
        Assert.Equal(expected.PreviousPartition, actual.PreviousPartition);
        Assert.Equal(expected.FooterPartition, actual.FooterPartition);
        Assert.Equal(expected.HeaderByteCount, actual.HeaderByteCount);
        Assert.Equal(expected.IndexByteCount, actual.IndexByteCount);
        Assert.Equal(expected.IndexSid, actual.IndexSid);
        Assert.Equal(expected.BodyOffset, actual.BodyOffset);
        Assert.Equal(expected.BodySid, actual.BodySid);
        Assert.Equal(Op1a, actual.OperationalPattern);
        Assert.Equal(Container, Assert.Single(actual.EssenceContainerLabels));
    }

    [Fact]
    public void Invalid_labels_fail_before_serialization()
    {
        var input = new MxfPartitionPackReader.Partition(
            1, 3, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            Op1a, [new byte[16]]);
        Assert.Throws<ArgumentException>(() => MxfPartitionPackWriter.Encode(input));
    }

    [Fact]
    public void Broken_offset_chain_fails_before_serialization()
    {
        var input = new MxfPartitionPackReader.Partition(
            1, 3, 1, 128, 256, 0, 0, 0, 0, 0, 0,
            Op1a, []);
        Assert.Throws<ArgumentException>(() => MxfPartitionPackWriter.Encode(input));
    }
}
