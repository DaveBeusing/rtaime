// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class MediaInterchangeMetadataContractTests
{
    private static readonly string Fingerprint = new('A', 64);
    private static readonly DateTimeOffset Observed = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static MediaInterchangeField Field(string path, string value,
        MediaInterchangeSource origin = MediaInterchangeSource.Embedded) =>
        new(path, value, origin, "embedded fixture metadata", Observed);

    private static MediaInterchangeMetadata Projection(params MediaInterchangeField[] fields) =>
        new(MediaInterchangeMetadata.CurrentFormat, Fingerprint, fields);

    [Fact]
    public void Json_roundtrip_preserves_exact_rate_timecode_roles_rights_and_unmapped_source_values()
    {
        var source = Projection(
            Field("title", "Studio feed", MediaInterchangeSource.User),
            Field("rights", "License information retained"),
            Field("video.0.frameRate", "30000/1001"),
            Field("video.0.transfer", "PQ"),
            Field("audio.0.language", "de-DE"),
            Field("audio.0.channelRole", "L,R"),
            Field("caption.0.format", "CEA-608"),
            Field("timecode.start", "01:02:03;04"),
            Field("timecode.dropFrame", "true")) with
        {
            Unmapped = new Dictionary<string, string> { ["supplier:opaque"] = "{\\"nested\\":[1,2]}" }
        };

        var json = MediaInterchangeJson.Export(source);
        var restored = MediaInterchangeJson.Import(json, Fingerprint);

        Assert.Equal(source.Format, restored.Format);
        Assert.Equal(source.FingerprintSha256, restored.FingerprintSha256);
        Assert.Equal(source.Fields, restored.Fields);
        Assert.Equal(source.Unmapped!["supplier:opaque"], restored.Unmapped!["supplier:opaque"]);
        Assert.Contains(restored.Fields, field =>
            field.Path == "title" && field.Source == MediaInterchangeSource.User);
        Assert.Contains("30000/1001", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:01:00;00", "true")]
    [InlineData("00:01:00;01", "true")]
    [InlineData("00:00:00:30", "false")]
    [InlineData("00:00:00;01", "false")]
    [InlineData("00:00:00:01", "true")]
    public void Invalid_drop_frame_labels_and_mode_mismatches_fail_closed(string value, string drop)
    {
        var projection = Projection(
            Field("video.0.frameRate", "30000/1001"),
            Field("timecode.start", value),
            Field("timecode.dropFrame", drop));
        Assert.Throws<InvalidDataException>(() => projection.Validate());
    }

    [Fact]
    public void Non_drop_frame_timecode_supports_explicit_rational_rate()
    {
        var projection = Projection(
            Field("video.0.frameRate", "25/1"),
            Field("timecode.start", "10:11:12:24"),
            Field("timecode.dropFrame", "false"));
        projection.Validate();
        Assert.Equal("10:11:12:24", MediaInterchangeJson.Import(MediaInterchangeJson.Export(projection)).Fields[1].Value);
    }

    [Fact]
    public void Lower_trust_source_cannot_silently_overwrite_user_authored_metadata()
    {
        var existing = Projection(Field("title", "Approved title", MediaInterchangeSource.User));
        var probe = Projection(Field("title", "Filename title", MediaInterchangeSource.ProviderProbe));
        Assert.Throws<InvalidOperationException>(() => existing.Merge(probe));
        var sameValue = Projection(Field("title", "Approved title", MediaInterchangeSource.ProviderProbe));
        Assert.Equal(MediaInterchangeSource.User, Assert.Single(existing.Merge(sameValue).Fields).Source);
        var userCorrection = Projection(Field("title", "Corrected", MediaInterchangeSource.User));
        Assert.Equal("Corrected", Assert.Single(existing.Merge(userCorrection).Fields).Value);
    }

    [Fact]
    public void Unmapped_conflict_or_fingerprint_mismatch_is_rejected()
    {
        var original = Projection(Field("language", "en")) with
        {
            Unmapped = new Dictionary<string, string> { ["vendor"] = "A" }
        };
        Assert.Throws<InvalidOperationException>(() => original.Merge(
            Projection() with { Unmapped = new Dictionary<string, string> { ["vendor"] = "B" } }));
        Assert.Throws<InvalidDataException>(() =>
            MediaInterchangeJson.Import(MediaInterchangeJson.Export(original), new string('B', 64)));
    }

    [Fact]
    public void Unknown_json_properties_and_oversized_documents_are_not_silently_dropped()
    {
        var source = MediaInterchangeJson.Export(Projection(Field("title", "A")));
        var malformed = source.Replace("\\"format\\":", "\\"unreviewed\\":1,\\"format\\":", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => MediaInterchangeJson.Import(malformed));
        Assert.Throws<InvalidDataException>(() => MediaInterchangeJson.Import(new string('x', 2049)));
        Assert.Throws<InvalidDataException>(() => Projection(Field("video.0.frameRate", "29.97")).Validate());
    }
}
