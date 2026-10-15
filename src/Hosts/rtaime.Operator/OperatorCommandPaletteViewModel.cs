// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorPaletteCommand(
	string Id, string Label, string Category, ICommand Command, object? Parameter = null,
	bool RequiresExplicitButton = false, Func<string?>? UnavailableReason = null);

public sealed class OperatorPaletteEntry : INotifyPropertyChanged, IDisposable
{
	private readonly Func<string> _gesture;
	private string _state = "", _shortcut = "";
	public OperatorPaletteEntry(OperatorPaletteCommand descriptor, Func<string> gesture)
	{
		Descriptor = descriptor; _gesture = gesture;
		descriptor.Command.CanExecuteChanged += OnCommandChanged;
		Refresh();
	}
	public OperatorPaletteCommand Descriptor { get; }
	public string Id => Descriptor.Id;
	public string Label => Descriptor.Label;
	public string Category => Descriptor.Category;
	public string Gesture => _shortcut;
	public string State => _state;
	public bool IsAvailable => Reason.Length == 0;
	public string Reason => Descriptor.UnavailableReason?.Invoke() ??
		(Descriptor.Command.CanExecute(Descriptor.Parameter) ? "" : "Unavailable in the current selection or command context.");
	public string AutomationLabel => $"{Label}. {Gesture}. {State}";
	public event PropertyChangedEventHandler? PropertyChanged;
	public void Refresh()
	{
		_shortcut = _gesture();
		var reason = Reason;
		_state = reason.Length > 0 ? reason : Descriptor.RequiresExplicitButton ? "Use the Execute button; typing Enter cannot run this action." : "Available";
		foreach (var name in new[] { nameof(Gesture), nameof(State), nameof(IsAvailable), nameof(AutomationLabel) })
			PropertyChanged?.Invoke(this, new(name));
	}
	private void OnCommandChanged(object? sender, EventArgs e) => Refresh();
	public void Dispose() => Descriptor.Command.CanExecuteChanged -= OnCommandChanged;
}

public static class OperatorPaletteSearch
{
	public static int Score(string label, string id, string category, string query)
	{
		query = query.Trim();
		if (query.Length == 0) return 0;
		var text = (label + " " + id + " " + category).ToUpperInvariant();
		var tokens = query.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (tokens.Length > 8) return -1;
		var score = 0;
		foreach (var token in tokens)
		{
			var exact = text.IndexOf(token, StringComparison.Ordinal);
			if (exact >= 0) { score += exact; continue; }
			var cursor = 0;
			foreach (var character in token)
			{
				var next = text.IndexOf(character, cursor);
				if (next < 0) return -1;
				score += 10 + next - cursor;
				cursor = next + 1;
			}
		}
		return score;
	}
}

