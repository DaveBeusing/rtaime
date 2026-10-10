// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// MXF local-set property encoder/reader with BER-encoded value lengths.
/// Key is a metadata-set UL, while local tags are resolved by the primer pack.
/// </summary>
internal static class MxfLocalSet
{
    internal readonly record struct Property(ushort Tag, byte[] Value);

    internal static byte[] Encode(IReadOnlyList<Property> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        using var output = new MemoryStream();
        var seen = new HashSet<ushort>();
        foreach (var property in properties)
        {
            if (property.Tag == 0 || !seen.Add(property.Tag))
                throw new ArgumentException("MXF local tags must be nonzero and unique.", nameof(properties));
            ArgumentNullException.ThrowIfNull(property.Value);
            Span<byte> tag = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(tag, property.Tag);
            output.Write(tag);
            MxfKlvWriter.WriteLength(output, (ulong)property.Value.Length);
            output.Write(property.Value);
        }
        return output.ToArray();
    }

    internal static IReadOnlyList<Property> Decode(ReadOnlySpan<byte> data, int maximumProperties = 4096)
    {
        if (maximumProperties <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumProperties));
        var output = new List<Property>();
        var seen = new HashSet<ushort>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (output.Count == maximumProperties || data.Length - offset < 3)
                throw new InvalidDataException("MXF local set exceeds limits or contains a truncated tag.");
            var tag = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            var first = data[offset++];
            ulong length;
            if (first < 0x80)
                length = first;
            else
            {
                var width = first & 0x7f;
                if (width is 0 or > 8 || width > data.Length - offset || data[offset] == 0)
                    throw new InvalidDataException("MXF local set has an invalid BER length.");
                length = 0;
                for (var i = 0; i < width; i++)
                    length = checked((length << 8) | data[offset++]);
                if (length < 0x80 || (width > 1 && length < (1UL << (8 * (width - 1)))))
                    throw new InvalidDataException("MXF local set uses non-canonical BER length.");
            }
            if (tag == 0 || !seen.Add(tag) || length > (ulong)(data.Length - offset))
                throw new InvalidDataException("MXF local property has invalid tag or value length.");
            output.Add(new Property(tag, data.Slice(offset, checked((int)length)).ToArray()));
            offset += checked((int)length);
        }
        return output;
    }
}
