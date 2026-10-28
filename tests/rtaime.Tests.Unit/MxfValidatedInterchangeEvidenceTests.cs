// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfValidatedInterchangeEvidenceTests
{
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 10, 28, 19, 0, 0, TimeSpan.Zero);
    private static readonly string Fingerprint = new('A', 64);

    [Fact]
    public void Independently_validated_MXF_streams_preserve_exact_rational_timebases_and_source_provenance()
    {
        using var input = JsonDocument.Parse("""
            {
              "video": {
                "time_base": "1001/60000",
                "color_transfer": "bt709",
                "color_primaries": "bt709",
                "tags": { "timecode": "01:02:03;04" }
              },
              "audio": {
                "time_base": "1/48000",
                "channel_layout": "stereo",
                "tags": { "language": "de-DE" }
              }
            }
            """);
        var observation = MxfValidatedInterchangeEvidence.FromValidatedStreams(
            input.RootElement.GetProperty("video"),
            input.RootElement.GetProperty("audio"),
            "60000/1001", 120, 96096);
        Assert.Equal("1001/60000", observation.VideoTimebase);
        Assert.Equal("1/48000", observation.AudioTimebase);
        Assert.Equal((ulong)120, observation.VideoFrames);
        Assert.Equal((ulong)96096, observation.AudioSampleFrames);
        Assert.Equal("01:02:03;04", observation.ReportedTimecode);
        Assert.Equal("de-DE", observation.ReportedAudioLanguage);

        var projection = observation.ToInterchangeMetadata(Fingerprint, ObservedAtUtc);
        var roundtripped = MediaInterchangeJson.Import(
            MediaInterchangeJson.Export(projection), Fingerprint);
        Assert.Contains(roundtripped.Fields, field =>
            field.Path == "video.0.frameRate" && field.Value == "60000/1001");
        Assert.Contains(roundtripped.Fields, field =>
            field.Path == "timecode.start" && field.Value == "01:02:03;04");
        Assert.Contains(roundtripped.Fields, field =>
            field.Path == "timecode.dropFrame" && field.Value == "true");
        Assert.Contains(roundtripped.Fields, field =>
            field.Path == "audio.0.language" &&
            field.Value == "de-DE" &&
            field.Source == MediaInterchangeSource.ProviderProbe);
        Assert.Equal("1001/60000", roundtripped.Unmapped!["mxf.video.timebase"]);
        Assert.Equal("1/48000", roundtripped.Unmapped["mxf.audio.timebase"]);
        Assert.DoesNotContain(roundtripped.Fields, f => f.Path == "video.0.hdr");
        Assert.DoesNotContain(roundtripped.Fields, f => f.Path == "audio.0.channelRole");
    }

    [Fact]
    public void Missing_source_timecode_language_and_hdr_must_remain_unmapped()
    {
        var observation = new MxfValidatedInterchangeEvidence(
            "50/1", "1/50", "1/48000", 50, 48000,
            null, null, null, null, null);
        var metadata = observation.ToInterchangeMetadata(Fingerprint, ObservedAtUtc);
        Assert.DoesNotContain(metadata.Fields, field =>
            field.Path is "timecode.start" or "timecode.dropFrame" or
                "audio.0.language" or "video.0.transfer" or "video.0.hdr");
        Assert.All(metadata.Fields, field =>
            Assert.Equal(MediaInterchangeSource.ProviderProbe, field.Source));
        Assert.Equal(Fingerprint, metadata.FingerprintSha256);
    }

    [Fact]
    public void Invalid_drop_frame_label_is_rejected_instead_of_publishing_false_provenance()
    {
        var observation = new MxfValidatedInterchangeEvidence(
            "60000/1001", "1001/60000", "1/48000", 60, 48048,
            "00:01:00;00", null, null, null, null);
        Assert.Throws<InvalidDataException>(() =>
            observation.ToInterchangeMetadata(Fingerprint, ObservedAtUtc));
    }

    [Fact]
    public void Invalid_unicode_language_is_retained_only_as_unknown_source_metadata()
    {
        var observation = new MxfValidatedInterchangeEvidence(
            "50/1", "1/50", "1/48000", 50, 48000,
            null, "unregistered_language:???", null, null, null);
        var metadata = observation.ToInterchangeMetadata(Fingerprint, ObservedAtUtc);
        Assert.DoesNotContain(metadata.Fields, field => field.Path == "audio.0.language");
        Assert.Equal("unregistered_language:???",
            metadata.Unmapped!["mxf.audio.language.raw"]);
    }

    [Fact]
    public void Caller_supplied_content_hash_is_validated_before_export()
    {
        var observation = new MxfValidatedInterchangeEvidence(
            "50/1", "1/50", "1/48000", 50, 48000,
            null, null, null, null, null);
        Assert.Throws<InvalidDataException>(() =>
            observation.ToInterchangeMetadata("NOT A HASH", ObservedAtUtc));
        Assert.Throws<InvalidDataException>(() =>
            observation.ToInterchangeMetadata(Fingerprint, ObservedAtUtc.ToOffset(TimeSpan.FromHours(2))));
    }
}
