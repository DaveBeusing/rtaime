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
	private bool _showProfiles, _disposed, _loaded, _loading, _importing, _exporting;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _saveGate = new();
    private OperatorShortcutProfile? _pendingSave;
    private bool _saveRunning;
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
		ExecuteCommand = new AsyncRelayCommand(() => { ExecuteSelected(false); return Task.CompletedTask; }, () => !_disposed && IsBrowsingCommands && Selected?.IsAvailable == true);
		ToggleProfilesCommand = new AsyncRelayCommand(() =>
		{
			SetProfileMode(!_showProfiles); return Task.CompletedTask;
		});
		AssignShortcutCommand = new AsyncRelayCommand(AssignAsync, () => !_disposed && !IsProfileBusy && EditingShortcut is not null);
		RemoveOverrideCommand = new AsyncRelayCommand(RemoveOverrideAsync, () => !_disposed && !IsProfileBusy && EditingShortcut is not null);
		ResetShortcutsCommand = new AsyncRelayCommand(() => ApplyAndSaveAsync(OperatorShortcutProfile.Defaults), () => !_disposed && !IsProfileBusy);
		ImportCommand = new AsyncRelayCommand(ImportAsync, () => !_disposed && !IsProfileBusy && _importPicker is not null);
		ExportCommand = new AsyncRelayCommand(ExportAsync, () => !_disposed && !IsProfileBusy && _exportPicker is not null);
		foreach (var entry in _entries) entry.PropertyChanged += OnEntryChanged;
		_registry.Changed += OnRegistryChanged;
		if (_operator is not null) _operator.PropertyChanged += OnOperatorChanged;
		EditingShortcut = EditableShortcuts.FirstOrDefault();
		Filter();
	}
    private void SetProfileMode(bool showProfiles)
    {
        _showProfiles = showProfiles;
        OnPropertyChanged(nameof(ProfilesVisibility)); OnPropertyChanged(nameof(ResultsVisibility));
        OnPropertyChanged(nameof(IsBrowsingCommands)); OnPropertyChanged(nameof(SelectedState));
        RaiseCommands();
    }
    public void BeginSearch()
    {
        if (_disposed) return;
        SetProfileMode(false); Query = ""; Refresh();
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
	public bool IsBrowsingCommands => !_showProfiles;
    public bool IsProfileBusy => _loading || _importing || _exporting;
    public string EditingBindingSummary => EditingShortcut is null ? "No editable command." :
        $"Current: {_registry.Definitions.First(item => item.Id == EditingShortcut.Id).Gesture} · Focus: {EditingShortcut.Scope switch { OperatorShortcutScope.Global => "Operator", OperatorShortcutScope.Timeline => "Timeline", _ => "Media Library" }}";
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
	public string SelectedState => _showProfiles ? "Editing shortcuts. Command execution is paused." : Selected?.State ?? "No commands match this search.";
	public OperatorShortcutDefinition? EditingShortcut
	{
		get => _editingShortcut;
		set
		{
			if (!Set(ref _editingShortcut, value)) return;
			SyncEditingGesture();
            OnPropertyChanged(nameof(EditingBindingSummary));
			RaiseCommands();
		}
	}
	public Key EditingKey { get => _editingKey; set { if (AllowedKeys.Contains(value)) Set(ref _editingKey, value); } }
	public ModifierKeys EditingModifiers { get => _editingModifiers; set { if (AllowedModifiers.Contains(value)) Set(ref _editingModifiers, value); } }

	public bool ExecuteSelected(bool fromTextEntry)
	{
		if (_disposed || !IsBrowsingCommands || Selected is not { } entry) return false;
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
		if (!IsBrowsingCommands || Results.Count == 0) return;
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
	public async Task LoadPreferencesAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded || IsProfileBusy || _disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
		_loading = true; RaiseProfileState();
		try
		{
			var profile = await _store.ReadAsync(cancellationToken: linked.Token);
			if (_disposed) return;
			if (_registry.TryApplyProfile(profile, out var error)) { _loaded = true; ProfileName = _registry.CurrentProfile.Name!; }
			else Status = "Shortcut preferences rejected; canonical defaults retained. " + error;
		}
		catch (Exception ex) when (IsFileFailure(ex))
		{ if (!_disposed) Status = "Shortcut preferences unavailable; canonical defaults retained. " + ex.Message; }
		finally { _loading = false; RaiseProfileState(); }
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
    public Task ApplyAndSaveAsync(OperatorShortcutProfile profile) =>
        _disposed || IsProfileBusy ? Task.CompletedTask : ApplyProfileAsync(profile);

    private Task ApplyProfileAsync(OperatorShortcutProfile profile)
    {
        if (!_registry.TryApplyProfile(profile, out var error)) { Status = error; return Task.CompletedTask; }
        _loaded = true; ProfileName = _registry.CurrentProfile.Name!;
        lock (_saveGate)
        {
            _pendingSave = _registry.CurrentProfile;
            if (!_saveRunning)
            {
                _saveRunning = true;
                _save = SavePendingAsync();
            }
            return _save;
        }
    }
    private async Task SavePendingAsync()
    {
        // One dispatcher turn coalesces a burst before any disk work begins.
        await Task.Yield();
        while (true)
        {
            OperatorShortcutProfile profile;
            lock (_saveGate)
            {
                if (_pendingSave is null) { _saveRunning = false; return; }
                profile = _pendingSave; _pendingSave = null;
            }
            try
            {
                await _store.WriteAsync(profile);
                if (!_disposed && ReferenceEquals(profile, _registry.CurrentProfile)) Status = "Shortcut profile saved locally.";
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                if (!_disposed && ReferenceEquals(profile, _registry.CurrentProfile))
                    Status = "Profile active for this session; save failed. " + ex.Message;
            }
        }
    }
    public async Task ImportProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsProfileBusy) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _importing = true; RaiseProfileState();
        try
        {
            var profile = await _store.ReadAsync(path, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            await ApplyProfileAsync(profile);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            if (!_disposed) Status = ex is OperationCanceledException ? "Import canceled; current profile retained." :
                "Import rejected; current profile retained. " + ex.Message;
        }
        finally { _importing = false; RaiseProfileState(); }
    }
    public async Task ExportProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsProfileBusy) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _exporting = true; RaiseProfileState();
        try
        {
            await _store.WriteAsync(_registry.CurrentProfile, path, linked.Token);
            if (!_disposed) Status = "Shortcut preset exported.";
        }
        catch (Exception ex) when (IsFileFailure(ex))
        { if (!_disposed) Status = "Export failed. " + ex.Message; }
        finally { _exporting = false; RaiseProfileState(); }
    }
	private async Task ImportAsync() { if (_importPicker?.Invoke() is { } path) await ImportProfileAsync(path); }
	private async Task ExportAsync() { if (_exportPicker?.Invoke() is { } path) await ExportProfileAsync(path); }
	public Task FlushAsync() { lock (_saveGate) return _save; }
    private void RaiseProfileState()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(IsProfileBusy));
        RaiseCommands();
    }
	private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException;
	private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (ReferenceEquals(sender, Selected) && e.PropertyName == nameof(OperatorPaletteEntry.State))
		{ OnPropertyChanged(nameof(SelectedState)); ((AsyncRelayCommand)ExecuteCommand).RaiseCanExecuteChanged(); }
	}
	private void SyncEditingGesture()
	{
		if (EditingShortcut is null) return;
		var binding = _registry.Definitions.First(item => item.Id == EditingShortcut.Id);
		EditingKey = AllowedKeys.Contains(binding.Key) ? binding.Key : Key.Q;
		EditingModifiers = AllowedModifiers.Contains(binding.Modifiers) ? binding.Modifiers : ModifierKeys.Control | ModifierKeys.Shift;
	}
	private void OnRegistryChanged(object? sender, EventArgs e) { SyncEditingGesture(); OnPropertyChanged(nameof(EditingBindingSummary)); Refresh(); }
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
		if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        _registry.Changed -= OnRegistryChanged;
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
