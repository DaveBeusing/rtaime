// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorLibraryTests
{
    private static OperatorLibraryViewModel Model(OperatorLibraryPreferenceStore store,
        Func<OperatorLibraryReference, OperatorLibraryResolution>? resolve = null, Func<OperatorLibraryReference, bool>? open = null) =>
        new(resolve ?? (r => new(r.Id, "Current catalogue", true)), open ?? (_ => true),
            () => [new("help", "Help", "Explicit inspection only.")], () => null, () => null, () => null, store);

    [Fact]
    public async Task History_is_opt_in_bounded_MRU_and_disabling_erases_it_without_erasing_favorites()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-library-" + Guid.NewGuid().ToString("N"));
        var store = new OperatorLibraryPreferenceStore(Path.Combine(root, "prefs.json"));
        try
        {
            using var model = Model(store);
            await model.LoadAsync();
            model.Visit(new("workspace", "", "MEDIA"));
            Assert.Empty(model.Rows); Assert.False(File.Exists(Path.Combine(root, "prefs.json")));
            model.ToggleHistoryCommand.Execute(null);
            for (var i = 0; i < 100; i++) model.Visit(new("media", "production-1", "clip:" + i));
            model.Visit(new("media", "production-1", "clip:90"));
            model.Visit(new("workspace", "", "MEDIA"));
            model.Visit(new("show", "production-1", "show-1"));
            await model.FlushAsync();
            var settings = await store.ReadAsync();
            Assert.Equal(24, settings.Recent.Count(r => r.Kind == "media"));
            Assert.Equal("clip:90", settings.Recent.First(r => r.Kind == "media").Id);
            Assert.DoesNotContain(settings.Recent, r => r.Id == "clip:0");
            model.ToggleFavorite(new("media", "production-1", "clip:90"));
            model.ToggleHistoryCommand.Execute(null); await model.FlushAsync();
            settings = await store.ReadAsync();
            Assert.False(settings.RememberRecent); Assert.Empty(settings.Recent); Assert.Single(settings.Favorites);
            using var reopened = Model(store);
            await reopened.LoadAsync(); reopened.View = "Favorites";
            Assert.Single(reopened.Rows); Assert.Equal("clip:90", reopened.Rows[0].Reference!.Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Stable_favorite_ids_resolve_renames_missing_items_and_new_production_without_automatic_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-library-" + Guid.NewGuid().ToString("N"));
        var store = new OperatorLibraryPreferenceStore(Path.Combine(root, "prefs.json"));
        var reference = new OperatorLibraryReference("scene", "production-1", "scene-1");
        var label = "Old name"; var available = true; var calls = 0;
        try
        {
            using (var first = Model(store))
            { await first.LoadAsync(); first.ToggleFavorite(reference); await first.FlushAsync(); }
            using var model = Model(store, _ => new(label, available ? "Current scene" : "Different production or deleted", available), _ => { calls++; return true; });
            await model.LoadAsync(); model.View = "Favorites";
            Assert.Equal(0, calls);
            label = "Renamed scene"; model.Refresh();
            Assert.Equal(label, model.Rows.Single().Label); Assert.Equal(reference, model.Rows.Single().Reference);
            Assert.Equal(0, calls);
            available = false; // CanExecute may still have old visual evidence; boundary must resolve again.
            model.OpenCommand.Execute(null); Assert.Equal(0, calls);
            model.Refresh(); Assert.False(model.OpenCommand.CanExecute(null));
            model.RemoveCommand.Execute(null); await model.FlushAsync(); Assert.Empty((await store.ReadAsync()).Favorites);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":1,\"RememberRecent\":false,\"Recent\":null,\"Favorites\":[]}")]
    [InlineData("{\"Version\":1,\"RememberRecent\":false,\"Recent\":[],\"Favorites\":[],\"Path\":\"private\"}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"RememberRecent\":false,\"Recent\":[],\"Favorites\":[]}")]
    [InlineData("{\"Version\":99,\"RememberRecent\":false,\"Recent\":[],\"Favorites\":[]}")]
    public async Task Corrupt_preferences_do_not_block_or_overwrite_on_load(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-library-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "prefs.json");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(path, json);
            using var model = Model(new(path));
            await model.LoadAsync();
            Assert.True(model.IsLoaded); Assert.Empty(model.Rows);
            Assert.Contains("Original file retained", model.Status);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
            model.ToggleFavorite(new("command", "", "media-grid")); await model.FlushAsync();
            Assert.Single((await new OperatorLibraryPreferenceStore(path).ReadAsync()).Favorites);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Preferences_reject_paths_unknown_kinds_and_oversize_and_honor_cancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-library-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "prefs.json"); Directory.CreateDirectory(root);
        try
        {
            Assert.False(new OperatorLibraryReference("media", "prod", @"C:\private\file.mp4").IsValid);
            Assert.False(new OperatorLibraryReference("media", "prod", "https://secret.example").IsValid);
            Assert.False(new OperatorLibraryReference("other", "", "id").IsValid);
            var store = new OperatorLibraryPreferenceStore(path);
            await File.WriteAllTextAsync(path, new string(' ', OperatorLibraryPreferenceStore.MaximumBytes + 1));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync());
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(canceled.Token));
            using var model = Model(store); await model.LoadAsync(canceled.Token);
            Assert.False(model.IsLoaded); Assert.Contains("canceled", model.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Favorites_are_bounded_and_coalesce_bursts_and_save_failures_stay_local()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-library-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "prefs.json");
        try
        {
            using var model = Model(new(path)); await model.LoadAsync();
            for (var i = 0; i < 33; i++) model.ToggleFavorite(new("command", "", "command-" + i));
            await model.FlushAsync(); model.View = "Favorites";
            Assert.Equal(32, model.Rows.Count); Assert.Equal(32, (await new OperatorLibraryPreferenceStore(path).ReadAsync()).Favorites.Length);
            for (var i = 0; i < 1000; i++) model.ToggleFavorite(new("command", "", "command-0"));
            await model.FlushAsync();
            Assert.Equal(model.Rows.Count, (await new OperatorLibraryPreferenceStore(path).ReadAsync()).Favorites.Length);
            using var failed = Model(new(root)); // An existing directory cannot be replaced by a settings file.
            await failed.LoadAsync(); failed.ToggleFavorite(new("command", "", "media-grid")); await failed.FlushAsync();
            Assert.Contains("Session only", failed.Status);
            failed.View = "Favorites"; Assert.Single(failed.Rows);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Embedded_help_comes_from_canonical_docs_and_is_bounded()
    {
        var topics = OperatorInlineHelp.RepositoryTopics;
        Assert.NotEmpty(topics); Assert.True(topics.Count <= 120);
        Assert.Contains(topics, t => t.Id.StartsWith("WindowsProductionLifecycle.md", StringComparison.Ordinal));
        Assert.Contains(topics, t => t.Id.StartsWith("DurableShowProjectState.md", StringComparison.Ordinal) && t.Body.Contains("ControlHost"));
        Assert.All(topics, t => Assert.True(t.Body.Length <= 8194));
    }

    internal static async Task VerifyWindowAsync(MainWindow main, Dispatcher dispatcher)
    {
        var owner = (OperatorViewModel)main.DataContext;
        var program = owner.ProgramSourceId;
        var scene = owner.ActiveSceneId;
        var workspace = main.Shell.SelectedWorkspace;
        main.OpenLibrary("Help");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
        var window = Assert.IsType<OperatorLibraryWindow>(main.LibraryWindow);
        try
        {
            Assert.True(window.IsVisible);
            window.Width = 360; window.Height = 360;
            var search = Assert.IsType<RtaimeTextBox>(window.FindName("SearchBox"));
            Assert.True(search.IsKeyboardFocusWithin);
            var open = Assert.IsType<RtaimeButton>(window.FindName("OpenItemButton")); Assert.False(open.IsDefault);
            Assert.NotEmpty(main.Library.Rows);
            main.Library.Query = "shortcuts";
            Assert.NotEmpty(main.Library.Rows);
            Assert.Contains(main.Library.Rows, r => r.Help.Contains(main.Shortcuts.ReferenceText, StringComparison.Ordinal));
            Assert.False(main.Library.OpenCommand.CanExecute(null));
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Return)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(enter); Assert.True(enter.Handled);
            Assert.Equal(workspace, main.Shell.SelectedWorkspace);
            Assert.Equal(program, owner.ProgramSourceId); Assert.Equal(scene, owner.ActiveSceneId);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(search)));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(escape); Assert.True(escape.Handled); Assert.False(window.IsVisible);
            main.OpenLibrary("Favorites");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Assert.True(Assert.IsType<RtaimeTextBox>(main.LibraryWindow!.FindName("SearchBox")).IsKeyboardFocusWithin);
            Assert.Equal(program, owner.ProgramSourceId);
            await main.Library.LoadAsync();
            main.Library.ToggleHistoryCommand.Execute(null);
            main.Shell.SelectWorkspace("MEDIA"); main.Shell.SelectWorkspace("OUTPUTS");
            main.Library.View = "Recent"; main.Library.Query = "";
            main.Library.Selected = main.Library.Rows.Single(r => r.Reference?.Id == "MEDIA");
            main.Library.OpenCommand.Execute(null);
            Assert.Equal("MEDIA", main.Shell.SelectedWorkspace);
            Assert.Equal(program, owner.ProgramSourceId);
            main.CommandPalette.Query = "cut";
            main.CommandPalette.Selected = main.CommandPalette.Entries.Single(e => e.Id == "cut");
            main.Library.FavoriteCommandCommand.Execute(null);
            main.Library.View = "Favorites"; main.Library.Refresh();
            main.Library.Selected = main.Library.Rows.Single(r => r.Reference?.Id == "cut");
            main.Library.OpenCommand.Execute(null);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Assert.True(main.CommandPaletteWindow!.IsVisible);
            Assert.Equal("cut", main.CommandPalette.Selected!.Id);
            Assert.Equal(program, owner.ProgramSourceId);
            Assert.Equal(scene, owner.ActiveSceneId);
            main.CommandPaletteWindow.Close();
            main.Library.ToggleHistoryCommand.Execute(null);
            await main.Library.FlushAsync();
        }
        finally { main.LibraryWindow?.Close(); if (window.IsVisible) window.Close(); }
        Console.WriteLine("RECENT FAVORITES HELP PASS: compact productive window, embedded help, active shortcut search, protected Enter, close/reopen focus, no Program activation.");
    }
}
