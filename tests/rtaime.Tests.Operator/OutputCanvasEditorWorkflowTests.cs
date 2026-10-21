// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using Xunit;
using System.IO;
using rtaime.Client;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;

namespace rtaime.Tests.Operator;

public sealed class OutputCanvasEditorWorkflowTests
{
    private static readonly Guid Configuration = Guid.Parse("aa760000-0000-0000-0000-000000000001");
    private static readonly Guid Canvas = Guid.Parse("aa760000-0000-0000-0000-000000000002");
    private static readonly Guid Surface = Guid.Parse("aa760000-0000-0000-0000-000000000003");
    private static readonly Guid Output = Guid.Parse("aa760000-0000-0000-0000-000000000004");
    private static readonly ProductionSourceId Source = ProductionSourceId.New();

    [Fact]
    public async Task Saving_a_two_surface_geometry_is_not_live_until_explicit_qualified_apply_and_boundary()
    {
        await using var owner = ReadyOwner();
        var gateway = new ReferenceGateway();
        using var editor = new OutputCanvasEditorViewModel(owner, gateway);
        await editor.ReloadAsync();
        Assert.False(editor.IsDirty);
        Assert.Equal("UNVERIFIED", editor.ActiveRevision);

        editor.AddSurfaceCommand.Execute(null);
        Assert.True(editor.IsDirty);
        Assert.Equal("1", editor.DraftRevision);
        Assert.False(editor.SaveCommand.CanExecute(null));
        await editor.ValidateAsync();
        Assert.True(editor.IsDraftValidated);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.Equal(0, gateway.Saves);
        Assert.Equal(0, gateway.Applies);
        await editor.SaveAsync();
        Assert.Equal(1, gateway.Saves);
        Assert.Equal(0, gateway.Applies);
        Assert.Equal("1", editor.SavedRevision);
        Assert.Equal("UNVERIFIED", editor.ActiveRevision);
        Assert.False(editor.IsDirty);
        Assert.False(editor.ApplyCommand.CanExecute(null));

        await editor.ValidateAsync();
        Assert.True(editor.ApplyCommand.CanExecute(null));
        await editor.ApplyAsync();
        Assert.Equal(1, gateway.Applies);
        Assert.Equal("PENDING_BOUNDARY", editor.ActivationState);
        Assert.Equal("UNVERIFIED", editor.ActiveRevision);
        gateway.ConfirmMapping();
        await editor.ObserveBoundaryAsync();
        Assert.Equal("ACTIVE", editor.ActivationState);
        Assert.Equal("1", editor.ActiveRevision);
        Assert.True(editor.IsActiveMappingConfirmed);
        Assert.Equal("virtual-program", editor.RuntimeOutputTarget);
        Assert.Equal("1920×1080", editor.RuntimeOutputSize);

        await editor.DisableAsync();
        Assert.Equal("PENDING_BOUNDARY", editor.ActivationState);
        Assert.Equal("1", editor.ActiveRevision);
        gateway.ConfirmDisable();
        await editor.ObserveBoundaryAsync();
        Assert.Equal("UNMAPPED", editor.ActivationState);
        Assert.Equal("UNVERIFIED", editor.ActiveRevision);
    }

