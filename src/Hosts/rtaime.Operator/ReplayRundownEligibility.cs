// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

/// <summary>
/// Confirms the materialized replay's immutable catalogue identity and source
/// fingerprint before a local Rundown draft can reference it. This is not
/// decoder admission; PREPARE must still qualify the current native source.
/// </summary>
internal static class ReplayRundownEligibility
{
    internal static bool TryVerifyAsset(
        ReplayClipAssetResult? clip, MediaAssetDescriptor? asset, out string reason)
    {
        if (clip is not { Succeeded: true, AssetId: { Length: > 0 },
            SourceLocation: { Length: > 0 }, Sha256: { Length: 64 } } ||
            clip.Version != ReplayControlContractVersion.Current ||
            clip.SourceIn < TimeSpan.Zero || clip.SourceOut <= clip.SourceIn ||
            clip.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            reason = "Replay result is not finalized with a valid time range, asset ID and SHA-256.";
            return false;
        }
        if (asset is null || !string.Equals(asset.AssetId.ToString(), clip.AssetId,
            StringComparison.OrdinalIgnoreCase))
        {
            reason = "The adopted replay asset is absent from the current Media Library.";
            return false;
        }
        if (asset.Availability != MediaAssetAvailability.Online ||
            !string.Equals(asset.SourceLocation, clip.SourceLocation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(asset.FingerprintSha256, clip.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Replay source path, fingerprint or online availability changed. Refresh/relink before staging.";
            return false;
        }
        if (asset.VideoCodec != MediaVideoCodec.H264 ||
            asset.AudioCodec != MediaAudioCodec.Aac ||
            asset.Duration <= TimeSpan.Zero)
        {
            reason = "Replay asset lacks qualified H.264/AAC video and audio.";
            return false;
        }
        // The source capture timestamps are not the file's local IN/OUT; the
        // finalized clip is rebased to zero. Allow one frame of container rounding.
        var frameRate = asset.VideoFormat.FrameRate;
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
        {
            reason = "Replay source frame cadence is unavailable.";
            return false;
        }
        var tolerance = TimeSpan.FromSeconds(
            (double)frameRate.Denominator / frameRate.Numerator + 0.001);
        if ((asset.Duration - clip.Duration).Duration() > tolerance)
        {
            reason = "The adopted clip duration differs from the finalized capture range.";
            return false;
        }
        reason = "Catalogued replay content and source time range match; resident decoder not yet proven.";
        return true;
    }
}
