// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Operator;

namespace rtaime.Tests.Operator;

public sealed class OutputCanvasDraftSessionTests
{
    private static readonly Guid CanvasId = Guid.Parse("ac999000-0000-0000-0000-000000000001");
    private static readonly Guid SurfaceId = Guid.Parse("ac999000-0000-0000-0000-000000000002");
    private static readonly Guid OutputId = Guid.Parse("ac999000-0000-0000-0000-000000000003");

    private static OutputCanvasConfiguration Saved() => new(
        OutputCanvasContract.Version, Guid.Parse("ac999000-0000-0000-0000-000000000004"), 5,
        [new(CanvasId, "Stage A", 1920, 1080)],
        [new(SurfaceId, CanvasId, new(BusRoleId: "program"), 0,
            new(0, 0, 1, 1), OutputCanvasQuad.Rectangle(0, 0, 1920, 1080))],
        [new(OutputId, CanvasId, "reference", "virtual-program", "production")]);

    [Fact]
    public void Draft_revision_stays_one_ahead_and_undo_redo_never_mutate_saved_state()
    {
        var session = new OutputCanvasDraftSession(Saved());
        Assert.Equal(6UL, session.Draft.EditRevision);
        Assert.False(session.IsDirty);
        var newQuad = OutputCanvasQuad.Rectangle(8, 12, 1600, 920);
        session.Mutate(draft => draft with { Surfaces =
            [draft.Surfaces[0] with { Destination = newQuad }] });
        Assert.True(session.IsDirty);
        Assert.Equal(5UL, session.Saved.EditRevision);
        Assert.Equal(6UL, session.Draft.EditRevision);
        Assert.True(session.Undo());
        Assert.False(session.IsDirty);
        Assert.True(session.Redo());
        Assert.True(session.IsDirty);
        session.Discard();
        Assert.False(session.IsDirty);
        Assert.False(session.CanRedo);
        Assert.Equal(5UL, session.Saved.EditRevision);
    }

    [Fact]
    public void Bound_surface_removal_unbinds_before_deletion_and_restores_surviving_target()
    {
        var saved = Saved();
        var session = new OutputCanvasDraftSession(saved);
        session.Mutate(draft => draft with { Surfaces = [] });
        var edits = session.CreateSaveRequest(Guid.NewGuid()).Edits;
        Assert.Equal([OutputCanvasEditKind.Unbind, OutputCanvasEditKind.RemoveSurface,
            OutputCanvasEditKind.Bind], edits.Select(e => e.Kind).ToArray());
        var request = session.CreateSaveRequest(Guid.NewGuid());
        var persisted = OutputCanvasModel.Apply(saved, request);
        Assert.Empty(persisted.Surfaces);
        Assert.Equal(OutputId, Assert.Single(persisted.Bindings).OutputId);
        Assert.Equal(saved.EditRevision + 1, persisted.EditRevision);
    }

    [Fact]
    public void Two_surfaces_and_edge_blend_compile_to_a_valid_atomic_save_request()
    {
        var saved = Saved();
        var session = new OutputCanvasDraftSession(saved);
        var second = new OutputCanvasSurface(
            Guid.NewGuid(), CanvasId, new(BusRoleId: "program"), 1,
            new(.25, 0, .75, 1), OutputCanvasQuad.Rectangle(960, 0, 960, 1080),
            EdgeBlend: new(.15, 0, .10, 0, "smoothstep"));
        session.Mutate(draft => draft with { Surfaces = [..draft.Surfaces, second] });
        var request = session.CreateSaveRequest(Guid.NewGuid());
        Assert.Single(request.Edits);
        Assert.Equal(OutputCanvasEditKind.AddSurface, request.Edits[0].Kind);
        var stored = OutputCanvasModel.Apply(saved, request);
        Assert.True(OutputCanvasModel.Validate(stored).IsValid);
        Assert.Equal(2, stored.Surfaces.Length);
        Assert.Equal(second.EdgeBlend, stored.Surfaces[1].EdgeBlend);
    }

    [Fact]
    public void Invalid_geometry_cannot_enter_history_or_save()
    {
        var session = new OutputCanvasDraftSession(Saved());
        var degenerate = new OutputCanvasQuad(new(0,0), new(0,0), new(1,1), new(0,1));
        Assert.False(OutputCanvasDraftSession.IsValidQuad(degenerate));
        Assert.Throws<ArgumentException>(() =>
            session.Mutate(d => d with { Surfaces =
                [d.Surfaces[0] with { Destination = degenerate }] }));
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.Throws<InvalidOperationException>(() => session.CreateSaveRequest(Guid.NewGuid()));
        Assert.False(OutputCanvasDraftSession.IsValidCrop(new(0.8, 0, .5, 1)));
        Assert.False(OutputCanvasDraftSession.IsValidBlend(new(.6, 0, 0, 0)));
    }

    [Fact]
    public void Saved_revision_replacement_clears_local_undo_and_fences_old_edits()
    {
        var session = new OutputCanvasDraftSession(Saved());
        session.Mutate(d => d with { Canvases = [d.Canvases[0] with { Name = "Edited" }] });
        session.ReplaceSaved(Saved() with { EditRevision = 8 });
        Assert.Equal(9UL, session.Draft.EditRevision);
        Assert.False(session.IsDirty);
        Assert.False(session.Undo());
    }
}
