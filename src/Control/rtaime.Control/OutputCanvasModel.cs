// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
namespace rtaime.Control;

public static class OutputCanvasModel
{
    public static OutputCanvasValidation Validate(OutputCanvasConfiguration model,
        IReadOnlyCollection<string>? sources = null, IReadOnlyCollection<string>? buses = null,
        IReadOnlyCollection<OutputCanvasTarget>? targets = null)
    {
        var issues = new List<OutputCanvasIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));
        if (model.Version != OutputCanvasContract.Version) Error("canvas.version", "version", "Unsupported canvas model version.");
        if (model.ConfigurationId == Guid.Empty) Error("canvas.identity", "configurationId", "Configuration identity is required.");
        if (model.Canvases is null || model.Surfaces is null || model.Bindings is null ||
            model.Canvases.Any(x => x is null) || model.Surfaces.Any(x => x is null) || model.Bindings.Any(x => x is null))
        {
            Error("canvas.payload", "$", "Non-null collections and entries are required.");
            return new(model.ConfigurationId, model.EditRevision, issues.ToArray());
        }
        if (model.Canvases.Length > OutputCanvasContract.MaximumCanvases || model.Surfaces.Length > OutputCanvasContract.MaximumSurfaces ||
            model.Bindings.Length > OutputCanvasContract.MaximumBindings)
            Error("canvas.capacity", "$", "Canvas, surface or output count exceeds the finite authored-model cap.");
        var ids = model.Canvases.Select(x => x.CanvasId).Concat(model.Surfaces.Select(x => x.SurfaceId)).Concat(model.Bindings.Select(x => x.OutputId)).ToArray();
        if (ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            Error("canvas.identity", "$", "Canvas, surface and output identities must be nonempty and globally unique.");
        long pixels = 0;
        foreach (var c in model.Canvases)
        {
            var path = $"canvases/{c.CanvasId}";
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 128) Error("canvas.name", path, "Name requires 1-128 characters.");
            if (c.Width < 1 || c.Height < 1 || c.Width > OutputCanvasContract.MaximumDimension || c.Height > OutputCanvasContract.MaximumDimension)
                Error("canvas.size", path, "Invalid logical canvas dimensions.");
            else pixels += (long)c.Width * c.Height;
        }
        if (pixels > OutputCanvasContract.MaximumLogicalPixels) Error("canvas.capacity", "canvases", "Total logical area exceeds the authored-model budget.");
        foreach (var s in model.Surfaces)
        {
            var path = $"surfaces/{s.SurfaceId}";
            if (!model.Canvases.Any(c => c.CanvasId == s.CanvasId)) Error("canvas.reference", path, "Canvas does not exist.");
            if (s.Input is null || (s.Input.SourceId is null) == (s.Input.BusRoleId is null))
                Error("canvas.input", path, "Exactly one source or governed output bus reference is required.");
            else if (s.Input.SourceId is { } source && (!Guid.TryParse(source, out var sourceId) || sourceId == Guid.Empty ||
                (sources is not null && !sources.Contains(source))))
                Error("canvas.source", path, "Source is absent from the authoritative production catalog.");
            else if (s.Input.BusRoleId is { } bus && (bus is not ("program" or "aux") || (buses is not null && !buses.Contains(bus))))
                Error("canvas.bus", path, "Bus must be an existing governed output role.");
            var crop = s.Crop;
            if (crop is null || !double.IsFinite(crop.X) || !double.IsFinite(crop.Y) || !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height) ||
                crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 || crop.X + crop.Width > 1 || crop.Y + crop.Height > 1)
                Error("canvas.crop", path, "Crop must have positive finite area inside normalized source edges [0,1].");
            if (s.Destination is null || !OutputCanvasGeometry.IsValid(s.Destination))
                Error("canvas.quad", path, "Destination must be finite, convex, clockwise, nonsingular and within the coordinate bound.");
            if (s.EdgeBlend is { } blend &&
                (blend.Curve is not ("linear" or "smoothstep") ||
                 !EdgeIsValid(blend.Left) || !EdgeIsValid(blend.Top) ||
                 !EdgeIsValid(blend.Right) || !EdgeIsValid(blend.Bottom)))
                Error("canvas.edge_blend", path, "Edge blend requires finite normalized widths in [0,0.5] and an accepted curve.");
        }
        if (model.Bindings.Select(b => (b.ProviderId, b.TargetId)).Distinct().Count() != model.Bindings.Length)
            Error("canvas.target_conflict", "bindings", "A provider target may be assigned only once, including disabled bindings.");
        foreach (var b in model.Bindings)
        {
            var path = $"bindings/{b.OutputId}";
            if (!model.Canvases.Any(c => c.CanvasId == b.CanvasId)) Error("canvas.reference", path, "Bound canvas does not exist.");
            if (string.IsNullOrWhiteSpace(b.ProviderId) || b.ProviderId.Length > 128 || string.IsNullOrWhiteSpace(b.TargetId) ||
                b.TargetId.Length > 128 || string.IsNullOrWhiteSpace(b.ProfileId) || b.ProfileId.Length > 128)
                Error("canvas.target", path, "Provider, target and profile identities require 1-128 characters.");
            else if (targets is not null && !targets.Any(t => t.ProviderId == b.ProviderId && t.TargetId == b.TargetId && t.ProfileId == b.ProfileId))
                Error("canvas.target_unsupported", path, "Provider target/profile is not currently available and authorized.");
        }
        return new(model.ConfigurationId, model.EditRevision, issues.ToArray());
    }

    private static bool EdgeIsValid(double value) => double.IsFinite(value) && value >= 0 && value <= 0.5;

    public static OutputCanvasConfiguration Apply(OutputCanvasConfiguration current, OutputCanvasSaveRequest request)
    {
        if (request.Version != OutputCanvasContract.Version || request.ConfigurationId != current.ConfigurationId ||
            request.TransactionId == Guid.Empty || request.ExpectedSavedRevision != current.EditRevision ||
            request.Edits is null || request.Edits.Length is < 1 or > OutputCanvasContract.MaximumOperations ||
            request.Edits.Any(e => e is null))
            throw new ArgumentException("Unsupported, stale, malformed or excessive canvas transaction.");
        var canvases = current.Canvases.ToList(); var surfaces = current.Surfaces.ToList(); var bindings = current.Bindings.ToList();
        foreach (var e in request.Edits)
        {
            if (e.Id == Guid.Empty) throw new ArgumentException("Edit identity is required.");
            var payloads = (e.Canvas is null ? 0 : 1) + (e.Surface is null ? 0 : 1) + (e.Binding is null ? 0 : 1);
            switch (e.Kind)
            {
                case OutputCanvasEditKind.AddCanvas:
                case OutputCanvasEditKind.UpdateCanvas:
                    if (payloads != 1 || e.Canvas is null || e.Canvas.CanvasId != e.Id ||
                        canvases.Any(c => c.CanvasId == e.Id) != (e.Kind == OutputCanvasEditKind.UpdateCanvas))
                        throw new ArgumentException("Canvas edit identity or existence mismatch.");
                    canvases.RemoveAll(c => c.CanvasId == e.Id); canvases.Add(e.Canvas); break;
                case OutputCanvasEditKind.RemoveCanvas:
                    if (payloads != 0 || bindings.Any(b => b.CanvasId == e.Id) || surfaces.Any(s => s.CanvasId == e.Id) ||
                        canvases.RemoveAll(c => c.CanvasId == e.Id) != 1)
                        throw new ArgumentException("Remove surfaces and explicitly unbind outputs before removing their canvas.");
                    break;
                case OutputCanvasEditKind.AddSurface:
                case OutputCanvasEditKind.UpdateSurface:
                    if (payloads != 1 || e.Surface is null || e.Surface.SurfaceId != e.Id ||
                        surfaces.Any(s => s.SurfaceId == e.Id) != (e.Kind == OutputCanvasEditKind.UpdateSurface))
                        throw new ArgumentException("Surface edit identity or existence mismatch.");
                    surfaces.RemoveAll(s => s.SurfaceId == e.Id); surfaces.Add(e.Surface); break;
                case OutputCanvasEditKind.RemoveSurface:
                    var surface = surfaces.SingleOrDefault(s => s.SurfaceId == e.Id);
                    if (payloads != 0 || surface is null || bindings.Any(b => b.CanvasId == surface.CanvasId))
                        throw new ArgumentException("Explicitly unbind outputs before removing a bound canvas surface.");
                    surfaces.Remove(surface); break;
                case OutputCanvasEditKind.Bind:
                    if (payloads != 1 || e.Binding is null || e.Binding.OutputId != e.Id)
                        throw new ArgumentException("Binding edit identity mismatch.");
                    bindings.RemoveAll(b => b.OutputId == e.Id); bindings.Add(e.Binding); break;
                case OutputCanvasEditKind.Unbind:
                    if (payloads != 0 || bindings.RemoveAll(b => b.OutputId == e.Id) != 1)
                        throw new ArgumentException("Output binding does not exist.");
                    break;
                default: throw new ArgumentException("Unknown canvas edit kind.");
            }
        }
        return current with { EditRevision = checked(current.EditRevision + 1),
            Canvases = canvases.ToArray(), Surfaces = surfaces.ToArray(), Bindings = bindings.ToArray() };
    }
}
