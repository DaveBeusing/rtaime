// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using Xunit;
namespace rtaime.Tests.Operator;

internal static class AudioMixerWorkspaceQualification
{
    internal static async Task VerifyAsync(MainWindow window, Dispatcher dispatcher)
    {
        var mixer = ((OperatorViewModel)window.DataContext).AudioMixer;
        // Visual-only retained snapshot fixture; real host/sample qualification is separate.
        Assert.Empty(mixer.Sources);
        var buses = new[] { AudioBusId.Program, new AudioBusId("aux"), new AudioBusId("clean"), new AudioBusId("iso") };
        var ids = Enumerable.Range(0,8).Select(_ => new MediaSourceId(Identity.New())).ToArray();
        var config = new AudioProductionConfiguration(1, buses.Select(b => new AudioProductionBusConfiguration(b,1,false)).ToArray(),
            ids.Select(id => new AudioProductionSourceConfiguration(id,1,false,true,buses)).ToArray());
        var evidence = buses.Select(b => new OperatorAudioProductionBusDescriptor(b.Value,1,false,.4,.2,.4,false,0,8,0)).ToArray();
        var projection = new OperatorAudioProductionDescriptor(config,.4,.2,.4,false,0,1,0,true,null,8,0,evidence);
        mixer.ApplyProjection(projection,ids.Select((id,i) => new OperatorSourceDescriptor(id.ToString(),"Source "+(i+1))).ToArray(),
            ids.Select((id,i) => new OperatorAudioInputDescriptor(id.ToString(),"stream",1,false,i==0?0:.4,.2,.4,false,"HEALTHY")).ToArray(),[],true);
        mixer.SetEvidenceAvailability("STALE");
        var view = window.AudioMixerView;
        var selected = mixer.SelectedSource;
        try
        {
            window.Docking.Float("AUDIO");
            await dispatcher.InvokeAsync(() => { },DispatcherPriority.Loaded);
            var floating = window.Docking.FloatingWindows["AUDIO"];
            floating.Width=960; floating.Height=760;
            await dispatcher.InvokeAsync(floating.UpdateLayout,DispatcherPriority.Render);
            Assert.Same(view,window.Docking.Panels["AUDIO"]);
            Assert.Same(mixer,view.DataContext);
            Assert.Same(window.MediaPool,view.Inspector);
            var faders = Descendants<RtaimeSlider>(view).ToArray();
            Assert.NotEmpty(faders);
            Assert.All(faders, f => { Assert.Equal(Orientation.Vertical,f.Orientation); Assert.Equal(104,f.Height); Assert.True(f.ActualWidth>=30); });
            var field = Descendants<RtaimeNumericField>(view).First(f => f.Definition.Id=="audio.gain");
            field.Focus(); field.Text="NaN";
            Assert.True(field.HasInputError);
            Assert.False(((IAudioMixerDraftStrip)field.DataContext).InputsValid);
            field.CancelInput();
            Assert.True(((IAudioMixerDraftStrip)field.DataContext).InputsValid);
            var cell = Descendants<RtaimeToggleButton>(view).First(t => t.DataContext is OperatorAudioBusAssignmentViewModel);
            Assert.Contains("Source",AutomationProperties.GetName(cell));
            Assert.Contains("→",AutomationProperties.GetName(cell));
            Assert.True(OperatorKeyboardCommandRegistry.IsTextEntryContext(cell));
            var presses=0;
            var guard=new OperatorKeyboardCommandRegistry([new("take","Take",Key.Space,ModifierKeys.None,new OperatorShellCommand(()=>presses++))]);
            Assert.False(guard.TryHandle(Key.Space,ModifierKeys.None,false,OperatorKeyboardCommandRegistry.IsTextEntryContext(cell)));
            Assert.Equal(0,presses);
            Capture(floating,"mixer-detailed");
            mixer.CompactPresentationCommand.Execute(null);
            await dispatcher.InvokeAsync(floating.UpdateLayout,DispatcherPriority.Render);
            Assert.False(mixer.ShowProcessing); Assert.Same(selected,mixer.SelectedSource);
            Assert.All(Descendants<RtaimeSlider>(view),f=>Assert.Equal(104,f.Height));
            Capture(floating,"mixer-compact");
            var bank=Descendants<RtaimeListBox>(view).First(b=>AutomationProperties.GetName(b)=="Audio source strips");
            Assert.Equal(8,bank.Items.Count);
            bank.ScrollIntoView(mixer.Sources[^1]);
            await dispatcher.InvokeAsync(floating.UpdateLayout,DispatcherPriority.Render);
            Assert.NotNull(bank.ItemContainerGenerator.ContainerFromItem(mixer.Sources[^1]));
            floating.Close();
            await dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded);
            Assert.Empty(window.Docking.FloatingWindows);
            Assert.Same(mixer,view.DataContext);
        }
        finally
        {
            mixer.Sources.Clear(); mixer.Buses.Clear(); mixer.SelectedSource=null;
            mixer.SetEvidenceAvailability("UNAVAILABLE");
            mixer.DetailedPresentationCommand.Execute(null);
            window.Shell.ResetLayoutCommand.Execute(null);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T found)yield return found;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
    private static void Capture(Window window,string name)
    {
        var root=Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
        if(string.IsNullOrWhiteSpace(root))return;
        Directory.CreateDirectory(root);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(root,name+".png")))encoder.Save(file);
        File.WriteAllText(Path.Combine(root,name+".json"),JsonSerializer.Serialize(new {
            sourceCommit=Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),sdk=Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
            scope="Software WPF snapshot fixture; physical audio devices and mixed-monitor behavior unverified",width=bitmap.PixelWidth,height=bitmap.PixelHeight }));
    }
}
