// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Recording;

/// <summary>
/// Writes canonical definite-length SMPTE KLV records to a seekable output.
/// This primitive does not construct MXF partitions or prove OP1a conformance.
/// </summary>
internal static class MxfKlvWriter
{
    internal static void Write(Stream destination, ReadOnlySpan<byte> universalLabel, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("MXF output must be writable.", nameof(destination));
        ValidateLabel(universalLabel);
        destination.Write(universalLabel);
        WriteLength(destination, checked((ulong)value.Length));
        destination.Write(value);
    }

    internal static void WriteLength(Stream destination, ulong value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (value < 0x80)
        {
            destination.WriteByte((byte)value);
            return;
        }

        Span<byte> bytes = stackalloc byte[9];
        var length = 0;
        var remaining = value;
        while (remaining != 0)
        {
            bytes[8 - length] = (byte)remaining;
            remaining >>= 8;
            length++;
        }
        bytes[0] = checked((byte)(0x80 | length));
        destination.Write(bytes[..1]);
        destination.Write(bytes[(9 - length)..]);
    }

    private static void ValidateLabel(ReadOnlySpan<byte> label)
    {
        if (label.Length != 16 || label[0] != 0x06 || label[1] != 0x0e ||
            label[2] != 0x2b || label[3] != 0x34)
            throw new ArgumentException("A sixteen-byte SMPTE universal label is required.", nameof(label));
    }
}
