// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfPartitionPackReaderTests
{
    private static byte[] ValidPayload()
    {
        var data = new byte[104];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8), 512);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(80), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(84), 16);
        data[64] = data[88] = 0x06;
        data[65] = data[89] = 0x0e;
        data[66] = data[90] = 0x2b;
        data[67] = data[91] = 0x34;
        return data;
    }

    [Fact]
    public void Valid_partition_pack_preserves_offsets_and_essence_labels()
    {
        var partition = MxfPartitionPackReader.Parse(ValidPayload(), 512);
        Assert.Equal(512UL, partition.ThisPartition);
        Assert.Equal(1U, partition.KagSize);
        Assert.Single(partition.EssenceContainerLabels);
    }

    [Fact]
    public void Rejects_invalid_partition_location()
    {
        Assert.Throws<InvalidDataException>(() => MxfPartitionPackReader.Parse(ValidPayload(), 1024));
    }

    [Fact]
    public void Rejects_truncated_essence_batch()
    {
        Assert.Throws<InvalidDataException>(() => MxfPartitionPackReader.Parse(ValidPayload()[..99], 512));
    }

    [Fact]
    public void Rejects_invalid_essence_element_size()
    {
        var payload = ValidPayload();
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(84), 32);
        Assert.Throws<InvalidDataException>(() => MxfPartitionPackReader.Parse(payload, 512));
    }

    [Fact]
    public void Rejects_corrupted_operational_pattern()
    {
        var payload = ValidPayload();
        payload[64] = 0;
        Assert.Throws<InvalidDataException>(() => MxfPartitionPackReader.Parse(payload, 512));
    }
}
