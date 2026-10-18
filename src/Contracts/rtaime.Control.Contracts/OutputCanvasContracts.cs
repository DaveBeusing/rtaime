// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Control.Contracts;

// Independent authored-model version; existing Control/Runtime wire versions retain their identities.
public static class OutputCanvasContract
{
    public const string Version = "1.0";
    public const int MaximumCanvases = 16;
    public const int MaximumSurfaces = 128;
    public const int MaximumBindings = 32;
    public const int MaximumOperations = 256;
    public const int MaximumDimension = 16384;
    public const long MaximumLogicalPixels = 67108864;
    public const int MaximumJsonBytes = 262144;
}

public sealed record OutputCanvasDefinition(Guid CanvasId, string Name, int Width, int Height);
public readonly record struct OutputCanvasPoint(double X, double Y);
public sealed record OutputCanvasCrop(double X, double Y, double Width, double Height);
public sealed record OutputCanvasQuad(OutputCanvasPoint TopLeft, OutputCanvasPoint TopRight,
    OutputCanvasPoint BottomRight, OutputCanvasPoint BottomLeft)
{
    public static OutputCanvasQuad Rectangle(double x, double y, double width, double height) =>
        new(new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height));
}
public sealed record OutputCanvasInput(string? SourceId = null, string? BusRoleId = null);
public sealed record OutputCanvasSurface(Guid SurfaceId, Guid CanvasId, OutputCanvasInput Input,
    int ZOrder, OutputCanvasCrop Crop, OutputCanvasQuad Destination, bool Enabled = true);
public sealed record OutputCanvasBinding(Guid OutputId, Guid CanvasId, string ProviderId,
    string TargetId, string ProfileId, bool Enabled = true);
public sealed record OutputCanvasConfiguration(string Version, Guid ConfigurationId, ulong EditRevision,
    OutputCanvasDefinition[] Canvases, OutputCanvasSurface[] Surfaces, OutputCanvasBinding[] Bindings);
public sealed record OutputCanvasTarget(string ProviderId, string TargetId, string ProfileId,
    int Width, int Height, string Evidence);
public sealed record OutputCanvasIssue(string Code, string Path, string Message);
public sealed record OutputCanvasValidation(Guid ConfigurationId, ulong EditRevision, OutputCanvasIssue[] Issues)
{
    public bool IsValid => Issues.Length == 0;
}
public sealed record OutputCanvasSnapshot(OutputCanvasConfiguration Saved, ulong SavedRevision,
    ulong? ActiveRenderRevision, OutputCanvasTarget[] AvailableTargets, string ActivationState);

public enum OutputCanvasEditKind
{
    AddCanvas = 1, UpdateCanvas = 2, RemoveCanvas = 3,
    AddSurface = 4, UpdateSurface = 5, RemoveSurface = 6,
    Bind = 7, Unbind = 8
}
public sealed record OutputCanvasEdit(OutputCanvasEditKind Kind, Guid Id,
    OutputCanvasDefinition? Canvas = null, OutputCanvasSurface? Surface = null,
    OutputCanvasBinding? Binding = null);
public sealed record OutputCanvasSaveRequest(string Version, Guid TransactionId,
    Guid ConfigurationId, ulong ExpectedSavedRevision, OutputCanvasEdit[] Edits);
public sealed record OutputCanvasSaveResult(bool Saved, ulong SavedRevision,
    OutputCanvasConfiguration Configuration, OutputCanvasValidation Validation, bool Replayed = false);
