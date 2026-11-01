// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using rtaime.Control.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class OutputCanvasMenuQualificationTests
{
    private const string ChildVariable = "RTAIME_OUTPUT_CANVAS_MENU_CHILD";

    [Fact]
    public void Menus_preserve_canvas_surface_view_and_geometry_command_owners()
    {
        var document = Surface();
        var canvas = Menu(document, "OutputCanvasActionsMenu");
        var surface = Menu(document, "OutputSurfaceActionsMenu");
        var view = Menu(document, "OutputCanvasViewMenu");
        var tools = Menu(document, "OutputCanvasToolsMenu");

        foreach (var menu in new[] { canvas, surface, view, tools })
            Assert.Equal("{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}",
                menu.Attribute("DataContext")?.Value);

        foreach (var (menu, command) in new[]
        {
            (canvas, "ReloadCommand"), (canvas, "DiscardAndReloadCommand"),
            (canvas, "UndoCommand"), (canvas, "RedoCommand"),
            (canvas, "AddCanvasCommand"), (canvas, "RemoveCanvasCommand"),
            (surface, "AddSurfaceCommand"), (surface, "RemoveSurfaceCommand"),
            (view, "FitCommand"), (view, "ZoomInCommand"), (view, "ZoomOutCommand"),
            (tools, "ToggleGeometryDetailsCommand")
        })
            Assert.Contains(menu.Descendants(), item =>
                item.Name.LocalName == "RtaimeMenuItem" &&
                item.Attribute("Command")?.Value == "{Binding " + command + "}");

        foreach (var draftOption in new[] { "IsSurfaceEnabled", "SnapEnabled", "ShowGuides", "ShowSafeAreas" })
        {
            var owner = draftOption == "IsSurfaceEnabled" ? surface : view;
            Assert.Contains(owner.Descendants(), item =>
                item.Name.LocalName == "RtaimeMenuItem" &&
                item.Attribute("IsCheckable")?.Value == "True" &&
                item.Attribute("IsChecked")?.Value == "{Binding " + draftOption + ", Mode=TwoWay}");
        }

        foreach (var buttonName in new[] { "CanvasMenuButton", "SurfaceMenuButton", "ViewMenuButton", "ToolsMenuButton" })
        {
            var button = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "RtaimeButton" &&
                element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == buttonName));
            Assert.Equal("OpenEditorMenu_Click", button.Attribute("Click")?.Value);
            Assert.Equal("OnEditorMenuKeyDown", button.Attribute("PreviewKeyDown")?.Value);
            Assert.NotNull(button.Attribute("ContextMenu"));
            Assert.NotNull(button.Attribute("AutomationProperties.Name"));
        }

        Assert.Contains(surface.Descendants(), item => item.Attribute("Click")?.Value == "ToggleAlignmentTools_Click");
        Assert.Contains(view.Descendants(), item => item.Attribute("Click")?.Value == "ToggleGuideDetails_Click");
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName is
            "Button" or "Menu" or "MenuItem" or "ContextMenu" or "ComboBox" or "Slider");
    }

    [Fact]
    public void Stage_and_revision_evidence_precede_save_apply_and_recovery_actions()
    {
        var document = Surface();
        var xaml = File.ReadAllText(SourcePath);
        static int Position(string text, string keyword)
        {
            var index = text.IndexOf(keyword, StringComparison.Ordinal);
            Assert.True(index >= 0, "Missing canvas presentation marker: " + keyword);
            return index;
        }

        Assert.True(Position(xaml, "Text=\"SAVED REVISION\"") < Position(xaml, "Editor=\"{Binding}\""));
        Assert.True(Position(xaml, "Text=\"ACTIVE RUNTIME REVISION\"") < Position(xaml, "Editor=\"{Binding}\""));
        Assert.True(Position(xaml, "Text=\"AVAILABLE PROGRAM TARGET\"") < Position(xaml, "Editor=\"{Binding}\""));
        Assert.True(Position(xaml, "Editor=\"{Binding}\"") < Position(xaml, "Content=\"BIND SELECTED CANVAS TO PROGRAM DRAFT\""));
        Assert.True(Position(xaml, "Content=\"BIND SELECTED CANVAS TO PROGRAM DRAFT\"") <
            Position(xaml, "Content=\"{Binding ValidationActionLabel}\""));
        Assert.True(Position(xaml, "Content=\"{Binding ValidationActionLabel}\"") <
            Position(xaml, "Content=\"SAVE\""));
        Assert.True(Position(xaml, "Content=\"SAVE\"") < Position(xaml, "Content=\"REQUEST RUNTIME APPLY\""));

        foreach (var evidence in new[] { "WorkflowStage", "DraftSafety", "SavedRevision", "DraftRevision",
            "PendingRevision", "ActiveRevision", "TargetEligibility", "SavedTargetEvidence", "ObservedTargetEvidence" })
            Assert.Contains(document.Descendants(), node =>
                node.Attribute("Text")?.Value == "{Binding " + evidence + "}");

        var stage = Assert.Single(document.Descendants(), node =>
            node.Name.LocalName == "OutputCanvasStageSurface");
        Assert.Equal("{Binding}", stage.Attribute("Editor")?.Value);
        Assert.Equal("285", stage.Attribute("Height")?.Value);
        Assert.Equal("180", stage.Attribute("MinHeight")?.Value);

        var disable = Action(document, "DISABLE MAPPING");
        Assert.Equal("{Binding DisableCommand}", disable.Attribute("Command")?.Value);
        Assert.Contains(disable.Descendants(), trigger => trigger.Name.LocalName == "DataTrigger" &&
            trigger.Attribute("Binding")?.Value == "{Binding ActivationState}" &&
            trigger.Attribute("Value")?.Value == "ACTIVE");
        Assert.Contains(disable.Descendants(), trigger => trigger.Name.LocalName == "DataTrigger" &&
            trigger.Attribute("Binding")?.Value == "{Binding WorkflowStage}" &&
            trigger.Attribute("Value")?.Value == "APPLY ACCEPTED · RUNTIME CONFIRMATION PENDING");

        var reconcile = Action(document, "RECONCILE UNKNOWN");
        Assert.Equal("{Binding ReconcileCommand}", reconcile.Attribute("Command")?.Value);
        Assert.Contains(reconcile.Descendants(), trigger =>
            trigger.Name.LocalName == "DataTrigger" &&
            trigger.Attribute("Binding")?.Value == "{Binding ApplyOutcomeUnconfirmed}" &&
            trigger.Attribute("Value")?.Value == "True");
        foreach (var action in new[] { disable, reconcile })
            Assert.DoesNotContain(action.Ancestors(), ancestor =>
                ancestor.Attributes().Any(a => a.Name.LocalName == "Name" &&
                    a.Value is "GuideDetails" or "AlignmentTools"));
    }

    [Fact]
    public async Task No_target_or_saved_state_does_not_enable_a_live_mapping_mutation()
    {
        await using var owner = new OperatorViewModel();
        using var editor = new OutputCanvasEditorViewModel(owner);
        editor.SetWorkspaceVisible(true);

        Assert.Empty(editor.Canvases);
        Assert.Null(editor.SelectedCanvas);
        Assert.Null(editor.SelectedSurface);
        Assert.Null(editor.SelectedTarget);
        Assert.False(editor.IsDirty);
        Assert.Equal("UNAVAILABLE", editor.SavedRevision);
        Assert.Equal("UNVERIFIED", editor.ActiveRevision);
        Assert.False(editor.IsActiveMappingConfirmed);
        Assert.False(editor.BindProgramCommand.CanExecute(null));
        Assert.False(editor.AddSurfaceCommand.CanExecute(null));
        Assert.False(editor.RemoveSurfaceCommand.CanExecute(null));
        Assert.False(editor.ValidateCommand.CanExecute(null));
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.ApplyCommand.CanExecute(null));
        Assert.False(editor.DisableCommand.CanExecute(null));
        Assert.False(editor.ReconcileCommand.CanExecute(null));

        editor.Zoom = 1.25;
        editor.SnapEnabled = false;
        editor.ShowGuides = false;
        editor.ShowSafeAreas = true;
        Assert.False(editor.IsDirty);
        Assert.Equal("UNAVAILABLE", editor.SavedRevision);
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Output_stage_view_commands_retain_identical_logical_coordinates_at_dpi(double dpi)
    {
        var stage = new OutputCanvasViewport(760, 480, 1920, 1080, 1.4, -23, 18);
        foreach (var point in new[]
        {
            new OutputCanvasPoint(0, 0),
            new OutputCanvasPoint(276.125, 458.75),
            new OutputCanvasPoint(1920, 1080)
        })
        {
            var onscreen = stage.ToView(point);
            var back = stage.ToCanvas(onscreen);
            Assert.InRange(Math.Abs(back.X - point.X), 0, 1e-8);
            Assert.InRange(Math.Abs(back.Y - point.Y), 0, 1e-8);
            var aligned = OutputCanvasViewport.Align(onscreen, dpi);
            Assert.InRange(Math.Abs(aligned.X - onscreen.X), 0, .5 / dpi + 1e-8);
            Assert.InRange(Math.Abs(aligned.Y - onscreen.Y), 0, .5 / dpi + 1e-8);
        }
    }

    [Fact]
    public async Task Real_Wpf_menus_are_keyboard_accessible_and_do_not_change_drafts_or_output()
    {
        if (Environment.GetEnvironmentVariable(ChildVariable) != "1")
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(OutputCanvasMenuQualificationTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.OutputCanvasMenuQualificationTests." +
                nameof(Real_Wpf_menus_are_keyboard_accessible_and_do_not_change_drafts_or_output));
            start.ArgumentList.Add("--logger:console;verbosity=detailed");
            start.Environment[ChildVariable] = "1";

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var output = await stdout;
                Assert.True(process.ExitCode == 0,
                    $"Output canvas WPF qualification failed. Exit={process.ExitCode}{Environment.NewLine}{output}{Environment.NewLine}{await stderr}");
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
            OperatorViewModel? owner = null;
            OutputCanvasEditorViewModel? editor = null;
            try
            {
                app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                owner = new OperatorViewModel();
                editor = new OutputCanvasEditorViewModel(owner);
                editor.SetWorkspaceVisible(true);
                var control = new OutputCanvasEditorControl { DataContext = editor };
                window = new Window { Width = 760, Height = 650, ShowInTaskbar = false, Content = control };
                window.Show();
                window.UpdateLayout();
                DrainDispatcher();

                var stage = Assert.Single(Descendants<OutputCanvasStageSurface>(control));
                Assert.True(stage.IsLoaded && stage.IsVisible);
                Assert.Same(editor, stage.Editor);
                var initial = (editor.Zoom, editor.SnapEnabled, editor.ShowGuides,
                    editor.ShowSafeAreas, editor.IsDirty, editor.SavedRevision);

                foreach (var id in new[] { "CanvasMenuButton", "SurfaceMenuButton", "ViewMenuButton", "ToolsMenuButton" })
                {
                    var button = Assert.IsType<RtaimeButton>(control.FindName(id));
                    Assert.True(button.Focusable && button.IsVisible);
                    Assert.NotNull(button.ContextMenu);
                    if (id == "CanvasMenuButton")
                    {
                        var key = new KeyEventArgs(Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(button)!, 0, Key.Apps)
                        {
                            RoutedEvent = Keyboard.PreviewKeyDownEvent
                        };
                        button.RaiseEvent(key);
                        Assert.True(key.Handled);
                    }
                    else
                    {
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    }
                    DrainDispatcher();
                    Assert.True(button.ContextMenu.IsOpen);
                    Assert.Same(button, button.ContextMenu.PlacementTarget);
                    Assert.Same(editor, button.ContextMenu.DataContext);
                    button.ContextMenu.IsOpen = false;
                }
                Assert.Equal(initial, (editor.Zoom, editor.SnapEnabled, editor.ShowGuides,
                    editor.ShowSafeAreas, editor.IsDirty, editor.SavedRevision));

                Assert.Equal(Visibility.Collapsed,
                    Assert.IsAssignableFrom<FrameworkElement>(control.FindName("AlignmentTools")).Visibility);
                Assert.Equal(Visibility.Collapsed,
                    Assert.IsAssignableFrom<FrameworkElement>(control.FindName("GuideDetails")).Visibility);
                var surfaceMenu = Assert.IsType<RtaimeButton>(control.FindName("SurfaceMenuButton")).ContextMenu;
                var surfaceToggle = Assert.Single(surfaceMenu.Items.OfType<RtaimeMenuItem>(),
                    item => Equals(item.Header, "Show / hide alignment tools"));
                surfaceToggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, surfaceToggle));
                Assert.Equal(Visibility.Visible,
                    Assert.IsAssignableFrom<FrameworkElement>(control.FindName("AlignmentTools")).Visibility);
                surfaceToggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, surfaceToggle));
                Assert.Equal(Visibility.Collapsed,
                    Assert.IsAssignableFrom<FrameworkElement>(control.FindName("AlignmentTools")).Visibility);

                window.Content = null;
                window.UpdateLayout();
                DrainDispatcher();
                Assert.False(stage.IsLoaded);
                Assert.False(stage.IsMouseCaptured);
                Assert.False(editor.IsDirty);
            }
            finally
            {
                window?.Close();
                editor?.Dispose();
                if (owner is not null) owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                app?.Shutdown();
            }
        });
    }

    private static XElement Menu(XDocument document, string key) =>
        Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "RtaimeContextMenu" &&
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value == key));

    private static XElement Action(XDocument document, string content) =>
        Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "RtaimeButton" &&
            element.Attribute("Content")?.Value == content);

    private static XDocument Surface() => XDocument.Load(SourcePath);

    private static string SourcePath
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return Path.Combine(directory.FullName, "src", "Hosts", "rtaime.Operator", "OutputCanvasEditorControl.xaml");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Output canvas contextual menu qualification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Output canvas WPF STA did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
