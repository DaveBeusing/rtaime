// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using rtaime.Operator.Controls;

namespace rtaime.Operator;

public partial class AudioMixerControl : UserControl
{
    public MediaPoolInspectorViewModel? Inspector { get; internal set; }
    public AudioMixerControl()
    {
        InitializeComponent();
        AddHandler(RtaimeNumericField.InputValidityChangedEvent, new RoutedEventHandler(NumericValidityChanged));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private void NumericValidityChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is RtaimeNumericField { DataContext: IAudioMixerDraftStrip strip })
            strip.InputsValid = !Descendants<RtaimeNumericField>(this).Any(f => ReferenceEquals(f.DataContext, strip) && f.HasInputError);
    }
    private void UnityDraft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IAudioMixerDraftStrip strip })
        {
            strip.UnityDraft();
            foreach (var field in Descendants<RtaimeNumericField>(this).Where(f => ReferenceEquals(f.DataContext, strip) && f.Definition.Id.Contains("gain")))
                field.CancelInput();
        }
    }
    private void DiscardDraft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IAudioMixerDraftStrip strip })
        {
            foreach (var field in Descendants<RtaimeNumericField>(this).Where(f => ReferenceEquals(f.DataContext, strip))) field.CancelInput();
            strip.InputsValid = true;
        }
    }
    private void CopyGain_Click(object sender, RoutedEventArgs e)
    {
        var ok = sender is FrameworkElement { DataContext: IAudioMixerDraftStrip strip } &&
            strip.CopyGain() is { } payload && Inspector?.WriteAudioGainClipboard(payload) == true;
        if (DataContext is OperatorAudioMixerViewModel mixer)
            mixer.AttributeTransferStatus = ok ? "Gain copied to Inspector clipboard; destination Apply remains explicit." : "Copy unavailable: synchronize and correct invalid input.";
    }
    private void PasteGain_Click(object sender, RoutedEventArgs e)
    {
        var ok = sender is FrameworkElement { DataContext: IAudioMixerDraftStrip strip } &&
            Inspector?.ReadAudioGainClipboard() is { } payload && strip.PasteGain(payload);
        if (DataContext is OperatorAudioMixerViewModel mixer)
            mixer.AttributeTransferStatus = ok ? "Gain pasted into local draft. Apply to request Runtime confirmation." : "Paste rejected: incompatible or stale Inspector attributes.";
    }
    private void DetachMixer_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        var main = window as MainWindow ?? window?.Owner as MainWindow;
        main?.Docking.Float("AUDIO");
    }
}
