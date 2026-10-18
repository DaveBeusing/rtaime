// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Windows;
using rtaime.Operator;
using Xunit;
namespace rtaime.Tests.Operator;

public sealed partial class WorkspacePresentationTemplateTests
{
    [Fact]
    public async Task Registered_destinations_preserve_legacy_ids_and_unknown_layouts_without_dead_aliases()
    {
        var old = new[] { "MEDIA", "EDIT", "LIVE", "SCENES", "COMPOSITING", "OUTPUTS", "HEALTH", "SETTINGS" };
        Assert.All(old, id => Assert.Contains(id, OperatorWorkspaceNames.All));
        Assert.Contains("AI", OperatorWorkspaceNames.All); Assert.Contains("MULTIVIEW", OperatorWorkspaceNames.All);
        Assert.Equal(OperatorWorkspaceNames.All.Count, OperatorWorkspaceNames.All.Distinct().Count());
        var settings = OperatorLayoutSettings.Default;
        settings.Workspaces["FUTURE_EDITOR"] = settings.Workspaces["MEDIA"] with { LeftPanelWidth = 470 };
        await using var f = await Fixture.Create(false);
        var path = Path.Combine(f.Directory, "registered.json");
        var store = new OperatorLayoutStore(path); await store.SaveAsync(settings);
        var shell = new OperatorShellViewModel(store, _ => { });
        foreach (var destination in OperatorWorkspaceRegistry.Destinations)
        {
            shell.SelectWorkspaceCommand.Execute(destination.Id);
            Assert.Equal(destination.Id, shell.SelectedWorkspace);
            Assert.Single(shell.NavigationGroups.SelectMany(g => g.Items), i => i.IsActive);
        }
        shell.SelectWorkspace("GRAPHICS"); Assert.Equal("COMPOSITING", shell.SelectedWorkspace);
        shell.SelectWorkspace("SYSTEM"); Assert.Equal("SETTINGS", shell.SelectedWorkspace);
        shell.SelectWorkspace("FUTURE_EDITOR"); Assert.Equal("LIVE", shell.SelectedWorkspace);
        await shell.SaveAsync();
        Assert.Equal(470, store.Load().Workspaces["FUTURE_EDITOR"].LeftPanelWidth);
    }
    [Theory]
    [InlineData(1920,1080)] [InlineData(1600,900)] [InlineData(1536,864)] [InlineData(1280,720)] [InlineData(960,540)]
    public async Task Live_media_allocation_and_compact_bus_switch_preserve_reference_intent(double width, double height)
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell; shell.SelectWorkspace("LIVE"); shell.UpdateViewportSize(width, height);
        Assert.True(shell.HasTimelineRegion);
        var usable = width - shell.NavigationRailWidth - 6;
        var media = usable - shell.LeftColumnWidth.Value - shell.RightColumnWidth.Value - shell.LeftSplitterWidth - shell.RightSplitterWidth;
        Assert.True(media >= usable / 2, $"Media allocation {media}/{usable}");
        Assert.Equal(Visibility.Collapsed, shell.ProductionDetailsVisibility);
        if (shell.IsCompactViewport)
        {
            Assert.Equal("DUAL", shell.ViewerMode);
            Assert.Equal(Visibility.Collapsed, shell.PreviewViewerVisibility);
            Assert.Equal(Visibility.Visible, shell.ProgramViewerVisibility);
            shell.SelectCompactBusCommand.Execute("PREVIEW");
            Assert.Equal(Visibility.Visible, shell.PreviewViewerVisibility);
            Assert.Equal(Visibility.Collapsed, shell.ProgramViewerVisibility);
            shell.ToggleLeftPanelCommand.Execute(null); Assert.InRange(shell.LeftColumnWidth.Value, 280, 320);
            shell.ToggleRightPanelCommand.Execute(null); Assert.Equal(0, shell.LeftColumnWidth.Value);
            Assert.InRange(shell.RightColumnWidth.Value, 260, 320);
            Assert.False(shell.IsLeftCollapsed); Assert.False(shell.IsRightCollapsed);
            shell.ResetLayoutCommand.Execute(null);
            Assert.Equal(0, shell.LeftColumnWidth.Value); Assert.Equal(0, shell.RightColumnWidth.Value);
            Assert.Equal("PROGRAM", shell.EffectiveViewerMode);
            shell.UpdateViewportSize(1920,1080);
            Assert.Equal("DUAL", shell.EffectiveViewerMode);
            Assert.Equal(400, shell.LeftPanelWidth); Assert.Equal(340, shell.RightPanelWidth);
        }
        else Assert.Equal(shell.PreviewViewerWidth, shell.ProgramViewerWidth);
    }
    [Fact]
    public async Task One_hundred_workspace_dock_float_restore_transactions_retain_bounded_panel_identities()
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell;
        for (var i=0;i<100;i++)
        {
            shell.SelectWorkspace(OperatorWorkspaceNames.All[i % OperatorWorkspaceNames.All.Count]);
            var layout = shell.Docking;
            shell.SetDocking(layout.Float("INSPECTOR", OperatorPanelPlacement.Default with { Left=-900, Top=40 }));
            Assert.Single(shell.Docking.Floating);
            shell.SetDocking(shell.Docking.Dock("INSPECTOR","RIGHT"));
            Assert.Empty(shell.Docking.Floating);
            Assert.Equal(5, shell.Docking.Groups.SelectMany(g=>g.Panels).Distinct().Count());
        }
        await shell.SaveAsync();
    }
}
