// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaLibraryMenuQualificationTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Default_library_keeps_browse_add_search_and_explicit_preview_with_secondary_actions_in_menus()
    {
        var view = Load("src", "Hosts", "rtaime.Operator", "OperatorMediaLibraryView.xaml");
        Assert.Contains(view.Descendants(), node =>
            node.Name.LocalName == "RtaimeSearchBox" &&
            node.Attribute("Text")?.Value.Contains("MediaPool.SearchText", StringComparison.Ordinal) == true);
        Assert.Contains(view.Descendants(), node =>
            node.Name.LocalName == "RtaimeComboBox" &&
            node.Attribute("SelectedItem")?.Value.Contains("SelectedCategory", StringComparison.Ordinal) == true);
        foreach (var name in new[] { "MediaAssetGrid", "MediaAssetList" })
            Assert.Contains(view.Descendants(), node => node.Attribute(Xaml + "Name")?.Value == name);

        var primary = Assert.Single(view.Descendants().Where(node =>
            node.Name.LocalName == "RtaimeButton" && node.Attribute("Content")?.Value == "OPEN PREVIEW"));
        Assert.Contains("CanOpenLibraryPreview", primary.Attribute("IsEnabled")!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(view.Descendants(), node =>
            node.Name.LocalName == "RtaimeButton" && node.Attribute("Content")?.Value == "IMPORT MEDIA");
        Assert.DoesNotContain(view.Descendants(), node =>
            node.Name.LocalName == "RtaimeButton" && node.Attribute("Content")?.Value is
                "INSPECT" or "PREPARE OPTIONS" or "RELINK…");

        var menus = view.Descendants().Where(node => node.Name.LocalName == "RtaimeContextMenu").ToArray();
        Assert.Contains(menus, menu => menu.Elements().Any(node =>
            node.Attribute("Header")?.Value == "Import Media" &&
            node.Attribute("Click")?.Value == "OnMediaPoolImportMenuClick") &&
            menu.Elements().Any(node => node.Attribute("Header")?.Value == "Test Signal" &&
                node.Attribute("Click")?.Value == "OnMediaPoolTestSignalMenuClick"));
        Assert.Contains(menus, menu => menu.Elements().Any(node => node.Attribute("Header")?.Value == "Refresh availability") &&
            menu.Elements().Any(node => node.Attribute("Header")?.Value == "Smart Bins and filters…"));

        var selection = FindNamed(view, "MediaLibrarySelectionMenuButton");
        Assert.Equal("OnMediaPoolAddClick", selection.Attribute("Click")?.Value);
        Assert.Contains("Shift+F10", selection.Attribute("ToolTip")!.Value, StringComparison.Ordinal);
        var selectedMenu = Assert.Single(selection.Descendants().Where(node =>
            node.Name.LocalName == "RtaimeContextMenu"));
        Assert.Equal("OnMediaLibrarySelectionMenuOpened", selectedMenu.Attribute("Opened")?.Value);
        Assert.Contains(selectedMenu.Elements(), node => node.Attribute("Header")?.Value == "Offline QC");
        Assert.Contains(selectedMenu.Elements(), node => node.Attribute("Header")?.Value == "ADM metadata");
        Assert.Contains(selectedMenu.Descendants(), node => node.Attribute("Header")?.Value == "Batch · Remove selected…");

        Assert.Equal("Collapsed", FindNamed(view, "MediaAdvancedTools").Attribute("Visibility")?.Value);
        Assert.Equal("Collapsed", FindNamed(view, "MediaSelectedDetails").Attribute("Visibility")?.Value);
    }

    [Fact]
    public void Grid_and_list_share_one_nonshared_target_menu_with_the_same_eight_guarded_actions()
    {
        var view = Load("src", "Hosts", "rtaime.Operator", "OperatorMediaLibraryView.xaml");
        var resources = Assert.Single(view.Root!.Elements().Where(node => node.Name.LocalName == "UserControl.Resources"));
        var menu = Assert.Single(resources.Elements().Where(node => node.Attribute(Xaml + "Key")?.Value == "MediaAssetContextMenu"));
        Assert.Equal("RtaimeContextMenu", menu.Name.LocalName);
        Assert.Equal("False", menu.Attribute(Xaml + "Shared")?.Value);
        Assert.Equal("{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}",
            menu.Attribute("DataContext")?.Value);
        var expected = new[] { "Prepare selected assets…", "Open in Preview", "Add to Timeline",
            "Add Cue at Playhead", "Reveal in Explorer", "Relink Media…", "Remove from Library", "Properties" };
        Assert.Equal(expected, menu.Elements().Where(node => node.Name.LocalName == "RtaimeMenuItem")
            .Select(node => node.Attribute("Header")?.Value).ToArray());

        var gridItem = Assert.Single(view.Descendants().Where(node => node.Name.LocalName == "RtaimeMediaTile"));
        var listRow = Assert.Single(view.Descendants().Where(node => node.Name.LocalName == "Grid" &&
            node.Attribute("ContextMenu")?.Value == "{StaticResource MediaAssetContextMenu}"));
        foreach (var target in new[] { gridItem, listRow })
        {
            Assert.Equal("{StaticResource MediaAssetContextMenu}", target.Attribute("ContextMenu")?.Value);
            Assert.Equal("OnMediaLibraryContextOpening", target.Attribute("ContextMenuOpening")?.Value);
        }
        foreach (var name in new[] { "MediaAssetGrid", "MediaAssetList" })
        {
            var browser = FindNamed(view, name);
            Assert.Equal("Extended", browser.Attribute("SelectionMode")?.Value);
            Assert.Equal("OnMediaPoolSelectionChanged", browser.Attribute("SelectionChanged")?.Value);
            Assert.Equal("OnMediaPoolViewVisibilityChanged", browser.Attribute("IsVisibleChanged")?.Value);
        }

        var code = Read("src", "Hosts", "rtaime.Operator", "OperatorMediaLibraryView.xaml.cs");
        Assert.Contains("PrepareMediaLibraryContextMenu(target)", code, StringComparison.Ordinal);
        Assert.Contains("MediaPool.HasMultipleSelection", code, StringComparison.Ordinal);
        Assert.Contains("MediaPool.CanOpenLibraryPreviewItem(item)", code, StringComparison.Ordinal);
        Assert.Contains("MediaPool.CancelHover();", code, StringComparison.Ordinal);
        Assert.Contains("CurrentLibraryItem(GetMediaPoolItemFromSender(sender))", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Offline_QC_ADM_and_batch_recovery_are_discoverable_without_permanent_tool_rows()
    {
        var view = Load("src", "Hosts", "rtaime.Operator", "OperatorMediaLibraryView.xaml");
        var qc = FindNamed(view, "MediaQcDetails");
        var adm = FindNamed(view, "MediaAdmDetails");
        Assert.Equal("Collapsed", qc.Attribute("Visibility")?.Value);
        Assert.Equal("Collapsed", adm.Attribute("Visibility")?.Value);

        Assert.Contains(qc.Descendants(), node => node.Attribute("Command")?.Value?.Contains("StartQcCommand", StringComparison.Ordinal) == true);
        foreach (var action in new[] { "OnMediaQcImportClick", "OnMediaQcExportClick" })
            Assert.Contains(qc.Descendants(), node => node.Attribute("Click")?.Value == action);
        foreach (var action in new[] { "OnMediaAdmImportClick", "OnMediaAdmExportXmlClick", "OnMediaAdmExportJsonClick" })
            Assert.Contains(adm.Descendants(), node => node.Attribute("Click")?.Value == action);

        var activity = FindNamed(view, "MediaLibraryActivityStrip");
        foreach (var command in new[] { "CancelQcCommand", "CancelPreparationCommand", "CancelBatchCommand" })
            Assert.Contains(activity.Descendants(), node => node.Name.LocalName == "RtaimeButton" &&
                node.Attribute("Command")?.Value?.Contains("MediaPool." + command, StringComparison.Ordinal) == true);
        foreach (var status in new[] { "QcStatus", "PreparationStatus", "BatchStatus" })
            Assert.Contains(activity.Descendants(), node =>
                node.Attribute("Text")?.Value?.Contains("MediaPool." + status, StringComparison.Ordinal) == true);

        foreach (var command in new[] { "PrepareAssetsCommand", "ExecuteBatchCommand", "RetryBatchCommand",
            "CloseBatchCommand" })
            Assert.Contains(view.Descendants(), node =>
                node.Attribute("Command")?.Value?.Contains("MediaPool." + command, StringComparison.Ordinal) == true);
        Assert.Contains(view.Descendants(), node => node.Attribute("AutomationProperties.Name")?.Value ==
            "Per-check QC outcomes including NOT EXECUTED and UNSUPPORTED");
        Assert.Contains(view.Descendants(), node => node.Attribute("AutomationProperties.Name")?.Value ==
            "ADM physical tracks channel roles languages objects and timing");

        var code = Read("src", "Hosts", "rtaime.Operator", "OperatorMediaLibraryView.xaml.cs");
        foreach (var handler in new[] { "OnMediaLibraryStartQcClick", "OnMediaLibraryCancelQcClick",
            "OnMediaLibraryBatchRemoveClick", "OnMediaLibraryShowQcDetailsClick", "OnMediaLibraryShowAdmDetailsClick" })
            Assert.Contains("private void " + handler + "(", code, StringComparison.Ordinal);
        Assert.Contains("pool.StartQcCommand.Execute(null);", code, StringComparison.Ordinal);
        Assert.Contains("pool.CancelQcCommand.Execute(null);", code, StringComparison.Ordinal);
        Assert.Contains("pool.PrepareBatchRemoveCommand.Execute(null);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_open_and_context_invalidation_are_ui_only_and_preserve_keyboard_text_entry()
    {
        var code = Read("src", "Hosts", "rtaime.Operator", "OperatorMediaLibraryView.xaml.cs");
        Assert.Contains("menu.Placement = PlacementMode.Bottom;", code, StringComparison.Ordinal);
        Assert.Contains("menu.IsOpen = true;", code, StringComparison.Ordinal);
        Assert.Contains("if (_openSelectionMenu is not null &&", code, StringComparison.Ordinal);
        Assert.Contains("_openMenuSelectionSignature != SelectedLibrarySignature()", code, StringComparison.Ordinal);
        Assert.Contains("CloseSelectionMenu();", code, StringComparison.Ordinal);
        Assert.Contains("e.OriginalSource is TextBoxBase", code, StringComparison.Ordinal);
        Assert.Contains("Keyboard.Modifiers != ModifierKeys.Control", code, StringComparison.Ordinal);
        Assert.Contains("MediaPool.CanOpenLibraryPreview", code, StringComparison.Ordinal);

        Assert.DoesNotContain("CutProgramAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DissolveProgramAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TakeProgram", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectPreviewAsync", code, StringComparison.Ordinal);
    }

    private static XElement FindNamed(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(node => node.Attribute(Xaml + "Name")?.Value == name));

    private static XDocument Load(params string[] path) => XDocument.Load(Resolve(path));
    private static string Read(params string[] path) => File.ReadAllText(Resolve(path));

    private static string Resolve(string[] path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null)
        {
            if (File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
                return Path.Combine(new[] { root.FullName }.Concat(path).ToArray());
            root = root.Parent;
        }
        throw new InvalidOperationException("Repository root containing rtaime.slnx not found.");
    }
}
