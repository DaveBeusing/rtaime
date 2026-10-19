// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using Xunit;

namespace rtaime.Tests.Contracts;

public sealed class SequenceShuttleContractTests
{
    private static readonly SequencePreparationReference Preparation = new(
        SequencePreparationContract.Version,
        Guid.Parse("cc000000-0000-0000-0000-000000000001"), 9,
        "cc000000-0000-0000-0000-000000000002",
        "cc000000-0000-0000-0000-000000000003", 3);

    [Fact]
    public void Command_is_exactly_prepared_and_rejects_cross_action_payloads()
    {
        var cue = new SequenceShuttleRequest(SequenceShuttleContract.Version,
            Preparation, 0, SequenceShuttleAction.Cue, PositionTicks: 4800);
        cue.Validate();
        Assert.Throws<ArgumentException>(() => (cue with { Version = "1.0" }).Validate());
        Assert.Throws<ArgumentException>(() => (cue with { Action = SequenceShuttleAction.Play }).Validate());
        Assert.Throws<ArgumentException>(() => (cue with { PositionTicks = null }).Validate());
        Assert.Throws<ArgumentException>(() => (cue with { PositionTicks = SequenceContract.MaximumTime }).Validate());
        Assert.Throws<ArgumentException>(() => (cue with { Preparation = Preparation with {
            RuntimeHostInstanceId = "stale" } }).Validate());
        new SequenceShuttleRequest(SequenceShuttleContract.Version, Preparation,
            4, SequenceShuttleAction.StepBackward).Validate();
        new SequenceShuttleRequest(SequenceShuttleContract.Version, Preparation,
            4, SequenceShuttleAction.SetLoop, LoopEnabled: true).Validate();
        Assert.Throws<ArgumentException>(() => new SequenceShuttleRequest(
            SequenceShuttleContract.Version, Preparation, 4, SequenceShuttleAction.SetLoop).Validate());
    }

    [Fact]
    public void Shuttle_observation_never_claims_an_unverified_Program_or_Preview_frame()
    {
        var observed = new SequenceShuttleObservation(SequenceShuttleContract.Version,
            Preparation, 3, 7, 0, "PAUSED", 4800, 23, false,
            true, "UNAVAILABLE", "Native decoders ready; no Preview sink.");
        observed.Validate();
        Assert.Throws<InvalidDataException>(() =>
            (observed with { OutputState = "VISIBLE" }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (observed with { State = "COMMITTED" }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (observed with { DecodeGeneration = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (observed with { Detail = new string('X', 513) }).Validate());
    }

    [Fact]
    public void Strict_parser_rejects_unrecognized_media_paths_and_duplicated_authority()
    {
        var request = new SequenceShuttleRequest(SequenceShuttleContract.Version,
            Preparation, 3, SequenceShuttleAction.Play);
        var json = System.Text.Json.JsonSerializer.Serialize(request, SequenceJson.Options);
        Assert.Equal(request, SequenceJson.Read<SequenceShuttleRequest>(json));
        Assert.Throws<System.Text.Json.JsonException>(() => SequenceJson.Read<SequenceShuttleRequest>(
            json.Insert(json.LastIndexOf('}'), ",\"localPath\":\"C:/media/input.mp4\"")));
        Assert.Throws<InvalidDataException>(() => SequenceJson.Read<SequenceShuttleRequest>(
            json.Insert(json.LastIndexOf('}'), ",\"expectedStateRevision\":12")));
    }
}
