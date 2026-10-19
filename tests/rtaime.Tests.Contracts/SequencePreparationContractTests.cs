// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using Xunit;

namespace rtaime.Tests.Contracts;

public sealed class SequencePreparationContractTests
{
    private static readonly Guid Saved = Guid.Parse("c7000000-0000-0000-0000-000000000001");
    private static readonly string Host = "c7000000-0000-0000-0000-000000000002";
    private static readonly string Prepared = "c7000000-0000-0000-0000-000000000003";

    [Fact]
    public void Preparation_request_is_bound_to_saved_revision_and_real_RuntimeHost_epoch()
    {
        var request = new SequencePreparationRequest(SequencePreparationContract.Version, Saved, 7, Host);
        request.Validate();
        Assert.Throws<ArgumentException>(() => (request with { Version = "1.0" }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { SequenceId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { ExpectedAuthoringRevision = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { ExpectedRuntimeHostInstanceId = "unknown" }).Validate());
    }

    [Fact]
    public void Preparation_reference_never_accepts_unknown_ownership()
    {
        var reference = new SequencePreparationReference(
            SequencePreparationContract.Version, Saved, 7, Host, Prepared, 11);
        reference.Validate();
        Assert.Throws<ArgumentException>(() => (reference with { PreparedExecutionId = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (reference with { RuntimeHostInstanceId = "stale" }).Validate());
        Assert.Throws<ArgumentException>(() => (reference with { Generation = 0 }).Validate());
    }

    [Fact]
    public void Staged_native_owners_must_not_be_reported_as_Program_playback()
    {
        var status = new SequencePreparationStatus(
            SequencePreparationContract.Version, Saved, 7, Host, Prepared,
            11, 2, "PREPARED", null, "Native decoders are prepared.");
        status.Validate();
        Assert.Throws<InvalidDataException>(() => (status with { DecoderOwners = 5 }).Validate());
        Assert.Throws<InvalidDataException>(() => (status with { State = "PLAYING" }).Validate());
        Assert.Throws<InvalidDataException>(() => (status with { State = "CANCELED" }).Validate());
        var canceled = status with { State = "CANCELED", DecoderOwners = 0 };
        canceled.Validate();
        Assert.Throws<InvalidDataException>(() => (status with { Detail = new string('X', 513) }).Validate());
    }

    [Fact]
    public void Strict_transport_parser_rejects_unrecognized_and_duplicate_fields()
    {
        var request = new SequencePreparationRequest(SequencePreparationContract.Version, Saved, 7, Host);
        var json = System.Text.Json.JsonSerializer.Serialize(request, SequenceJson.Options);
        Assert.Equal(request, SequenceJson.Read<SequencePreparationRequest>(json));
        Assert.Throws<InvalidDataException>(() => SequenceJson.Read<SequencePreparationRequest>(
            json.Insert(json.LastIndexOf('}'), ",\"sequenceId\":\"" + Saved + "\"")));
        Assert.Throws<System.Text.Json.JsonException>(() => SequenceJson.Read<SequencePreparationRequest>(
            json.Insert(json.LastIndexOf('}'), ",\"localPath\":\"C:\\\\media.mp4\"")));
    }
}
