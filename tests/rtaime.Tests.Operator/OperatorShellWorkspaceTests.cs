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
            Assert.Single(shell.NavigationGroups.Concat(shell.SecondaryNavigationGroups)
                .SelectMany(g => g.Items), link => link.IsActive);
            Assert.Equal(OperatorWorkspaceRegistry.IsSecondaryWorkspace(destination.Id),
                shell.IsSecondaryWorkspaceSelected);
        }
        shell.SelectWorkspace("GRAPHICS"); Assert.Equal("COMPOSITING", shell.SelectedWorkspace);
        shell.SelectWorkspace("SYSTEM"); Assert.Equal("SETTINGS", shell.SelectedWorkspace);
        shell.SelectWorkspace("FUTURE_EDITOR"); Assert.Equal("LIVE", shell.SelectedWorkspace);
        await shell.SaveAsync();
        Assert.Equal(470, store.Load().Workspaces["FUTURE_EDITOR"].LeftPanelWidth);
    }
    [Theory]
    [InlineData(960,500)] [InlineData(1280,720)] [InlineData(1920,1080)]
    public async Task Media_server_navigation_preserves_secondary_layouts_and_stable_id_selection(
        double width, double height)
    {
        await using var fixture = await Fixture.Create(false);
        var store = new OperatorLayoutStore(Path.Combine(fixture.Directory, "media-server-navigation.json"));
        var seeded = OperatorLayoutSettings.Default with { SelectedWorkspace = "COMPOSITING" };
        seeded.Workspaces["COMPOSITING"] = seeded.Workspaces["COMPOSITING"] with { LeftPanelWidth = 470 };
        await store.SaveAsync(seeded);
        var shell = new OperatorShellViewModel(store, _ => { });
        shell.UpdateViewportSize(width, height);
        Assert.Equal(7, OperatorLayoutSettings.CurrentVersion);
        Assert.Equal("COMPOSITING", shell.SelectedWorkspace);
        Assert.True(shell.IsSecondaryWorkspaceSelected);
        Assert.Contains("Compositing", shell.SecondaryNavigationDescription);

        var primary = shell.NavigationGroups.SelectMany(group => group.Items).ToArray();
        Assert.Equal(new[] { "LIVE", "MEDIA", "EDIT", "OUTPUTS" }, primary.Select(link => link.Id));
        Assert.Equal("Timeline", primary.Single(link => link.Id == "EDIT").Label);
        var secondary = shell.SecondaryNavigationGroups.SelectMany(group => group.Items).ToArray();
        Assert.Equal(new[] { "SCENES", "MULTIVIEW", "COMPOSITING", "HEALTH", "AI", "SETTINGS" },
            secondary.Select(link => link.Id));
        Assert.Equal(new[] { "LIVE TOOLS", "CREATION", "SYSTEM" },
            shell.SecondaryNavigationGroups.Select(group => group.Label));
        Assert.Equal(OperatorWorkspaceNames.All.Count, primary.Length + secondary.Length);
        Assert.Equal(OperatorWorkspaceNames.All.Order(), primary.Concat(secondary).Select(link => link.Id).Order());

        foreach (var destination in OperatorWorkspaceRegistry.Destinations)
        {
            shell.SelectWorkspaceCommand.Execute(destination.Id);
            Assert.Equal(destination.Id, shell.SelectedWorkspace);
            Assert.Single(primary.Concat(secondary), link => link.IsActive);
        }
        shell.SelectWorkspace("EDIT");
        Assert.Equal("TIMELINE WORKSPACE", shell.WorkspaceTitle);
        Assert.False(shell.IsSecondaryWorkspaceSelected);
        shell.SelectWorkspace("COMPOSITING");
        await shell.SaveAsync();
        var loaded = new OperatorShellViewModel(store, _ => { });
        Assert.Equal("COMPOSITING", loaded.SelectedWorkspace);
        Assert.Equal(470, loaded.LeftPanelWidth);
        Assert.Equal(OperatorWorkspaceNames.All.Count, loaded.Workspaces.Count);
    }

    [Theory]
    [InlineData(1920,1080,105,185,190,120,70,140,46)]
    [InlineData(1600,900,105,185,190,120,70,140,46)]
    [InlineData(1280,720,105,185,190,120,70,140,46)]
    [InlineData(960,540,58,155,150,112,72,130,40)]
    [InlineData(960,500,58,155,150,112,72,130,40)]
    public async Task Compact_shell_retains_readiness_program_and_timecode_space_without_persisting_geometry(
        double width, double height, double brand, double project, double engine, double timecode,
        double transmission, double program, double settings)
    {
        await using var fixture = await Fixture.Create(false);
        var shell = fixture.Shell;
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.UpdateViewportSize(width, height);
        Assert.Equal(brand, shell.TopBarBrandWidth.Value);
        Assert.Equal(project, shell.TopBarProjectWidth.Value);
        Assert.Equal(engine, shell.TopBarEngineWidth.Value);
        Assert.Equal(timecode, shell.TopBarTimecodeWidth.Value);
        Assert.Equal(transmission, shell.TopBarTransmissionWidth.Value);
        Assert.Equal(program, shell.TopBarProgramWidth.Value);
        Assert.Equal(settings, shell.TopBarSettingsWidth.Value);
        // Two 1-DIP separators. All other widths must leave positive readiness capacity.
        var fixedWidth = brand + project + engine + timecode + transmission + program + settings + 2;
        Assert.True(width - fixedWidth >= 180, $"Readiness capacity: {width - fixedWidth}");
        Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        shell.UpdateViewportSize(1920, 1080);
        Assert.Equal(105, shell.TopBarBrandWidth.Value);
        Assert.Equal(before, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
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
    [Theory]
    [InlineData("SCENES")] [InlineData("OUTPUTS")] [InlineData("SETTINGS")]
    public async Task Compact_details_remain_reachable_without_persisting_temporary_columns(string id)
    {
        await using var f = await Fixture.Create(false); var shell = f.Shell;
        shell.SelectWorkspace(id); shell.UpdateViewportSize(960,540);
        var before = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        Assert.Equal(Visibility.Visible, shell.AuxiliaryNavigationVisibility);
        shell.SelectCompactAuxiliaryCommand.Execute("DETAILS");
        Assert.Equal(0,shell.PrimaryWorkspaceWidth.Value); Assert.True(shell.AuxiliaryWorkspaceColumnWidth.IsStar);
        Assert.Equal(before,JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
        shell.SelectCompactAuxiliaryCommand.Execute("PRIMARY");
        Assert.True(shell.PrimaryWorkspaceWidth.IsStar); Assert.Equal(0,shell.AuxiliaryWorkspaceColumnWidth.Value);
    }
    [Theory]
    [InlineData(1920,1080)]
    [InlineData(960,540)]
    public async Task AI_workspace_owns_a_full_center_view_and_preserves_independent_layouts(double width, double height)
    {
        await using var fixture = await Fixture.Create(false);
        var shell = fixture.Shell;
        shell.SelectWorkspace("AI");
        shell.UpdateViewportSize(width, height);
        Assert.Equal(Visibility.Visible, shell.AIWorkspaceVisibility);
        Assert.Equal(Visibility.Collapsed, shell.NonLiveCenterVisibility);
        Assert.Equal(Visibility.Collapsed, shell.StandardViewerVisibility);
        Assert.False(shell.HasLeftRegion);
        Assert.False(shell.HasTimelineRegion);
        Assert.Equal(0, shell.LeftColumnWidth.Value);
        var layout = JsonSerializer.Serialize(shell.CurrentWorkspaceLayout());
        shell.SelectWorkspace("COMPOSITING");
        Assert.Equal(Visibility.Collapsed, shell.AIWorkspaceVisibility);
        Assert.Equal(Visibility.Visible, shell.CompositingGraphVisibility);
        shell.SelectWorkspace("AI");
        Assert.Equal(layout, JsonSerializer.Serialize(shell.CurrentWorkspaceLayout()));
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
