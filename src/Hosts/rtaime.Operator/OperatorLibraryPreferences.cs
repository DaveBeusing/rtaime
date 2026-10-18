// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace rtaime.Operator;

// References only. Labels, paths, snapshots and authority are always resolved from current state.
public sealed record OperatorLibraryReference(string Kind, string Scope, string Id)
{
    public bool IsValid => Kind is "workspace" or "media" or "show" or "scene" or "command" &&
        Safe(Id, false) && Safe(Scope, true) &&
        (Kind is "workspace" or "command" ? Scope.Length == 0 : Scope.Length > 0);
    private static bool Safe(string? value, bool empty) => value is not null && value.Length <= 256 &&
        (empty || value.Length > 0) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
public sealed record OperatorLibraryPreferences(int Version, bool RememberRecent,
    OperatorLibraryReference[] Recent, OperatorLibraryReference[] Favorites)
{
    public const int MaximumRecentPerKind = 24, MaximumFavorites = 32;
    public static OperatorLibraryPreferences Empty => new(1, false, [], []);
    public bool IsValid => Version == 1 && Recent is { Length: <= 72 } && Favorites is { Length: <= MaximumFavorites } &&
        Recent.All(r => r is not null && r.IsValid && r.Kind is "workspace" or "media" or "show") &&
        Favorites.All(r => r is not null && r.IsValid && r.Kind is "media" or "scene" or "command") &&
        Recent.Distinct().Count() == Recent.Length && Favorites.Distinct().Count() == Favorites.Length &&
        Recent.GroupBy(r => r.Kind).All(g => g.Count() <= MaximumRecentPerKind) &&
        (RememberRecent || Recent.Length == 0);
}

public sealed class OperatorLibraryPreferenceStore(string? path = null)
{
    public const int MaximumBytes = 64 * 1024;
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rtaime", "operator-library.json");
    internal string OnboardingPath => Path.ChangeExtension(_path, "onboarding.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<OperatorLibraryPreferences> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return OperatorLibraryPreferences.Empty;
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumBytes) throw new InvalidDataException("Library preferences exceed 64 KiB.");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            RejectDuplicates(json.RootElement);
            var settings = JsonSerializer.Deserialize<OperatorLibraryPreferences>(bytes.AsSpan(0, count), Options);
            if (settings?.IsValid != true) throw new InvalidDataException("Invalid library preferences.");
            return settings;
        }
        finally { _gate.Release(); }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate library preference property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    public async Task WriteAsync(OperatorLibraryPreferences settings)
    {
        if (!settings.IsValid) throw new InvalidDataException("Invalid library preferences.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Library preferences exceed 64 KiB.");
        await _gate.WaitAsync().ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _gate.Release();
        }
    }
}