    [Fact]
    public async Task Remote_revision_conflict_keeps_local_geometry_and_blocks_mutations_until_discard()
    {
        await using var owner = ReadyOwner();
        var gateway = new ReferenceGateway();
        using var editor = new OutputCanvasEditorViewModel(owner, gateway);
        await editor.ReloadAsync();
        editor.NudgeSelected(12, 7);
        Assert.True(editor.IsDirty);
        var previous = editor.Fields.First(f => f.Key == "tlx").Value;
        gateway.AdvanceStored();
        await editor.ReloadAsync();

        Assert.True(editor.HasSavedConflict);
        Assert.True(editor.IsDirty);
        Assert.Equal(previous, editor.Fields.First(f => f.Key == "tlx").Value);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(0, gateway.Saves);
        await editor.ReloadAsync(discardDraft: true);
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasSavedConflict);
        Assert.Equal("1", editor.SavedRevision);
    }

    [Fact]
    public async Task Unconfirmed_save_requires_authoritative_reconciliation_not_implicit_retry()
    {
        await using var owner = ReadyOwner();
        var gateway = new ReferenceGateway { DropSaveResponse = true };
        using var editor = new OutputCanvasEditorViewModel(owner, gateway);
        await editor.ReloadAsync();
        editor.NudgeSelected(4, 5);
        Assert.False(editor.SaveCommand.CanExecute(null));
        await editor.ValidateAsync();
        await editor.SaveAsync();
        Assert.True(editor.IsDirty);
        Assert.True(editor.SaveOutcomeUnconfirmed);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(1, gateway.Saves);
        Assert.Equal(0, gateway.Applies);

        await editor.ReloadAsync();
        Assert.False(editor.SaveOutcomeUnconfirmed);
        Assert.False(editor.IsDirty);
        Assert.Equal("1", editor.SavedRevision);
        Assert.Equal(1, gateway.Saves);
        Assert.False(editor.ApplyCommand.CanExecute(null));
        await editor.ValidateAsync();
        Assert.True(editor.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Invalid_geometry_and_unsupported_provider_do_not_mutate_authoritative_output()
    {
        await using var owner = ReadyOwner();
        var gateway = new ReferenceGateway();
        using var editor = new OutputCanvasEditorViewModel(owner, gateway);
        await editor.ReloadAsync();
        Assert.NotNull(editor.SelectedSurfaceId);
        var invalid = new OutputCanvasQuad(new(0,0),new(0,0),new(5,5),new(0,5));
        editor.CommitSurfaceQuad(editor.SelectedSurfaceId!.Value, invalid);
        Assert.False(editor.IsDirty);
        Assert.Equal(0, gateway.Saves);
        Assert.Equal(0, gateway.Applies);

        gateway.RejectWith = "canvas.target_unsupported";
        editor.NudgeSelected(2, 0);
        await editor.ValidateAsync();
        Assert.False(editor.SaveCommand.CanExecute(null));
        await editor.SaveAsync();
        Assert.True(editor.IsDirty);
        Assert.NotEmpty(editor.Issues);
        Assert.Equal(0, gateway.Saves);
        Assert.Equal(0, gateway.Applies);
    }

    [Fact]
    public async Task Lost_apply_reply_fences_retry_until_runtime_confirms_exact_saved_revision()
    {
        await using var owner = ReadyOwner();
        var gateway = new ReferenceGateway { DropApplyResponse = true };
        using var editor = new OutputCanvasEditorViewModel(owner, gateway);
        await editor.ReloadAsync();
        await editor.ValidateAsync();
        Assert.True(editor.ApplyCommand.CanExecute(null));
        await editor.ApplyAsync();
        Assert.Equal(1, gateway.Applies);
        Assert.True(editor.ApplyOutcomeUnconfirmed);
        Assert.False(editor.ApplyCommand.CanExecute(null));
        Assert.False(editor.IsActiveMappingConfirmed);
        Assert.Contains("UNKNOWN", editor.WorkflowStage);

        await editor.ObserveBoundaryAsync();
        Assert.True(editor.ApplyOutcomeUnconfirmed);
        Assert.Equal(1, gateway.Applies);

        gateway.ConfirmMapping();
        await editor.ObserveBoundaryAsync();
        Assert.False(editor.ApplyOutcomeUnconfirmed);
        Assert.True(editor.IsActiveMappingConfirmed);
        Assert.Equal(1, gateway.Applies);
        Assert.Equal("RUNTIME CONFIRMED · ACTIVE MAPPING", editor.WorkflowStage);
    }

    [Fact]
    public async Task Draft_revalidation_is_invalidated_by_a_followup_local_surface_edit()
    {
        await using var owner = ReadyOwner();
        var gateway = new ReferenceGateway();
        using var editor = new OutputCanvasEditorViewModel(owner, gateway);
        await editor.ReloadAsync();
        editor.NudgeSelected(2, 0);
        await editor.ValidateAsync();
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.NudgeSelected(3, 0);
        Assert.False(editor.IsDraftValidated);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(0, gateway.Saves);
        Assert.Equal(0, gateway.Applies);
    }

    [Fact]
    public void Output_role_evidence_never_inherits_global_Runtime_PASS_for_missing_inactive_or_stale_targets()
    {
        OperatorOutputRoleDescriptor Role(bool active, string health, Failure? failure = null) =>
            new("program", "PROGRAM", Source.ToString(), "virtual-program", "reference",
                1920, 1080, "50", "RGBA8", "FRAME_BOUNDARY", "CONNECTED",
                active, health, "Runtime Program target observation", failure);

        var healthy = Role(true, "PASS");
        Assert.Equal("PASS", OutputRoutingHealthViewModel.RoleEvidence(healthy, true, false, "PASS"));
        Assert.Equal("UNVERIFIED", OutputRoutingHealthViewModel.RoleEvidence(null, true, false, "PASS"));
        Assert.Equal("UNVERIFIED", OutputRoutingHealthViewModel.RoleEvidence(healthy, false, false, "PASS"));
        Assert.Equal("UNVERIFIED", OutputRoutingHealthViewModel.RoleEvidence(healthy, true, true, "PASS"));
        Assert.Equal("UNVERIFIED", OutputRoutingHealthViewModel.RoleEvidence(healthy, true, false, "UNVERIFIED"));
        Assert.Equal("UNVERIFIED", OutputRoutingHealthViewModel.RoleEvidence(Role(false, "PASS"), true, false, "PASS"));
        Assert.Equal("FAIL", OutputRoutingHealthViewModel.RoleEvidence(
            Role(true, "FAIL", new Failure("output.writer_unavailable", "Target writer unavailable.")),
            true, false, "PASS"));

        Assert.Contains("MONITOR ONLY", new OutputStatusViewModel("preview", "PREVIEW").OutputKind);
        Assert.Contains("NOT A PHYSICAL", new OutputStatusViewModel("clean-program", "CLEAN FEED").OutputKind);
        Assert.Contains("UNVERIFIED", new OutputStatusViewModel("aux", "AUX").DeliveryEvidence);
    }

    private static OperatorViewModel ReadyOwner()
    {
        var owner = new OperatorViewModel(new OperatorControlClient(new PassiveTransport()),
            runtimeReadiness: new ReadyService());
        var state = new AuthoritativeProductionState(ControlContractVersion.Current,
            ProductionId.New(), new Revision(1), new ProductionRoutingState(Source,Source));
        owner.ApplyConfirmedSnapshot(new OperatorStatusSnapshot(
            state, [new OperatorSourceDescriptor(Source.ToString(), "Reference Camera")],
            "READY", "LOCKED", "HEALTHY", "OFF", "IDLE", false, 0.2));
        return owner;
    }

    private sealed class ReferenceGateway : IOutputCanvasGateway
    {
        public OutputCanvasConfiguration Stored { get; private set; } = new(
            OutputCanvasContract.Version, Configuration, 0,
            [new(Canvas, "Reference Stage", 1920, 1080)],
            [new(Surface, Canvas, new(BusRoleId: "program"), 0,
                new(0,0,1,1), OutputCanvasQuad.Rectangle(0,0,1920,1080))],
            [new(Output, Canvas, "reference", "virtual-program", "production")]);
        private OutputCanvasActivationResult _runtime = new(null,null,"UNMAPPED");
        public int Saves { get; private set; }
        public int Applies { get; private set; }
        public string? RejectWith { get; set; }
        public bool DropSaveResponse { get; set; }
        public bool DropApplyResponse { get; set; }

        public ValueTask<OutputCanvasSnapshot> GetAsync(CancellationToken token = default) =>
            ValueTask.FromResult(new OutputCanvasSnapshot(Stored,Stored.EditRevision,
                _runtime.ActiveRenderRevision,
                [new("reference","virtual-program","production",1920,1080,
                    "Existing Runtime virtual sink",RoleId: "program")],_runtime.ActivationState));

        public ValueTask<OutputCanvasActivationResult> EvidenceAsync(CancellationToken token = default) =>
            ValueTask.FromResult(_runtime);

        public ValueTask<OutputCanvasValidation> ValidateAsync(OutputCanvasConfiguration draft,
            CancellationToken token = default)
        {
            var valid = OutputCanvasModel.Validate(draft);
            var issues = valid.Issues.ToList();
            if (draft.ConfigurationId != Stored.ConfigurationId ||
                draft.EditRevision != Stored.EditRevision+1)
                issues.Add(new("canvas.revision_conflict","editRevision","Stored configuration changed."));
            if (RejectWith is { } code) issues.Add(new(code,"surfaces/unknown","Reference provider rejected."));
            return ValueTask.FromResult(valid with { Issues = issues.ToArray() });
        }

        public ValueTask<OutputCanvasSaveResult> SaveAsync(OutputCanvasSaveRequest request,
            CancellationToken token = default)
        {
            Saves++;
            var result = OutputCanvasModel.Apply(Stored,request);
            Stored=result;
            if (DropSaveResponse) throw new TimeoutException("Response lost after durable commit.");
            return ValueTask.FromResult(new OutputCanvasSaveResult(true,Stored.EditRevision,Stored,
                new(Stored.ConfigurationId,Stored.EditRevision,[])));
        }

        public ValueTask<OutputCanvasActivationResult> ActivateAsync(OutputCanvasActivationRequest request,
            CancellationToken token = default)
        {
            Applies++;
            Assert.Equal(Stored.ConfigurationId,request.ConfigurationId);
            Assert.Equal(Stored.EditRevision,request.ExpectedSavedRevision);
            _runtime = _runtime with { ActivationState="PENDING_BOUNDARY",
                PendingRenderRevision=request.Enabled?Stored.EditRevision:null, FailureCode=null };
            if (DropApplyResponse) throw new TimeoutException("Apply response lost after Runtime accepted the request.");
            return ValueTask.FromResult(_runtime);
        }

        public void ConfirmMapping() => _runtime=new(Stored.EditRevision,null,
            "ACTIVE",TargetId:"virtual-program",TargetWidth:1920,TargetHeight:1080,
            LastRenderedSequence:8);
        public void ConfirmDisable() => _runtime=new(null,null,"UNMAPPED");
        public void AdvanceStored() => Stored=Stored with { EditRevision=Stored.EditRevision+1 };
    }

    private sealed class PassiveTransport : IOperatorControlTransport
    {
        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken token = default) =>
            ValueTask.FromException<OperatorStatusSnapshot>(new IOException("No background snapshots required."));
        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command,
            CancellationToken token = default) =>
            ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command,
            CancellationToken token = default) =>
            ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command,
            CancellationToken token = default) =>
            ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
    }

    private sealed class ReadyService : IRuntimeReadinessService
    {
        public RuntimeReadinessSnapshot Current { get; } = new(RuntimeReadinessState.Ready,
            DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch,Array.Empty<ReadinessReason>(),
            true,RuntimePerformanceVerificationSnapshot.Unverified);
        public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed { add {} remove {} }
        public void Observe(RuntimeReadinessObservation observation) {}
        public void InvalidatePerformance(string detail) {}
    }
}
