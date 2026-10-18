// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using rtaime.Operator;
using Xunit;
namespace rtaime.Tests.Operator;

public sealed class OperatorShellPersistenceTests
{
    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task Supported_layouts_load_without_writing_and_preserve_geometry_templates_and_floating(int version)
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "layout.json");
        try
        {
            var layout = OperatorLayoutSettings.CreateCanonicalLayouts()["MEDIA"] with {
                LeftPanelWidth = 430, RightPanelWidth = 390, LowerPanelHeight = 280, IsRightCollapsed = true,
                Docking = OperatorDockLayout.Default.Float("INSPECTOR", OperatorPanelPlacement.Default with { Left = -800, Top = 30 }) };
            var source = new OperatorLayoutSettings { Version = version, SelectedWorkspace = "MEDIA",
                WindowPlacement = OperatorWindowPlacementSettings.Default with { Left = -1200, Top = 20 },
                Templates = [new("Preparation", "MEDIA", layout)] };
            source.Workspaces["MEDIA"] = layout;
            var original = JsonSerializer.Serialize(source);
            await File.WriteAllTextAsync(path, original);
            var store = new OperatorLayoutStore(path);
            var loaded = store.Load();
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal(7, loaded.Version);
            Assert.Equal(430, loaded.Workspaces["MEDIA"].LeftPanelWidth);
            Assert.True(loaded.Workspaces["MEDIA"].IsRightCollapsed);
            Assert.Equal(layout.Docking.Floating, loaded.Workspaces["MEDIA"].Docking.Floating);
            Assert.Single(loaded.Templates);
            Assert.Equal(-1200, loaded.WindowPlacement.Left);
            await store.SaveAsync(loaded);
            Assert.Null(store.LastSaveError);
            Assert.Equal(7, JsonSerializer.Deserialize<OperatorLayoutSettings>(await File.ReadAllTextAsync(path))!.Version);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Future_layout_is_preserved_even_after_a_save_or_external_replacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "layout.json");
        try
        {
            var future = "{\"Version\":999,\"Future\":\"retain exactly\"}";
            await File.WriteAllTextAsync(path, future);
            var store = new OperatorLayoutStore(path); _ = store.Load();
            await store.SaveAsync(OperatorLayoutSettings.Default);
            Assert.NotNull(store.LastSaveError); Assert.Equal(future, await File.ReadAllTextAsync(path));
            var second = new OperatorLayoutStore(path);
            await second.SaveAsync(OperatorLayoutSettings.Default);
            Assert.NotNull(second.LastSaveError); Assert.Equal(future, await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, new string('x', 1024 * 1024 + 1));
            var oversized = new OperatorLayoutStore(path); _ = oversized.Load();
            await oversized.SaveAsync(OperatorLayoutSettings.Default);
            Assert.NotNull(oversized.LastSaveError); Assert.Equal(1024 * 1024 + 1, new FileInfo(path).Length);
            await File.WriteAllTextAsync(path, "{\"Version\":\"broken\"}");
            Assert.Equal("LIVE", new OperatorLayoutStore(path).Load().SelectedWorkspace);
            await File.WriteAllTextAsync(path, "{bad");
            var recovered = new OperatorLayoutStore(path); Assert.Equal("LIVE", recovered.Load().SelectedWorkspace);
            Assert.Equal("{bad", await File.ReadAllTextAsync(path));
            await recovered.SaveAsync(OperatorLayoutSettings.Default);
            // A partial temporary sibling from an interrupted write cannot replace the committed layout.
            await File.WriteAllTextAsync(path + ".tmp", "{incomplete");
            Assert.Equal("LIVE", new OperatorLayoutStore(path).Load().SelectedWorkspace);
        }
        finally { Directory.Delete(root, true); }
    }
}
