// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorMediaWorkspaceLifecycleTests
{
    [Fact]
    public void Replaced_monitor_restores_uncommitted_roi_to_original_view()
    {
        RunSta(() =>
        {
            var original = new MonitorView { InspectionRoi = new MediaInspectionRoi(10, 20, 30, 40) };
            var replacement = new MonitorView();
            var presentation = new RtaimeMonitorPresentation { Monitor = original };

            // The binding can change while the pointer still owns an ROI drag.
            Field(presentation, "_roiGestureMonitor").SetValue(presentation, original);
            Field(presentation, "_roiBeforeGesture").SetValue(presentation, original.InspectionRoi);
            Field(presentation, "_roiSelecting").SetValue(presentation, true);
            original.InspectionRoi = new MediaInspectionRoi(35, 45, 10, 10);
            presentation.Monitor = replacement;

            Assert.Equal(new MediaInspectionRoi(10, 20, 30, 40), original.InspectionRoi);
            Assert.Null(replacement.InspectionRoi);
            Assert.Null(Field(presentation, "_roiGestureMonitor").GetValue(presentation));
            Assert.False((bool)Field(presentation, "_roiSelecting").GetValue(presentation)!);
        });
    }

    [Fact]
    public void Output_canvas_does_not_retain_editor_subscription_after_window_unload()
    {
        RunSta(() =>
        {
            var owner = new OperatorViewModel();
            using var editor = new OutputCanvasEditorViewModel(owner);
            var stage = new OutputCanvasStageSurface { Editor = editor };
            var subscribed = Field(stage, "_attached");
            Assert.Null(subscribed.GetValue(stage));

            var window = new Window
            {
                Width = 640, Height = 420, ShowInTaskbar = false, Content = stage
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                DrainDispatcher();
                Assert.True(stage.IsLoaded);
                Assert.Same(editor, subscribed.GetValue(stage));

                window.Content = null;
                window.UpdateLayout();
                DrainDispatcher();
                Assert.Null(subscribed.GetValue(stage));

                stage.Editor = null;
                Assert.Null(subscribed.GetValue(stage));
            }
            finally
            {
                window.Close();
                owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    private static FieldInfo Field(object instance, string name) =>
        Assert.IsType<FieldInfo>(instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic));

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
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        {
            IsBackground = true,
            Name = "Operator media workspace lifecycle qualification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF lifecycle STA thread did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
