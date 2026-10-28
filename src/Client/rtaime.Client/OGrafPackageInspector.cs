// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text.Json;

namespace rtaime.Client;

// Reads local OGraf manifests for *inspection*. This does not execute JavaScript,
// open network resources, grant OS access to web content or admit Program output.
public static class OGrafPackageInspector
{
    public static async ValueTask<OGrafPackageInspection> InspectAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("An OGraf manifest path is required.", nameof(manifestPath));
        if (!manifestPath.EndsWith(".ograf.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local *.ograf.json manifest.");

        var manifest = Path.GetFullPath(manifestPath);
        var root = Path.GetDirectoryName(manifest)
            ?? throw new InvalidDataException("OGraf package directory is missing.");
        EnsureNoReparsePoint(manifest, root);
        var bytes = await ReadBoundedAsync(manifest, OGrafV1Profile.MaxManifestBytes, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 20 });
        var node = document.RootElement;
        if (node.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OGraf manifest must be a JSON object.");
        if (ReadString(node, "$schema", 256) != OGrafV1Profile.ManifestSchema)
            throw new InvalidDataException("Unsupported OGraf graphics schema; v1 is required.");
        var graphicId = ReadString(node, "id", 256);
        if (graphicId.Contains('/') || graphicId.Any(char.IsControl))
            throw new InvalidDataException("OGraf graphic identity must not contain '/' or control characters.");
        var name = ReadString(node, "name", 128);
        var version = ReadOptionalString(node, "version", 64);
        var mainRef = ReadString(node, "main", 256);
        if (!mainRef.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) &&
            !mainRef.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OGraf main must reference a local JavaScript module.");
        var main = ResolveLocalResource(root, mainRef);
        var jsBytes = await ReadBoundedAsync(main, OGrafV1Profile.MaxScriptBytes, cancellationToken)
            .ConfigureAwait(false);

        var realtime = ReadBoolean(node, "supportsRealTime");
        var nonRealtime = ReadBoolean(node, "supportsNonRealTime");
        if (!realtime && !nonRealtime)
            throw new InvalidDataException("OGraf requires at least one supported rendering mode.");
        var stepCount = ReadOptionalInt(node, "stepCount", 1);
        if (stepCount < -1 || stepCount > OGrafV1Profile.MaxSteps)
            throw new InvalidDataException("OGraf step count exceeds this inspection profile.");
        ValidateOfflineRenderRequirements(node);

        var parameters = ParseParameters(node);
        var actions = ParseCustomActions(node);
        var durations = ParseActionDurations(node, actions, stepCount);
        var thumbnail = ParseThumbnails(node, root, cancellationToken);
        return new OGrafPackageInspection(
            manifest, graphicId, name, version, main,
            Convert.ToHexString(SHA256.HashData(bytes)),
            Convert.ToHexString(SHA256.HashData(jsBytes)),
            parameters, durations, actions,
            thumbnail, realtime, nonRealtime, stepCount);
    }

    // Separate, bounded PNG preview metadata; it is NEVER an OGraf-rendered frame.
    // Operators must not interpret it as an approved live frame or video preview.
    public static async ValueTask<byte[]?> ReadThumbnailAsync(
        OGrafPackageInspection inspected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspected);
        if (inspected.ThumbnailPath is null) return null;
        return await ReadBoundedAsync(inspected.ThumbnailPath, OGrafV1Profile.MaxThumbnailBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ReadString(JsonElement node, string key, int maxLength)
    {
        if (!node.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) || text.Length > maxLength ||
            text.Any(char.IsControl))
            throw new InvalidDataException($"OGraf field '{key}' is missing or invalid.");
        return text;
    }

    private static string? ReadOptionalString(JsonElement node, string key, int maxLength) =>
        node.TryGetProperty(key, out _) ? ReadString(node, key, maxLength) : null;

