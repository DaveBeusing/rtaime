// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media;

// Reads only bounded SPS geometry and early VUI metadata, never media samples.
internal static class H264LocalMediaSequenceMetadata
{
    public static void Validate(ReadOnlySpan<byte> nal, uint width, uint height)
    {
        if (nal.Length is < 4 or > 65535 || (nal[0] & 0x9F) != 7)
            throw new InvalidDataException("AVC sequence parameter NAL is invalid.");
        var rbsp = new byte[nal.Length - 1];
        var count = 0;
        var zeros = 0;
        for (var index = 1; index < nal.Length; index++)
        {
            var value = nal[index];
            if (zeros >= 2 && value == 3)
            {
                if (index + 1 >= nal.Length || nal[index + 1] > 3)
                    throw new InvalidDataException("AVC emulation prevention is invalid.");
                zeros = 0; continue;
            }
            rbsp[count++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        var bits = new Bits(rbsp.AsSpan(0, count));
        var profile = bits.Read(8);
        bits.Read(8); bits.Read(8);
        if (bits.Unsigned() > 31) throw new InvalidDataException("AVC sequence identity is invalid.");
        if (profile is not (66 or 77 or 88 or 100))
            throw new LocalMediaProbeException("media.file.profile_unsupported", "AVC sequence profile is not qualified.");
        if (profile == 100)
        {
            if (bits.Unsigned() != 1 || bits.Unsigned() != 0 || bits.Unsigned() != 0)
                throw new LocalMediaProbeException("media.file.profile_unsupported", "AVC sequence requires 8-bit 4:2:0.");
            bits.Read(1);
            if (bits.Read(1) != 0)
            {
                for (var list = 0; list < 8; list++)
                {
                    if (bits.Read(1) == 0) continue;
                    long last = 8, next = 8;
                    for (var entry = 0; entry < (list < 6 ? 16 : 64); entry++)
                    {
                        if (next != 0) next = ((last + bits.Signed()) % 256 + 256) % 256;
                        if (next != 0) last = next;
                    }
                }
            }
        }
        if (bits.Unsigned() > 12) throw new InvalidDataException("AVC frame-number width is invalid.");
        var order = bits.Unsigned();
        if (order == 0)
        {
            if (bits.Unsigned() > 12) throw new InvalidDataException("AVC picture-order width is invalid.");
        }
        else if (order == 1)
        {
            bits.Read(1); bits.Signed(); bits.Signed();
            var cycle = bits.Unsigned();
            if (cycle > 255) throw new InvalidDataException("AVC picture-order cycle exceeds the probe limit.");
            for (var index = 0u; index < cycle; index++) bits.Signed();
        }
        else if (order != 2) throw new InvalidDataException("AVC picture-order mode is invalid.");
        if (bits.Unsigned() > 16) throw new InvalidDataException("AVC reference count exceeds the probe limit.");
        bits.Read(1);
        var macroblocksWide = (ulong)bits.Unsigned() + 1;
        var mapUnitsHigh = (ulong)bits.Unsigned() + 1;
        var frameOnly = bits.Read(1);
        if (frameOnly == 0) bits.Read(1);
        bits.Read(1);
        ulong left = 0, right = 0, top = 0, bottom = 0;
        if (bits.Read(1) != 0)
        { left = bits.Unsigned(); right = bits.Unsigned(); top = bits.Unsigned(); bottom = bits.Unsigned(); }
        var codedWidth = macroblocksWide * 16;
        var codedHeight = mapUnitsHigh * 16 * (2 - frameOnly);
        var cropWidth = (left + right) * 2;
        var cropHeight = (top + bottom) * 2 * (2 - frameOnly);
        if (codedWidth > LocalMediaInputPolicy.MaximumWidth || codedHeight > LocalMediaInputPolicy.MaximumHeight ||
            cropWidth >= codedWidth || cropHeight >= codedHeight ||
            codedWidth - cropWidth != width || codedHeight - cropHeight != height)
            throw new LocalMediaProbeException("media.file.metadata_mismatch", "AVC sequence and container visible geometry disagree.");
        if (bits.Read(1) == 0) return;
        if (bits.Read(1) != 0)
        {
            var aspect = bits.Read(8);
            if (aspect == 255)
            {
                var horizontal = bits.Read(16); var vertical = bits.Read(16);
                if (horizontal == 0 || horizontal != vertical)
                    throw new LocalMediaProbeException("media.file.pixel_aspect_unsupported", "AVC sequence reports non-square pixels.");
            }
            else if (aspect is not (0 or 1))
                throw new LocalMediaProbeException("media.file.pixel_aspect_unsupported", "AVC sequence reports non-square pixels.");
        }
        if (bits.Read(1) != 0) bits.Read(1);
        if (bits.Read(1) == 0) return;
        bits.Read(3); bits.Read(1);
        if (bits.Read(1) == 0) return;
        var primaries = bits.Read(8); var transfer = bits.Read(8); var matrix = bits.Read(8);
        if (primaries is not (1 or 2 or 5 or 6) || transfer is not (1 or 2 or 6) || matrix is not (1 or 2 or 5 or 6))
            throw new LocalMediaProbeException("media.file.color_unsupported", "AVC sequence color conversion is not qualified.");
    }

    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        public uint Read(int count)
        {
            if (count is < 0 or > 32 || count > _data.Length * 8 - _position)
                throw new InvalidDataException("AVC sequence metadata is truncated.");
            uint value = 0;
            for (var index = 0; index < count; index++)
            {
                value = (value << 1) | (uint)((_data[_position / 8] >> (7 - _position % 8)) & 1);
                _position++;
            }
            return value;
        }
        public uint Unsigned()
        {
            var zeros = 0;
            while (Read(1) == 0)
                if (++zeros > 31) throw new InvalidDataException("AVC exponential code exceeds the probe limit.");
            return ((1u << zeros) - 1) + Read(zeros);
        }
        public long Signed()
        {
            var code = Unsigned();
            return (code & 1) == 0 ? -(long)(code / 2) : ((long)code + 1) / 2;
        }
    }
}
