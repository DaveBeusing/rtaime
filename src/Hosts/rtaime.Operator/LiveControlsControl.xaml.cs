// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using rtaime.Operator.Controls;

namespace rtaime.Operator;

public partial class LiveControlsControl : UserControl
{
    public LiveControlsControl()
    {
        InitializeComponent();
        LiveConsoleInput.Attach(this);
    }

    // Presentation-only expansion. The action controls retain their original
    // ViewModel/Window command owners and no live mutation occurs on opening.
    private void ToggleSecondaryDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RtaimeButton { Tag: string sectionName } button ||
            FindName(sectionName) is not FrameworkElement section)
            return;

        var expanded = section.Visibility != Visibility.Visible;
        section.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        var label = sectionName switch
        {
            "TakeDetails" => "TAKE DETAILS",
            "LayerDetails" => "LAYER SETTINGS",
            "MonitoringDetails" => "CLEAN MONITORING",
            "TransmissionDetails" => "TRANSMISSION DETAILS",
            _ => "DETAILS"
        };
        button.Content = (expanded ? "HIDE " : "SHOW ") + label;
        AutomationProperties.SetName(button, (expanded ? "Hide " : "Show ") + label.ToLowerInvariant());
    }
}
