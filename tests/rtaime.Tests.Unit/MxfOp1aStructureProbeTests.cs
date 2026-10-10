// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfOp1aStructureProbeTests
{
    private static readonly byte[] HeaderKey =
        [0x06,0x0e,0x2b,0x34,0x02,0x05,0x01,0x01,0x0d,0x01,0x02,0x01,0x01,0x02,0x04,0x00];
    private static readonly byte[] RipKey =
        [0x06,0x0e,0x2b,0x34,0x02,0x05,0x01,0x01,0x0d,0x01,0x02,0x01,0x01,0x11,0x01,0x00];

    [Fact]
    public void Rejects_empty_or_incomplete_input()
    {
        using var empty = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => MxfOp1aStructureProbe.Probe(empty));

        using var onlyHeader = new MemoryStream();
        MxfKlvWriter.Write(onlyHeader, HeaderKey, new byte[88]);
        Assert.Throws<InvalidDataException>(() => MxfOp1aStructureProbe.Probe(onlyHeader));
    }

    [Fact]
    public void Rejects_unsupported_op1a_and_missing_footer()
    {
        using var stream = new MemoryStream();
        var partition = new byte[88];
        partition[0] = 0;
        MxfKlvWriter.Write(stream, HeaderKey, partition);
        MxfKlvWriter.Write(stream, HeaderKey, partition);
        MxfKlvWriter.Write(stream, RipKey, new byte[16]);
        Assert.Throws<InvalidDataException>(() => MxfOp1aStructureProbe.Probe(stream));
    }

    [Fact]
    public void Rejects_malformed_klv_before_partition_projection()
    {
        using var stream = new MemoryStream();
        stream.Write(HeaderKey);
        stream.WriteByte(100);
        stream.WriteByte(0);
        Assert.Throws<InvalidDataException>(() => MxfOp1aStructureProbe.Probe(stream));
    }
}
