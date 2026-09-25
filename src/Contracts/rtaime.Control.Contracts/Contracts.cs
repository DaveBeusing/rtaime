using System.Collections.ObjectModel;
using rtaime.Core;

namespace rtaime.Control.Contracts;

public static class ControlContractVersion
{
    public static CompatibilityVersion Current { get; } = new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported Control contract version '{version}'. Supported version is '{Current}'.");
    }
}

public readonly record struct ProductionId
{
    public ProductionId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Production identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ProductionId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductionSourceId
{
    public ProductionSourceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Production source identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ProductionSourceId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct SceneId
{
    public SceneId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Scene identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static SceneId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct CommandId
{
    public CommandId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Command identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static CommandId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public sealed record ProductionSourceSpecification
{
    public ProductionSourceSpecification(ProductionSourceId sourceId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Production source name is required.", nameof(name));

        SourceId = sourceId;
        Name = name.Trim();
    }

    public ProductionSourceId SourceId { get; }
    public string Name { get; }
}

public sealed record ProductionRoutingState(ProductionSourceId PreviewSourceId, ProductionSourceId ProgramSourceId);

public enum ProductionCompositingLayerKind
{
    LegacyVisual = 1,
    BitmapGraphics = 2,
    ProductionCg = 3
}

public enum ProductionCompositingProcessingNodeKind
{
    ColorGrade = 1
}

public sealed record ProductionColorGradeSettings
{
    public ProductionColorGradeSettings(double brightness, double contrast, double saturation)
    {
        if (!double.IsFinite(brightness) || brightness is < -1.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(brightness), "Brightness must be finite and in the inclusive range -1..1.");
        if (!double.IsFinite(contrast) || contrast is < 0.0 or > 2.0)
            throw new ArgumentOutOfRangeException(nameof(contrast), "Contrast must be finite and in the inclusive range 0..2.");
        if (!double.IsFinite(saturation) || saturation is < 0.0 or > 2.0)
            throw new ArgumentOutOfRangeException(nameof(saturation), "Saturation must be finite and in the inclusive range 0..2.");

        Brightness = brightness;
        Contrast = contrast;
        Saturation = saturation;
    }

    public double Brightness { get; }
    public double Contrast { get; }
    public double Saturation { get; }
}

public sealed record ProductionCompositingProcessingNodeState
{
    public ProductionCompositingProcessingNodeState(
        string nodeId,
        ProductionCompositingProcessingNodeKind kind,
        bool enabled,
        ProductionColorGradeSettings colorGrade)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 64)
            throw new ArgumentException("Processing node identity is required and must not exceed 64 characters.", nameof(nodeId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind != ProductionCompositingProcessingNodeKind.ColorGrade)
            throw new NotSupportedException($"Processing node kind '{kind}' is not supported.");

        NodeId = nodeId.Trim();
        Kind = kind;
        Enabled = enabled;
        ColorGrade = colorGrade ?? throw new ArgumentNullException(nameof(colorGrade));
    }

    public string NodeId { get; }
    public ProductionCompositingProcessingNodeKind Kind { get; }
    public bool Enabled { get; }
    public ProductionColorGradeSettings ColorGrade { get; }
}

public static class ProductionCompositingLayerIds
{
    public const string LegacyVisual = "legacy-visual";
    public const string BitmapGraphics = "bitmap-graphics";
    public const string ProductionCg = "production-cg";
    public const int MaximumLayerCount = 8;

    public static bool MatchesKind(string layerId, ProductionCompositingLayerKind kind) =>
        (layerId, kind) switch
        {
            (LegacyVisual, ProductionCompositingLayerKind.LegacyVisual) => true,
            (BitmapGraphics, ProductionCompositingLayerKind.BitmapGraphics) => true,
            (ProductionCg, ProductionCompositingLayerKind.ProductionCg) => true,
            _ => false
        };
}

public sealed record ProductionCompositingLayerState
{
    public ProductionCompositingLayerState(
        string layerId,
        ProductionCompositingLayerKind kind,
        int order,
        bool visible,
        byte opacity,
        double positionX,
        double positionY,
        double scale,
        string contentIdentity,
        double rotationDegrees = 0.0,
        double anchorX = 0.0,
        double anchorY = 0.0,
        double cropLeft = 0.0,
        double cropTop = 0.0,
        double cropRight = 0.0,
        double cropBottom = 0.0,
        ProductionCompositingProcessingNodeState? processingNode = null)
    {
        if (string.IsNullOrWhiteSpace(layerId))
            throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!ProductionCompositingLayerIds.MatchesKind(layerId.Trim(), kind))
            throw new ArgumentException("Compositing layer identity does not match its declared kind.", nameof(layerId));
        if (order is < 0 or >= ProductionCompositingLayerIds.MaximumLayerCount)
            throw new ArgumentOutOfRangeException(nameof(order));
        ValidateNormalized(positionX, nameof(positionX));
        ValidateNormalized(positionY, nameof(positionY));
        if (!double.IsFinite(scale) || scale is < 0.05 or > 4.0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (!double.IsFinite(rotationDegrees) || rotationDegrees is < -180.0 or > 180.0)
            throw new ArgumentOutOfRangeException(nameof(rotationDegrees), "Rotation must be finite and in the inclusive range -180..180 degrees.");
        ValidateNormalized(anchorX, nameof(anchorX));
        ValidateNormalized(anchorY, nameof(anchorY));
        ValidateCrop(cropLeft, cropTop, cropRight, cropBottom);
        if (kind == ProductionCompositingLayerKind.LegacyVisual &&
            (rotationDegrees != 0.0 || anchorX != 0.0 || anchorY != 0.0 ||
             cropLeft != 0.0 || cropTop != 0.0 || cropRight != 0.0 || cropBottom != 0.0 ||
             processingNode is not null))
        {
            throw new ArgumentException("Legacy visual layers do not expose transform extensions or processing nodes.");
        }
        if (string.IsNullOrWhiteSpace(contentIdentity) || contentIdentity.Length > 512)
            throw new ArgumentException("Compositing layer content identity is required and must not exceed 512 characters.", nameof(contentIdentity));

        LayerId = layerId.Trim();
        Kind = kind;
        Order = order;
        Visible = visible;
        Opacity = opacity;
        PositionX = positionX;
        PositionY = positionY;
        Scale = scale;
        RotationDegrees = rotationDegrees;
        AnchorX = anchorX;
        AnchorY = anchorY;
        CropLeft = cropLeft;
        CropTop = cropTop;
        CropRight = cropRight;
        CropBottom = cropBottom;
        ContentIdentity = contentIdentity.Trim();
        ProcessingNode = processingNode;
    }

    public string LayerId { get; }
    public ProductionCompositingLayerKind Kind { get; }
    public int Order { get; }
    public bool Visible { get; }
    public byte Opacity { get; }
    public double PositionX { get; }
    public double PositionY { get; }
    public double Scale { get; }
    public double RotationDegrees { get; }
    public double AnchorX { get; }
    public double AnchorY { get; }
    public double CropLeft { get; }
    public double CropTop { get; }
    public double CropRight { get; }
    public double CropBottom { get; }
    public string ContentIdentity { get; }
    public ProductionCompositingProcessingNodeState? ProcessingNode { get; }

    private static void ValidateNormalized(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(parameterName, "Normalized values must be finite and in the inclusive range 0..1.");
    }

    private static void ValidateCrop(double left, double top, double right, double bottom)
    {
        ValidateNormalized(left, nameof(left));
        ValidateNormalized(top, nameof(top));
        ValidateNormalized(right, nameof(right));
        ValidateNormalized(bottom, nameof(bottom));
        if (left + right >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(right), "Horizontal crop edges must leave a non-empty source region.");
        if (top + bottom >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(bottom), "Vertical crop edges must leave a non-empty source region.");
    }
}

public sealed class ProductionCompositingState : IEquatable<ProductionCompositingState>
{
    public static CompatibilityVersion CurrentVersion { get; } = new(1, 0);
    private readonly ReadOnlyCollection<ProductionCompositingLayerState> _layers;

    public ProductionCompositingState(
        CompatibilityVersion version,
        IReadOnlyList<ProductionCompositingLayerState> layers)
    {
        if (version != CurrentVersion)
            throw new NotSupportedException($"Unsupported compositing state version '{version}'. Supported version is '{CurrentVersion}'.");
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count > ProductionCompositingLayerIds.MaximumLayerCount)
            throw new ArgumentException($"Compositing state supports at most {ProductionCompositingLayerIds.MaximumLayerCount} layers.", nameof(layers));
        if (layers.Any(layer => layer is null))
            throw new ArgumentException("Compositing state must not contain null layers.", nameof(layers));

        var ordered = layers
            .OrderBy(layer => layer.Order)
            .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Select(layer => layer.LayerId).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Compositing layer identities must be unique.", nameof(layers));
        if (ordered.Select(layer => layer.Order).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Compositing layer order values must be unique.", nameof(layers));
        if (ordered.Select((layer, index) => layer.Order == index).Any(matches => !matches))
            throw new ArgumentException("Compositing layer order must be contiguous and start at zero.", nameof(layers));

        Version = version;
        _layers = Array.AsReadOnly(ordered);
    }

    public CompatibilityVersion Version { get; }
    public IReadOnlyList<ProductionCompositingLayerState> Layers => _layers;

    public bool Equals(ProductionCompositingState? other) =>
        other is not null &&
        Version == other.Version &&
        _layers.SequenceEqual(other._layers);

    public override bool Equals(object? obj) => obj is ProductionCompositingState other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        foreach (var layer in _layers)
            hash.Add(layer);
        return hash.ToHashCode();
    }
}

public sealed record ProductionSceneSpecification
{
    public ProductionSceneSpecification(
        SceneId sceneId,
        string name,
        ProductionRoutingState routing,
        ProductionCompositingState? compositingState = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Scene name is required.", nameof(name));

        SceneId = sceneId;
        Name = name.Trim();
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        CompositingState = compositingState;
    }

    public SceneId SceneId { get; }
    public string Name { get; }
    public ProductionRoutingState Routing { get; }
    public ProductionCompositingState? CompositingState { get; }
}

public sealed class ProductionSpecification
{
    private readonly ReadOnlyCollection<ProductionSourceSpecification> _sources;
    private readonly ReadOnlyCollection<ProductionSceneSpecification> _scenes;
    private readonly OutputRoleStateCollection _initialOutputRoles;

    public ProductionSpecification(
        CompatibilityVersion version,
        ProductionId productionId,
        string name,
        IReadOnlyList<ProductionSourceSpecification> sources,
        ProductionRoutingState initialRouting,
        IReadOnlyList<ProductionSceneSpecification>? scenes = null,
        IReadOnlyList<ProductionOutputRoleState>? initialOutputRoles = null)
    {
        ControlContractVersion.EnsureSupported(version);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Production name is required.", nameof(name));
        if (sources is null)
            throw new ArgumentNullException(nameof(sources));
        if (initialRouting is null)
            throw new ArgumentNullException(nameof(initialRouting));
        if (sources.Count == 0)
            throw new ArgumentException("A production specification requires at least one logical source.", nameof(sources));
        if (sources.Any(source => source is null))
            throw new ArgumentException("Production sources must not contain null values.", nameof(sources));

        var snapshot = sources.ToArray();
        if (snapshot.Select(source => source.SourceId).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Production source identities must be unique.", nameof(sources));

        var sceneSnapshot = scenes?.ToArray()
            ?? snapshot.Select(source => new ProductionSceneSpecification(
                new SceneId(source.SourceId.Value),
                source.Name,
                new ProductionRoutingState(source.SourceId, source.SourceId))).ToArray();
        if (sceneSnapshot.Any(scene => scene is null))
            throw new ArgumentException("Production scenes must not contain null values.", nameof(scenes));
        if (sceneSnapshot.Select(scene => scene.SceneId).Distinct().Count() != sceneSnapshot.Length)
            throw new ArgumentException("Production scene identities must be unique.", nameof(scenes));

        Version = version;
        ProductionId = productionId;
        Name = name.Trim();
        _sources = Array.AsReadOnly(snapshot);
        _scenes = Array.AsReadOnly(sceneSnapshot);
        _initialOutputRoles = OutputRoleStateCollection.Normalize(initialOutputRoles, initialRouting.ProgramSourceId);
        InitialRouting = initialRouting;
    }

    public CompatibilityVersion Version { get; }
    public ProductionId ProductionId { get; }
    public string Name { get; }
    public IReadOnlyList<ProductionSourceSpecification> Sources => _sources;
    public IReadOnlyList<ProductionSceneSpecification> Scenes => _scenes;
    public IReadOnlyList<ProductionOutputRoleState> InitialOutputRoles => _initialOutputRoles;
    public ProductionRoutingState InitialRouting { get; }
}

public sealed record DesiredProductionState
{
    private readonly OutputRoleStateCollection _outputRoles;

    public DesiredProductionState(
        CompatibilityVersion version,
        ProductionId productionId,
        Revision basedOnAuthoritativeRevision,
        ProductionRoutingState routing,
        SceneId? activeSceneId = null,
        IReadOnlyList<ProductionOutputRoleState>? outputRoles = null,
        ProductionCompositingState? compositingState = null)
    {
        ControlContractVersion.EnsureSupported(version);
        Version = version;
        ProductionId = productionId;
        BasedOnAuthoritativeRevision = basedOnAuthoritativeRevision;
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        ActiveSceneId = activeSceneId;
        _outputRoles = OutputRoleStateCollection.Normalize(outputRoles, Routing.ProgramSourceId);
        CompositingState = compositingState;
    }

    public CompatibilityVersion Version { get; }
    public ProductionId ProductionId { get; }
    public Revision BasedOnAuthoritativeRevision { get; }
    public ProductionRoutingState Routing { get; }
    public SceneId? ActiveSceneId { get; }
    public IReadOnlyList<ProductionOutputRoleState> OutputRoles => _outputRoles;
    public ProductionCompositingState? CompositingState { get; }
}

public sealed record AuthoritativeProductionState
{
    private readonly OutputRoleStateCollection _outputRoles;

    public AuthoritativeProductionState(
        CompatibilityVersion version,
        ProductionId productionId,
        Revision revision,
        ProductionRoutingState routing,
        SceneId? activeSceneId = null,
        IReadOnlyList<ProductionOutputRoleState>? outputRoles = null,
        ProductionCompositingState? compositingState = null)
    {
        ControlContractVersion.EnsureSupported(version);
        Version = version;
        ProductionId = productionId;
        Revision = revision;
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        ActiveSceneId = activeSceneId;
        _outputRoles = OutputRoleStateCollection.Normalize(outputRoles, Routing.ProgramSourceId);
        CompositingState = compositingState;
    }

    public CompatibilityVersion Version { get; }
    public ProductionId ProductionId { get; }
    public Revision Revision { get; }
    public ProductionRoutingState Routing { get; }
    public SceneId? ActiveSceneId { get; }
    public IReadOnlyList<ProductionOutputRoleState> OutputRoles => _outputRoles;
    public ProductionCompositingState? CompositingState { get; }
}

public sealed record ControlCommandMetadata
{
    public ControlCommandMetadata(
        CompatibilityVersion version,
        CommandId commandId,
        ProductionId productionId,
        Revision expectedRevision)
    {
        ControlContractVersion.EnsureSupported(version);
        Version = version;
        CommandId = commandId;
        ProductionId = productionId;
        ExpectedRevision = expectedRevision;
    }

    public CompatibilityVersion Version { get; }
    public CommandId CommandId { get; }
    public ProductionId ProductionId { get; }
    public Revision ExpectedRevision { get; }
}

public sealed record SelectPreviewCommand
{
    public SelectPreviewCommand(ControlCommandMetadata metadata, ProductionSourceId sourceId)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SourceId = sourceId;
    }

    public ControlCommandMetadata Metadata { get; }
    public ProductionSourceId SourceId { get; }
}

public sealed record ActivateSceneCommand
{
    public ActivateSceneCommand(ControlCommandMetadata metadata, SceneId sceneId)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SceneId = sceneId;
    }

    public ControlCommandMetadata Metadata { get; }
    public SceneId SceneId { get; }
}

public sealed record CutProgramCommand
{
    public CutProgramCommand(ControlCommandMetadata metadata, ProductionSourceId sourceId)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SourceId = sourceId;
    }

    public ControlCommandMetadata Metadata { get; }
    public ProductionSourceId SourceId { get; }
}

public sealed class ControlValidationReport
{
    private readonly ReadOnlyCollection<ValidationIssue> _issues;

    public ControlValidationReport(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues is null)
            throw new ArgumentNullException(nameof(issues));
        if (issues.Any(issue => issue is null))
            throw new ArgumentException("Validation issues must not contain null values.", nameof(issues));

        _issues = Array.AsReadOnly(issues.ToArray());
    }

    public bool IsValid => _issues.Count == 0;
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public static ControlValidationReport Valid { get; } = new(Array.Empty<ValidationIssue>());

    public static ControlValidationReport Invalid(params ValidationIssue[] issues) =>
        issues is { Length: > 0 }
            ? new ControlValidationReport(issues)
            : throw new ArgumentException("An invalid report requires at least one issue.", nameof(issues));
}
