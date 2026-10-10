// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// Independent structural validation for MXF partition-pack payloads.
/// Does not equate a valid partition pack with a compliant OP1a file.
/// </summary>
internal static class MxfPartitionPackReader
{
    internal readonly record struct Partition(
        ushort MajorVersion, ushort MinorVersion, uint KagSize,
        ulong ThisPartition, ulong PreviousPartition, ulong FooterPartition,
        ulong HeaderByteCount, ulong IndexByteCount, uint IndexSid,
        ulong BodyOffset, uint BodySid, byte[] OperationalPattern,
        IReadOnlyList<byte[]> EssenceContainerLabels);

    internal static Partition Parse(ReadOnlySpan<byte> payload, ulong expectedPartitionOffset)
    {
        if (payload.Length < 88)
            throw new InvalidDataException("MXF partition pack is truncated.");
        var major = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var minor = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        var kag = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
        var current = BinaryPrimitives.ReadUInt64BigEndian(payload[8..]);
        var previous = BinaryPrimitives.ReadUInt64BigEndian(payload[16..]);
        var footer = BinaryPrimitives.ReadUInt64BigEndian(payload[24..]);
        var headerBytes = BinaryPrimitives.ReadUInt64BigEndian(payload[32..]);
        var indexBytes = BinaryPrimitives.ReadUInt64BigEndian(payload[40..]);
        var indexSid = BinaryPrimitives.ReadUInt32BigEndian(payload[48..]);
        var bodyOffset = BinaryPrimitives.ReadUInt64BigEndian(payload[52..]);
        var bodySid = BinaryPrimitives.ReadUInt32BigEndian(payload[60..]);
        var pattern = payload.Slice(64, 16).ToArray();
        var count = BinaryPrimitives.ReadUInt32BigEndian(payload[80..]);
        var itemLength = BinaryPrimitives.ReadUInt32BigEndian(payload[84..]);
        if (major == 0 || kag == 0 || current != expectedPartitionOffset)
            throw new InvalidDataException("Invalid MXF partition version, alignment or offset.");
        if (previous > current || (footer != 0 && footer < current))
            throw new InvalidDataException("MXF partition references an invalid offset.");
        if (count > 4096 || itemLength != 16 || 88L + (long)count * 16 != payload.Length)
            throw new InvalidDataException("Invalid MXF essence-container batch.");
        if (!pattern.AsSpan(0, 4).SequenceEqual(new byte[] { 0x06, 0x0e, 0x2b, 0x34 }))
            throw new InvalidDataException("MXF operational pattern must be a SMPTE universal label.");
        var labels = new List<byte[]>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            var label = payload.Slice(checked(88 + i * 16), 16).ToArray();
            if (!label.AsSpan(0, 4).SequenceEqual(new byte[] { 0x06, 0x0e, 0x2b, 0x34 }))
                throw new InvalidDataException("Invalid MXF essence-container universal label.");
            labels.Add(label);
        }
        return new Partition(major, minor, kag, current, previous, footer, headerBytes, indexBytes, indexSid,
            bodyOffset, bodySid, pattern, labels);
    }
}
