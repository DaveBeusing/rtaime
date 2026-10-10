// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Recording;

/// <summary>
/// Worker-side uncompressed UYVY and PCM16 conversion for the constrained
/// MXF recording essence. Container labels/descriptors are defined separately.
/// </summary>
internal static class MxfUncompressedEssenceConverter
{
    internal static void ConvertVideo(ReadOnlySpan<byte> rgba, Span<byte> uyvy, int width, int height)
    {
        if (width != 1920 || height != 1080)
            throw new ArgumentOutOfRangeException(nameof(width), "MXF input must be 1920x1080 progressive RGBA8.");
        ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(rgba, uyvy, width, height);
    }

    internal static void ConvertAudio(ReadOnlySpan<byte> interleavedFloat32, Span<byte> pcm16)
    {
        if (interleavedFloat32.Length == 0 || interleavedFloat32.Length % (sizeof(float) * 2) != 0)
            throw new ArgumentException("Audio must contain complete interleaved stereo Float32 sample frames.", nameof(interleavedFloat32));
        if (pcm16.Length != interleavedFloat32.Length / 2)
            throw new ArgumentException("PCM16 output length must be exactly half the Float32 input length.", nameof(pcm16));
        WindowsMediaFoundationMp4RecordingWriter.ConvertFloat32ToPcm16(interleavedFloat32, pcm16);
    }
}
