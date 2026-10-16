// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class WorkspaceTemplatesWindow : Window
{
    public WorkspaceTemplatesWindow(MainWindow owner)
    {
        InitializeComponent(); Owner = owner; DataContext = owner.Shell;
        SourceInitialized += (_, _) => MainWindow.ApplyDarkWindowCaption(this);
        Loaded += (_, _) =>
        {
            OperatorMonitorGeometry.Restore(this, OperatorMonitorGeometry.Capture(owner) with { Width = 560, Height = 520 });
            TemplateNameBox.Focus();
        };
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        // This preference editor never forwards production shortcuts.
        base.OnPreviewKeyDown(e);
    }
}
