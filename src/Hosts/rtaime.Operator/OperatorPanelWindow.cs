// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace rtaime.Operator;

public sealed class OperatorPanelWindow : Window
{
    private readonly MainWindow _owner;
    private readonly OperatorDockController _controller;

    // Detached panels change their nearest Window. Bind presentation flags to the
    // MainWindow owner rather than introducing a second timeline or mode selection.
    public static readonly DependencyProperty TimelineSequenceVisibilityProperty =
        DependencyProperty.Register(nameof(TimelineSequenceVisibility), typeof(Visibility),
            typeof(OperatorPanelWindow), new PropertyMetadata(Visibility.Visible));

    public static readonly DependencyProperty TimelineMediaVisibilityProperty =
        DependencyProperty.Register(nameof(TimelineMediaVisibility), typeof(Visibility),
            typeof(OperatorPanelWindow), new PropertyMetadata(Visibility.Collapsed));

    public Visibility TimelineSequenceVisibility => (Visibility)GetValue(TimelineSequenceVisibilityProperty);
    public Visibility TimelineMediaVisibility => (Visibility)GetValue(TimelineMediaVisibilityProperty);

    public string PanelId { get; }
    internal OperatorPanelWindow(MainWindow owner, OperatorDockController controller, string panel, OperatorPanelPlacement placement)
    {
        _owner = owner; _controller = controller; PanelId = panel;
        Title = $"rtaime — {panel}";
        Width = 800; Height = 600; MinWidth = 320; MinHeight = 220;
        Background = (Brush)owner.FindResource("OperatorBackgroundBrush");
        Foreground = (Brush)owner.FindResource("OperatorTextBrush");
        FontFamily = owner.FontFamily;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("RootWindow", owner);
        BindingOperations.SetBinding(this, TimelineSequenceVisibilityProperty,
            new Binding(nameof(MainWindow.TimelineSequenceVisibility)) { Source = owner, Mode = BindingMode.OneWay });
        BindingOperations.SetBinding(this, TimelineMediaVisibilityProperty,
            new Binding(nameof(MainWindow.TimelineMediaVisibility)) { Source = owner, Mode = BindingMode.OneWay });
    }
    public StartupLifecycleViewModel Startup => _owner.Startup;
    public OperatorNotificationCenterViewModel Notifications => _owner.Notifications;
    public OperatorWorkspaceStateViewModel WorkspaceStates => _owner.WorkspaceStates;
    public OperatorMonitoringViewModel Monitoring => _owner.Monitoring;
    public ProgramOutputController ProgramOutput => _owner.ProgramOutput;
    public OutputRoutingHealthViewModel OutputHealth => _owner.OutputHealth;
    public RuntimePerformanceStatusBarViewModel RuntimePerformanceStatus => _owner.RuntimePerformanceStatus;
    public OperatorHealthSnapshotProvider HealthProvider => _owner.HealthProvider;
    public HealthCenterViewModel HealthCenter => _owner.HealthCenter;
    public OperatorShellViewModel Shell => _owner.Shell;
    public MediaDeckViewModel MediaDeck => _owner.MediaDeck;
    public MediaTimelineViewModel Timeline => _owner.Timeline;
    public SequenceTimelineEditorViewModel SequenceTimeline => _owner.SequenceTimeline;
    public ReplayViewModel Replay => _owner.Replay;
    public DemoProductionPackageController DemoProduction => _owner.DemoProduction;
    public MediaPoolInspectorViewModel MediaPool => _owner.MediaPool;
    public NdiSourceDiscoveryViewModel NdiSources => _owner.NdiSources;
    public RundownViewModel Rundown => _owner.Rundown;
    public CompositingGraphViewModel CompositingGraph => _owner.CompositingGraph;
    public OperatorQuickControlsViewModel QuickControls => _owner.QuickControls;
    public ShowControlViewModel ShowControl => _owner.ShowControl;
    public ProductionMacroViewModel ProductionMacros => _owner.ProductionMacros;
    public OperatorKeyboardCommandRegistry Shortcuts => _owner.Shortcuts;
    public OperatorDockController Docking => _controller;
    public ICommand OpenWorkspaceTemplatesCommand => _owner.OpenWorkspaceTemplatesCommand;
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MainWindow.ApplyDarkWindowCaption(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Message);
    }
    private IntPtr Message(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0232 || message == 0x02E0) Dispatcher.BeginInvoke(() => _controller.Capture());
        return IntPtr.Zero;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_owner.Shortcuts.TryHandle(e)) return;
        base.OnPreviewKeyDown(e);
    }
}
