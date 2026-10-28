// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Media.Contracts;

/// <summary>
/// Exact 2160p SDR RGB candidates. These identities do not advertise a provider,
/// qualify a physical output or reinterpret legacy UnknownRgba8 frames.
/// 8-bit YCbCr and limited-range conversion require a separately admitted
/// pixel-layout/conversion contract and are deliberately absent here.
/// </summary>
public static class UhdSdrVideoProfiles
{
    public static VideoFormat Rgb2160p25 { get; } = Create(new FrameRate(25, 1));
    public static VideoFormat Rgb2160p50 { get; } = Create(FrameRate.Fps50);
    public static VideoFormat Rgb2160p59_94 { get; } = Create(FrameRate.Fps59_94);

    private static readonly IReadOnlyList<VideoFormat> ExactRgbCandidates =
        Array.AsReadOnly(new[] { Rgb2160p25, Rgb2160p50, Rgb2160p59_94 });

    public static IReadOnlyList<VideoFormat> Candidates => ExactRgbCandidates;

    public static bool Has2160pGeometry(VideoFormat format) =>
        format.Width == 3840 && format.Height == 2160;

    public static bool IsExactSdrRgbCandidate(VideoFormat format) =>
        ExactRgbCandidates.Contains(format);

    /// <summary>Checked, tightly packed RGBA8 payload arithmetic only; no allocation.</summary>
    public static long RequiredFrameBytes(VideoFormat format)
    {
        EnsureCandidate(format);
        return checked((long)format.Width * format.Height * 4);
    }

    /// <summary>
    /// Ceiling of payload bytes per second at the exact rational frame rate.
    /// Does not claim measured PCIe/DRAM throughput or a complete transfer budget.
    /// </summary>
    public static long RequiredDirectionalBytesPerSecond(VideoFormat format)
    {
        var frameBytes = RequiredFrameBytes(format);
        var bytes = checked((Int128)frameBytes * format.FrameRate.Numerator);
        return checked((long)((bytes + format.FrameRate.Denominator - 1) /
            format.FrameRate.Denominator));
    }

    /// <summary>
    /// Absolute 48-kHz sample boundary; frame-to-frame differences preserve
    /// rational 60000/1001 cadence without a rounded samples-per-frame clock.
    /// </summary>
    public static long AudioSamplesBeforeFrame(VideoFormat format, ulong frameIndex)
    {
        EnsureCandidate(format);
        var samples = checked((Int128)frameIndex * 48_000 *
            format.FrameRate.Denominator / format.FrameRate.Numerator);
        return checked((long)samples);
    }

    /// <summary>Ceiling of the single-frame deadline in TimeSpan ticks.</summary>
    public static long FrameDeadlineTicks(VideoFormat format)
    {
        EnsureCandidate(format);
        var duration = checked((Int128)TimeSpan.TicksPerSecond *
            format.FrameRate.Denominator);
        return checked((long)((duration + format.FrameRate.Numerator - 1) /
            format.FrameRate.Numerator));
    }

    private static VideoFormat Create(FrameRate rate) =>
        new(3840, 2160, rate, PixelFormat.Rgba8, ScanMode.Progressive,
            ColorDescription.Rec709FullRgba8);

    private static void EnsureCandidate(VideoFormat format)
    {
        if (!IsExactSdrRgbCandidate(format))
            throw new ArgumentException(
                "The format must be an exact 2160p BT.709 SDR, full-range RGBA8 candidate.",
                nameof(format));
    }
}
