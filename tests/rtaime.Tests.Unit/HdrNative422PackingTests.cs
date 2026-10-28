// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class HdrNative422PackingTests
{
    private static void Write(byte[] bytes, HdrPlaneLayout plane,
        uint x, uint y, ushort code)
    {
        var index = checked((int)(plane.OffsetBytes + (long)y * plane.RowPitchBytes + x * 2L));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index, 2),
            checked((ushort)(code << 6)));
    }

    [Fact]
    public void Packed_422_has_known_uncompressed_RFC4175_pgroup_order_and_lossless_native_roundtrip()
    {
        var layout = HdrNativeFrameLayout.TightlyPacked(2, 2,
            HdrNativePixelLayout.PlanarYcbcr422P10Le);
        var bytes = new byte[checked((int)layout.RequiredBytes)];
        for (uint y = 0; y < 2; y++)
        {
            Write(bytes, layout.Planes[0], 0, y, 64);
            Write(bytes, layout.Planes[0], 1, y, 940);
            Write(bytes, layout.Planes[1], 0, y, 512);
            Write(bytes, layout.Planes[2], 0, y, 512);
        }
        var packed = HdrNative422Packing.Pack(layout, bytes);
        Assert.Equal(10, packed.Length);
        Assert.Equal(new byte[] { 0x80, 0x04, 0x08, 0x03, 0xAC },
            packed.Take(5).ToArray());
        Assert.Equal(bytes, HdrNative422Packing.Unpack(layout, packed));
    }

    [Fact]
    public void Native_ten_bit_gamut_and_subcode_ramp_survives_packing_without_8bit_truncation()
    {
        var layout = HdrNativeFrameLayout.TightlyPacked(4, 2,
            HdrNativePixelLayout.PlanarYcbcr422P10Le);
        var bytes = new byte[checked((int)layout.RequiredBytes)];
        var values = new ushort[] { 0, 1, 1022, 1023 };
        for (uint y = 0; y < 2; y++)
        for (uint x = 0; x < 4; x++)
            Write(bytes, layout.Planes[0], x, y, values[x]);
        for (uint y = 0; y < 2; y++)
        for (uint x = 0; x < 2; x++)
        {
            Write(bytes, layout.Planes[1], x, y, (ushort)(250 + x));
            Write(bytes, layout.Planes[2], x, y, (ushort)(850 + x));
        }
        Assert.Equal(bytes, HdrNative422Packing.Unpack(layout,
            HdrNative422Packing.Pack(layout, bytes)));
        Assert.Equal(4 * 2 / 2 * 5, HdrNative422Packing.RequiredPackedBytes(layout));
    }

    [Fact]
    public void Bad_low_padding_short_payload_and_420_alias_fail_closed()
    {
        var layout = HdrNativeFrameLayout.TightlyPacked(2, 2,
            HdrNativePixelLayout.PlanarYcbcr422P10Le);
        var buffer = new byte[checked((int)layout.RequiredBytes)];
        buffer[0] = 1;
        Assert.Throws<FormatException>(() => HdrNative422Packing.Pack(layout, buffer));
        buffer[0] = 0;
        Assert.Throws<ArgumentException>(() => HdrNative422Packing.Pack(
            layout, buffer.AsSpan(1)));
        Assert.Throws<ArgumentException>(() => HdrNative422Packing.Unpack(
            layout, new byte[9]));
        Assert.Throws<NotSupportedException>(() => HdrNative422Packing.Pack(
            HdrNativeFrameLayout.TightlyPacked(2, 2,
                HdrNativePixelLayout.P010Ycbcr420Le), new byte[12]));
    }
}
