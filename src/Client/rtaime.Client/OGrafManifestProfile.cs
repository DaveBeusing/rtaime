// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

// This is an inspection projection, not an admitted HTML renderer or
// an implementation of the OGraf Server API.
public static class OGrafV1Profile
{
    public const string ManifestSchema =
        "https://ograf.ebu.io/v1/specification/json-schemas/graphics/schema.json";
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxScriptBytes = 2 * 1024 * 1024;
    public const int MaxThumbnailBytes = 1024 * 1024;
    public const int MaxParameters = 16;
    public const int MaxActions = 16;
    public const int MaxThumbnails = 4;
    public const int MaxSteps = 32;
    public const int MaxDurationMilliseconds = 60_000;
}

public sealed record OGrafParameterInspection(
    string Name,
    string Type,
    bool Required,
    string? DefaultJson);

public sealed record OGrafActionDurationInspection(
    string Type,
    int DurationMilliseconds,
    string? CustomActionId);

public sealed record OGrafPackageInspection(
    string ManifestPath,
    string GraphicId,
    string Name,
    string? Version,
    string MainPath,
    string ManifestSha256,
    string MainSha256,
    IReadOnlyList<OGrafParameterInspection> Parameters,
    IReadOnlyList<OGrafActionDurationInspection> ActionDurations,
    IReadOnlyList<string> CustomActionIds,
    string? ThumbnailPath,
    bool SupportsRealTime,
    bool SupportsNonRealTime,
    int StepCount)
{
    // A manifest parser cannot prove JS isolation, per-frame pixel provenance,
    // render budget or Preview confirmation. These gates must remain closed.
    public bool RendererQualified => false;
    public bool PreviewFrameConfirmed => false;
    public bool CanTakeToProgram => false;
    public string AdmissionStatus =>
        "INSPECTED ONLY · isolated OGraf renderer and frame qualification unavailable";
}
