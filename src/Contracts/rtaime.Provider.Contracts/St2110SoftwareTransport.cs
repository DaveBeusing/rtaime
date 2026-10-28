// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Net;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Contracts;

/// <summary>
/// Narrow, versioned ST 2110-20 / ST 2110-30 software-reference tuple.
/// This is NOT a claim of physical ST 2110-21, PTP, NIC or interoperability qualification.
/// </summary>
public sealed record St2110SoftwareProfile
{
    public const int FormatVersion = 1;
    public const int VideoRtpClockRate = 90000;
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;
    public const int VideoPixelGroupBytes = 5; // YCbCr 4:2:2 10-bit, two pixels
    public const int AudioPacketSamples = 48; // ST 2110-30 Level A, 1 ms
    public const byte VideoPayloadType = 96;
    public const byte AudioPayloadType = 97;
    public const int MaximumRtpDatagramBytes = 1200;

    public St2110SoftwareProfile(int width, int height, FrameRate frameRate)
    {
        if (width < 2 || width > 1920 || (width & 1) != 0 ||
            height < 1 || height > 1080 ||
            !((frameRate.Numerator == 25 && frameRate.Denominator == 1) ||
              (frameRate.Numerator == 50 && frameRate.Denominator == 1) ||
              (frameRate.Numerator == 60000 && frameRate.Denominator == 1001)))
            throw new NotSupportedException("Software ST 2110 reference supports progressive 4:2:2 10-bit at 25, 50, or 60000/1001 fps, at most 1920x1080.");
        Width = width;
        Height = height;
        FrameRate = frameRate;
    }

    public int Width { get; }
    public int Height { get; }
    public FrameRate FrameRate { get; }
    public int PackedFrameBytes => checked(Width * Height / 2 * VideoPixelGroupBytes);
    public int PackedLineBytes => checked(Width / 2 * VideoPixelGroupBytes);
    public int FormatRevision => FormatVersion;

    public static St2110SoftwareProfile FromRuntime(VideoFormat format)
    {
        if (format != VideoFormat.Hd1080p50Rgba8 &&
            format != VideoFormat.Hd1080p59_94Rgba8)
            throw new NotSupportedException("Runtime ST 2110 reference requires a supported progressive 1080p RGBA8 format.");
        return new St2110SoftwareProfile(checked((int)format.Width), checked((int)format.Height), format.FrameRate);
    }
}

/// <summary>
/// Intentionally restricted to local test traffic until real PTP, NIC bandwidth,
/// multicast admission and switch shaping are independently qualified.
/// </summary>
public sealed record St2110SoftwareEndpoint
{
    public St2110SoftwareEndpoint(IPAddress address, int videoPort, int audioPort,
        uint videoSsrc, uint audioSsrc)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IPAddress.IsLoopback(address))
            throw new NotSupportedException("Physical NIC, multicast and non-loopback ST 2110 routing are NOT_SUPPORTED.");
        if (videoPort is < 1024 or > 65532 || (videoPort & 1) != 0 ||
            audioPort != videoPort + 2 || audioPort > 65534)
            throw new ArgumentOutOfRangeException(nameof(videoPort), "Reference uses consecutive even video and audio RTP ports (video, video+2).");
        if (videoSsrc == 0 || audioSsrc == 0 || videoSsrc == audioSsrc)
            throw new ArgumentException("Video and audio require distinct non-zero RTP SSRCs.");
        Address = address;
        VideoPort = videoPort;
        AudioPort = audioPort;
        VideoSsrc = videoSsrc;
        AudioSsrc = audioSsrc;
    }

    public IPAddress Address { get; }
    public int VideoPort { get; }
    public int AudioPort { get; }
    public uint VideoSsrc { get; }
    public uint AudioSsrc { get; }
    public string SafeIdentity => $"rtp://{Address}:{VideoPort},{AudioPort};software-loopback";
}

public static class St2110TransportAdmission
{
    public static void RequireSoftwareReference(St2110SoftwareProfile profile,
        St2110SoftwareEndpoint endpoint, MediaReferenceClockObservation? reference)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(endpoint);
        // An actual hardware-synchronized output must be admitted by a
        // hardware-specific PTP/NIC provider, which is intentionally absent.
        // A simulated reference must never be projected as hardware evidence.
        if (reference is not null)
        {
            reference.Validate();
            if (reference.Source is MediaReferenceClockSource.HardwarePtp or MediaReferenceClockSource.ExternalGenlock &&
                reference.Evidence != MediaReferenceEvidence.HardwareQualified)
                throw new NotSupportedException("Hardware reference evidence is not verified.");
        }
    }

    public static uint VideoTimestamp(St2110SoftwareProfile profile, ulong frameIndex, uint epoch = 0) =>
        unchecked(epoch + (uint)((Int128)frameIndex *
            profile.FrameRate.Denominator * St2110SoftwareProfile.VideoRtpClockRate /
            profile.FrameRate.Numerator));

    public static uint AudioTimestamp(St2110SoftwareProfile profile, ulong frameIndex, uint epoch = 0) =>
        unchecked(epoch + (uint)((Int128)frameIndex *
            profile.FrameRate.Denominator * St2110SoftwareProfile.AudioSampleRate /
            profile.FrameRate.Numerator));
}
