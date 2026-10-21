// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using Xunit;

namespace rtaime.Tests.Contracts;

public sealed class SequenceOutputContractTests
{
    private static readonly SequencePreparationReference Reference = new(
        SequencePreparationContract.Version, Guid.NewGuid(), 7,
        Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 1);

    private static SequenceOutputCommand Command() => new(
        SequenceOutputContract.Version, Reference,
        Guid.NewGuid().ToString("D"), 11, Guid.NewGuid());

    [Fact]
    public void Source_and_revision_bound_Take_requires_opt_in_version_and_unique_transaction()
    {
        var accepted = Command();
        accepted.Validate();
        Assert.Throws<InvalidDataException>(() =>
            (accepted with { Version = "rtaime.sequence-output.legacy" }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (accepted with { PreviewSourceId = Guid.Empty.ToString("D") }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (accepted with { TransactionId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (accepted with { Preparation = Reference with { AuthoringRevision = 0 } }).Validate());
    }

    [Theory]
    [InlineData("UNAVAILABLE")]
    [InlineData("PREVIEW_READY")]
    [InlineData("BUFFERING")]
    [InlineData("TAKE_PENDING")]
    [InlineData("TAKE_COMMITTED")]
    [InlineData("PROGRAM_CONFIRMED")]
    [InlineData("STALE")]
    [InlineData("FAILED")]
    public void Output_reports_only_explicit_separate_authority_and_sink_states(string state)
    {
        var output = new SequenceOutputObservation(
            SequenceOutputContract.Version, Reference, Guid.NewGuid().ToString("D"),
            state, 4, 0, 6, state == "PROGRAM_CONFIRMED" ? 6 : null,
            12, null, "Native media and Program status must be source-bound.");
        output.Validate();
    }

    [Fact]
    public void Program_readback_claim_is_denied_without_a_matching_source_boundary()
    {
        var command = Command();
        var output = new SequenceOutputObservation(
            SequenceOutputContract.Version, Reference, command.PreviewSourceId,
            "PROGRAM_CONFIRMED", 1, 0, 4, 5, 12, null, "Foraged Program evidence");
        Assert.Throws<InvalidDataException>(() => output.Validate());
        Assert.Throws<InvalidDataException>(() =>
            (output with { OutputState = "OUTPUT_PASS", LastProgramSlot = 4 }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (output with { Version = "unsupported", LastProgramSlot = 4 }).Validate());
    }
}