    private static bool ReadBoolean(JsonElement node, string key)
    {
        if (!node.TryGetProperty(key, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"OGraf field '{key}' must be a Boolean.");
        return value.GetBoolean();
    }

    private static int ReadOptionalInt(JsonElement node, string key, int fallback)
    {
        if (!node.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new InvalidDataException($"OGraf field '{key}' must be an integer.");
        return number;
    }

    private static OGrafParameterInspection[] ParseParameters(JsonElement node)
    {
        if (!node.TryGetProperty("schema", out var schema)) return [];
        if (schema.ValueKind != JsonValueKind.Object ||
            ReadString(schema, "type", 16) != "object")
            throw new InvalidDataException("This OGraf profile requires a flat object data schema.");
        if (schema.TryGetProperty("oneOf", out _) || schema.TryGetProperty("allOf", out _) ||
            schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("$ref", out _))
            throw new InvalidDataException("Composite OGraf data schemas require a qualified renderer.");
        if (!schema.TryGetProperty("properties", out var properties)) return [];
        if (properties.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OGraf data schema properties must be an object.");
        var fields = properties.EnumerateObject().ToArray();
        if (fields.Length > OGrafV1Profile.MaxParameters ||
            fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new InvalidDataException("OGraf parameter count/identity is invalid.");
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredNode))
        {
            if (requiredNode.ValueKind != JsonValueKind.Array ||
                requiredNode.GetArrayLength() > OGrafV1Profile.MaxParameters)
                throw new InvalidDataException("OGraf required parameters are invalid.");
            foreach (var member in requiredNode.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.String ||
                    member.GetString() is not { } key || !required.Add(key))
                    throw new InvalidDataException("OGraf required parameter identity is invalid.");
            }
        }

        var output = new List<OGrafParameterInspection>(fields.Length);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 64 ||
                field.Name.Any(char.IsControl) || field.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OGraf parameter identity/schema is invalid.");
            var descriptor = field.Value;
            if (descriptor.TryGetProperty("$ref", out _) ||
                descriptor.TryGetProperty("oneOf", out _) ||
                descriptor.TryGetProperty("anyOf", out _) ||
                descriptor.TryGetProperty("allOf", out _))
                throw new InvalidDataException("Nested/indirect parameter schemas are not in this profile.");
            var type = ReadString(descriptor, "type", 16);
            if (type is not ("string" or "integer" or "number" or "boolean"))
                throw new InvalidDataException("Only flat scalar OGraf parameters are inspected.");
            string? defaultJson = null;
            if (descriptor.TryGetProperty("default", out var defaultValue))
            {
                var validDefault = type switch
                {
                    "string" => defaultValue.ValueKind == JsonValueKind.String &&
                        defaultValue.GetString()!.Length <= 256,
                    "integer" => defaultValue.ValueKind == JsonValueKind.Number &&
                        defaultValue.TryGetInt64(out _),
                    "number" => defaultValue.ValueKind == JsonValueKind.Number &&
                        defaultValue.TryGetDouble(out var number) && double.IsFinite(number),
                    "boolean" => defaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => false
                };
                if (!validDefault)
                    throw new InvalidDataException($"OGraf parameter '{field.Name}' has an invalid default.");
                defaultJson = defaultValue.GetRawText();
            }
            output.Add(new OGrafParameterInspection(field.Name, type,
                required.Contains(field.Name), defaultJson));
        }
        if (required.Except(fields.Select(field => field.Name), StringComparer.Ordinal).Any())
            throw new InvalidDataException("OGraf required parameter has no schema definition.");
        return output.ToArray();
    }