public sealed class OperatorCommandPaletteViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly OperatorKeyboardCommandRegistry _registry;
	private readonly OperatorShortcutProfileStore _store;
	private readonly OperatorViewModel? _operator;
	private readonly Func<string?>? _importPicker, _exportPicker;
	private readonly OperatorPaletteEntry[] _entries;
	private string _query = "", _status = "Search commands. Production actions require the Execute button.", _profileName = "Custom shortcuts";
	private OperatorPaletteEntry? _selected;
	private OperatorShortcutDefinition? _editingShortcut;
	private Key _editingKey = Key.Q;
	private ModifierKeys _editingModifiers = ModifierKeys.Control | ModifierKeys.Shift;
	private bool _showProfiles, _disposed, _loaded, _loading;
	private Task _save = Task.CompletedTask;

	public OperatorCommandPaletteViewModel(OperatorKeyboardCommandRegistry registry,
		IEnumerable<OperatorPaletteCommand> commands, OperatorShortcutProfileStore? store = null,
		OperatorViewModel? @operator = null, Func<string?>? importPicker = null, Func<string?>? exportPicker = null)
	{
		_registry = registry; _store = store ?? new(); _operator = @operator;
		_importPicker = importPicker; _exportPicker = exportPicker;
		var descriptors = commands.ToArray();
		if (descriptors.Length > 96 || descriptors.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != descriptors.Length)
			throw new ArgumentException("Command catalogue must contain at most 96 unique commands.", nameof(commands));
		_entries = descriptors.Select(command => new OperatorPaletteEntry(command,
			() => _registry.Definitions.FirstOrDefault(item => item.Id == command.Id)?.Gesture ?? "")).ToArray();
		ExecuteCommand = new AsyncRelayCommand(() => { ExecuteSelected(false); return Task.CompletedTask; }, () => !_disposed && Selected?.IsAvailable == true);
		ToggleProfilesCommand = new AsyncRelayCommand(() =>
		{
			_showProfiles = !_showProfiles; OnPropertyChanged(nameof(ProfilesVisibility)); OnPropertyChanged(nameof(ResultsVisibility)); return Task.CompletedTask;
		});
		AssignShortcutCommand = new AsyncRelayCommand(AssignAsync, () => !_disposed && !_loading && EditingShortcut is not null);
		RemoveOverrideCommand = new AsyncRelayCommand(RemoveOverrideAsync, () => !_disposed && !_loading && EditingShortcut is not null);
		ResetShortcutsCommand = new AsyncRelayCommand(() => ApplyAndSaveAsync(OperatorShortcutProfile.Defaults), () => !_disposed && !_loading);
		ImportCommand = new AsyncRelayCommand(ImportAsync, () => !_disposed && !_loading && _importPicker is not null);
		ExportCommand = new AsyncRelayCommand(ExportAsync, () => !_disposed && !_loading && _exportPicker is not null);
		foreach (var entry in _entries) entry.PropertyChanged += OnEntryChanged;
		_registry.Changed += OnRegistryChanged;
		if (_operator is not null) _operator.PropertyChanged += OnOperatorChanged;
		EditingShortcut = EditableShortcuts.FirstOrDefault();
		Filter();
	}
	public ObservableCollection<OperatorPaletteEntry> Results { get; } = [];
	public IReadOnlyList<OperatorPaletteEntry> Entries => _entries;
	public IReadOnlyList<OperatorShortcutDefinition> EditableShortcuts => _registry.CanonicalDefinitions
		.Where(item => !OperatorShortcutProfiles.IsReservedCommand(item.Id)).ToArray();
	public IReadOnlyList<Key> AllowedKeys { get; } = Enum.GetValues<Key>().Distinct()
		.Where(key => OperatorShortcutProfiles.IsSafeOverride(key, ModifierKeys.Control)).ToArray();
	public IReadOnlyList<ModifierKeys> AllowedModifiers { get; } = [ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Shift];
	public ICommand ExecuteCommand { get; }
	public ICommand ToggleProfilesCommand { get; }
	public ICommand AssignShortcutCommand { get; }
	public ICommand RemoveOverrideCommand { get; }
	public ICommand ResetShortcutsCommand { get; }
	public ICommand ImportCommand { get; }
	public ICommand ExportCommand { get; }
	public Visibility ResultsVisibility => _showProfiles ? Visibility.Collapsed : Visibility.Visible;
	public Visibility ProfilesVisibility => _showProfiles ? Visibility.Visible : Visibility.Collapsed;
	public event PropertyChangedEventHandler? PropertyChanged;
	public event EventHandler? ExecutionCompleted;
	public string Query { get => _query; set { if (Set(ref _query, (value ?? "")[..Math.Min(value?.Length ?? 0, 128)])) Filter(); } }
	public string Status { get => _status; private set => Set(ref _status, value); }
	public string ProfileName { get => _profileName; set => Set(ref _profileName, (value ?? "")[..Math.Min(value?.Length ?? 0, 64)]); }
	public OperatorPaletteEntry? Selected
	{
		get => _selected;
		set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(SelectedState)); RaiseCommands(); } }
	}
	public string SelectedState => Selected?.State ?? "No commands match this search.";
	public OperatorShortcutDefinition? EditingShortcut
	{
		get => _editingShortcut;
		set
		{
			if (!Set(ref _editingShortcut, value)) return;
			if (value is not null)
			{
				var binding = _registry.Definitions.First(item => item.Id == value.Id);
				EditingKey = AllowedKeys.Contains(binding.Key) ? binding.Key : Key.Q;
				EditingModifiers = AllowedModifiers.Contains(binding.Modifiers) ? binding.Modifiers : ModifierKeys.Control | ModifierKeys.Shift;
			}
			RaiseCommands();
		}
	}
	public Key EditingKey { get => _editingKey; set { if (AllowedKeys.Contains(value)) Set(ref _editingKey, value); } }
	public ModifierKeys EditingModifiers { get => _editingModifiers; set { if (AllowedModifiers.Contains(value)) Set(ref _editingModifiers, value); } }

	public bool ExecuteSelected(bool fromTextEntry)
	{
		if (_disposed || Selected is not { } entry) return false;
		var reason = entry.Reason;
		if (reason.Length > 0) { Status = reason; Refresh(); return false; }
		if (fromTextEntry && entry.Descriptor.RequiresExplicitButton)
		{ Status = "Use the Execute button for this action; Enter in a text field is protected."; return false; }
		// Recheck at the command boundary; a displayed result is never an authorization token.
		if (!entry.Descriptor.Command.CanExecute(entry.Descriptor.Parameter)) { Refresh(); return false; }
		entry.Descriptor.Command.Execute(entry.Descriptor.Parameter);
		ExecutionCompleted?.Invoke(this, EventArgs.Empty);
		return true;
	}
	public void MoveSelection(int direction)
	{
		if (Results.Count == 0) return;
		var index = Selected is null ? 0 : Results.IndexOf(Selected);
		Selected = Results[Math.Clamp(index + direction, 0, Results.Count - 1)];
	}
	private void Filter()
	{
		var previous = Selected?.Id;
		var matches = _entries.Select(entry => (Entry: entry, Score: OperatorPaletteSearch.Score(entry.Label, entry.Id, entry.Category, Query)))
			.Where(item => item.Score >= 0).OrderBy(item => item.Score)
			.ThenBy(item => item.Entry.Category, StringComparer.Ordinal).ThenBy(item => item.Entry.Label, StringComparer.Ordinal)
			.Take(64).Select(item => item.Entry).ToArray();
		Results.Clear(); foreach (var entry in matches) Results.Add(entry);
		Selected = matches.FirstOrDefault(entry => entry.Id == previous) ?? matches.FirstOrDefault();
		OnPropertyChanged(nameof(SelectedState));
	}
	public void Refresh()
	{
		foreach (var entry in _entries) entry.Refresh();
		OnPropertyChanged(nameof(SelectedState)); RaiseCommands();
	}
	public async Task LoadPreferencesAsync()
	{
		if (_loaded || _loading || _disposed) return;
		_loading = true; RaiseCommands();
		try
		{
			var profile = await _store.ReadAsync();
			if (_disposed) return;
			if (_registry.TryApplyProfile(profile, out var error)) { _loaded = true; ProfileName = _registry.CurrentProfile.Name!; }
			else Status = "Shortcut preferences rejected; canonical defaults retained. " + error;
		}
		catch (Exception ex) when (IsFileFailure(ex))
		{ Status = "Shortcut preferences unavailable; canonical defaults retained. " + ex.Message; }
		finally { _loading = false; RaiseCommands(); }
	}
	private Task AssignAsync()
	{
		if (EditingShortcut is null) return Task.CompletedTask;
		var bindings = _registry.CurrentProfile.Bindings.Where(item => item.CommandId != EditingShortcut.Id)
			.Append(new OperatorShortcutOverride(EditingShortcut.Id, EditingKey, EditingModifiers)).ToArray();
		return ApplyAndSaveAsync(new(2, ProfileName, bindings));
	}
	private Task RemoveOverrideAsync() => ApplyAndSaveAsync(new(2, ProfileName,
		_registry.CurrentProfile.Bindings.Where(item => item.CommandId != EditingShortcut?.Id).ToArray()));
	public Task ApplyAndSaveAsync(OperatorShortcutProfile profile)
	{
		if (_disposed || _loading) return Task.CompletedTask;
		if (!_registry.TryApplyProfile(profile, out var error)) { Status = error; return Task.CompletedTask; }
		_loaded = true; ProfileName = _registry.CurrentProfile.Name!;
		var snapshot = _registry.CurrentProfile;
		_save = SaveAfterAsync(_save, snapshot);
		return _save;
	}
	private async Task SaveAfterAsync(Task previous, OperatorShortcutProfile profile)
	{
		await previous;
		try { await _store.WriteAsync(profile); Status = "Shortcut profile saved locally."; }
		catch (Exception ex) when (IsFileFailure(ex)) { Status = "Profile active for this session; save failed. " + ex.Message; }
	}
	public async Task ImportProfileAsync(string path)
	{
		try
		{
			var profile = await _store.ReadAsync(path);
			if (_disposed) return;
			await ApplyAndSaveAsync(profile);
		}
		catch (Exception ex) when (IsFileFailure(ex)) { Status = "Import rejected; current profile retained. " + ex.Message; }
	}
	public async Task ExportProfileAsync(string path)
	{
		if (_disposed) return;
		try
		{
			await _store.WriteAsync(_registry.CurrentProfile, path);
			Status = "Shortcut preset exported.";
		}
		catch (Exception ex) when (IsFileFailure(ex)) { Status = "Export failed. " + ex.Message; }
	}
	private async Task ImportAsync() { if (_importPicker?.Invoke() is { } path) await ImportProfileAsync(path); }
	private async Task ExportAsync() { if (_exportPicker?.Invoke() is { } path) await ExportProfileAsync(path); }
	public Task FlushAsync() => _save;
	private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException;
	private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (ReferenceEquals(sender, Selected) && e.PropertyName == nameof(OperatorPaletteEntry.State))
		{ OnPropertyChanged(nameof(SelectedState)); ((AsyncRelayCommand)ExecuteCommand).RaiseCanExecuteChanged(); }
	}
	private void OnRegistryChanged(object? sender, EventArgs e) => Refresh();
	private void OnOperatorChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is null or nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale) or
			nameof(OperatorViewModel.IsBusy) or nameof(OperatorViewModel.ProgramSafety) or nameof(OperatorViewModel.RuntimeStatus) or nameof(OperatorViewModel.PreviewSourceId))
			Refresh();
	}
	private void RaiseCommands()
	{
		foreach (var command in new[] { ExecuteCommand, AssignShortcutCommand, RemoveOverrideCommand, ResetShortcutsCommand, ImportCommand, ExportCommand }.OfType<AsyncRelayCommand>())
			command.RaiseCanExecuteChanged();
	}
	public void Dispose()
	{
		_disposed = true; _registry.Changed -= OnRegistryChanged;
		if (_operator is not null) _operator.PropertyChanged -= OnOperatorChanged;
		foreach (var entry in _entries) { entry.PropertyChanged -= OnEntryChanged; entry.Dispose(); }
	}
	private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value; OnPropertyChanged(property); return true;
	}
	private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
