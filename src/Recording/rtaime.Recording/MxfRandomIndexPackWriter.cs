// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// Writes an MXF random index pack (RIP) for completed partition boundaries.
/// The index is serialized independently from the partition-pack encoder.
/// </summary>
internal static class MxfRandomIndexPackWriter
{
    internal static readonly byte[] UniversalLabel =
        [0x06, 0x0e, 0x2b, 0x34, 0x02, 0x05, 0x01, 0x01,
         0x0d, 0x01, 0x02, 0x01, 0x01, 0x11, 0x01, 0x00];

    internal static void Write(Stream stream, IReadOnlyList<(uint BodySid, ulong PartitionOffset)> partitions)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(partitions);
        if (!stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("MXF RIP requires a writable seekable stream.", nameof(stream));
        if (partitions.Count < 2 || partitions.Count > 4096)
            throw new ArgumentOutOfRangeException(nameof(partitions), "MXF RIP requires at least header and footer partitions.");
        if (partitions[0].PartitionOffset != 0)
            throw new InvalidDataException("The first MXF partition must start at byte zero.");
        for (var i = 1; i < partitions.Count; i++)
            if (partitions[i].PartitionOffset <= partitions[i - 1].PartitionOffset)
                throw new InvalidDataException("MXF partition offsets must be strictly increasing.");

        var data = new byte[checked(12 * partitions.Count + 4)];
        for (var i = 0; i < partitions.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12 * i), partitions[i].BodySid);
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(12 * i + 4), partitions[i].PartitionOffset);
        }

        // BER length includes the RIP trailing 4-byte total KLV length field.
        using var encoded = new MemoryStream();
        MxfKlvWriter.Write(encoded, UniversalLabel, data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(data.Length - 4),
            checked((uint)encoded.Length));
        MxfKlvWriter.Write(stream, UniversalLabel, data);
    }
}
