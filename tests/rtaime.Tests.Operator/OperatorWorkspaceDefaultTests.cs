// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Windows;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorWorkspaceDefaultTests
{
    [Theory]
    [InlineData("LIVE", 400, 340, 280, false, true, "DUAL")]
    [InlineData("MULTIVIEW", 340, 340, 320, false, false, "DUAL")]
    [InlineData("MEDIA", 400, 300, 280, false, true, "PREVIEW")]
    [InlineData("EDIT", 400, 340, 400, false, false, "DUAL")]
    [InlineData("SCENES", 400, 300, 300, false, true, "DUAL")]
    [InlineData("COMPOSITING", 400, 340, 320, false, true, "DUAL")]
    [InlineData("OUTPUTS", 400, 300, 320, true, true, "PROGRAM")]
    [InlineData("HEALTH", 400, 340, 320, true, true, "PROGRAM")]
    [InlineData("AI", 400, 340, 320, true, true, "PROGRAM")]
    [InlineData("SETTINGS", 400, 340, 320, true, true, "PROGRAM")]
    public void New_workspace_defaults_use_existing_ids_and_role_specific_presentation(
        string id, double left, double right, double lower, bool leftHidden, bool rightHidden,
        string viewerMode)
    {
        var layouts = OperatorLayoutSettings.CreateCanonicalLayouts();
        Assert.Equal(OperatorLayoutSettings.CurrentVersion, 7);
        Assert.Equal(OperatorWorkspaceRegistry.Destinations.Count, layouts.Count);
        Assert.Equal(OperatorWorkspaceRegistry.Destinations.Select(d => d.Id).Order(),
            layouts.Keys.Order());

        var layout = layouts[id];
        Assert.Equal(left, layout.LeftPanelWidth);
        Assert.Equal(right, layout.RightPanelWidth);
        Assert.Equal(lower, layout.LowerPanelHeight);
        Assert.Equal(leftHidden, layout.IsLeftCollapsed);
        Assert.Equal(rightHidden, layout.IsRightCollapsed);
        Assert.Equal(viewerMode, layout.ViewerMode);
        Assert.False(layout.IsCenterMaximized);
        Assert.False(layout.Docking.Enabled);
        Assert.Empty(layout.Docking.Floating);
        Assert.Equal(OperatorDockLayout.CurrentVersion, layout.Docking.Version);
    }

    [Fact]
    public async Task Missing_workspace_layout_uses_new_default_without_rewriting_any_saved_preference()
    {
        var root = CreateDirectory();
        var path = Path.Combine(root, "layout.json");
        try
        {
            var media = OperatorLayoutSettings.CreateCanonicalLayouts()["MEDIA"] with
            {
                LeftPanelWidth = 470,
                RightPanelWidth = 390,
                LowerPanelHeight = 370,
                IsRightCollapsed = false,
                Docking = OperatorDockLayout.Default.Float("MONITORS",
                    OperatorPanelPlacement.Default with { Monitor = "display-2", Left = 130 })
            };
            var stored = new OperatorLayoutSettings
            {
                SelectedWorkspace = "MEDIA",
                Workspaces = new Dictionary<string, OperatorWorkspaceLayoutSettings>(StringComparer.Ordinal)
                {
                    ["MEDIA"] = media
                },
                Templates = [new("Saved custom layout", "MEDIA", media)],
                WindowPlacement = OperatorWindowPlacementSettings.Default with { Width = 1380, Height = 810 }
            };
            var original = JsonSerializer.Serialize(stored);
            await File.WriteAllTextAsync(path, original);
            var file = new OperatorLayoutStore(path);
            var loaded = file.Load();
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal(JsonSerializer.Serialize(media), JsonSerializer.Serialize(loaded.Workspaces["MEDIA"]));
            Assert.Equal(JsonSerializer.Serialize(OperatorLayoutSettings.CreateCanonicalLayouts()["LIVE"]),
                JsonSerializer.Serialize(loaded.Workspaces["LIVE"]));
            Assert.True(loaded.Workspaces["LIVE"].IsRightCollapsed);
            Assert.Equal("Saved custom layout", Assert.Single(loaded.Templates).Name);

            var shell = new OperatorShellViewModel(file, _ => { });
            try
            {
                Assert.Equal("MEDIA", shell.SelectedWorkspace);
                Assert.Equal(470, shell.LeftPanelWidth);
                Assert.False(shell.IsRightCollapsed);
                shell.RestoreWorkspace("LIVE"); // Session restore is not an explicit preference save.
                Assert.Equal(original, await File.ReadAllTextAsync(path));
            }
            finally { await shell.StopTemplateFilesAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Compact_panels_and_preview_choice_remain_transient_and_reset_recovers_default()
    {
        await using var fixture = new Fixture();
        var shell = fixture.Shell;
        Assert.Equal("LIVE", shell.SelectedWorkspace);
        shell.UpdateViewportSize(1600, 900);
        Assert.True(shell.LeftColumnWidth.Value > 0);
        Assert.Equal(0, shell.RightColumnWidth.Value);
        Assert.Equal("DUAL", shell.EffectiveViewerMode);
        Assert.Equal(280, shell.LowerPanelHeight);

        var persisted = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.UpdateViewportSize(960, 540);
        Assert.True(shell.IsCompactViewport);
        Assert.Equal("DUAL", shell.ViewerMode);
        Assert.Equal("PROGRAM", shell.EffectiveViewerMode);
        Assert.Equal(0, shell.LeftColumnWidth.Value);
        Assert.Equal(0, shell.RightColumnWidth.Value);
        Assert.Equal(200, shell.LowerRowHeight.Value);
        shell.SelectCompactBusCommand.Execute("PREVIEW");
        Assert.Equal("PREVIEW", shell.EffectiveViewerMode);
        shell.ToggleLeftPanelCommand.Execute(null);
        Assert.InRange(shell.LeftColumnWidth.Value, 280, 320);
        shell.ToggleRightPanelCommand.Execute(null);
        Assert.Equal(0, shell.LeftColumnWidth.Value);
        Assert.InRange(shell.RightColumnWidth.Value, 280, 320);
        Assert.Equal(persisted, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        await shell.SaveAsync();

        var saved = new OperatorLayoutStore(fixture.Path).Load();
        Assert.Equal(persisted, JsonSerializer.Serialize(saved.Workspaces["LIVE"]));
        shell.ResetLayoutCommand.Execute(null);
        Assert.Equal("PROGRAM", shell.EffectiveViewerMode);
        Assert.Equal(0, shell.LeftColumnWidth.Value);
        Assert.Equal(0, shell.RightColumnWidth.Value);
        shell.UpdateViewportSize(1600, 900);
        Assert.Equal("DUAL", shell.EffectiveViewerMode);
        Assert.True(shell.LeftColumnWidth.Value > 0);
        Assert.Equal(0, shell.RightColumnWidth.Value);
        await shell.SaveAsync();
        Assert.Equal(280, new OperatorLayoutStore(fixture.Path).Load().Workspaces["LIVE"].LowerPanelHeight);
    }

    [Fact]
    public async Task Output_stage_and_no_timeline_destinations_reclaim_unused_lower_region()
    {
        await using var fixture = new Fixture();
        var shell = fixture.Shell;
        foreach (var workspace in new[] { "OUTPUTS", "HEALTH", "AI", "SETTINGS", "MULTIVIEW" })
        {
            shell.SelectWorkspace(workspace);
            Assert.False(shell.HasTimelineRegion);
            Assert.Equal(0, shell.LowerRowHeight.Value);
            Assert.Equal(0, shell.LowerSplitterHeight);
        }
        shell.SelectWorkspace("OUTPUTS");
        Assert.Equal(0, shell.RightColumnWidth.Value);
        shell.ToggleRightPanelCommand.Execute(null);
        Assert.False(shell.IsRightCollapsed);
        Assert.True(shell.RightColumnWidth.Value > 0);
        shell.ToggleRightPanelCommand.Execute(null);
        Assert.True(shell.IsRightCollapsed);
        shell.SelectWorkspace("MEDIA");
        Assert.True(shell.HasTimelineRegion);
        Assert.True(shell.LowerRowHeight.Value > 0);
        Assert.Equal("PREVIEW", shell.ViewerMode);
        shell.SelectWorkspace("LIVE");
        Assert.Equal("DUAL", shell.ViewerMode);
    }

    [Fact]
    public async Task Reset_only_replaces_selected_layout_and_does_not_remove_floating_or_named_profiles_elsewhere()
    {
        await using var fixture = new Fixture();
        var shell = fixture.Shell;
        shell.SelectWorkspace("MEDIA");
        shell.SetDocking(shell.Docking.Float("INSPECTOR",
            OperatorPanelPlacement.Default with { Monitor = "removed-display", Left = 9000 }));
        shell.ToggleRightPanelCommand.Execute(null);
        var changedMedia = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.TemplateName = "Media inspection";
        shell.SaveTemplateCommand.Execute(null);
        await Until(() => shell.TemplateStatus.StartsWith("Saved", StringComparison.Ordinal));
        Assert.Single(shell.WorkspaceTemplates);

        shell.SelectWorkspace("LIVE");
        shell.SetDocking(shell.Docking.Float("TOOLS",
            OperatorPanelPlacement.Default with { Monitor = "on-air", Left = 160 }));
        shell.ResetLayoutCommand.Execute(null);
        await shell.SaveAsync();
        Assert.False(shell.Docking.Enabled);
        Assert.Empty(shell.Docking.Floating);
        Assert.True(shell.IsRightCollapsed);
        Assert.Equal("LIVE", shell.SelectedWorkspace);

        shell.SelectWorkspace("MEDIA");
        Assert.Equal(changedMedia, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        Assert.Single(shell.Docking.Floating);
        Assert.Single(shell.WorkspaceTemplates);
        Assert.Equal("Media inspection", shell.WorkspaceTemplates[0].Name);
        await shell.SaveAsync();

        var restarted = new OperatorShellViewModel(new OperatorLayoutStore(fixture.Path), _ => { });
        try
        {
            Assert.Equal("MEDIA", restarted.SelectedWorkspace);
            Assert.Equal(changedMedia, JsonSerializer.Serialize(restarted.CurrentWorkspaceLayout()));
            Assert.Single(restarted.WorkspaceTemplates);
            restarted.SelectedTemplate = restarted.WorkspaceTemplates[0];
            restarted.ApplyTemplateCommand.Execute(null);
            await restarted.SaveAsync();
            Assert.Equal(changedMedia, JsonSerializer.Serialize(restarted.CurrentWorkspaceLayout()));
        }
        finally { await restarted.StopTemplateFilesAsync(); }
    }

    [Fact]
    public async Task Focus_cinema_template_and_removed_monitor_recovery_do_not_mutate_saved_layout()
    {
        await using var fixture = new Fixture();
        var shell = fixture.Shell;
        shell.SelectWorkspace("MEDIA");
        shell.SetDocking(shell.Docking.Float("MONITORS",
            OperatorPanelPlacement.Default with
            {
                Monitor = "removed-display", Dpi = 120,
                Left = 9000, Top = -1200, Width = 800, Height = 600
            }));
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.BeginPresentation("FOCUS");
        shell.SetDocking(shell.Docking.Dock("MONITORS", "CENTER"));
        await shell.SaveAsync();
        Assert.Equal(before, JsonSerializer.Serialize(new OperatorLayoutStore(fixture.Path).Load().Workspaces["MEDIA"]));
        shell.RestorePresentation();
        Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));

        shell.BeginPresentation("CINEMA");
        Assert.Equal("MEDIA", shell.SelectedWorkspace);
        shell.MaximizeProgramCommand.Execute(null);
        Assert.False(shell.PresentationActive);
        Assert.Equal("PROGRAM", shell.ViewerMode);
        shell.RestoreViewersCommand.Execute(null);
        Assert.Equal("DUAL", shell.ViewerMode);
        shell.ResetLayoutCommand.Execute(null);
        Assert.Equal("PREVIEW", shell.ViewerMode);
        Assert.Empty(shell.Docking.Floating);

        var removed = OperatorPanelPlacement.Default with
        {
            Monitor = "removed", Dpi = 96, Left = 9999, Top = -9999
        };
        var bounds = OperatorPanelRestore.Restore(removed,
        [
            new OperatorMonitorArea("primary", 0, 0, 1280, 720, 144, true)
        ]);
        Assert.InRange(bounds.Left, 0, 1280 - bounds.Width);
        Assert.InRange(bounds.Top, 0, 720 - bounds.Height);
        Assert.InRange(bounds.Width, 320, 1280);
        Assert.InRange(bounds.Height, 220, 720);
    }

    [Fact]
    public async Task Unsupported_or_corrupt_layouts_recover_without_a_hidden_overwrite()
    {
        var root = CreateDirectory();
        try
        {
            foreach (var source in new[]
            {
                "{broken",
                "{\"Version\":999,\"Workspaces\":{},\"Unknown\":\"future\"}",
                "{\"Version\":7,\"Workspaces\":{\"MEDIA\":{\"Docking\":{\"Version\":999}}}}"
            })
            {
                var path = Path.Combine(root, "current.json");
                await File.WriteAllTextAsync(path, source);
                var store = new OperatorLayoutStore(path);
                var loaded = store.Load();
                Assert.Equal("LIVE", loaded.SelectedWorkspace);
                Assert.True(loaded.Workspaces["LIVE"].IsRightCollapsed);
                Assert.Equal(source, await File.ReadAllTextAsync(path));
                if (!source.StartsWith("{broken", StringComparison.Ordinal))
                {
                    await store.SaveAsync(loaded);
                    Assert.NotNull(store.LastSaveError);
                    Assert.Equal(source, await File.ReadAllTextAsync(path));
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(predicate());
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "rtaime-workspace-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = CreateDirectory();
        public string Path => System.IO.Path.Combine(DirectoryPath, "layout.json");
        public OperatorShellViewModel Shell { get; }
        public Fixture() => Shell = new(new OperatorLayoutStore(Path), _ => { });
        public async ValueTask DisposeAsync()
        {
            await Shell.StopTemplateFilesAsync();
            await Shell.SaveAsync();
            System.IO.Directory.Delete(DirectoryPath, true);
        }
    }
}
