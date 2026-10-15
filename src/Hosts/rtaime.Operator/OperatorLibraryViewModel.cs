// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorLibraryResolution(string Label, string Detail, bool Available);
public sealed record OperatorHelpTopic(string Id, string Label, string Body);
public sealed record OperatorLibraryRow(OperatorLibraryReference? Reference, string Label, string Detail,
    bool Available, bool Favorite, string Help = "")
{
    public string AutomationLabel => Label + ". " + Detail + (Favorite ? ". Favorite." : "");
    public string Marker => Favorite ? "FAVORITE" : Reference?.Kind.ToUpperInvariant() ?? "HELP";
}

public sealed class OperatorLibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OperatorLibraryPreferenceStore _store;
    private readonly Func<OperatorLibraryReference, OperatorLibraryResolution> _resolve;
    private readonly Func<OperatorLibraryReference, bool> _open;
    private readonly Func<IReadOnlyList<OperatorHelpTopic>> _help;
    private readonly Func<OperatorLibraryReference?> _currentMedia, _currentScene, _currentCommand;
    private readonly CancellationTokenSource _lifetime = new();
    private OperatorLibraryPreferences _settings = OperatorLibraryPreferences.Empty;
    private OperatorLibraryPreferences? _pending;
    private readonly object _saveGate = new();
    private Task _save = Task.CompletedTask;
    private bool _saving, _loading, _loaded, _disposed;
    private string _view = "Recent", _query = "", _status = "History is off. Favorites store identifiers only.";
    private OperatorLibraryRow? _selected;
    public OperatorLibraryViewModel(Func<OperatorLibraryReference, OperatorLibraryResolution> resolve,
        Func<OperatorLibraryReference, bool> open, Func<IReadOnlyList<OperatorHelpTopic>> help,
        Func<OperatorLibraryReference?> currentMedia, Func<OperatorLibraryReference?> currentScene,
        Func<OperatorLibraryReference?> currentCommand, OperatorLibraryPreferenceStore? store = null)
    {
        _resolve = resolve; _open = open; _help = help; _currentMedia = currentMedia;
        _currentScene = currentScene; _currentCommand = currentCommand; _store = store ?? new();
        OpenCommand = new OperatorShellCommand(OpenSelected, () => !_disposed && Selected?.Available == true);
        ToggleHistoryCommand = new OperatorShellCommand(() => Change(_settings with
        { RememberRecent = !RememberRecent, Recent = [] }), CanEdit);
        ClearHistoryCommand = new OperatorShellCommand(() => Change(_settings with { Recent = [] }), CanEdit);
        RemoveCommand = new OperatorShellCommand(RemoveSelected, () => CanEdit() && Selected?.Reference is not null);
        FavoriteMediaCommand = new OperatorShellCommand(() => ToggleFavorite(_currentMedia()), CanEdit);
        FavoriteSceneCommand = new OperatorShellCommand(() => ToggleFavorite(_currentScene()), CanEdit);
        FavoriteCommandCommand = new OperatorShellCommand(() => ToggleFavorite(_currentCommand()), CanEdit);
        RefreshCommand = new OperatorShellCommand(Refresh);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<OperatorLibraryRow> Rows { get; } = [];
    public IReadOnlyList<string> Views { get; } = ["Recent", "Favorites", "Help"];
    public ICommand OpenCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand FavoriteMediaCommand { get; }
    public ICommand FavoriteSceneCommand { get; }
    public ICommand FavoriteCommandCommand { get; }
    public ICommand RefreshCommand { get; }
    public bool RememberRecent => _settings.RememberRecent;
    public string HistoryLabel => RememberRecent ? "History on · turn off and erase" : "History off · enable locally";
    public string View { get => _view; set { if (Views.Contains(value) && Set(ref _view, value)) Refresh(); } }
    public string Query { get => _query; set { if (Set(ref _query, (value ?? "")[..Math.Min(value?.Length ?? 0, 128)])) Refresh(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public OperatorLibraryRow? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Notify(nameof(SelectedHelp)); RaiseCommands(); } }
    }
    public string SelectedHelp => Selected?.Help is { Length: > 0 } body ? body :
        Selected?.Detail ?? (View == "Recent" && !RememberRecent ? "History is off. Enable it explicitly to remember visits." : "No matching entries.");
    public bool IsLoaded => _loaded;
    private bool CanEdit() => _loaded && !_loading && !_disposed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded || _loading || _disposed) return;
        _loading = true; RaiseCommands();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        try
        {
            var settings = await _store.ReadAsync(linked.Token);
            if (_disposed) return;
            _settings = settings; _loaded = true;
            Status = "Local references loaded. Open inspects an item; it never activates Program.";
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Preference loading canceled; retry by reopening."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_disposed) return;
            _loaded = true;
            Status = "Preferences unavailable or invalid. Original file retained; explicit edits replace preferences only.";
        }
        finally
        {
            _loading = false;
            if (!_disposed) { Notify(nameof(IsLoaded)); Notify(nameof(HistoryLabel)); Notify(nameof(RememberRecent)); Refresh(); RaiseCommands(); }
        }
    }
    public void Visit(OperatorLibraryReference? reference)
    {
        if (!CanEdit() || !RememberRecent || reference?.IsValid != true || reference.Kind is not ("workspace" or "media" or "show")) return;
        var recent = new[] { reference }.Concat(_settings.Recent.Where(r => r != reference))
            .GroupBy(r => r.Kind).SelectMany(g => g.Take(OperatorLibraryPreferences.MaximumRecentPerKind)).ToArray();
        // Grouping retains per-kind MRU order; display groups are explicit.
        Change(_settings with { Recent = recent });
    }
    public void ToggleFavorite(OperatorLibraryReference? reference)
    {
        if (!CanEdit()) return;
        if (reference?.IsValid != true || reference.Kind is not ("media" or "scene" or "command"))
        { Status = "Select a catalogue media item, scene or command first."; return; }
        var exists = _settings.Favorites.Contains(reference);
        if (!exists && _settings.Favorites.Length >= OperatorLibraryPreferences.MaximumFavorites)
        { Status = "32 favorites maximum. Remove an entry first."; return; }
        Change(_settings with { Favorites = exists ? _settings.Favorites.Where(r => r != reference).ToArray()
            : _settings.Favorites.Append(reference).ToArray() });
    }
    private void RemoveSelected()
    {
        if (!CanEdit() || Selected?.Reference is not { } reference) return;
        Change(View == "Favorites" ? _settings with { Favorites = _settings.Favorites.Where(r => r != reference).ToArray() }
            : _settings with { Recent = _settings.Recent.Where(r => r != reference).ToArray() });
    }
    private void OpenSelected()
    {
        if (_disposed || Selected is not { Available: true, Reference: { } reference }) return;
        // Resolve again immediately; previous display evidence is never authority.
        if (!_resolve(reference).Available || !_open(reference))
        { Refresh(); Status = "Item is unavailable. Refresh the catalogue, relink in Media Library, or remove this local reference."; return; }
        Visit(reference);
        Status = "Opened for inspection. Use the existing explicit production controls to activate.";
    }
    private void Change(OperatorLibraryPreferences settings)
    {
        if (!CanEdit()) return;
        _settings = settings;
        Notify(nameof(RememberRecent)); Notify(nameof(HistoryLabel));
        Status = "Saving local references…";
        lock (_saveGate)
        {
            _pending = settings;
            if (!_saving) { _saving = true; _save = SaveLoopAsync(); }
        }
        Refresh();
    }
    private async Task SaveLoopAsync()
    {
        await Task.Yield();
        while (true)
        {
            OperatorLibraryPreferences snapshot;
            lock (_saveGate)
            {
                if (_pending is null) { _saving = false; return; }
                snapshot = _pending; _pending = null;
            }
            try
            {
                await _store.WriteAsync(snapshot);
                if (!_disposed && ReferenceEquals(_settings, snapshot)) Status = "Saved locally. No production state changed.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_disposed && ReferenceEquals(_settings, snapshot)) Status = "Session only: local preferences could not be saved.";
            }
        }
    }
    public Task FlushAsync() { lock (_saveGate) return _save; }
    public void Refresh()
    {
        if (_disposed) return;
        var previous = Selected?.Reference;
        IEnumerable<OperatorLibraryRow> rows;
        if (View == "Help")
            rows = _help().Take(128).Where(t => Matches(t.Label + " " + t.Body))
                .Take(32).Select(t => new OperatorLibraryRow(null, t.Label, "Repository help · " + t.Id, false, false, t.Body));
        else
            rows = (View == "Favorites" ? _settings.Favorites : _settings.Recent).Select(r =>
            {
                var resolved = _resolve(r);
                return new OperatorLibraryRow(r, resolved.Label, resolved.Detail, resolved.Available, _settings.Favorites.Contains(r));
            }).Where(r => Matches(r.Label + " " + r.Detail + " " + r.Reference!.Id));
        var snapshot = rows.Take(72).ToArray();
        Rows.Clear(); foreach (var row in snapshot) Rows.Add(row);
        Selected = snapshot.FirstOrDefault(r => previous is not null && r.Reference == previous) ?? snapshot.FirstOrDefault();
        Notify(nameof(SelectedHelp)); RaiseCommands();
    }
    private bool Matches(string value) => Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8)
        .All(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    private void RaiseCommands()
    {
        foreach (var command in new[] { OpenCommand, ToggleHistoryCommand, ClearHistoryCommand, RemoveCommand,
            FavoriteMediaCommand, FavoriteSceneCommand, FavoriteCommandCommand, RefreshCommand })
            (command as OperatorShellCommand)?.RaiseCanExecuteChanged();
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name!); return true;
    }
}
