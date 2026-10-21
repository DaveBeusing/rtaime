// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media;

/// <summary>
/// Optional, decoder-thread-owned software attribution. Operations describe
/// accepted payload transfers, not physical DRAM, file I/O or PCIe traffic.
/// </summary>
public readonly record struct LocalMediaCopyMeasurements(
    ulong Operations, ulong PayloadBytes, LocalMediaReadMeasurements CpuWall)
{
    public LocalMediaCopyMeasurements Observe(int bytes, double milliseconds)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        return new(
            Operations == ulong.MaxValue ? Operations : Operations + 1,
            PayloadBytes > ulong.MaxValue - (ulong)bytes ? ulong.MaxValue : PayloadBytes + (ulong)bytes,
            CpuWall.Observe(milliseconds));
    }
}

public readonly record struct LocalMediaDecodeCopyMeasurements(
    LocalMediaCopyMeasurements Rgb32ToScratch,
    LocalMediaCopyMeasurements RgbaSwizzle,
    LocalMediaCopyMeasurements NativePcmToScratch,
    LocalMediaCopyMeasurements Pcm16ToFloat32);
