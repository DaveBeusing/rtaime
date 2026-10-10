// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// MXF header metadata primer pack: local tag to SMPTE property UL mappings.
/// Keeps serialization separate from independent validation.
/// </summary>
internal static class MxfPrimerPack
{
    internal static readonly byte[] Key =
        [0x06, 0x0e, 0x2b, 0x34, 0x02, 0x05, 0x01, 0x01,
         0x0d, 0x01, 0x02, 0x01, 0x01, 0x05, 0x01, 0x00];

    internal readonly record struct Entry(ushort LocalTag, byte[] PropertyUl);

    internal static byte[] Encode(IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(entries));
        var output = new byte[checked(8 + entries.Count * 18)];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4, 4), 18);
        var tags = new HashSet<ushort>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.LocalTag == 0 || !tags.Add(entry.LocalTag))
                throw new ArgumentException("MXF primer local tags must be nonzero and unique.", nameof(entries));
            if (entry.PropertyUl is not { Length: 16 } ||
                !entry.PropertyUl.AsSpan(0, 4).SequenceEqual(new byte[] { 0x06, 0x0e, 0x2b, 0x34 }))
                throw new ArgumentException("MXF primer entry requires a valid SMPTE property UL.", nameof(entries));
            var offset = checked(8 + 18 * i);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset, 2), entry.LocalTag);
            entry.PropertyUl.CopyTo(output, offset + 2);
        }
        return output;
    }

    internal static IReadOnlyList<Entry> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("MXF primer pack is truncated.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data);
        var itemLength = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (itemLength != 18 || count > ushort.MaxValue || (long)count * 18 + 8 != data.Length)
            throw new InvalidDataException("MXF primer pack has an invalid batch.");
        var entries = new List<Entry>((int)count);
        var seen = new HashSet<ushort>();
        for (var i = 0; i < count; i++)
        {
            var offset = checked(8 + (int)i * 18);
            var tag = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var ul = data.Slice(offset + 2, 16).ToArray();
            if (tag == 0 || !seen.Add(tag) || !ul.AsSpan(0, 4).SequenceEqual(
                new byte[] { 0x06, 0x0e, 0x2b, 0x34 }))
                throw new InvalidDataException("MXF primer contains duplicate/invalid property mappings.");
            entries.Add(new Entry(tag, ul));
        }
        return entries;
    }
}
