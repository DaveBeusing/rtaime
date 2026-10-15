// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class MainWindow
{
    public OperatorLibraryViewModel Library { get; private set; } = null!;
    public ICommand OpenLibraryCommand { get; private set; } = null!;
    public ICommand OpenInlineHelpCommand { get; private set; } = null!;
    private OperatorLibraryWindow? _libraryWindow;
    internal OperatorLibraryWindow? LibraryWindow => _libraryWindow;
    private OperatorLibraryReference? _lastObservedShow;
    private string? LibraryScope => (DataContext as OperatorViewModel)?.Client?.Snapshot?.Production.ProductionId.ToString();
    private OperatorLibraryReference? CurrentMediaReference() =>
        MediaPool.SelectedItem is { Kind: MediaPoolItemKind.Source or MediaPoolItemKind.Clip or MediaPoolItemKind.Audio } item &&
        LibraryScope is { } scope ? new("media", scope, item.Key) : null;
    private void InitializeLibrary(OperatorViewModel owner)
    {
        Library = new(ResolveLibraryReference, OpenLibraryReference, BuildInlineHelp, CurrentMediaReference,
            () => owner.SelectedScene is { } scene && LibraryScope is { } scope ? new("scene", scope, scene.Id) : null,
            () => CommandPalette.Selected is { } entry ? new("command", "", entry.Id) : null);
        Library.HistoryChanged += OnLibraryHistoryChanged;
        MediaPool.LibraryItemVisited += OnLibraryItemVisited;
        owner.PropertyChanged += OnLibraryOwnerChanged;
    }
    private void OnLibraryHistoryChanged(object? sender, EventArgs e)
    {
        _lastObservedShow = null;
        if (Library.RememberRecent) { Library.Visit(new("workspace", "", Shell.SelectedWorkspace)); ObserveCurrentShow(); }
    }
    private void OnLibraryItemVisited(MediaPoolItemViewModel item)
    {
        if (LibraryScope is { } scope && item.Kind is MediaPoolItemKind.Source or MediaPoolItemKind.Clip or MediaPoolItemKind.Audio)
            Library.Visit(new("media", scope, item.Key));
    }
    private void OnLibraryOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperatorViewModel.ShowProjectName) or nameof(OperatorViewModel.ShowProjectState))
            ObserveCurrentShow();
    }
    private void OnLibraryClick(object sender, RoutedEventArgs e) => OpenLibrary();
    private void ObserveCurrentShow()
    {
        if (!Library.IsLoaded || !Library.RememberRecent || DataContext is not OperatorViewModel owner || LibraryScope is not { } scope ||
            owner.Client?.Snapshot?.ShowProject is not { State: not "UNAVAILABLE" } show) return;
        var reference = new OperatorLibraryReference("show", scope, show.ProjectId);
        if (_lastObservedShow == reference) return;
        _lastObservedShow = reference;
        Library.Visit(reference);
    }
    private async Task LoadLibraryAsync()
    {
        await Library.LoadAsync();
        ObserveCurrentShow();
    }
    private OperatorLibraryResolution ResolveLibraryReference(OperatorLibraryReference reference)
    {
        OperatorLibraryResolution Missing(string text) => new(reference.Kind + " · " + reference.Id, text, false);
        if (reference.Kind == "workspace")
            return OperatorWorkspaceNames.All.Contains(reference.Id) ? new(reference.Id, "Open workspace · local navigation.", true)
                : Missing("Workspace unavailable. Remove this local reference.");
        if (reference.Kind == "command")
        {
            var entry = CommandPalette.Entries.FirstOrDefault(c => c.Id == reference.Id);
            return entry is null ? Missing("Command removed. Remove this local favorite.") :
                new(entry.Label, "Open command details · " + entry.Gesture + " · " + entry.State, true);
        }
        if (reference.Scope != LibraryScope) return Missing("Another production or disconnected. Synchronize the intended production or remove this reference.");
        if (DataContext is not OperatorViewModel owner) return Missing("Operator context unavailable.");
        if (reference.Kind == "media")
        {
            var item = MediaPool.FindLibraryItem(reference.Id);
            return item is null ? Missing("Catalogue item unavailable or removed. Refresh Media Library or remove this reference.") :
                new(item.Name, item.ReadinessLabel + ". Open inspects only. Missing files: use Relink in Media Library.", true);
        }
        if (reference.Kind == "scene")
        {
            var scene = owner.Scenes.FirstOrDefault(s => s.Id == reference.Id);
            return scene is null ? Missing("Scene removed or unavailable. Remove this favorite.") :
                new(scene.Name, "Select scene for inspection. Explicit activation remains required.", true);
        }
        var show = owner.Client?.Snapshot?.ShowProject;
        return show is not null && show.ProjectId == reference.Id && show.State != "UNAVAILABLE"
            ? new(show.Name, show.State + ". Open show workspace. This production has one canonical show; no file replacement.", true)
            : Missing("Show unavailable in this production. Reconnect through the supported lifecycle or remove this reference.");
    }
    private bool OpenLibraryReference(OperatorLibraryReference reference)
    {
        if (!ResolveLibraryReference(reference).Available) return false;
        if (reference.Kind == "workspace") Shell.SelectWorkspace(reference.Id);
        else if (reference.Kind == "command")
        {
            OpenCommandPalette();
            CommandPalette.Query = reference.Id;
            CommandPalette.Selected = CommandPalette.Entries.First(c => c.Id == reference.Id);
        }
        else if (reference.Kind == "media") { if (!MediaPool.BrowseLibraryItem(reference.Id)) return false; Shell.SelectWorkspace("MEDIA"); }
        else if (reference.Kind == "scene")
        {
            ((OperatorViewModel)DataContext).SelectedScene = ((OperatorViewModel)DataContext).Scenes.First(s => s.Id == reference.Id);
            Shell.SelectWorkspace("SCENES");
        }
        else Shell.SelectWorkspace("SCENES");
        return true;
    }
    private IReadOnlyList<OperatorHelpTopic> BuildInlineHelp()
    {
        var owner = DataContext as OperatorViewModel;
        var topics = new List<OperatorHelpTopic>
        {
            new("current-context", "Current context · " + Shell.SelectedWorkspace,
                "Connection: " + owner?.ConnectionState + "\nReadiness: " + owner?.GlobalReadinessSummary +
                "\nShow: " + owner?.ShowProjectState + "\n" + owner?.ShowProjectDetail +
                "\n" + owner?.LastError + "\nSearch the lifecycle, Media Library and show preflight help below. Help never retries production commands."),
            new("active-shortcuts", "Active keyboard shortcuts · focus and gesture reference", Shortcuts.ReferenceText)
        };
        // Prefer the current workspace's canonical document sections without duplicating help text.
        var preferred = Shell.SelectedWorkspace switch
        { "MEDIA" => "MediaLibrary", "HEALTH" or "OUTPUTS" => "WindowsProduction", "SCENES" => "DurableShow", _ => "OperatorCommands" };
        topics.AddRange(OperatorInlineHelp.RepositoryTopics.OrderByDescending(t => t.Id.StartsWith(preferred, StringComparison.Ordinal)));
        return topics;
    }
    internal void OpenLibrary(string view = "Recent")
    {
        if (_shutdownStarted) return;
        Library.View = view; Library.Query = ""; Library.Refresh();
        if (_libraryWindow is { } existing) { existing.Activate(); existing.FocusSearch(); return; }
        var focusedWindow = Keyboard.FocusedElement is DependencyObject focused ? Window.GetWindow(focused) : null;
        var window = new OperatorLibraryWindow(Library) { Owner = focusedWindow is OperatorPanelWindow ? focusedWindow : this };
        _libraryWindow = window; window.Closed += (_, _) => _libraryWindow = null;
        window.Show(); _ = LoadLibraryAsync();
    }
    private void CloseLibrary()
    {
        _libraryWindow?.Close();
        Library.HistoryChanged -= OnLibraryHistoryChanged;
        MediaPool.LibraryItemVisited -= OnLibraryItemVisited;
        if (DataContext is OperatorViewModel owner) owner.PropertyChanged -= OnLibraryOwnerChanged;
        Library.Dispose();
    }
}
