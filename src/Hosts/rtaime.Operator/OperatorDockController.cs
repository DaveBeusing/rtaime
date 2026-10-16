// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using rtaime.Operator.Controls;

namespace rtaime.Operator;

// Moves existing view instances only. It has no transport, production command or frame subscription.
public sealed class OperatorDockController : IDisposable
{
    private readonly MainWindow _owner;
    private readonly Grid _legacy;
    private readonly Grid _host;
    private readonly Dictionary<string, FrameworkElement> _panels;
    private readonly Dictionary<string, (Panel Parent, int Index, BindingBase? Visibility, Visibility Value)> _homes;
    private readonly Dictionary<string, OperatorPanelWindow> _floating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentControl> _presenters = new(StringComparer.Ordinal);
    private WeakReference<UIElement>? _presentationFocus;
    private string _presentationSelected = "MONITORS";
    private long _focusGeneration;
    private bool _rebuilding, _disposed;
    private bool _compact;
    private string _selected = "MONITORS";
    private string? _focus;
    private int _displayUpdate;
    public OperatorDockController(MainWindow owner, Grid legacy, Grid host, IReadOnlyDictionary<string, FrameworkElement> panels)
    {
        _owner = owner; _legacy = legacy; _host = host; _compact = owner.Shell.IsCompactViewport;
        _panels = panels.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _homes = _panels.ToDictionary(p => p.Key, p =>
        {
            var parent = (Panel)p.Value.Parent;
            return (parent, parent.Children.IndexOf(p.Value), (BindingBase?)BindingOperations.GetBindingBase(p.Value, UIElement.VisibilityProperty), p.Value.Visibility);
        }, StringComparer.Ordinal);
        ToggleCommand = new OperatorShellCommand(Toggle);
        FloatCommand = new OperatorShellCommand(() => Float(_selected));
        FocusCommand = new OperatorShellCommand(() => TogglePresentation("FOCUS"));
        CinemaCommand = new OperatorShellCommand(() => TogglePresentation("CINEMA"));
        RestoreCommand = new OperatorShellCommand(() => _owner.Shell.RestorePresentation(), () => _owner.Shell.PresentationActive);
        NextCommand = new OperatorShellCommand(Next);
        LeftCommand = new OperatorShellCommand(() => Dock(_selected, "LEFT"));
        CenterCommand = new OperatorShellCommand(() => Dock(_selected, "CENTER"));
        RightCommand = new OperatorShellCommand(() => Dock(_selected, "RIGHT"));
        BottomCommand = new OperatorShellCommand(() => Dock(_selected, "BOTTOM"));
        owner.Shell.PropertyChanged += Changed;
        owner.SizeChanged += Sized;
        owner.Loaded += Loaded;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }
    public ICommand ToggleCommand { get; }
    public ICommand FloatCommand { get; }
    public ICommand FocusCommand { get; }
    public ICommand CinemaCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand LeftCommand { get; }
    public ICommand CenterCommand { get; }
    public ICommand RightCommand { get; }
    public ICommand BottomCommand { get; }
    public IReadOnlyDictionary<string, OperatorPanelWindow> FloatingWindows => _floating;
    public IReadOnlyDictionary<string, FrameworkElement> Panels => _panels;
    public string SelectedPanel => _selected;
    public void Toggle()
    {
        _owner.Shell.RestorePresentation();
        _owner.Shell.SetDocking(_owner.Shell.Docking with { Enabled = !_owner.Shell.Docking.Enabled });
    }
    private void TogglePresentation(string mode)
    {
        if (_owner.Shell.PresentationMode == mode) { _owner.Shell.RestorePresentation(); return; }
        _owner.Shell.RestorePresentation();
        Capture();
        _presentationSelected = _selected;
        _presentationFocus = Keyboard.FocusedElement is UIElement focused ? new(focused) : null;
        _focusGeneration++;
        _owner.Shell.BeginPresentation(mode);
        _focus = mode == "CINEMA" ? "MONITORS" : _selected;
        _selected = _focus;
        _owner.Shell.SetDocking(_owner.Shell.Docking.Dock(_focus, "CENTER"));
        if (mode == "CINEMA") _owner.Shell.FullscreenProgramCommand.Execute(null);
        FocusSelected();
    }
    public void Dock(string panel, string group)
    {
        if (!_panels.ContainsKey(panel) || !OperatorDockLayout.GroupIds.Contains(group)) return;
        _owner.Shell.RestorePresentation();
        _selected = panel; _focus = null;
        _owner.Shell.SetDocking(_owner.Shell.Docking.Dock(panel, group));
    }
    public void Float(string panel)
    {
        if (!_panels.ContainsKey(panel)) return;
        _selected = panel; _focus = null;
        if (_floating.TryGetValue(panel, out var existing)) { existing.Activate(); return; }
        var placement = OperatorMonitorGeometry.Capture(_owner) with { Width = 800, Height = 600 };
        _owner.Shell.SetDocking(_owner.Shell.Docking.Float(panel, placement));
    }
    public void Capture()
    {
        if (_disposed || _rebuilding || _floating.Count == 0) return;
        var layout = _owner.Shell.Docking;
        _owner.Shell.SetDocking(layout with
        {
            Floating = layout.Floating.Select(p => _floating.TryGetValue(p.Panel, out var window)
                ? p with { Placement = OperatorMonitorGeometry.Capture(window) } : p).ToArray()
        }, notify: false);
    }
    private void Loaded(object sender, RoutedEventArgs e) => Rebuild();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperatorShellViewModel.SelectedWorkspace)) _focus = null;
        if (e.PropertyName == nameof(OperatorShellViewModel.Docking))
        {
            if (!_owner.Shell.PresentationActive && _focus is not null) { _focus = null; _selected = _presentationSelected; }
            Rebuild();
        }
        if (e.PropertyName == nameof(OperatorShellViewModel.PresentationActive))
        {
            (RestoreCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
            if (!_owner.Shell.PresentationActive)
            {
                var generation = ++_focusGeneration;
                var reference = _presentationFocus; _presentationFocus = null;
                _owner.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && generation == _focusGeneration && reference is not null && reference.TryGetTarget(out var target) &&
                        target.IsVisible && target.IsEnabled)
                    { Window.GetWindow(target)?.Activate(); target.Focus(); }
                });
            }
        }
    }
    private void Sized(object sender, SizeChangedEventArgs e)
    {
        if (_compact == _owner.Shell.IsCompactViewport) return;
        _compact = _owner.Shell.IsCompactViewport;
        if (_owner.Shell.Docking.Enabled) Rebuild();
    }
    private void Next()
    {
        _owner.Shell.RestorePresentation();
        if (!_owner.Shell.Docking.Enabled) _owner.Shell.SetDocking(_owner.Shell.Docking with { Enabled = true });
        var docked = _owner.Shell.Docking.Groups.SelectMany(g => g.Panels).ToArray();
        if (docked.Length == 0) { _floating.Values.FirstOrDefault()?.Activate(); return; }
        _selected = docked[(Array.IndexOf(docked, _selected) + 1) % docked.Length];
        var group = _owner.Shell.Docking.Groups.Single(g => g.Panels.Contains(_selected));
        _owner.Shell.SetDocking(_owner.Shell.Docking.SelectTab(group.Id, _selected));
        FocusSelected();
    }
    private void Rebuild()
    {
        if (_disposed || _rebuilding || !_owner.IsLoaded) return;
        _rebuilding = true;
        var focused = Keyboard.FocusedElement as UIElement;
        try
        {
            var desired = _owner.Shell.Docking.Normalize();
            var preserved = desired.Enabled ? desired.Floating.Select(p => p.Panel).Where(_floating.ContainsKey).ToHashSet(StringComparer.Ordinal) : [];
            foreach (var pair in _presenters.Where(p => !preserved.Contains(p.Key)).ToArray())
            { pair.Value.Content = null; _presenters.Remove(pair.Key); }
            foreach (var pair in _floating.Where(p => !preserved.Contains(p.Key)).ToArray())
            { pair.Value.Content = null; pair.Value.Close(); _floating.Remove(pair.Key); }
            foreach (var entry in _panels.Where(p => !preserved.Contains(p.Key)))
            {
                var panel = entry.Value;
                if (panel.Parent is Panel parent) parent.Children.Remove(panel);
                else if (panel.Parent is Decorator decorator) decorator.Child = null;
                else if (panel.Parent is ContentControl control) control.Content = null;
            }
            _host.Children.Clear(); _host.RowDefinitions.Clear(); _host.ColumnDefinitions.Clear();
            var layout = _owner.Shell.Docking.Normalize();
            _host.Visibility = layout.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!layout.Enabled)
            {
                foreach (var entry in _panels.OrderBy(p => _homes[p.Key].Index))
                {
                    var home = _homes[entry.Key];
                    if (home.Visibility is not null) BindingOperations.SetBinding(entry.Value, UIElement.VisibilityProperty, home.Visibility);
                    else entry.Value.Visibility = home.Value;
                    home.Parent.Children.Insert(Math.Min(home.Index, home.Parent.Children.Count), entry.Value);
                }
                _focus = null;
                return;
            }
            foreach (var panel in _panels.Values)
            {
                BindingOperations.ClearBinding(panel, UIElement.VisibilityProperty);
                panel.Visibility = Visibility.Visible;
            }
            var compact = _owner.Shell.IsCompactViewport;
            _host.ColumnDefinitions.Add(new() { Width = new GridLength(compact || _focus is not null ? 0 : layout.LeftRatio, GridUnitType.Star) });
            _host.ColumnDefinitions.Add(new() { Width = new GridLength(1 - layout.LeftRatio - layout.RightRatio, GridUnitType.Star) });
            _host.ColumnDefinitions.Add(new() { Width = new GridLength(compact || _focus is not null ? 0 : layout.RightRatio, GridUnitType.Star) });
            _host.RowDefinitions.Add(new() { Height = new GridLength(1 - layout.BottomRatio, GridUnitType.Star) });
            _host.RowDefinitions.Add(new() { Height = new GridLength(compact || _focus is not null ? 0 : layout.BottomRatio, GridUnitType.Star) });
            if (_focus is not null && !layout.Floating.Any(p => p.Panel == _focus))
                AddGroup(new("CENTER", [_focus], _focus), 1, 0, 1);
            else if (compact)
                AddGroup(new("CENTER", layout.Groups.SelectMany(g => g.Panels).ToArray(), _selected), 1, 0, 1);
            else
            {
                AddGroup(layout.Groups[0], 0, 0, 1); AddGroup(layout.Groups[1], 1, 0, 1);
                AddGroup(layout.Groups[2], 2, 0, 1); AddGroup(layout.Groups[3], 0, 1, 3);
                AddSplitter(0, true); AddSplitter(1, true); AddSplitter(0, false);
            }
            foreach (var floating in layout.Floating)
            {
                if (_floating.TryGetValue(floating.Panel, out var retained))
                {
                    OperatorMonitorGeometry.Restore(retained, floating.Placement);
                    continue;
                }
                var window = new OperatorPanelWindow(_owner, this, floating.Panel, floating.Placement)
                {
                    Owner = _owner, DataContext = _owner.DataContext,
                    Content = Chrome(floating.Panel, _panels[floating.Panel])
                };
                _floating.Add(floating.Panel, window);
                window.Closed += (_, _) =>
                {
                    if (_disposed || _rebuilding) return;
                    Dock(floating.Panel, _owner.Shell.Docking.Floating.FirstOrDefault(p => p.Panel == floating.Panel)?.ReturnGroup ?? floating.ReturnGroup);
                    FocusSelected();
                };
                window.Activated += (_, _) => _selected = floating.Panel;
                window.Show();
                OperatorMonitorGeometry.Restore(window, floating.Placement);
            }
        }
        finally
        {
            _rebuilding = false;
            if (focused is not null) _owner.Dispatcher.BeginInvoke(() => { if (!_disposed && focused.IsVisible) focused.Focus(); });
        }
    }
    private void AddGroup(OperatorDockGroup group, int column, int row, int span)
    {
        var root = new Grid { Margin = new Thickness(3), Background = Brush("OperatorPanelBrush", "OperatorBackgroundBrush"), AllowDrop = true };
        Grid.SetColumn(root, column); Grid.SetRow(root, row); Grid.SetColumnSpan(root, span);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new WrapPanel();
        foreach (var panel in group.Panels)
        {
            var tab = Button(panel, $"Show {panel}; drag to another group. Ctrl+Shift+N cycles panels.", () =>
            {
                _selected = panel;
                var home = _owner.Shell.Docking.Groups.Single(g => g.Panels.Contains(panel));
                _owner.Shell.SetDocking(_owner.Shell.Docking.SelectTab(home.Id, panel));
            });
            var start = new Point();
            tab.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(tab);
            tab.PreviewMouseMove += (_, e) =>
            {
                var point = e.GetPosition(tab);
                if (e.LeftButton == MouseButtonState.Pressed &&
                    (Math.Abs(point.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                     Math.Abs(point.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance))
                    DragDrop.DoDragDrop(tab, new DataObject("rtaime.operator.panel", panel), DragDropEffects.Move);
            };
            tabs.Children.Add(tab);
        }
        root.Children.Add(tabs);
        var active = group.Panels.Contains(group.ActivePanel) ? group.ActivePanel : group.Panels.FirstOrDefault();
        if (group.Panels.Contains(_selected) && (_owner.Shell.IsCompactViewport || _focus is not null)) active = _selected;
        if (active is not null)
        {
            var content = Chrome(active, _panels[active]); Grid.SetRow(content, 1); root.Children.Add(content);
        }
        root.Drop += (_, e) =>
        {
            if (e.Data.GetData("rtaime.operator.panel") is string panel && _panels.ContainsKey(panel))
            { e.Handled = true; Dock(panel, group.Id); }
        };
        root.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent("rtaime.operator.panel")) return;
            e.Effects = DragDropEffects.Move; e.Handled = true;
        };
        AutomationProperties.SetName(root, $"Dock group {group.Id}");
        _host.Children.Add(root);
    }
    private Grid Chrome(string panel, FrameworkElement view)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new WrapPanel { Background = Brush("OperatorTopBarBrush", "OperatorBackgroundBrush") };
        var tally = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), Style = _owner.TryFindResource("OperatorLabel") as Style };
        var status = new MultiBinding { StringFormat = "PROGRAM {0} · {1} · {2}" };
        status.Bindings.Add(new Binding("ProgramSourceName") { Source = _owner.DataContext });
        status.Bindings.Add(new Binding("ConnectionState") { Source = _owner.DataContext });
        status.Bindings.Add(new Binding("ProgramSafety") { Source = _owner.DataContext });
        BindingOperations.SetBinding(tally, TextBlock.TextProperty, status);
        AutomationProperties.SetName(tally, "Current Program identity, connection and safety");
        AutomationProperties.SetLiveSetting(tally, AutomationLiveSetting.Polite);
        header.Children.Add(tally);
        header.Children.Add(Button("RESTORE", "Restore presentation arrangement (Escape).", () => _owner.Shell.RestorePresentation()));
        header.Children.Add(Button(panel, "Focus panel. Ctrl+Shift+F restores the previous arrangement.", () => { _selected = panel; FocusCommand.Execute(null); }));
        header.Children.Add(Button("FLOAT", "Detach panel to a secondary window. Ctrl+Shift+W.", () => Float(panel)));
        foreach (var group in OperatorDockLayout.GroupIds) header.Children.Add(Button(group, $"Dock {panel} to {group}; existing panels become tabs.", () => Dock(panel, group)));
        var presenter = new ContentControl { Content = view, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        presenter.PreviewGotKeyboardFocus += (_, _) => _selected = panel;
        _presenters[panel] = presenter;
        Grid.SetRow(presenter, 1); grid.Children.Add(header); grid.Children.Add(presenter);
        return grid;
    }
    private void AddSplitter(int column, bool vertical)
    {
        var splitter = new RtaimeSplitter
        {
            Width = vertical ? 6 : double.NaN, Height = vertical ? double.NaN : 6,
            HorizontalAlignment = vertical ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
            ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.CurrentAndNext,
            Background = Brush("OperatorBorderBrush", "OperatorBackgroundBrush"),
            ToolTip = "Resize dock split. Use arrow keys while focused."
        };
        Grid.SetColumn(splitter, column); Grid.SetRow(splitter, 0);
        if (!vertical) Grid.SetColumnSpan(splitter, 3);
        AutomationProperties.SetName(splitter, vertical ? $"Resize dock columns {column + 1}" : "Resize dock lower group");
        splitter.DragCompleted += (_, _) => SaveRatios();
        splitter.PreviewKeyUp += (_, _) => SaveRatios();
        Panel.SetZIndex(splitter, 2); _host.Children.Add(splitter);
    }
    private void SaveRatios()
    {
        var total = _host.ColumnDefinitions.Sum(c => c.ActualWidth);
        var height = _host.RowDefinitions.Sum(r => r.ActualHeight);
        if (total <= 0 || height <= 0) return;
        _owner.Shell.SetDocking(_owner.Shell.Docking with
        {
            LeftRatio = _host.ColumnDefinitions[0].ActualWidth / total,
            RightRatio = _host.ColumnDefinitions[2].ActualWidth / total,
            BottomRatio = _host.RowDefinitions[1].ActualHeight / height
        });
    }
    private Brush Brush(string key, string fallback) => (_owner.TryFindResource(key) ?? _owner.FindResource(fallback)) as Brush ?? Brushes.Black;
    private RtaimeButton Button(string text, string tooltip, Action action)
    {
        var button = new RtaimeButton { Content = text, IsCompact = true, Margin = new Thickness(2), ToolTip = tooltip };
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }
    private void FocusSelected()
    {
        _owner.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_floating.TryGetValue(_selected, out var window)) window.Activate();
            _panels[_selected].MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
    private void DisplayChanged(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _displayUpdate, 1) != 0) return;
        _owner.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _displayUpdate, 0);
            if (_disposed) return;
            if (_owner.WindowState == WindowState.Normal) OperatorMonitorGeometry.Restore(_owner, OperatorMonitorGeometry.Capture(_owner));
            foreach (var pair in _floating)
            {
                var placement = _owner.Shell.Docking.Floating.Single(p => p.Panel == pair.Key).Placement;
                OperatorMonitorGeometry.Restore(pair.Value, placement);
            }
            Capture();
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        Capture(); _disposed = true; _focusGeneration++; _presentationFocus = null;
        _owner.Shell.RestorePresentation();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        _owner.Shell.PropertyChanged -= Changed; _owner.SizeChanged -= Sized; _owner.Loaded -= Loaded;
        foreach (var presenter in _presenters.Values) presenter.Content = null;
        _presenters.Clear();
        foreach (var window in _floating.Values) { window.Content = null; window.Close(); }
        _floating.Clear(); _host.Children.Clear();
    }
}
