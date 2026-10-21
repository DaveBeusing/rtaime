// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;

namespace rtaime.Media;

/// <summary>
/// One decoder-thread-owned native PCM16 packet. The readable extent is
/// overwritten only after the preceding packet has been fully consumed or
/// explicitly discarded by a seek. No worker or asynchronous consumer receives
/// a reference to this buffer; decoded Float32 output remains independently owned.
/// </summary>
internal sealed class LocalMediaPcmPacketScratch
{
    private const int MaximumPacketBytes = 192_000;
    private byte[] _storage = Array.Empty<byte>();

    internal int Length { get; private set; }
    internal int Capacity => _storage.Length;
    internal ReadOnlySpan<byte> Payload => _storage.AsSpan(0, Length);

    internal void Reset() => Length = 0;

    internal int CopyFromNative(IntPtr address, uint currentLength, uint maximumLength)
    {
        Length = 0;
        if (address == IntPtr.Zero || currentLength > maximumLength)
            throw new LocalMediaDecodeException("media.file.audio_payload_invalid",
                "Native PCM buffer extent is invalid.");
        var length = LocalMediaSampleProgress.ValidatePcmLength(currentLength);
        EnsureCapacity(length);
        Marshal.Copy(address, _storage, 0, length);
        Length = length;
        return length;
    }

    // In-memory regression input. Production samples always use CopyFromNative.
    internal void CopyFrom(ReadOnlySpan<byte> payload)
    {
        Length = 0;
        var length = LocalMediaSampleProgress.ValidatePcmLength(checked((uint)payload.Length));
        EnsureCapacity(length);
        payload.CopyTo(_storage);
        Length = length;
    }

    private void EnsureCapacity(int length)
    {
        if (length <= _storage.Length) return;
        var target = Math.Min(MaximumPacketBytes, Math.Max(length,
            _storage.Length == 0 ? 4_096 : checked(_storage.Length * 2)));
        _storage = new byte[target];
    }
}
