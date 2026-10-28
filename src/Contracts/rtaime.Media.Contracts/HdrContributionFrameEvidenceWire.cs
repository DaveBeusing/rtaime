// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;

namespace rtaime.Media.Contracts;

/// <summary>
/// Separate opt-in reference sidecar for a native HLG frame. It is NOT a change
/// to MediaContractVersion 1.0, MonitoringFrameWire 1.3, Program IPC or Shows.
/// Consumers without explicit HDR-v1 negotiation must reject it; a parsed
/// sidecar does not grant provider, recording or physical output qualification.
/// </summary>
public sealed class HdrContributionFrameEvidence
{
    private readonly byte[] _contentHash;

    public HdrContributionFrameEvidence(HdrContributionVideoProfile profile,
        HdrNativeFrameLayout layout, FrameTiming timing, ReadOnlySpan<byte> contentSha256)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(layout);
        if (!profile.IsExactR153LiveHlg ||
            layout.Width != profile.Width || layout.Height != profile.Height ||
            layout.PixelLayout != profile.Layout ||
            contentSha256.Length != 32)
            throw new ArgumentException("Exact authoritative R153 native layout and SHA-256 are required.");
        Profile = profile;
        Layout = layout;
        Timing = timing;
        _contentHash = contentSha256.ToArray();
    }

    public HdrContributionVideoProfile Profile { get; }
    public HdrNativeFrameLayout Layout { get; }
    public FrameTiming Timing { get; }
    public ReadOnlyMemory<byte> ContentSha256 => _contentHash;

    public bool VerifyPayload(ReadOnlySpan<byte> payload) =>
        payload.Length == Layout.RequiredBytes &&
        CryptographicOperations.FixedTimeEquals(_contentHash, SHA256.HashData(payload));
}

/// <summary>
/// Fixed-size, bounded, little-endian metadata sidecar. Source provenance is
/// required, unknown versions/extra fields/reserved bytes are rejected; frames
/// stay in an independent native 10-bit payload, never an RGBA8 monitoring wire.
/// </summary>
public static class HdrContributionFrameEvidenceWire
{
    public const int FormatVersion = 1;
    public const int HeaderBytes = 384;
    private const uint Magic = 0x52444852; // RHDR in little endian
    private const int HashOffset = 84;
    private const int ProvenanceLengthOffset = 116;
    private const int ProvenanceOffset = 118;
    private const int MaximumProvenanceBytes = 256;
    private const int ReservedOffset = 374;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(HdrContributionFrameEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var provenance = StrictUtf8.GetBytes(evidence.Profile.Metadata.Provenance);
        if (provenance.Length is < 1 or > MaximumProvenanceBytes)
            throw new ArgumentException("A bounded, authoritative native metadata provenance is required.", nameof(evidence));
        // Explicitly preserve tight-profile identity in this V1 sidecar.
        if (!evidence.Layout.ExactlyMatches(HdrNativeFrameLayout.TightlyPacked(
            evidence.Profile.Width, evidence.Profile.Height, evidence.Profile.Layout)))
            throw new NotSupportedException(
                "HDR sidecar v1 records exact tightly packed 4:2:2; arbitrary pitched planes require a new negotiated version.");

        var data = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), evidence.Profile.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), evidence.Profile.Height);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16, 8), evidence.Profile.FrameRate.Numerator);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24, 8), evidence.Profile.FrameRate.Denominator);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32, 4), (int)evidence.Profile.Layout);
        var color = evidence.Profile.Metadata;
        data[36] = color.PrimariesCicp;
        data[37] = color.TransferCicp;
        data[38] = color.MatrixCicp;
        data[39] = color.LimitedRange ? (byte)1 : (byte)0;
        data[40] = color.BitDepth;
        data[41] = (byte)color.ChromaLocation;
        data[42] = (byte)color.Alpha;
        data[43] = (byte)color.Authority;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(44, 8), evidence.Timing.SequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(52, 8), evidence.Timing.PresentationTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(60, 8), evidence.Timing.Timebase.Numerator);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(68, 8), evidence.Timing.Timebase.Denominator);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(76, 8), evidence.Layout.RequiredBytes);
        evidence.ContentSha256.Span.CopyTo(data.AsSpan(HashOffset, 32));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(ProvenanceLengthOffset, 2),
            checked((ushort)provenance.Length));
        provenance.CopyTo(data.AsSpan(ProvenanceOffset));
        return data;
    }

    public static HdrContributionFrameEvidence Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != HeaderBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2)) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2)) != HeaderBytes ||
            bytes[ReservedOffset..].IndexOfAnyExcept((byte)0) >= 0)
            throw new FormatException("HDR contribution evidence magic, version, size or reserved bytes are invalid.");

        var provenanceLength = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.Slice(ProvenanceLengthOffset, 2));
        if (provenanceLength is < 1 or > MaximumProvenanceBytes ||
            bytes.Slice(ProvenanceOffset + provenanceLength,
                MaximumProvenanceBytes - provenanceLength).IndexOfAnyExcept((byte)0) >= 0)
            throw new FormatException("HDR metadata provenance length or padding is invalid.");

        string provenance;
        try { provenance = StrictUtf8.GetString(bytes.Slice(ProvenanceOffset, provenanceLength)); }
        catch (DecoderFallbackException e) { throw new FormatException("HDR metadata provenance is not valid UTF-8.", e); }

        try
        {
            var rate = new FrameRate(
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(16, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(24, 8)));
            var metadata = new HdrSignalMetadata(bytes[36], bytes[37], bytes[38],
                bytes[39] == 1, bytes[40], (HdrChromaSampleLocation)bytes[41],
                (HdrPrecisionAlphaMode)bytes[42],
                (ColorMetadataAuthority)bytes[43], provenance);
            if (bytes[39] != 1)
                throw new FormatException("HLG contribution video range must be explicitly limited.");
            var profile = new HdrContributionVideoProfile(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4)),
                rate, (HdrNativePixelLayout)BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(32, 4)),
                metadata);
            if (!profile.IsExactR153LiveHlg)
                throw new FormatException("No exact, authoritative R153 HDR profile matches this sidecar.");
            var layout = HdrNativeFrameLayout.TightlyPacked(
                profile.Width, profile.Height, profile.Layout);
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(76, 8)) != layout.RequiredBytes)
                throw new FormatException("HDR sidecar native payload size is inconsistent.");
            var timebase = new Timebase(
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(60, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(68, 8)));
            var timing = new FrameTiming(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(44, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(52, 8)), timebase);
            return new HdrContributionFrameEvidence(profile, layout, timing,
                bytes.Slice(HashOffset, 32));
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or DivideByZeroException)
        {
            throw new FormatException("HDR sidecar contains invalid rational, color or plane metadata.", e);
        }
    }
}
