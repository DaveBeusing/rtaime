using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Failure;

public sealed class CrossHostCommitFailureTests
{
    [Fact]
    public async Task Rejected_runtime_commit_discards_staged_control_state_and_preserves_authoritative_revision()
    {
        var productionId = new ProductionId(Identity.Parse("81000000-0000-0000-0000-000000000001"));
        var sourceA = new ProductionSourceId(Identity.Parse("81000000-0000-0000-0000-00000000000a"));
        var sourceB = new ProductionSourceId(Identity.Parse("81000000-0000-0000-0000-00000000000b"));
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            productionId,
            "Cross-host commit failure proof",
            new[]
            {
                new ProductionSourceSpecification(sourceA, "Input 1"),
                new ProductionSourceSpecification(sourceB, "Input 2")
            },
            new ProductionRoutingState(sourceA, sourceA));
        var virtualMedia = new VirtualMediaReferenceProvider(
            new MediaSourceId(sourceA.Value),
            new MediaSourceId(sourceB.Value),
            VideoFormat.Hd1080p50Rgba8);

        await using var journal = new BoundedProductionJournal(64);
        var control = new ControlHostService(specification, new[] { virtualMedia.Descriptor }, journal);

        var initial = control.Initialize();
        Assert.True(initial.Accepted);
        Assert.True(control.HasPendingExecution);
        var initialExecution = Assert.IsType<ControlHostExecutionPackage>(initial.Execution);
        var initialCommit = new RuntimeCommitResult(
            RuntimeContractVersion.Current,
            RuntimeCommitStatus.Committed,
            ExecutionInstanceId.New(),
            new Revision(1),
            null);
        var initialConfirmation = control.ConfirmRuntimeCommit(
            initialExecution.PreparedExecution.PreparedExecutionId,
            initialCommit);
        Assert.True(initialConfirmation.Committed);
        Assert.Equal(Revision.Initial, control.State.Revision);
        Assert.Equal(sourceA, control.State.Routing.PreviewSourceId);

        var command = new SelectPreviewCommand(
            new ControlCommandMetadata(
                ControlContractVersion.Current,
                CommandId.New(),
                productionId,
                control.State.Revision),
            sourceB);
        var staged = control.SelectPreview(command);

        Assert.True(staged.Accepted);
        Assert.True(control.HasPendingExecution);
        Assert.Equal(new Revision(1), staged.State.Revision);
        Assert.Equal(sourceB, staged.State.Routing.PreviewSourceId);
        Assert.Equal(Revision.Initial, control.State.Revision);
        Assert.Equal(sourceA, control.State.Routing.PreviewSourceId);

        var runtimeFailure = new Failure(
            "runtime.commit.injected_rejection",
            "Injected Runtime commit rejection for AP-12 failure evidence.");
        var rejectedCommit = new RuntimeCommitResult(
            RuntimeContractVersion.Current,
            RuntimeCommitStatus.Rejected,
            null,
            initialCommit.ExecutionRevision,
            runtimeFailure);
        var confirmation = control.ConfirmRuntimeCommit(
            staged.Execution!.PreparedExecution.PreparedExecutionId,
            rejectedCommit);

        Assert.False(confirmation.Committed);
        Assert.Equal(runtimeFailure, confirmation.Failure);
        Assert.False(control.HasPendingExecution);
        Assert.Equal(Revision.Initial, control.State.Revision);
        Assert.Equal(sourceA, control.State.Routing.PreviewSourceId);
        Assert.Equal(sourceA, control.State.Routing.ProgramSourceId);

        await journal.FlushAsync();
        Assert.Contains(journal.Entries, entry => entry.Event.Code == "runtime.commit.rejected");
        Assert.DoesNotContain(
            journal.Entries,
            entry => entry.Event.Code == "control.authoritative.committed" && entry.Event.AuthoritativeRevision == new Revision(1));
    }
}
