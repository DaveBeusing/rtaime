// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Recording;

/// <summary>
/// Independently parses bounded MXF key-length-value records. This is a structural
/// primitive, not an OP1a conformance decision or an essence decoder.
/// </summary>
internal static class MxfKlvReader
{
    internal readonly record struct Element(long Offset, byte[] Key, long ValueOffset, long Length)
    {
        public long EndOffset => checked(ValueOffset + Length);
    }

    internal static IReadOnlyList<Element> Scan(Stream stream, int maximumElements = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("MXF KLV scanning requires a readable, seekable stream.", nameof(stream));
        if (maximumElements <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumElements));
        var result = new List<Element>();
        stream.Position = 0;
        while (stream.Position < stream.Length)
        {
            if (result.Count == maximumElements)
                throw new InvalidDataException("MXF contains too many KLV records.");
            var offset = stream.Position;
            Span<byte> key = stackalloc byte[16];
            stream.ReadExactly(key);
            if (!key[..4].SequenceEqual(new byte[] { 0x06, 0x0e, 0x2b, 0x34 }))
                throw new InvalidDataException("Invalid MXF SMPTE universal-label prefix.");
            var size = ReadBerLength(stream);
            var valueOffset = stream.Position;
            if (size < 0 || size > stream.Length - valueOffset)
                throw new InvalidDataException("MXF KLV length exceeds the file boundary.");
            result.Add(new Element(offset, key.ToArray(), valueOffset, size));
            stream.Position = checked(valueOffset + size);
        }
        return result;
    }

    private static long ReadBerLength(Stream stream)
    {
        var first = stream.ReadByte();
        if (first < 0)
            throw new EndOfStreamException("Truncated MXF BER length.");
        if (first < 0x80)
            return first;
        var count = first & 0x7f;
        if (count is 0 or > 8)
            throw new InvalidDataException("MXF BER lengths must use one to eight definite-length bytes.");
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactly(bytes[..count]);
        // MXF permits fixed-width long-form BER lengths so the writer can
        // reserve space for a final essence or partition size. Do not require
        // minimal BER encoding when reading existing MXF containers.
        ulong value = 0;
        for (var i = 0; i < count; i++)
            value = checked((value << 8) | bytes[i]);
        if (value > long.MaxValue)
            throw new InvalidDataException("MXF BER length exceeds supported stream range.");
        return (long)value;
    }
}
