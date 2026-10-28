// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class HdrContributionFrameEvidenceWireTests
{
    private static HdrContributionFrameEvidence Evidence(
        FrameRate? rate = null, byte[]? hash = null)
    {
        var actualRate = rate ?? FrameRate.Fps50;
        var profile = new HdrContributionVideoProfile(3840, 2160, actualRate,
            HdrNativePixelLayout.PlanarYcbcr422P10Le,
            HdrSignalMetadata.AuthoritativeHlg422("source-probe:r153/reference-2026-10"));
        var layout = HdrNativeFrameLayout.TightlyPacked(profile.Width, profile.Height, profile.Layout);
        return new HdrContributionFrameEvidence(profile, layout,
            new FrameTiming(120, 240_240, new Timebase(1, 90_000)),
            hash ?? SHA256.HashData(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(50, 1)]
    [InlineData(60_000, 1_001)]
    public void Versioned_sidecar_roundtrips_exact_native_profile_color_provenance_and_PTS(
        long numerator, long denominator)
    {
        var original = Evidence(new FrameRate(numerator, denominator));
        var encoded = HdrContributionFrameEvidenceWire.Encode(original);
        var decoded = HdrContributionFrameEvidenceWire.Decode(encoded);
        Assert.Equal(HdrContributionFrameEvidenceWire.HeaderBytes, encoded.Length);
        Assert.Equal(original.Profile.FrameRate, decoded.Profile.FrameRate);
        Assert.Equal(original.Profile.Metadata, decoded.Profile.Metadata);
        Assert.Equal(original.Layout.RequiredBytes, decoded.Layout.RequiredBytes);
        Assert.Equal(original.Timing, decoded.Timing);
        Assert.Equal(original.ContentSha256.ToArray(), decoded.ContentSha256.ToArray());
        Assert.Equal(encoded, HdrContributionFrameEvidenceWire.Encode(decoded));
        Assert.Equal(new CompatibilityVersion(1, 0), MediaContractVersion.Current);
    }

    [Fact]
    public void SHA256_is_bound_to_the_exact_native_frame_bytes()
    {
        var profile = HdrContributionProfiles.Live2160p50("source-probe");
        var layout = HdrNativeFrameLayout.TightlyPacked(3840, 2160, profile.Layout);
        var payload = new byte[checked((int)layout.RequiredBytes)];
        payload[13] = 0x40;
        var source = new HdrContributionFrameEvidence(profile, layout,
            new FrameTiming(1, 1, new Timebase(1, 50)), SHA256.HashData(payload));
        var proof = HdrContributionFrameEvidenceWire.Decode(
            HdrContributionFrameEvidenceWire.Encode(source));
        Assert.True(proof.VerifyPayload(payload));
        payload[13] = 0;
        Assert.False(proof.VerifyPayload(payload));
        Assert.False(proof.VerifyPayload(new byte[100]));
    }

    [Fact]
    public void Unknown_version_transfer_primaries_range_chroma_and_reserved_bytes_fail_closed()
    {
        var correct = HdrContributionFrameEvidenceWire.Encode(Evidence());
        foreach (var mutation in new Action<byte[]>[]
        {
            bytes => bytes[4] = 2, // unsupported header revision
            bytes => bytes[0] = 0, // magic
            bytes => bytes[36] = 1, // BT.709 primaries
            bytes => bytes[37] = 16, // PQ
            bytes => bytes[38] = 1, // BT.709 matrix
            bytes => bytes[39] = 0, // unverified full/unknown range
            bytes => bytes[40] = 8, // lost 10-bit precision
            bytes => bytes[41] = 2, // wrong chroma position
            bytes => bytes[42] = 2, // alpha
            bytes => bytes[43] = 2, // inferred metadata
            bytes => bytes[374] = 1, // reserved data
            bytes => bytes[373] = 1, // provenance padding
            bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(76, 8), 8),
            bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(60, 8), 0)
        })
        {
            var forged = correct.ToArray();
            mutation(forged);
            Assert.Throws<FormatException>(() =>
                HdrContributionFrameEvidenceWire.Decode(forged));
        }
    }

    [Fact]
    public void Omitted_or_oversized_sidecar_and_invalid_Utf8_are_rejected()
    {
        var correct = HdrContributionFrameEvidenceWire.Encode(Evidence());
        Assert.Throws<FormatException>(() =>
            HdrContributionFrameEvidenceWire.Decode(correct.AsSpan(0, correct.Length - 1)));
        var emptyProvenance = correct.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(emptyProvenance.AsSpan(116, 2), 0);
        Assert.Throws<FormatException>(() =>
            HdrContributionFrameEvidenceWire.Decode(emptyProvenance));
        var invalidUtf8 = correct.ToArray();
        invalidUtf8[118] = 0xFF;
        Assert.Throws<FormatException>(() =>
            HdrContributionFrameEvidenceWire.Decode(invalidUtf8));
    }
}
