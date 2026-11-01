// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class AudioMixerMenuQualificationTests
{
    [Fact]
    public void Tools_menu_uses_existing_authoritative_commands_and_dirty_action_visibility()
    {
        var document = MixerSurface();
        var tools = Assert.Single(document.Descendants().Where(node =>
            node.Name.LocalName == "RtaimeButton" &&
            node.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "MixerToolsButton")));
        Assert.Equal("OpenMixerTools_Click", tools.Attribute("Click")?.Value);

        var menu = Assert.Single(tools.Descendants().Where(node => node.Name.LocalName == "RtaimeContextMenu"));
        Assert.Equal("{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}",
            menu.Attribute("DataContext")?.Value);
        var items = menu.Descendants().Where(node => node.Name.LocalName == "RtaimeMenuItem").ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal("{Binding Owner.SetAudioFollowVideoCommand}", items[0].Attribute("Command")?.Value);
        Assert.Equal("{Binding Owner.SetAudioBreakawayCommand}", items[1].Attribute("Command")?.Value);
        Assert.Equal("DetachMixer_Click", items[2].Attribute("Click")?.Value);

        var compact = Assert.Single(document.Descendants().Where(node =>
            node.Name.LocalName == "RtaimeScrollViewer" &&
            node.Attribute("Visibility")?.Value == "{Binding CompactVisibility}"));
        var advanced = Assert.Single(document.Descendants().Where(node =>
            node.Name.LocalName == "RtaimeScrollViewer" &&
            node.Attribute("Visibility")?.Value == "{Binding AdvancedVisibility}"));
        foreach (var mode in new[] { compact, advanced })
        {
            Assert.DoesNotContain(mode.Descendants(), node =>
                node.Attribute("Command")?.Value is "{Binding Owner.SetAudioFollowVideoCommand}" or
                    "{Binding Owner.SetAudioBreakawayCommand}");
        }
        Assert.Contains(advanced.Descendants(), node =>
            node.Name.LocalName == "RtaimeNumericField" &&
            node.Attribute("Value")?.Value == "{Binding LowGainDb, Mode=TwoWay}");
        Assert.Contains(advanced.Descendants(), node =>
            node.Name.LocalName == "RtaimeNumericField" &&
            node.Attribute("Value")?.Value == "{Binding CompressorThresholdDbFs, Mode=TwoWay}");

        var actionGroups = compact.Descendants().Where(node =>
            node.Name.LocalName == "StackPanel" &&
            node.Attribute("Style")?.Value == "{StaticResource MixerDirtyActions}").ToArray();
        Assert.Equal(2, actionGroups.Length);
        foreach (var group in actionGroups)
        {
            Assert.Contains(group.Descendants(), node => node.Attribute("Command")?.Value == "{Binding ApplyCommand}");
            Assert.Contains(group.Descendants(), node => node.Attribute("Command")?.Value == "{Binding DiscardDraftCommand}");
        }
        var dirtyStyle = Assert.Single(document.Descendants().Where(node =>
            node.Name.LocalName == "Style" && node.Attributes().Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value == "MixerDirtyActions")));
        Assert.Contains(dirtyStyle.Descendants(), node =>
            node.Name.LocalName == "DataTrigger" && node.Attribute("Binding")?.Value == "{Binding HasDraftChanges}");
        Assert.Contains(dirtyStyle.Descendants(), node =>
            node.Name.LocalName == "DataTrigger" && node.Attribute("Binding")?.Value == "{Binding MutationState}" &&
            node.Attribute("Value")?.Value == "REJECTED");
    }

    private const string QualificationChildVariable = "RTAIME_MIXER_MENU_QUALIFICATION_CHILD";

    [Fact]
    public async Task Real_Wpf_mixer_opens_tools_and_preserves_drafts_across_modes_without_a_control_command()
    {
        // A WPF Application must be created in a dedicated process.
        // Running the test again with the child marker owns one fresh STA resource lifetime.
        if (Environment.GetEnvironmentVariable(QualificationChildVariable) != "1")
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(AudioMixerMenuQualificationTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.AudioMixerMenuQualificationTests." +
                nameof(Real_Wpf_mixer_opens_tools_and_preserves_drafts_across_modes_without_a_control_command));
            start.Environment[QualificationChildVariable] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(process.ExitCode == 0,
                    $"Mixer WPF qualification failed. Exit={process.ExitCode}{Environment.NewLine}{await output}{Environment.NewLine}{await errors}");
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

        OnSta(() =>
        {
            OperatorResourceApplication? application = null;
            OperatorViewModel? owner = null;
            Window? window = null;
            RtaimeContextMenu? menu = null;
            try
            {
                application = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.InitializeComponent();
                owner = new OperatorViewModel();
                var mixer = owner.AudioMixer;
                var sourceId = new MediaSourceId(Identity.New());
                var configuration = new AudioProductionConfiguration(1,
                    [new AudioProductionBusConfiguration(AudioBusId.Program, 1, false)],
                    [new AudioProductionSourceConfiguration(sourceId, 1, false, true, [AudioBusId.Program])]);
                var evidence = new OperatorAudioProductionBusDescriptor("program", 1, false,
                    0.4, 0.2, 0.4, false, 0, 1, 0);
                mixer.ApplyProjection(
                    new OperatorAudioProductionDescriptor(configuration, 0.4, 0.2, 0.4, false,
                        0, 1, 0, true, null, 1, 0, [evidence]),
                    [new OperatorSourceDescriptor(sourceId.ToString(), "Camera A")],
                    [new OperatorAudioInputDescriptor(sourceId.ToString(), "stream-a", 1, false,
                        0.4, 0.2, 0.4, false, "HEALTHY")],
                    [], refreshDrafts: true);

                var view = new AudioMixerControl { DataContext = mixer };
                window = new Window
                {
                    Width = 960,
                    Height = 780,
                    ShowInTaskbar = false,
                    Content = view
                };
                window.Show();
                window.UpdateLayout();
                DrainDispatcher();

                Assert.False(mixer.ShowProcessing);
                Assert.Equal(Visibility.Visible, mixer.CompactVisibility);
                var tools = Assert.IsType<RtaimeButton>(view.FindName("MixerToolsButton"));
                Assert.True(tools.IsVisible && tools.Focusable);
                tools.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tools));
                DrainDispatcher();
                menu = Assert.IsType<RtaimeContextMenu>(tools.ContextMenu);
                Assert.True(menu.IsOpen);
                Assert.Same(tools, menu.PlacementTarget);
                var items = menu.Items.OfType<RtaimeMenuItem>().ToArray();
                Assert.Equal(3, items.Length);
                Assert.Same(owner.SetAudioFollowVideoCommand, items[0].Command);
                Assert.Same(owner.SetAudioBreakawayCommand, items[1].Command);
                Assert.Equal("Detach Mixer", items[2].Header);
                Assert.False(mixer.ShowProcessing);
                menu.IsOpen = false;

                var source = Assert.Single(mixer.Sources);
                var bus = Assert.Single(mixer.Buses);
                var sourceApply = Assert.Single(Descendants<RtaimeButton>(view).Where(button =>
                    AutomationProperties.GetName(button) == "Apply source draft to Control"));
                var busApply = Assert.Single(Descendants<RtaimeButton>(view).Where(button =>
                    AutomationProperties.GetName(button) == "Apply bus draft to Control"));
                Assert.False(sourceApply.IsVisible);
                Assert.False(busApply.IsVisible);
                source.Gain = 0.6;
                bus.MasterGain = 0.75;
                window.UpdateLayout();
                DrainDispatcher();
                Assert.True(source.HasDraftChanges && bus.HasDraftChanges);
                Assert.True(sourceApply.IsVisible && busApply.IsVisible);
                Assert.False(source.ApplyCommand.CanExecute(null));
                Assert.False(bus.ApplyCommand.CanExecute(null));

                mixer.DetailedPresentationCommand.Execute(null);
                window.UpdateLayout();
                DrainDispatcher();
                Assert.Equal(Visibility.Visible, mixer.AdvancedVisibility);
                Assert.Equal(0.6, source.Gain, 8);
                Assert.Equal(0.75, bus.MasterGain, 8);
                Assert.Contains(Descendants<RtaimeNumericField>(view),
                    field => field.DataContext is OperatorAudioSourceStripViewModel &&
                        field.Definition.Id == "audio.gain");

                mixer.CompactPresentationCommand.Execute(null);
                window.UpdateLayout();
                DrainDispatcher();
                Assert.Equal(Visibility.Visible, mixer.CompactVisibility);
                mixer.SetEvidenceAvailability("STALE");
                Assert.True(source.HasDraftChanges && bus.HasDraftChanges);
                Assert.Equal("STALE", mixer.Availability);
                Assert.Equal("—", source.LeftPeakLabel);
                source.DiscardDraftCommand.Execute(null);
                bus.DiscardDraftCommand.Execute(null);
                window.UpdateLayout();
                DrainDispatcher();
                Assert.False(source.HasDraftChanges || bus.HasDraftChanges);
                Assert.False(sourceApply.IsVisible || busApply.IsVisible);
                Assert.Equal(configuration.Sources[0].Gain, source.Gain, 8);
            }
            finally
            {
                if (menu is not null) menu.IsOpen = false;
                window?.Close();
                if (owner is not null) owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                application?.Shutdown();
            }
        });
    }

    private static XDocument MixerSurface()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root.FullName, "src", "Hosts", "rtaime.Operator", "AudioMixerControl.xaml"));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action action)
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
            Name = "Mixer progressive controls qualification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "Mixer WPF qualification did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