    private static string[] ParseCustomActions(JsonElement node)
    {
        if (!node.TryGetProperty("customActions", out var list)) return [];
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > OGrafV1Profile.MaxActions)
            throw new InvalidDataException("OGraf custom action limit exceeded.");
        var actions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in list.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OGraf custom action must be an object.");
            var id = ReadString(action, "id", 128);
            _ = ReadString(action, "name", 128);
            if (!actions.Add(id))
                throw new InvalidDataException("Duplicate OGraf custom action identity.");
        }
        return actions.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static OGrafActionDurationInspection[] ParseActionDurations(
        JsonElement node, IReadOnlyCollection<string> actions, int stepCount)
    {
        if (!node.TryGetProperty("actionDurations", out var list)) return [];
        if (list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() > OGrafV1Profile.MaxActions)
            throw new InvalidDataException("OGraf action duration list exceeds its bound.");
        var result = new List<OGrafActionDurationInspection>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OGraf action duration must be an object.");
            var type = ReadString(entry, "type", 32);
            if (type is not ("playAction" or "stopAction" or "updateAction" or "customAction"))
                throw new InvalidDataException("Unknown OGraf action duration type.");
            var duration = ReadOptionalInt(entry, "duration", int.MinValue);
            if (duration is < -1 or > OGrafV1Profile.MaxDurationMilliseconds)
                throw new InvalidDataException("OGraf action duration is invalid or unbounded.");
            var customId = ReadOptionalString(entry, "customActionId", 128);
            if (type == "customAction"
                ? customId is null || !actions.Contains(customId)
                : customId is not null)
                throw new InvalidDataException("OGraf custom action duration identity mismatch.");
            if (!identifiers.Add(customId is null ? type : "custom:" + customId))
                throw new InvalidDataException("Duplicate OGraf action duration.");
            if (entry.TryGetProperty("steps", out var steps))
            {
                if (type != "playAction" || steps.ValueKind != JsonValueKind.Array ||
                    steps.GetArrayLength() > OGrafV1Profile.MaxSteps)
                    throw new InvalidDataException("OGraf step durations are invalid.");
                var ids = new HashSet<int>();
                var hasFallback = false;
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("OGraf action step must be an object.");
                    var ms = ReadOptionalInt(step, "duration", int.MinValue);
                    if (ms is < -1 or > OGrafV1Profile.MaxDurationMilliseconds)
                        throw new InvalidDataException("OGraf action step duration is invalid.");
                    if (step.TryGetProperty("step", out _))
                    {
                        var id = ReadOptionalInt(step, "step", -1);
                        if (id < 0 || (stepCount >= 0 && id >= stepCount) || !ids.Add(id))
                            throw new InvalidDataException("OGraf action step identity is invalid.");
                    }
                    else if (hasFallback) throw new InvalidDataException("Duplicate fallback step duration.");
                    else hasFallback = true;
                }
            }
            result.Add(new OGrafActionDurationInspection(type, duration, customId));
        }
        return result.ToArray();
    }

    private static void ValidateOfflineRenderRequirements(JsonElement node)
    {
        if (!node.TryGetProperty("renderRequirements", out var list)) return;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 8)
            throw new InvalidDataException("OGraf render requirements list is invalid.");
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OGraf render requirement must be an object.");
            if (!entry.TryGetProperty("accessToPublicInternet", out var internet))
                continue;
            if (internet.ValueKind == JsonValueKind.False) continue;
            if (internet.ValueKind == JsonValueKind.Object &&
                internet.TryGetProperty("exact", out var exact) &&
                exact.ValueKind == JsonValueKind.False &&
                !internet.TryGetProperty("ideal", out _))
                continue;
            throw new InvalidDataException(
                "This OGraf inspection profile does not admit public-network render requirements.");
        }
    }

    private static string? ParseThumbnails(JsonElement node, string root, CancellationToken cancellationToken)
    {
        if (!node.TryGetProperty("thumbnails", out var list)) return null;
        if (list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() > OGrafV1Profile.MaxThumbnails)
            throw new InvalidDataException("OGraf thumbnail count exceeds its bound.");
        string? thumbnail = null;
        foreach (var item in list.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OGraf thumbnail must be an object.");
            var file = ReadString(item, "file", 256);
            var path = ResolveLocalResource(root, file);
            var info = new FileInfo(path);
            if (info.Length > OGrafV1Profile.MaxThumbnailBytes || info.Length == 0)
                throw new InvalidDataException("OGraf thumbnail exceeds the local inspection budget.");
            // A thumbnail is not a rendered preview frame; only PNG is decoded by Operator.
            if (thumbnail is null && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                thumbnail = path;
        }
        return thumbnail;
    }

    private static string ResolveLocalResource(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 256 ||
            path.StartsWith('/') || path.Contains('\\') ||
            path.Contains(':') || path.Contains('%') ||
            Uri.TryCreate(path, UriKind.Absolute, out _) ||
            path.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".." ||
                segment.Any(char.IsControl) ||
                segment.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new InvalidDataException("OGraf resources must use plain relative local paths.");
        var combined = Path.GetFullPath(Path.Combine(root,
            path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("OGraf resource escapes the selected package directory.");
        EnsureNoReparsePoint(combined, root);
        return combined;
    }

    private static void EnsureNoReparsePoint(string path, string root)
    {
        // Resolve all package-owned child segments independently. Reparse points can
        // redirect outside the package even when GetFullPath appears to be in root.
        var basePath = Path.GetFullPath(root);
        var target = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(basePath, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("OGraf resource is outside the selected package root.");
        var current = basePath;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("OGraf package root cannot be a reparse point.");
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("OGraf package resources cannot contain symbolic links.");
        }
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        string path, int limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > int.MaxValue || stream.Length > limit)
            throw new InvalidDataException("OGraf package file exceeds its permitted size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("OGraf package file changed during inspection.");
        return bytes;
    }
}
