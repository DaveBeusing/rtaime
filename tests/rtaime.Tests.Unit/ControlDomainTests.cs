using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class ControlDomainTests
{
    [Fact]
    public void Initialize_creates_separate_desired_and_authoritative_state_at_revision_zero()
    {
        var fixture = CreateFixture();

        var result = ControlDomainEngine.Initialize(fixture.Specification);

        Assert.True(result.Succeeded);
        var state = Assert.IsType<ControlStateSnapshot>(result.State);
        Assert.Equal(Revision.Initial, state.Authoritative.Revision);
        Assert.Equal(Revision.Initial, state.Desired.BasedOnAuthoritativeRevision);
        Assert.Equal(fixture.Specification.InitialRouting, state.Authoritative.Routing);
        Assert.Equal(fixture.Specification.InitialRouting, state.Desired.Routing);
        Assert.NotSame(state.Authoritative, state.Desired);
    }

    [Fact]
    public void Initialize_fails_closed_when_initial_routing_references_unknown_sources()
    {
        var source = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera A");
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            ProductionId.New(),
            "Invalid production",
            new[] { source },
            new ProductionRoutingState(ProductionSourceId.New(), ProductionSourceId.New()));

        var result = ControlDomainEngine.Initialize(specification);

        Assert.False(result.Succeeded);
        Assert.Null(result.State);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.specification.preview_source_unknown");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.specification.program_source_unknown");
    }

    [Fact]
    public void Select_preview_commits_desired_then_advances_authoritative_revision_once()
    {
        var fixture = CreateFixture();
        var initial = InitializedState(fixture.Specification);
        var command = new SelectPreviewCommand(
            Metadata(fixture.Specification, initial.Authoritative.Revision),
            fixture.SourceB.SourceId);

        var result = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);

        Assert.True(result.Committed);
        var desired = Assert.IsType<DesiredProductionState>(result.DesiredState);
        Assert.Equal(initial.Authoritative.Revision, desired.BasedOnAuthoritativeRevision);
        Assert.Equal(fixture.SourceB.SourceId, desired.Routing.PreviewSourceId);
        Assert.Equal(initial.Authoritative.Routing.ProgramSourceId, desired.Routing.ProgramSourceId);
        Assert.Equal(initial.Authoritative.Revision.Next(), result.AuthoritativeState.Revision);
        Assert.Equal(desired.Routing, result.AuthoritativeState.Routing);
        Assert.Equal(Revision.Initial, initial.Authoritative.Revision);
    }

    [Fact]
    public void Cut_program_changes_only_program_route_and_advances_revision_once()
    {
        var fixture = CreateFixture();
        var initial = InitializedState(fixture.Specification);
        var command = new CutProgramCommand(
            Metadata(fixture.Specification, initial.Authoritative.Revision),
            fixture.SourceA.SourceId);

        var result = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);

        Assert.True(result.Committed);
        var desired = Assert.IsType<DesiredProductionState>(result.DesiredState);
        Assert.Equal(initial.Authoritative.Routing.PreviewSourceId, desired.Routing.PreviewSourceId);
        Assert.Equal(fixture.SourceA.SourceId, desired.Routing.ProgramSourceId);
        Assert.Equal(initial.Authoritative.Revision.Next(), result.AuthoritativeState.Revision);
    }

    [Fact]
    public void Stale_revision_is_rejected_without_authoritative_mutation()
    {
        var fixture = CreateFixture();
        var initial = InitializedState(fixture.Specification);
        var command = new SelectPreviewCommand(
            Metadata(fixture.Specification, new Revision(9)),
            fixture.SourceB.SourceId);

        var result = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);

        Assert.False(result.Committed);
        Assert.Null(result.DesiredState);
        Assert.Same(initial.Authoritative, result.AuthoritativeState);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.command.revision_conflict");
    }

    [Fact]
    public void Command_for_different_production_is_rejected()
    {
        var fixture = CreateFixture();
        var initial = InitializedState(fixture.Specification);
        var metadata = new ControlCommandMetadata(
            ControlContractVersion.Current,
            CommandId.New(),
            ProductionId.New(),
            initial.Authoritative.Revision);
        var command = new SelectPreviewCommand(metadata, fixture.SourceB.SourceId);

        var result = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);

        Assert.False(result.Committed);
        Assert.Same(initial.Authoritative, result.AuthoritativeState);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.command.production_mismatch");
    }

    [Fact]
    public void Unknown_command_source_is_rejected()
    {
        var fixture = CreateFixture();
        var initial = InitializedState(fixture.Specification);
        var command = new CutProgramCommand(
            Metadata(fixture.Specification, initial.Authoritative.Revision),
            ProductionSourceId.New());

        var result = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);

        Assert.False(result.Committed);
        Assert.Same(initial.Authoritative, result.AuthoritativeState);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.command.source_unknown");
    }

    [Fact]
    public void Invalid_authoritative_route_is_rejected_before_command_evaluation_commits()
    {
        var fixture = CreateFixture();
        var invalidState = new AuthoritativeProductionState(
            ControlContractVersion.Current,
            fixture.Specification.ProductionId,
            Revision.Initial,
            new ProductionRoutingState(ProductionSourceId.New(), fixture.SourceB.SourceId));
        var command = new SelectPreviewCommand(
            Metadata(fixture.Specification, invalidState.Revision),
            fixture.SourceA.SourceId);

        var result = ControlDomainEngine.Apply(fixture.Specification, invalidState, command);

        Assert.False(result.Committed);
        Assert.Same(invalidState, result.AuthoritativeState);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.state.preview_source_unknown");
    }

    [Fact]
    public void Exhausted_revision_is_rejected_instead_of_overflowing()
    {
        var fixture = CreateFixture();
        var exhausted = new AuthoritativeProductionState(
            ControlContractVersion.Current,
            fixture.Specification.ProductionId,
            new Revision(ulong.MaxValue),
            fixture.Specification.InitialRouting);
        var command = new SelectPreviewCommand(
            Metadata(fixture.Specification, exhausted.Revision),
            fixture.SourceB.SourceId);

        var result = ControlDomainEngine.Apply(fixture.Specification, exhausted, command);

        Assert.False(result.Committed);
        Assert.Same(exhausted, result.AuthoritativeState);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.state.revision_exhausted");
    }

    [Fact]
    public void Same_valid_input_produces_equal_deterministic_state_transition()
    {
        var fixture = CreateFixture();
        var initial = InitializedState(fixture.Specification);
        var command = new CutProgramCommand(
            Metadata(fixture.Specification, initial.Authoritative.Revision),
            fixture.SourceA.SourceId);

        var first = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);
        var second = ControlDomainEngine.Apply(fixture.Specification, initial.Authoritative, command);

        Assert.True(first.Committed);
        Assert.True(second.Committed);
        Assert.Equal(first.DesiredState, second.DesiredState);
        Assert.Equal(first.AuthoritativeState, second.AuthoritativeState);
    }

    private static ControlStateSnapshot InitializedState(ProductionSpecification specification)
    {
        var result = ControlDomainEngine.Initialize(specification);
        Assert.True(result.Succeeded);
        return Assert.IsType<ControlStateSnapshot>(result.State);
    }

    private static ControlCommandMetadata Metadata(ProductionSpecification specification, Revision revision) =>
        new(
            ControlContractVersion.Current,
            CommandId.New(),
            specification.ProductionId,
            revision);

    private static Fixture CreateFixture()
    {
        var sourceA = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera A");
        var sourceB = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera B");
        var productionId = ProductionId.New();
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            productionId,
            "Reference production",
            new[] { sourceA, sourceB },
            new ProductionRoutingState(sourceA.SourceId, sourceB.SourceId));

        return new Fixture(specification, sourceA, sourceB);
    }

    private sealed record Fixture(
        ProductionSpecification Specification,
        ProductionSourceSpecification SourceA,
        ProductionSourceSpecification SourceB);
}
