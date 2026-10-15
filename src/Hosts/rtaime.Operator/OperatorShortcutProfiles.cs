// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace rtaime.Operator;

public enum OperatorShortcutScope { Global, Timeline, MediaLibrary }
public sealed record OperatorShortcutOverride(string CommandId, Key Key, ModifierKeys Modifiers);
public sealed record OperatorShortcutProfile(int Version, string? Name, OperatorShortcutOverride[] Bindings)
{
	public const int CurrentVersion = 2;
	public const int MaximumBindings = 64;
	public static OperatorShortcutProfile Defaults => new(CurrentVersion, "Canonical defaults", []);
}

public static class OperatorShortcutProfiles
{
	public static bool IsReservedCommand(string id) => id is
		"command-palette" or "sync" or "media-search" or "auto" or "cut" or "record" or "show-control-go" or "fullscreen" or "exit-fullscreen";

	public static bool IsSafeOverride(Key key, ModifierKeys modifiers)
	{
		if (modifiers != ModifierKeys.Control && modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return false;
		if (!Enum.IsDefined(key)) return false;
		// Preserve local editing, navigation, system and production gestures.
		if (key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.F or Key.K or
			Key.F1 or Key.F4 or Key.F5 or Key.F9 or Key.F11) return false;
		return (int)key is >= (int)Key.A and <= (int)Key.Z or >= (int)Key.D0 and <= (int)Key.D9 or >= (int)Key.F1 and <= (int)Key.F24;
	}

	public static bool TryResolve(OperatorShortcutProfile profile, IReadOnlyList<OperatorShortcutDefinition> canonical,
		out OperatorShortcutProfile normalized, out OperatorShortcutDefinition[] resolved, out string error)
	{
		normalized = OperatorShortcutProfile.Defaults;
		resolved = [];
		error = "";
		if (profile is null || profile.Version is not (1 or OperatorShortcutProfile.CurrentVersion) ||
			profile.Bindings is null || profile.Bindings.Length > OperatorShortcutProfile.MaximumBindings)
		{ error = "Unsupported profile version or binding count (maximum 64)."; return false; }
		var name = profile.Version == 1 && string.IsNullOrWhiteSpace(profile.Name) ? "Imported shortcuts" : profile.Name;
		if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl))
		{ error = "Profile name must contain 1–64 printable characters."; return false; }
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var overrides = new Dictionary<string, OperatorShortcutOverride>(StringComparer.Ordinal);
		foreach (var binding in profile.Bindings)
		{
			if (binding is null || string.IsNullOrWhiteSpace(binding.CommandId) || binding.CommandId.Length > 64 ||
				!ids.Add(binding.CommandId) || !canonical.Any(command => command.Id == binding.CommandId))
			{ error = "Unknown or duplicate command in shortcut profile."; return false; }
			if (IsReservedCommand(binding.CommandId))
			{ error = "Reserved shortcut cannot be overridden: " + binding.CommandId; return false; }
			if (!IsSafeOverride(binding.Key, binding.Modifiers))
			{ error = "Use Ctrl or Ctrl+Shift with a permitted letter, digit or function key; editing/system keys are reserved."; return false; }
			overrides.Add(binding.CommandId, binding);
		}
		resolved = canonical.Select(command => overrides.TryGetValue(command.Id, out var binding)
			? command with { Key = binding.Key, Modifiers = binding.Modifiers } : command).ToArray();
		var conflicts = OperatorKeyboardCommandRegistry.FindConflicts(resolved);
		if (conflicts.Count > 0)
		{ error = "Shortcut collision in overlapping focus scopes: " + string.Join(", ", conflicts); resolved = []; return false; }
		normalized = new(OperatorShortcutProfile.CurrentVersion, name.Trim(), profile.Bindings.ToArray());
		return true;
	}
}

// File content contains command IDs and key values only, never executable commands.
public sealed class OperatorShortcutProfileStore(string? path = null)
{
	public const int MaximumBytes = 64 * 1024;
	private readonly string _path = path ?? Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtaime", "operator-shortcuts.json");
	private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions ReadOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
	public async Task<OperatorShortcutProfile> ReadAsync(string? path = null, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var source = path ?? _path;
			if (path is null && !File.Exists(source)) return OperatorShortcutProfile.Defaults;
			await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
			if (stream.Length > MaximumBytes) throw new InvalidDataException("Shortcut profile exceeds 64 KiB.");
			var bytes = new byte[MaximumBytes + 1];
			var count = 0;
			while (count < bytes.Length)
			{
				var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
				if (read == 0) break;
				count += read;
			}
			if (count > MaximumBytes) throw new InvalidDataException("Shortcut profile exceeds 64 KiB.");
			using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<OperatorShortcutProfile>(bytes.AsSpan(0, count), ReadOptions)
				?? throw new InvalidDataException("Shortcut profile is empty.");
		}
		finally { _gate.Release(); }
	}
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate shortcut profile field: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

	public async Task WriteAsync(OperatorShortcutProfile profile, string? path = null, CancellationToken cancellationToken = default)
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes(profile);
		if (bytes.Length > MaximumBytes) throw new InvalidDataException("Shortcut profile exceeds 64 KiB.");
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		var destination = path ?? _path;
		var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			var directory = Path.GetDirectoryName(destination);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
			File.Move(temporary, destination, overwrite: true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
			finally { _gate.Release(); }
		}
	}
}
