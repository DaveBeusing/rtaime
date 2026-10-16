// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class WorkspacePresentationTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_focus_cinema_and_saves_restore_exact_layout_and_fullscreen_without_persisting_modes(bool fullscreen)
    {
        await using var f = await Fixture.Create(fullscreen);
        var shell = f.Shell; shell.SelectWorkspace("MEDIA");
        shell.SetDocking(OperatorDockLayout.Default.Dock("TOOLS", "RIGHT").Float("INSPECTOR", OperatorPanelPlacement.Default));
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.BeginPresentation("FOCUS");
        shell.SetDocking(shell.Docking.Dock("MONITORS", "CENTER"));
        shell.FullscreenProgramCommand.Execute(null);
        await shell.SaveAsync();
        var stored = new OperatorLayoutStore(f.Path).Load();
        Assert.Equal(before, JsonSerializer.Serialize(stored.Workspaces["MEDIA"])); Assert.Equal(fullscreen, stored.IsFullscreen);
        Assert.True(shell.ExitFullscreenCommand.CanExecute(null));
        shell.ExitFullscreenCommand.Execute(null);
        Assert.False(shell.PresentationActive); Assert.Equal("NORMAL", shell.PresentationMode);
        Assert.Equal(fullscreen, shell.IsFullscreen);
        Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        shell.BeginPresentation("CINEMA"); shell.FullscreenProgramCommand.Execute(null);
        shell.SelectWorkspace("LIVE"); Assert.False(shell.PresentationActive);
        await shell.SaveAsync();
        Assert.Equal(before, JsonSerializer.Serialize(new OperatorLayoutStore(f.Path).Load().Workspaces["MEDIA"]));
    }
    [Fact]
    public async Task Template_capture_apply_and_restart_remain_independent_and_do_not_save_transient_projection()
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell;
        shell.SelectWorkspace("MEDIA"); shell.SetDocking(OperatorDockLayout.Default.Dock("TOOLS", "CENTER"));
        var captured = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.BeginPresentation("CINEMA"); shell.SetDocking(shell.Docking.Float("MONITORS", OperatorPanelPlacement.Default));
        shell.FullscreenProgramCommand.Execute(null);
        shell.TemplateName = "Media preparation"; shell.SaveTemplateCommand.Execute(null);
        await Until(() => shell.TemplateStatus.StartsWith("Saved", StringComparison.Ordinal));
        Assert.Equal(captured, JsonSerializer.Serialize(Assert.Single(shell.WorkspaceTemplates).Layout));
        shell.RestorePresentation(); shell.SelectWorkspace("LIVE");
        var live = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.ApplyTemplateCommand.Execute(null);
        Assert.Equal("MEDIA", shell.SelectedWorkspace); Assert.Equal(captured, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        shell.SelectWorkspace("LIVE"); Assert.Equal(live, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        await shell.SaveAsync();
        var restored = new OperatorShellViewModel(new OperatorLayoutStore(f.Path), _ => { });
        Assert.Single(restored.WorkspaceTemplates); Assert.Equal("Media preparation", restored.WorkspaceTemplates[0].Name);
        restored.SelectedTemplate = restored.WorkspaceTemplates[0]; restored.ApplyTemplateCommand.Execute(null);
        Assert.Equal("MEDIA", restored.SelectedWorkspace);
        await restored.SaveAsync();
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Portable_roundtrip_and_legacy_migration_preserve_only_approved_presentation_fields(int version)
    {
        await using var f = await Fixture.Create(false);
        var settings = new OperatorLayoutSettings { Version = version, SelectedWorkspace = "MEDIA" };
        settings.Workspaces["MEDIA"] = settings.Workspaces["MEDIA"] with { LeftPanelWidth = 430,
            Docking = OperatorDockLayout.Default.Float("MONITORS", OperatorPanelPlacement.Default with { Monitor = "removed", Left = 9000, Top = -9000 }) };
        var path = System.IO.Path.Combine(f.Directory, "import.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));
        var template = await OperatorWorkspaceTemplateFiles.ImportAsync(path);
        Assert.Equal(version < 4 ? 400 : 430, template.Layout.LeftPanelWidth);
        await OperatorWorkspaceTemplateFiles.ExportAsync(path, template);
        var second = await OperatorWorkspaceTemplateFiles.ImportAsync(path);
        Assert.Equal(JsonSerializer.Serialize(template), JsonSerializer.Serialize(second));
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            foreach (var panel in second.Layout.Docking.Floating)
            {
                var bounds = OperatorPanelRestore.Restore(panel.Placement, [new("primary", 0, 0, 1280, 720, dpi, true)]);
                Assert.InRange(bounds.Left, 0, 1280 - bounds.Width); Assert.InRange(bounds.Top, 0, 720 - bounds.Height);
            }
        Assert.DoesNotContain("ProgramSource", await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task Invalid_future_oversized_duplicate_and_cancelled_import_export_leave_saved_layout_unchanged()
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell;
        shell.TemplateName = "One"; shell.SaveTemplateCommand.Execute(null); await Until(() => shell.TemplateStatus.StartsWith("Saved"));
        shell.TemplateName = "one"; Assert.False(shell.SaveTemplateCommand.CanExecute(null));
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        var template = shell.WorkspaceTemplates[0]; var path = System.IO.Path.Combine(f.Directory, "portable.json");
        await OperatorWorkspaceTemplateFiles.ExportAsync(path, template);
        var valid = await File.ReadAllBytesAsync(path);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OperatorWorkspaceTemplateFiles.ExportAsync(path, template, cancel.Token));
        Assert.Equal(valid, await File.ReadAllBytesAsync(path));
        foreach (var invalid in new[]
        {
            "{bad", "[]", "{\"Workspaces\":{}}", "{\"Version\":6,\"Workspaces\":null}", new string('x', OperatorWorkspaceTemplateFiles.MaximumBytes + 1),
            JsonSerializer.Serialize(new OperatorWorkspaceTemplateDocument(OperatorWorkspaceTemplateFiles.Format, 99, 6, template)),
            JsonSerializer.Serialize(new OperatorWorkspaceTemplateDocument(OperatorWorkspaceTemplateFiles.Format, 1, 999, template)),
            JsonSerializer.Serialize(new OperatorWorkspaceTemplateDocument(OperatorWorkspaceTemplateFiles.Format, 1, 6, template with { Workspace = "UNKNOWN" })),
            JsonSerializer.Serialize(new OperatorWorkspaceTemplateDocument(OperatorWorkspaceTemplateFiles.Format, 1, 6, template with { Layout = null! }))
        })
        {
            await File.WriteAllTextAsync(path, invalid); await shell.ImportTemplateAsync(path);
            Assert.StartsWith("Import failed", shell.TemplateStatus);
            Assert.Single(shell.WorkspaceTemplates); Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        }
        await File.WriteAllBytesAsync(path, valid); await shell.ImportTemplateAsync(path);
        Assert.Single(shell.WorkspaceTemplates); Assert.Contains("unique name", shell.TemplateStatus);
        Assert.Empty(Directory.GetFiles(f.Directory, "*.tmp"));
    }
    [Fact]
    public async Task Template_collection_and_corrupt_preferences_are_bounded_and_apply_is_explicit()
    {
        await using var f = await Fixture.Create(false);
        var template = new OperatorWorkspaceTemplate("Layout", "MEDIA", OperatorLayoutSettings.CreateCanonicalLayouts()["MEDIA"]);
        var settings = new OperatorLayoutSettings { Templates = Enumerable.Range(0, 40).Select(i => template with { Name = "Layout " + i }).ToArray() };
        var normalized = settings.Normalize(); Assert.Equal(16, normalized.Templates.Length);
        await new OperatorLayoutStore(f.Path).SaveAsync(settings);
        var shell = new OperatorShellViewModel(new OperatorLayoutStore(f.Path), _ => { });
        Assert.Equal(16, shell.WorkspaceTemplates.Count);
        shell.TemplateName = "Overflow"; Assert.False(shell.SaveTemplateCommand.CanExecute(null));
        shell.SelectedTemplate = shell.WorkspaceTemplates[0];
        Assert.Equal("LIVE", shell.SelectedWorkspace); shell.ApplyTemplateCommand.Execute(null); Assert.Equal("MEDIA", shell.SelectedWorkspace);
        shell.DeleteTemplateCommand.Execute(null); await Until(() => shell.TemplateStatus.StartsWith("Removed"));
        Assert.Equal(15, shell.WorkspaceTemplates.Count); await shell.SaveAsync();
        var corrupt = settings with { Templates = [null!, template with { Name = "bad\nname" }, template with { Layout = null! }, template, template] };
        Assert.Single(corrupt.Normalize().Templates);
    }
    [Fact]
    public async Task Preference_write_failure_is_visible_and_does_not_replace_the_current_workspace()
    {
        await using var f = await Fixture.Create(false);
        var blocked = System.IO.Path.Combine(f.Directory, "blocked"); Directory.CreateDirectory(blocked);
        var shell = new OperatorShellViewModel(new OperatorLayoutStore(blocked), _ => { });
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.TemplateName = "Failure"; shell.SaveTemplateCommand.Execute(null);
        await Until(() => shell.TemplateStatus.Contains("not saved"));
        Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        Assert.True(Directory.Exists(blocked)); await shell.SaveAsync();
    }
    [Fact]
    public async Task Cinema_from_settings_uses_media_viewer_without_changing_saved_workspace_or_template_destination()
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell;
        shell.SelectWorkspace("SETTINGS");
        var original = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.BeginPresentation("CINEMA"); shell.FullscreenProgramCommand.Execute(null);
        Assert.Equal("MEDIA", shell.SelectedWorkspace); Assert.Equal("PROGRAM", shell.ViewerMode);
        shell.TemplateName = "Settings layout"; shell.SaveTemplateCommand.Execute(null);
        await Until(() => shell.TemplateStatus.StartsWith("Saved"));
        Assert.Equal("SETTINGS", Assert.Single(shell.WorkspaceTemplates).Workspace);
        await shell.SaveAsync();
        Assert.Equal("SETTINGS", new OperatorLayoutStore(f.Path).Load().SelectedWorkspace);
        shell.RestorePresentation();
        Assert.Equal("SETTINGS", shell.SelectedWorkspace); Assert.Equal(original, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        shell.BeginPresentation("CINEMA");
        shell.SelectWorkspace("MEDIA"); Assert.False(shell.PresentationActive); Assert.Equal("MEDIA", shell.SelectedWorkspace);
        await shell.SaveAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_fullscreen_and_viewer_keys_leave_modes_then_preserve_the_requested_action(bool fullscreen)
    {
        await using var f = await Fixture.Create(fullscreen); var shell = f.Shell;
        shell.SelectWorkspace("SETTINGS");
        shell.BeginPresentation("CINEMA"); shell.FullscreenProgramCommand.Execute(null);
        shell.ToggleFullscreenCommand.Execute(null);
        Assert.False(shell.PresentationActive); Assert.False(shell.IsFullscreen); Assert.Equal("SETTINGS", shell.SelectedWorkspace);
        shell.BeginPresentation("FOCUS"); shell.MaximizePreviewCommand.Execute(null);
        Assert.False(shell.PresentationActive); Assert.Equal("PREVIEW", shell.ViewerMode);
        await shell.SaveAsync();
        Assert.Equal("PREVIEW", new OperatorLayoutStore(f.Path).Load().Workspaces["SETTINGS"].ViewerMode);
    }
    [Fact]
    public async Task Template_file_shutdown_disables_admission_and_drains_without_late_import_or_layout_changes()
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell;
        shell.ConfigureTemplateFiles(() => "not-opened.json", () => "not-written.json", () => { });
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        Assert.True(shell.ImportTemplateCommand.CanExecute(null));
        await shell.StopTemplateFilesAsync(); await shell.StopTemplateFilesAsync();
        Assert.False(shell.ImportTemplateCommand.CanExecute(null)); Assert.False(shell.ExportTemplateCommand.CanExecute(null));
        await shell.ImportTemplateAsync("not-opened.json");
        Assert.Empty(shell.WorkspaceTemplates); Assert.Equal("Import cancelled.", shell.TemplateStatus);
        Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
    }
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtaime-layout-template-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "layout.json");
        public OperatorShellViewModel Shell { get; private set; } = null!;
        public static async Task<Fixture> Create(bool fullscreen)
        {
            var f = new Fixture(); System.IO.Directory.CreateDirectory(f.Directory);
            var store = new OperatorLayoutStore(f.Path); await store.SaveAsync(new OperatorLayoutSettings { IsFullscreen = fullscreen });
            f.Shell = new(store, full => f.Shell.SetFullscreenState(full)); return f;
        }
        public async ValueTask DisposeAsync()
        {
            await Shell.StopTemplateFilesAsync(); Shell.RestorePresentation(); await Shell.SaveAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
