// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class TimelineContextActionsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Sequence_editing_header_prioritizes_tools_fit_ruler_and_current_draft_evidence()
    {
        var root = Surface("SequenceTimelineControl.xaml");
        foreach (var (name, key) in new[]
        {
            ("SequenceEditActionsButton", "SequenceEditActionsMenu"),
            ("SequenceTrackActionsButton", "SequenceTrackActionsMenu"),
            ("SequenceToolsActionsButton", "SequenceToolsActionsMenu")
        })
        {
            var button = Named(root, name);
            Assert.Equal("OnSequenceActionsClick", button.Attribute("Click")?.Value);
            Assert.Equal("{Binding}", button.Attribute("Tag")?.Value);
            Assert.Equal("{StaticResource " + key + "}", button.Attribute("ContextMenu")?.Value);
            Assert.NotNull(button.Attribute("AutomationProperties.Name"));
        }
        var toolCommands = new[] { "SelectToolCommand", "MoveToolCommand", "TrimToolCommand", "FitCommand" };
        foreach (var command in toolCommands)
            Assert.Contains(root.Descendants(), e => e.Name.LocalName == "RtaimeButton" &&
                e.Attribute("Command")?.Value == "{Binding " + command + "}");
        Assert.NotNull(Named(root, "SequenceRuler"));
        Assert.NotNull(Named(root, "SequenceTrackViewport"));
        foreach (var binding in new[] { "ToolLabel", "EditCursorLabel", "GestureLabel", "Feedback", "SelectedStatus", "Snapping" })
            Assert.Contains(root.Descendants(), e => e.Attributes().Any(a =>
                a.Value.Contains("Binding " + binding, StringComparison.Ordinal)));
        var editorRow = Assert.Single(root.Descendants().Where(e =>
            e.Name.LocalName == "StackPanel" &&
            e.Descendants().Any(n => n.Attribute(Xaml + "Name")?.Value == "SequenceEditActionsButton")));
        Assert.DoesNotContain(editorRow.Descendants(), e => e.Name.LocalName == "RtaimeScrollViewer");
    }

    [Fact]
    public void Sequence_selected_clip_and_track_menus_use_only_the_existing_mutation_commands()
    {
        var root = Surface("SequenceTimelineControl.xaml");
        var edit = Menu(root, "SequenceEditActionsMenu");
        var commands = edit.Descendants().Where(e => e.Name.LocalName == "RtaimeMenuItem")
            .Select(e => e.Attribute("Command")?.Value).Where(c => c is not null).ToArray();
        foreach (var cmd in new[] { "SplitCommand", "DeleteCommand", "RippleDeleteCommand",
            "CopyCommand", "PasteCommand", "LinkCommand", "UnlinkCommand", "TransitionCommand",
            "UndoCommand", "RedoCommand", "DiscardDraftCommand" })
            Assert.Contains("{Binding " + cmd + "}", commands);

        var track = Menu(root, "SequenceTrackActionsMenu");
        Assert.Contains(track.Descendants(), e => e.Attribute("Command")?.Value == "{Binding ToggleTrackEnabledCommand}");
        Assert.Contains(track.Descendants(), e => e.Attribute("Command")?.Value == "{Binding ToggleTrackMuteCommand}");
        var tools = Menu(root, "SequenceToolsActionsMenu");
        foreach (var cmd in new[] { "WaveformCommand", "ZoomInCommand", "ZoomOutCommand",
            "ScrollBackwardCommand", "ScrollForwardCommand" })
            Assert.Contains(tools.Descendants(), e => e.Attribute("Command")?.Value == "{Binding " + cmd + "}");

        Assert.Contains(root.Descendants(), e => e.Attribute("ContextMenuOpening")?.Value == "OnClipActionsOpening");
        Assert.Contains(root.Descendants(), e => e.Attribute("ContextMenuOpening")?.Value == "OnTrackActionsOpening");
        Assert.All(root.Descendants().Where(e => e.Name.LocalName == "RtaimeMenuItem" &&
            e.Attribute("Command") is not null), e => Assert.NotNull(e.Attribute("AutomationProperties.Name")));
        var code = Code("SequenceTimelineControl.xaml.cs");
        Assert.Contains("model.SelectClip(clip.Id)", code, StringComparison.Ordinal);
        Assert.Contains("model.SelectClip(clip.ClipId)", code, StringComparison.Ordinal);
        Assert.Contains("model.SelectedIds.Contains(clip.Id)", code, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Playout.TakeCommand.Execute", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_playout_keeps_arm_take_release_and_recover_direct_and_separate()
    {
        var root = Surface("SequenceTimelineControl.xaml");
        var direct = root.Descendants().Where(e => e.Name.LocalName == "RtaimeButton")
            .Where(e => e.Attribute("DockPanel.Dock")?.Value == "Right").ToArray();
        foreach (var (caption, command) in new[]
        {
            ("TAKE", "Playout.TakeCommand"), ("ARM", "Playout.ArmCommand"),
            ("RELEASE", "Playout.CancelCommand"), ("RECOVER TAKE", "Playout.RecoverCommand")
        })
        {
            var button = Assert.Single(direct.Where(e => e.Attribute("Content")?.Value == caption));
            Assert.Equal("{Binding " + command + "}", button.Attribute("Command")?.Value);
            Assert.NotNull(button.Attribute("ToolTip"));
        }
        var playoutMenu = Menu(root, "SequencePlayoutActionsMenu");
        Assert.Contains(playoutMenu.Descendants(), e => e.Attribute("Command")?.Value == "{Binding Playout.CueCommand}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Command")?.Value == "{Binding Playout.PlayCommand}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Command")?.Value == "{Binding Playout.PauseCommand}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Command")?.Value == "{Binding Playout.StopCommand}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Text")?.Value == "{Binding Playout.Status}");
        Assert.DoesNotContain(playoutMenu.Descendants(), e =>
            e.Attribute("Command")?.Value == "{Binding Playout.TakeCommand}");
    }

    [Fact]
    public void Media_timeline_keeps_one_preview_transport_and_explicit_cursor_tools()
    {
        var root = Surface("MediaTimelineControl.xaml");
        var cursor = Named(root, "MediaTimelineCursorMenuButton");
        var tools = Named(root, "MediaTimelineToolsActionsButton");
        Assert.Equal("OnMediaTimelineActionsClick", cursor.Attribute("Click")?.Value);
        Assert.Equal("OnMediaTimelineActionsClick", tools.Attribute("Click")?.Value);
        var edit = Menu(root, "MediaTimelineCursorActionsMenu");
        foreach (var command in new[] { "UndoDraftCommand", "RedoDraftCommand",
            "CursorToSelectionCommand", "SeekEditCursorCommand", "JumpSelectedCueCommand",
            "SelectAllItemsCommand", "ClearSelectionCommand" })
            Assert.Contains(edit.Descendants(), e => e.Attribute("Command")?.Value == "{Binding " + command + "}");
        Assert.Contains(root.Descendants(), e => e.Attribute("ContextMenuOpening")?.Value == "OnMediaItemActionsOpening");
        Assert.Contains(root.Descendants(), e => e.Attribute("ContextMenuOpening")?.Value == "OnMediaCueActionsOpening");
        Assert.Equal(1, root.Descendants().Count(e =>
            e.Attribute("Command")?.Value?.Contains("MediaDeck.TogglePlayPauseCommand", StringComparison.Ordinal) == true));
        Assert.Equal(1, root.Descendants().Count(e =>
            e.Attribute("Command")?.Value?.Contains("MediaDeck.StopCommand", StringComparison.Ordinal) == true));
        Assert.Contains(root.Descendants(), e => e.Attribute("Command")?.Value == "{Binding PreviousCueCommand}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Command")?.Value == "{Binding NextCueCommand}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Text")?.Value == "{Binding EditCursorLabel}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Text")?.Value == "{Binding PreviewPositionLabel}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Text")?.Value == "{Binding ProgramPositionLabel}");
        Assert.Contains(root.Descendants(), e => e.Attribute("Content")?.Value?.Contains("CurrentTimecode", StringComparison.Ordinal) == true);
        Assert.Contains(root.Descendants(), e => e.Attribute("Command")?.Value?.Contains("MediaDeck.AddCueCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Timed_text_import_is_a_qualified_local_tool_and_keeps_review_visible()
    {
        var root = Surface("MediaTimelineControl.xaml");
        var tools = Menu(root, "MediaTimelineToolsActionsMenu");
        foreach (var click in new[] { "ImportTimedTextD_Click", "ImportTimedTextLive_Click" })
        {
            var entry = Assert.Single(tools.Descendants().Where(e => e.Attribute("Click")?.Value == click));
            foreach (var property in new[] { "RequiresSynchronization", "TimedTextSourceContext", "IsLoaded" })
                Assert.Contains(entry.Descendants(), e => e.Attribute("Binding")?.Value == "{Binding " + property + "}");
        }
        foreach (var property in new[] { "TimedTextReview.Status", "TimedTextReview.PreviewText",
            "TimedTextReview.Detail", "TimedTextReview.AvailableLanguages" })
            Assert.Contains(root.Descendants(), e => e.Attributes().Any(a => a.Value.Contains("Binding " + property, StringComparison.Ordinal)));
        var source = Code("MediaTimelineControl.xaml.cs");
        Assert.Contains("ImportTimedTextAsync", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TimedTextSourceContext is null", source, StringComparison.Ordinal);
        Assert.Contains("model.SelectCue(cue)", source, StringComparison.Ordinal);
        Assert.Contains("model.SelectItem(item)", source, StringComparison.Ordinal);
        Assert.Contains("Key.Apps", source, StringComparison.Ordinal);
        Assert.Contains("Key.F10", source, StringComparison.Ordinal);
        Assert.Contains("FindAncestor<TextBox>", source, StringComparison.Ordinal);
    }

    private const string ChildEnvironment = "RTAIME_TIMELINE_MENUS_WPF_CHILD";

    [Fact]
    public async Task Real_Wpf_menus_fit_supported_viewports_and_do_not_issue_edits_or_seeks()
    {
        // WPF Application resources are process-owned; qualify in a fresh STA child.
        if (Environment.GetEnvironmentVariable(ChildEnvironment) != "1")
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("vstest");
            psi.ArgumentList.Add(typeof(TimelineContextActionsTests).Assembly.Location);
            psi.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.TimelineContextActionsTests." +
                nameof(Real_Wpf_menus_fit_supported_viewports_and_do_not_issue_edits_or_seeks));
            psi.Environment[ChildEnvironment] = "1";
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(75));
                Assert.True(process.ExitCode == 0,
                    $"Timeline WPF qualification failed: {process.ExitCode}{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            return;
        }

        RunSta(() =>
        {
            OperatorResourceApplication? app = null;
            Window? window = null;
            RtaimeContextMenu? openMenu = null;
            using var editor = new SequenceTimelineEditorViewModel(null);
            var media = new MediaTimelineViewModel(new MediaTimelineController());
            try
            {
                app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var sequence = new SequenceTimelineControl { DataContext = editor };
                window = new Window
                {
                    Width = 960, Height = 540, MinWidth = 0, MinHeight = 0,
                    ShowInTaskbar = false, Content = sequence
                };
                window.Show();
                window.UpdateLayout();
                Drain();
                var edit = Assert.IsType<RtaimeButton>(sequence.FindName("SequenceEditActionsButton"));
                var track = Assert.IsType<RtaimeButton>(sequence.FindName("SequenceTrackActionsButton"));
                var tools = Assert.IsType<RtaimeButton>(sequence.FindName("SequenceToolsActionsButton"));
                foreach (var (width, height) in new[] { (960d, 540d), (1280d, 720d), (1600d, 900d) })
                {
                    window.Width = width; window.Height = height;
                    window.UpdateLayout(); Drain();
                    var ruler = Assert.IsType<Border>(sequence.FindName("SequenceRuler"));
                    var viewport = Assert.IsType<Grid>(sequence.FindName("SequenceTrackViewport"));
                    Assert.True(ruler.ActualWidth > 0 && viewport.ActualWidth > 0);
                    foreach (var anchor in new[] { edit, track, tools })
                    {
                        Assert.True(anchor.IsVisible);
                        anchor.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, anchor));
                        Drain();
                        openMenu = Assert.IsType<RtaimeContextMenu>(anchor.ContextMenu);
                        Assert.True(openMenu.IsOpen);
                        Assert.Same(anchor, openMenu.PlacementTarget);
                        openMenu.IsOpen = false;
                    }
                    Assert.Equal(0, editor.EditCursorTick);
                    Assert.Null(editor.SelectedClip);
                    Assert.False(editor.Playout.TakeCommand.CanExecute(null));
                }
                var first = Assert.IsType<RtaimeMenuItem>(edit.ContextMenu!.Items[0]);
                Assert.Same(editor.SplitCommand, first.Command);
                window.Close(); window = null;

                var timeline = new MediaTimelineControl { DataContext = media };
                window = new Window
                {
                    Width = 960, Height = 540, MinWidth = 0, MinHeight = 0,
                    ShowInTaskbar = false, Content = timeline
                };
                window.Show(); window.UpdateLayout(); Drain();
                var cursor = Assert.IsType<RtaimeButton>(timeline.FindName("MediaTimelineCursorMenuButton"));
                var mediaTools = Assert.IsType<RtaimeButton>(timeline.FindName("MediaTimelineToolsActionsButton"));
                foreach (var anchor in new[] { cursor, mediaTools })
                {
                    anchor.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, anchor));
                    Drain();
                    openMenu = Assert.IsType<RtaimeContextMenu>(anchor.ContextMenu);
                    Assert.True(openMenu.IsOpen);
                    Assert.Same(anchor, openMenu.PlacementTarget);
                    openMenu.IsOpen = false;
                }
                Assert.Same(media.UndoDraftCommand,
                    Assert.IsType<RtaimeMenuItem>(cursor.ContextMenu!.Items[0]).Command);
                Assert.False(media.HasPendingSeek);
                Assert.Equal(0, media.ConfirmedFrame);
            }
            finally
            {
                if (openMenu is not null) openMenu.IsOpen = false;
                window?.Close();
                media.DisposeAsync().AsTask().GetAwaiter().GetResult();
                app?.Shutdown();
            }
        });
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Timeline menu and viewport qualification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Timeline WPF menu qualification timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static XElement Menu(XDocument document, string key) =>
        Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "RtaimeContextMenu" &&
            e.Attribute(Xaml + "Key")?.Value == key));
    private static XElement Named(XDocument document, string name) =>
        Assert.Single(document.Descendants().Where(e => e.Attribute(Xaml + "Name")?.Value == name));
    private static XDocument Surface(string filename) =>
        XDocument.Load(Path.Combine(Root(), "src", "Hosts", "rtaime.Operator", filename));
    private static string Code(string filename) =>
        File.ReadAllText(Path.Combine(Root(), "src", "Hosts", "rtaime.Operator", filename));
    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "rtaime.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root containing rtaime.slnx was not found.");
    }
}
