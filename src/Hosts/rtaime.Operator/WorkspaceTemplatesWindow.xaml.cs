// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace rtaime.Operator;

public partial class WorkspaceTemplatesWindow : Window
{
    public WorkspaceTemplatesWindow(MainWindow owner)
    {
        InitializeComponent(); Owner = owner; DataContext = owner.Shell;
        SourceInitialized += (_, _) => MainWindow.ApplyDarkWindowCaption(this);
        Loaded += (_, _) =>
        {
            var placement = OperatorMonitorGeometry.Capture(owner);
            var scale = placement.Dpi / 96;
            OperatorMonitorGeometry.Restore(this, placement with { Width = Width * scale, Height = Height * scale });
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible) { Activate(); TemplateNameBox.Focus(); }
            }, DispatcherPriority.Input);
        };
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        // This preference editor never forwards production shortcuts.
        base.OnPreviewKeyDown(e);
    }
}
