// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// Independent file-level MXF structural preflight. This is not a replacement for
/// semantic OP1a metadata/essence conformance validation.
/// </summary>
internal static class MxfOp1aStructureProbe
{
    private static readonly byte[] PartitionPrefix =
        [0x06, 0x0e, 0x2b, 0x34, 0x02, 0x05, 0x01, 0x01,
         0x0d, 0x01, 0x02, 0x01, 0x01];
    private static readonly byte[] Op1a =
        [0x06, 0x0e, 0x2b, 0x34, 0x04, 0x01, 0x01, 0x01,
         0x0d, 0x01, 0x02, 0x01, 0x01, 0x01, 0x09, 0x00];
    private static readonly byte[] RipKey =
        [0x06, 0x0e, 0x2b, 0x34, 0x02, 0x05, 0x01, 0x01,
         0x0d, 0x01, 0x02, 0x01, 0x01, 0x11, 0x01, 0x00];

    internal readonly record struct Result(int Partitions, long FooterOffset, int IndexSegments);

    internal static Result Probe(string fileName)
    {
        using var file = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Probe(file);
    }

    internal static Result Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("MXF preflight requires readable, seekable input.", nameof(stream));

        var elements = MxfKlvReader.Scan(stream);
        if (elements.Count < 4)
            throw new InvalidDataException("MXF is missing required KLV structures.");
        var partitions = new List<MxfPartitionPackReader.Partition>();
        var offsets = new List<long>();
        var indexSegments = 0;
        var footer = -1L;

        foreach (var element in elements)
        {
            if (element.Key.AsSpan(0, 13).SequenceEqual(PartitionPrefix) && element.Key[13] is 2 or 3 or 4)
            {
                if (element.Length is < 88 or > 65536)
                    throw new InvalidDataException("MXF partition pack length is unsupported.");
                stream.Position = element.ValueOffset;
                var bytes = new byte[checked((int)element.Length)];
                stream.ReadExactly(bytes);
                var partition = MxfPartitionPackReader.Parse(bytes, checked((ulong)element.Offset));
                if (!partition.OperationalPattern.AsSpan().SequenceEqual(Op1a))
                    throw new InvalidDataException("MXF operational pattern is not OP1a.");
                if (element.Key[13] == 2 && partitions.Count != 0)
                    throw new InvalidDataException("MXF header partition is not first.");
                if (element.Key[13] == 4)
                    footer = element.Offset;
                partitions.Add(partition);
                offsets.Add(element.Offset);
            }
            if (element.Key.AsSpan(0, 15).SequenceEqual(
                new byte[] { 0x06,0x0e,0x2b,0x34,0x02,0x53,0x01,0x01,0x0d,0x01,0x02,0x01,0x01,0x10,0x01 }))
                indexSegments++;
        }

        if (partitions.Count < 2 || elements[0].Offset != 0 ||
            !elements[0].Key.AsSpan(0, 13).SequenceEqual(PartitionPrefix) ||
            elements[0].Key[13] != 2 || footer < 0 || indexSegments == 0)
            throw new InvalidDataException("MXF needs header/footer partitions and an index segment.");

        for (var i = 1; i < partitions.Count; i++)
            if (partitions[i].PreviousPartition != checked((ulong)offsets[i - 1]))
                throw new InvalidDataException("MXF previous-partition chain is inconsistent.");

        if (partitions[0].FooterPartition != checked((ulong)footer))
            throw new InvalidDataException("MXF header does not reference the footer partition.");

        var last = elements[^1];
        if (!last.Key.AsSpan().SequenceEqual(RipKey) || last.Length < 16 ||
            (last.Length - 4) % 12 != 0)
            throw new InvalidDataException("MXF random index pack is missing or malformed.");

        stream.Position = last.ValueOffset;
        var count = checked((int)((last.Length - 4) / 12));
        if (count != partitions.Count)
            throw new InvalidDataException("MXF random index partition count mismatch.");
        Span<byte> pair = stackalloc byte[12];
        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(pair);
            var bodySid = BinaryPrimitives.ReadUInt32BigEndian(pair);
            var offset = BinaryPrimitives.ReadUInt64BigEndian(pair[4..]);
            if (offset != checked((ulong)offsets[i]) || bodySid != partitions[i].BodySid)
                throw new InvalidDataException("MXF random index references an inconsistent partition.");
        }
        Span<byte> length = stackalloc byte[4];
        stream.ReadExactly(length);
        if (BinaryPrimitives.ReadUInt32BigEndian(length) != checked((uint)(last.EndOffset - last.Offset)))
            throw new InvalidDataException("MXF random index length mismatch.");
        return new Result(partitions.Count, footer, indexSegments);
    }
}
