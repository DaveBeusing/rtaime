// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Operator;

/// <summary>
/// Local authored geometry and bounded history. No operation in this type
/// changes saved Control state or the Runtime output plan.
/// </summary>
internal sealed class OutputCanvasDraftSession
{
    private const int HistoryCapacity = 48;
    private readonly Stack<OutputCanvasConfiguration> _undo = new();
    private readonly Stack<OutputCanvasConfiguration> _redo = new();
    private OutputCanvasConfiguration _saved;
    private OutputCanvasConfiguration _draft;

    public OutputCanvasDraftSession(OutputCanvasConfiguration saved)
    {
        _saved = Clone(saved);
        _draft = NextDraft(saved);
    }

    public OutputCanvasConfiguration Saved => _saved;
    public OutputCanvasConfiguration Draft => _draft;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => !Equivalent(_saved, _draft);

    public static bool IsValidQuad(OutputCanvasQuad? q)
    {
        if (q is null) return false;
        OutputCanvasPoint[] points = [q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft];
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            Math.Abs(p.X) > OutputCanvasContract.MaximumDimension ||
            Math.Abs(p.Y) > OutputCanvasContract.MaximumDimension)) return false;
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % 4]; var c = points[(i + 2) % 4];
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) <= 1e-8)
                return false;
        }
        return true;
    }

    public static bool IsValidCrop(OutputCanvasCrop? crop) =>
        crop is not null && double.IsFinite(crop.X) && double.IsFinite(crop.Y) &&
        double.IsFinite(crop.Width) && double.IsFinite(crop.Height) &&
        crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0 &&
        crop.X + crop.Width <= 1 && crop.Y + crop.Height <= 1;

    public static bool IsValidBlend(OutputCanvasEdgeBlend? blend) =>
        blend is null || ((blend.Curve is "linear" or "smoothstep") &&
            ValidEdge(blend.Left) && ValidEdge(blend.Top) &&
            ValidEdge(blend.Right) && ValidEdge(blend.Bottom));

    private static bool ValidEdge(double width) =>
        double.IsFinite(width) && width >= 0 && width <= .5;

    public void ReplaceSaved(OutputCanvasConfiguration saved)
    {
        _saved = Clone(saved);
        _draft = NextDraft(saved);
        _undo.Clear();
        _redo.Clear();
    }

    public void Discard()
    {
        _draft = NextDraft(_saved);
        _undo.Clear();
        _redo.Clear();
    }

    public void Mutate(Func<OutputCanvasConfiguration, OutputCanvasConfiguration> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_draft.EditRevision != checked(_saved.EditRevision + 1))
            throw new InvalidOperationException("Canvas draft revision is stale.");
        var candidate = edit(Clone(_draft)) ??
            throw new InvalidOperationException("Canvas edit must return a configuration.");
        if (candidate.Version != _draft.Version ||
            candidate.ConfigurationId != _draft.ConfigurationId ||
            candidate.EditRevision != _draft.EditRevision ||
            candidate.Canvases is null || candidate.Surfaces is null || candidate.Bindings is null ||
            candidate.Canvases.Length > OutputCanvasContract.MaximumCanvases ||
            candidate.Surfaces.Length > OutputCanvasContract.MaximumSurfaces ||
            candidate.Bindings.Length > OutputCanvasContract.MaximumBindings)
            throw new ArgumentException("Canvas edit violates identity, revision or capacity.");
        if (candidate.Surfaces.Any(surface => surface is null ||
            !IsValidQuad(surface.Destination) || !IsValidCrop(surface.Crop) ||
            !IsValidBlend(surface.EdgeBlend)))
            throw new ArgumentException("Canvas edit has invalid crop, destination or edge blend.");
        if (Equivalent(_draft, candidate)) return;
        _undo.Push(_draft);
        Trim(_undo);
        _draft = candidate;
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_draft);
        _draft = _undo.Pop();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_draft);
        Trim(_undo);
        _draft = _redo.Pop();
        return true;
    }

    public OutputCanvasSaveRequest CreateSaveRequest(Guid transactionId)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("A nonempty save transaction is required.");
        var edits = BuildEdits(_saved, _draft);
        if (edits.Length is < 1 or > OutputCanvasContract.MaximumOperations)
            throw new InvalidOperationException("Canvas transaction is empty or exceeds the finite edit limit.");
        return new(OutputCanvasContract.Version, transactionId, _saved.ConfigurationId,
            _saved.EditRevision, edits);
    }

    internal static OutputCanvasEdit[] BuildEdits(OutputCanvasConfiguration saved, OutputCanvasConfiguration draft)
    {
        var edits = new List<OutputCanvasEdit>();
        var oldCanvas = saved.Canvases.ToDictionary(c => c.CanvasId);
        var newCanvas = draft.Canvases.ToDictionary(c => c.CanvasId);
        var oldSurfaces = saved.Surfaces.ToDictionary(s => s.SurfaceId);
        var newSurfaces = draft.Surfaces.ToDictionary(s => s.SurfaceId);
        var oldBindings = saved.Bindings.ToDictionary(b => b.OutputId);
        var newBindings = draft.Bindings.ToDictionary(b => b.OutputId);
        var removedFromCanvas = saved.Surfaces
            .Where(s => !newSurfaces.ContainsKey(s.SurfaceId))
            .Select(s => s.CanvasId).ToHashSet();

        // Existing persistence rejects removing a bound surface. Unbind the
        // affected canvas first, then restore still-requested bindings last.
        var temporarilyUnbound = new HashSet<Guid>();
        foreach (var binding in saved.Bindings)
            if (!newBindings.ContainsKey(binding.OutputId) ||
                removedFromCanvas.Contains(binding.CanvasId) ||
                !newCanvas.ContainsKey(binding.CanvasId))
            {
                edits.Add(new(OutputCanvasEditKind.Unbind, binding.OutputId));
                temporarilyUnbound.Add(binding.OutputId);
            }

        foreach (var surface in saved.Surfaces)
            if (!newSurfaces.ContainsKey(surface.SurfaceId))
                edits.Add(new(OutputCanvasEditKind.RemoveSurface, surface.SurfaceId));
        foreach (var canvas in saved.Canvases)
            if (!newCanvas.ContainsKey(canvas.CanvasId))
                edits.Add(new(OutputCanvasEditKind.RemoveCanvas, canvas.CanvasId));
        foreach (var canvas in draft.Canvases)
            if (!oldCanvas.TryGetValue(canvas.CanvasId, out var previous))
                edits.Add(new(OutputCanvasEditKind.AddCanvas, canvas.CanvasId, Canvas: canvas));
            else if (previous != canvas)
                edits.Add(new(OutputCanvasEditKind.UpdateCanvas, canvas.CanvasId, Canvas: canvas));
        foreach (var surface in draft.Surfaces)
            if (!oldSurfaces.TryGetValue(surface.SurfaceId, out var previous))
                edits.Add(new(OutputCanvasEditKind.AddSurface, surface.SurfaceId, Surface: surface));
            else if (previous != surface)
                edits.Add(new(OutputCanvasEditKind.UpdateSurface, surface.SurfaceId, Surface: surface));
        foreach (var binding in draft.Bindings)
            if (!oldBindings.TryGetValue(binding.OutputId, out var previous) ||
                previous != binding || temporarilyUnbound.Contains(binding.OutputId))
                edits.Add(new(OutputCanvasEditKind.Bind, binding.OutputId, Binding: binding));
        return edits.ToArray();
    }

    private static OutputCanvasConfiguration NextDraft(OutputCanvasConfiguration saved) =>
        Clone(saved) with { EditRevision = checked(saved.EditRevision + 1) };

    private static OutputCanvasConfiguration Clone(OutputCanvasConfiguration value) =>
        value with
        {
            Canvases = (OutputCanvasDefinition[])value.Canvases.Clone(),
            Surfaces = (OutputCanvasSurface[])value.Surfaces.Clone(),
            Bindings = (OutputCanvasBinding[])value.Bindings.Clone()
        };

    private static bool Equivalent(OutputCanvasConfiguration left, OutputCanvasConfiguration right) =>
        left.ConfigurationId == right.ConfigurationId &&
        left.Canvases.SequenceEqual(right.Canvases) &&
        left.Surfaces.SequenceEqual(right.Surfaces) &&
        left.Bindings.SequenceEqual(right.Bindings);

    private static void Trim(Stack<OutputCanvasConfiguration> history)
    {
        if (history.Count <= HistoryCapacity) return;
        var newest = history.ToArray();
        history.Clear();
        for (var i = HistoryCapacity - 1; i >= 0; i--) history.Push(newest[i]);
    }
}
