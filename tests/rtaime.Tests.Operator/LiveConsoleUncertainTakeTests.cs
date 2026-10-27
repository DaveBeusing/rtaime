// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class LiveConsoleUncertainTakeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_take_cannot_be_reissued_until_explicit_Control_synchronization(bool dissolve)
    {
        var transport = new UncertainTakeTransport();
        await using var owner = new OperatorViewModel(new OperatorControlClient(transport),
            runtimeReadiness: new ReadyService());

        owner.SynchronizeCommand.Execute(null);
        await UntilAsync(() => owner.IsConnected && !owner.IsBusy);
        Assert.True(owner.CutCommand.CanExecute(null));
        Assert.True(owner.DissolveCommand.CanExecute(null));
        Assert.Equal(0, transport.TakeRequests);

        (dissolve ? owner.DissolveCommand : owner.CutCommand).Execute(null);
        await UntilAsync(() => owner.LiveTakeNeedsReconciliation && !owner.IsBusy);
        Assert.Equal(1, transport.TakeRequests);
        Assert.Equal(transport.ConfirmedProgramSourceId, owner.ProgramSourceId);
        Assert.Contains("UNCONFIRMED TAKE", owner.LiveTakeEvidence);
        Assert.Contains("synchronize", owner.LiveAvailability, StringComparison.OrdinalIgnoreCase);
        Assert.False(owner.CutCommand.CanExecute(null));
        Assert.False(owner.DissolveCommand.CanExecute(null));
        Assert.False(owner.SetPreviewCommand.CanExecute(null));
        Assert.False(owner.CanOperateLive);

        // No hidden retry. An ordinary latest-snapshot presentation refresh
        // cannot unlock the ambiguous request: the operator must sync explicitly.
        owner.CutCommand.Execute(null);
        owner.DissolveCommand.Execute(null);
        Assert.Equal(1, transport.TakeRequests);

        owner.SynchronizeCommand.Execute(null);
        await UntilAsync(() => !owner.IsBusy && !owner.LiveTakeNeedsReconciliation);
        Assert.True(owner.CutCommand.CanExecute(null));
        Assert.True(owner.CanOperateLive);
        Assert.Equal(transport.ConfirmedProgramSourceId, owner.ProgramSourceId);
        Assert.Equal(1, transport.TakeRequests);
        Assert.True(transport.SnapshotRequests >= 2);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Authoritative LIVE test condition did not settle.");
            await Task.Delay(10);
        }
    }

    private sealed class UncertainTakeTransport : IOperatorControlTransport
    {
        private readonly ProductionId _production = ProductionId.New();
        private readonly ProductionSourceId _source = ProductionSourceId.New();
        internal int SnapshotRequests { get; private set; }
        internal int TakeRequests { get; private set; }
        internal string ConfirmedProgramSourceId => _source.ToString();

        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            SnapshotRequests++;
            return ValueTask.FromResult(new OperatorStatusSnapshot(
                new AuthoritativeProductionState(ControlContractVersion.Current, _production,
                    new Revision(1), new ProductionRoutingState(_source, _source)),
                [new OperatorSourceDescriptor(_source.ToString(), "Confirmed source")],
                "READY", "LOCKED", "HEALTHY", "OFF", "IDLE", false, 0.2));
        }

        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<OperatorMutationResponse>(
                new InvalidOperationException("Selection is not part of this test."));

        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command,
            CancellationToken cancellationToken = default)
        {
            TakeRequests++;
            return ValueTask.FromException<OperatorMutationResponse>(
                new InvalidOperationException("The committed result could not be determined."));
        }

        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command,
            CancellationToken cancellationToken = default)
        {
            TakeRequests++;
            return ValueTask.FromException<OperatorMutationResponse>(
                new InvalidOperationException("The committed result could not be determined."));
        }
    }

    private sealed class ReadyService : IRuntimeReadinessService
    {
        public RuntimeReadinessSnapshot Current { get; } = new(RuntimeReadinessState.Ready,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            Array.Empty<ReadinessReason>(), true, RuntimePerformanceVerificationSnapshot.Unverified);
        public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed { add { } remove { } }
        public void Observe(RuntimeReadinessObservation observation) { }
        public void InvalidatePerformance(string detail) { }
    }
}
