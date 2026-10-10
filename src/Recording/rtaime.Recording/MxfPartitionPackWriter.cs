// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// Encodes ST 377-1 partition-pack fields independently of the read-only
/// partition parser. Does not assert complete OP1a file conformance.
/// </summary>
internal static class MxfPartitionPackWriter
{
    internal static byte[] Encode(MxfPartitionPackReader.Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition.OperationalPattern);
        ArgumentNullException.ThrowIfNull(partition.EssenceContainerLabels);
        if (partition.MajorVersion == 0 || partition.KagSize == 0)
            throw new ArgumentException("MXF partition needs nonzero version and KAG.");
        if (partition.PreviousPartition > partition.ThisPartition ||
            (partition.FooterPartition != 0 && partition.FooterPartition < partition.ThisPartition))
            throw new ArgumentException("MXF partition offsets are inconsistent.");
        if (partition.OperationalPattern.Length != 16 || !IsUl(partition.OperationalPattern))
            throw new ArgumentException("MXF operational pattern requires a sixteen-byte SMPTE UL.");
        if (partition.EssenceContainerLabels.Count > 4096)
            throw new ArgumentOutOfRangeException(nameof(partition), "Essence-container batch is too large.");
        var result = new byte[checked(88 + partition.EssenceContainerLabels.Count * 16)];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(0, 2), partition.MajorVersion);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), partition.MinorVersion);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), partition.KagSize);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8, 8), partition.ThisPartition);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(16, 8), partition.PreviousPartition);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(24, 8), partition.FooterPartition);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(32, 8), partition.HeaderByteCount);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(40, 8), partition.IndexByteCount);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(48, 4), partition.IndexSid);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(52, 8), partition.BodyOffset);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(60, 4), partition.BodySid);
        partition.OperationalPattern.CopyTo(result, 64);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(80, 4), (uint)partition.EssenceContainerLabels.Count);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(84, 4), 16);
        for (var i = 0; i < partition.EssenceContainerLabels.Count; i++)
        {
            var label = partition.EssenceContainerLabels[i];
            if (label is null || label.Length != 16 || !IsUl(label))
                throw new ArgumentException("MXF essence container requires a sixteen-byte SMPTE UL.");
            label.CopyTo(result, checked(88 + i * 16));
        }
        return result;
    }

    private static bool IsUl(ReadOnlySpan<byte> value) =>
        value.Length == 16 && value[0] == 0x06 && value[1] == 0x0e &&
        value[2] == 0x2b && value[3] == 0x34;
}
