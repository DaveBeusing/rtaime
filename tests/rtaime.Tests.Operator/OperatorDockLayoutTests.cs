// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorDockLayoutTests
{
    [Fact]
    public void Tabs_float_and_return_keep_each_panel_exactly_once()
    {
        var layout = OperatorDockLayout.Default.Dock("TOOLS", "CENTER").Float("MONITORS", OperatorPanelPlacement.Default);
        Assert.Equal(new[] { "TOOLS" }, layout.Groups.Single(g => g.Id == "CENTER").Panels);
        Assert.Equal("MONITORS", layout.Floating[0].Panel);
        Assert.Single(layout.Floating);
        layout = layout.Dock("MONITORS", layout.Floating[0].ReturnGroup);
        Assert.Empty(layout.Floating);
        Assert.Equal("MONITORS", layout.Groups.Single(g => g.Id == "CENTER").ActivePanel);
        Assert.Equal(5, layout.Groups.SelectMany(g => g.Panels).Distinct().Count());
    }
    [Fact]
    public void Legacy_dock_layout_preserves_placements_and_adds_one_audio_panel()
    {
        var previous = new OperatorDockLayout { Version = 1, Enabled = true,
            Groups = [new("LEFT", ["TOOLS"], "TOOLS"), new("CENTER", ["MONITORS"], "MONITORS"),
                new("RIGHT", [], null), new("BOTTOM", ["TIMELINE"], "TIMELINE")],
            Floating = [new("INSPECTOR", "RIGHT", OperatorPanelPlacement.Default)] };
        var migrated = previous.Normalize();
        Assert.Equal(2, migrated.Version);
        Assert.Equal(previous.Floating, migrated.Floating);
        Assert.Equal(new[] { "AUDIO" }, migrated.Groups.Single(g => g.Id == "RIGHT").Panels);
        var floating = migrated.Float("AUDIO", OperatorPanelPlacement.Default);
        Assert.Equal(2, floating.Floating.Length);
        Assert.Equal(5, floating.Groups.SelectMany(g => g.Panels).Concat(floating.Floating.Select(p => p.Panel)).Distinct().Count());
    }
    [Fact]
    public void Corrupt_duplicate_and_unknown_panel_preferences_recover_bounded_defaults()
    {
        var layout = new OperatorDockLayout
        {
            Enabled = true, LeftRatio = double.NaN, RightRatio = double.PositiveInfinity,
            Groups = [new("CENTER", ["TOOLS", "TOOLS", "UNKNOWN"], "UNKNOWN")],
            Floating = [new("TOOLS", "invalid", OperatorPanelPlacement.Default), new("MONITORS", "invalid",
                OperatorPanelPlacement.Default with { Left = double.NaN, Dpi = double.PositiveInfinity })]
        }.Normalize();
        var panels = layout.Groups.SelectMany(g => g.Panels).Concat(layout.Floating.Select(f => f.Panel)).ToArray();
        Assert.Equal(5, panels.Length); Assert.Equal(5, panels.Distinct().Count());
        Assert.Equal(.22, layout.LeftRatio); Assert.Equal(.22, layout.RightRatio);
        Assert.Equal("CENTER", layout.Floating[0].ReturnGroup);
        Assert.DoesNotContain("UNKNOWN", panels);
        Assert.Contains("\"Dpi\":96", JsonSerializer.Serialize(layout));
        Assert.False((layout with { Version = 999 }).Normalize().Enabled);
    }
    [Theory]
    [InlineData(6)]
    [InlineData(5)]
    [InlineData(4)]
    public void Existing_workspace_geometry_migrates_without_enabling_docking(int version)
    {
        var settings = new OperatorLayoutSettings { Version = version, IsFullscreen = false };
        settings.Workspaces["MEDIA"] = settings.Workspaces["MEDIA"] with { LeftPanelWidth = 420, ViewerMode = "PREVIEW" };
        var json = JsonSerializer.Serialize(settings).Replace(",\"Docking\":", ",\"IgnoredDocking\":", StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<OperatorLayoutSettings>(json)!.Normalize();
        Assert.Equal(420, restored.Workspaces["MEDIA"].LeftPanelWidth);
        Assert.Equal("PREVIEW", restored.Workspaces["MEDIA"].ViewerMode);
        Assert.False(restored.Workspaces["MEDIA"].Docking.Enabled);
        Assert.False(restored.IsFullscreen);
    }
    [Fact]
    public async Task Workspace_switch_restart_and_reset_preserve_independent_dock_layouts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rtaime-dock-layout", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "layout.json");
        try
        {
            var shell = new OperatorShellViewModel(new OperatorLayoutStore(path), _ => { });
            shell.SelectWorkspace("MEDIA"); shell.SetDocking(shell.Docking.Dock("TOOLS", "CENTER"));
            shell.SelectWorkspace("LIVE");
            Assert.False(shell.Docking.Enabled);
            shell.SetDocking(shell.Docking.Float("INSPECTOR", OperatorPanelPlacement.Default));
            await shell.SaveAsync();
            var restored = new OperatorShellViewModel(new OperatorLayoutStore(path), _ => { });
            Assert.Single(restored.Docking.Floating);
            restored.SelectWorkspace("MEDIA");
            Assert.Contains("TOOLS", restored.Docking.Groups.Single(g => g.Id == "CENTER").Panels);
            restored.ResetLayoutCommand.Execute(null);
            Assert.False(restored.Docking.Enabled);
            Assert.Equal(400, restored.LeftPanelWidth);
            await restored.SaveAsync();
            restored.SelectWorkspace("LIVE");
            Assert.Single(restored.Docking.Floating);
            await restored.SaveAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Monitor_removal_and_DPI_change_keep_entire_panel_and_chrome_on_real_work_area(int dpi)
    {
        var monitors = new[] { new OperatorMonitorArea("primary", 0, 0, 1920, 1040, dpi, true),
            new OperatorMonitorArea("left", -2560, 400, 2560, 1400, 144, false) };
        var gone = new OperatorPanelPlacement("removed", 8000, -5000, 800, 600, 7680, -5120, 96);
        var bounds = OperatorPanelRestore.Restore(gone, monitors);
        Assert.InRange(bounds.Left, 0, 1920 - bounds.Width);
        Assert.InRange(bounds.Top, 0, 1040 - bounds.Height);
        Assert.InRange(bounds.Width, 320, 1920);
        var left = OperatorPanelRestore.Restore(gone with { Monitor = "left", Left = -2500, Top = 430, WorkLeft = -2560, WorkTop = 400, Dpi = 144 }, monitors);
        Assert.InRange(left.Left, -2560, -left.Width);
        Assert.InRange(left.Top, 400, 1800 - left.Height);
    }
    [Fact]
    public void Virtual_desktop_gap_invalid_coordinates_and_huge_bounds_do_not_restore_offscreen()
    {
        var monitors = new[] { new OperatorMonitorArea("primary", 0, 0, 1000, 700, 96, true),
            new OperatorMonitorArea("other", 3000, 0, 1000, 700, 192, false) };
        var p = new OperatorPanelPlacement("unknown", 1800, double.NaN, double.PositiveInfinity, 1e300, 0, 0, double.NaN);
        var bounds = OperatorPanelRestore.Restore(p, monitors);
        Assert.InRange(bounds.Left, 0, 1000 - bounds.Width);
        Assert.InRange(bounds.Top, 0, 700 - bounds.Height);
        Assert.InRange(bounds.Width, 320, 1000); Assert.InRange(bounds.Height, 220, 700);
    }
}
