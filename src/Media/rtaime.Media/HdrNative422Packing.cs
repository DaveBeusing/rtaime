// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Media.Contracts;

namespace rtaime.Media;

/// <summary>
/// Lossless native 10-bit YCbCr422 planar <-> 5-byte/two-pixel pgroups.
/// Offline reference packing only: does not widen the existing HD/ST 2110
/// sender, authorize native UHD, infer colorimetry or modify frame timing.
/// The associated HLG/CICP identity must be negotiated separately.
/// </summary>
public static class HdrNative422Packing
{
    public static int RequiredPackedBytes(HdrNativeFrameLayout layout)
    {
        Validate422(layout);
        return checked((int)((long)layout.Width * layout.Height / 2 * 5));
    }

    public static byte[] Pack(HdrNativeFrameLayout layout, ReadOnlySpan<byte> planar)
    {
        Validate422(layout);
        if (planar.Length != layout.RequiredBytes)
            throw new ArgumentException("Native planar payload extent does not match the declared 10-bit layout.", nameof(planar));

        var result = new byte[RequiredPackedBytes(layout)];
        var rowPackedBytes = checked((int)(layout.Width / 2 * 5));
        for (uint y = 0; y < layout.Height; y++)
        for (uint x = 0; x < layout.Width; x += 2)
        {
            var cb = Sample(planar, layout.Planes[1], x / 2, y);
            var y0 = Sample(planar, layout.Planes[0], x, y);
            var cr = Sample(planar, layout.Planes[2], x / 2, y);
            var y1 = Sample(planar, layout.Planes[0], x + 1, y);
            var start = checked((int)(y * rowPackedBytes + x / 2 * 5));
            result[start] = (byte)(cb >> 2);
            result[start + 1] = (byte)(((cb & 3) << 6) | (y0 >> 4));
            result[start + 2] = (byte)(((y0 & 15) << 4) | (cr >> 6));
            result[start + 3] = (byte)(((cr & 63) << 2) | (y1 >> 8));
            result[start + 4] = (byte)y1;
        }
        return result;
    }

    public static byte[] Unpack(HdrNativeFrameLayout layout, ReadOnlySpan<byte> packed)
    {
        Validate422(layout);
        if (packed.Length != RequiredPackedBytes(layout))
            throw new ArgumentException("Packed 4:2:2 10-bit payload length must exactly match the declared raster.", nameof(packed));

        var result = new byte[checked((int)layout.RequiredBytes)];
        var rowPackedBytes = checked((int)(layout.Width / 2 * 5));
        for (uint y = 0; y < layout.Height; y++)
        for (uint x = 0; x < layout.Width; x += 2)
        {
            var start = checked((int)(y * rowPackedBytes + x / 2 * 5));
            var cb = (ushort)((packed[start] << 2) | (packed[start + 1] >> 6));
            var y0 = (ushort)(((packed[start + 1] & 63) << 4) | (packed[start + 2] >> 4));
            var cr = (ushort)(((packed[start + 2] & 15) << 6) | (packed[start + 3] >> 2));
            var y1 = (ushort)(((packed[start + 3] & 3) << 8) | packed[start + 4]);
            Store(result, layout.Planes[0], x, y, y0);
            Store(result, layout.Planes[0], x + 1, y, y1);
            Store(result, layout.Planes[1], x / 2, y, cb);
            Store(result, layout.Planes[2], x / 2, y, cr);
        }
        return result;
    }

    private static void Validate422(HdrNativeFrameLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.PixelLayout != HdrNativePixelLayout.PlanarYcbcr422P10Le)
            throw new NotSupportedException("Native pgroups require explicit planar 4:2:2 10-bit; P010 4:2:0 and RGB-half are not aliases.");
    }

    private static ushort Sample(ReadOnlySpan<byte> buffer,
        HdrPlaneLayout plane, uint x, uint y)
    {
        var offset = checked((int)(plane.OffsetBytes + (long)y * plane.RowPitchBytes + x * 2L));
        var word = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
        if ((word & 63) != 0)
            throw new FormatException("Native high-10-bit sample has nonzero low padding bits.");
        return (ushort)(word >> 6);
    }

    private static void Store(Span<byte> buffer, HdrPlaneLayout plane,
        uint x, uint y, ushort code)
    {
        var offset = checked((int)(plane.OffsetBytes + (long)y * plane.RowPitchBytes + x * 2L));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset, 2),
            checked((ushort)(code << 6)));
    }
}
