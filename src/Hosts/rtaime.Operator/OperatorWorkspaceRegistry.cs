// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public sealed record OperatorWorkspaceDestination(string Id, string Label, string Group, string IconKey, string ViewerMode = "DUAL");

// Add a destination only with a productive view connection; IDs also feed templates and the command catalogue.
public static class OperatorWorkspaceRegistry
{
    public const int MaximumRetainedLayouts = 32;
    public static IReadOnlyList<OperatorWorkspaceDestination> Destinations { get; } =
    [
        new("LIVE", "Live", "Live", "RtaimeIconLiveGeometry"),
        new("SCENES", "Scenes", "Live", "RtaimeIconMediaGeometry"),
        new("MULTIVIEW", "Multiview", "Live", "RtaimeIconGridGeometry"),
        new("MEDIA", "Media", "Media & Timeline", "RtaimeIconMediaGeometry", "PREVIEW"),
        new("EDIT", "Edit", "Media & Timeline", "RtaimeIconTimelineGeometry"),
        new("COMPOSITING", "Comp", "Media & Timeline", "RtaimeIconCompositingGeometry"),
        new("OUTPUTS", "Outputs", "Outputs & Mapping", "RtaimeIconOutputGeometry", "PROGRAM"),
        new("HEALTH", "Health", "System & Health", "RtaimeIconHealthGeometry", "PROGRAM"),
        new("AI", "AI", "System & Health", "RtaimeIconSettingsGeometry", "PROGRAM"),
        new("SETTINGS", "Settings", "System & Health", "RtaimeIconSettingsGeometry", "PROGRAM")
    ];
    public static bool IsPersistableId(string id) => id.Length is > 0 and <= 64 &&
        id.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
}
public sealed record OperatorWorkspaceGroup(string Label, IReadOnlyList<OperatorWorkspaceLink> Items);
public sealed class OperatorWorkspaceLink : INotifyPropertyChanged
{
    private readonly OperatorShellViewModel _shell;
    public OperatorWorkspaceLink(OperatorShellViewModel shell, OperatorWorkspaceDestination destination)
    { _shell = shell; Destination = destination; }
    public OperatorWorkspaceDestination Destination { get; }
    public string Id => Destination.Id;
    public string Label => Destination.Label;
    public string Description => Destination.Group + " · " + Destination.Label;
    public Geometry Icon => (Geometry)Application.Current.FindResource(Destination.IconKey);
    public ICommand Command => _shell.SelectWorkspaceCommand;
    public bool IsActive => _shell.SelectedWorkspace == Id;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(IsActive)));
}
public sealed partial class OperatorShellViewModel
{
    private string? _compactPane;
    private string _compactBus = "PROGRAM";
    private bool _compactAuxiliary;
    public IReadOnlyList<OperatorWorkspaceGroup> NavigationGroups { get; private set; } = [];
    public bool IsAiWorkspace => SelectedWorkspace == OperatorWorkspaceNames.Ai;
    public bool IsMultiviewWorkspace => SelectedWorkspace == OperatorWorkspaceNames.Multiview;
    public string LayoutPersistenceStatus => _store.LastSaveError ?? _store.LoadWarning ?? "Layout preferences available.";
    public string EffectiveViewerMode => IsCompactViewport && ViewerMode == "DUAL" ? _compactBus : ViewerMode;
    public GridLength PrimaryWorkspaceWidth => IsAiWorkspace || (IsCompactViewport && HasAuxiliaryWorkspaceColumn && _compactAuxiliary) ? new(0) : new(2.2, GridUnitType.Star);
    public Visibility AuxiliaryNavigationVisibility => IsCompactViewport && HasAuxiliaryWorkspaceColumn && !IsAiWorkspace ? Visibility.Visible : Visibility.Collapsed;
    public ICommand SelectCompactAuxiliaryCommand { get; private set; } = null!;
    public GridLength ProductionDetailsHeight => IsLiveWorkspace ? new(0) : new(304, GridUnitType.Star);
    public double ProductionDetailsGap => IsLiveWorkspace ? 0 : 6;
    public Visibility ProductionDetailsVisibility => IsLiveWorkspace ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LiveActionVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
    public GridLength RundownColumnWidth => new(IsCompactViewport ? 280 : 320);
    public ICommand SelectCompactBusCommand { get; private set; } = null!;
    private void InitializeNavigation()
    {
        NavigationGroups = OperatorWorkspaceRegistry.Destinations.GroupBy(d => d.Group)
            .Select(g => new OperatorWorkspaceGroup(g.Key, g.Select(d => new OperatorWorkspaceLink(this, d)).ToArray())).ToArray();
        SelectCompactAuxiliaryCommand = new OperatorShellCommand(p =>
        {
            _compactAuxiliary = p?.ToString() == "DETAILS";
            RaiseAdaptivePresentationChanged();
        }, () => IsCompactViewport && HasAuxiliaryWorkspaceColumn && !IsAiWorkspace);
        SelectCompactBusCommand = new OperatorShellCommand(p =>
        {
            _compactBus = p?.ToString() == "PREVIEW" ? "PREVIEW" : "PROGRAM";
            RaiseAdaptivePresentationChanged();
        });
    }
    private double CompactPaneWidth(string pane) => _compactPane == pane ? Math.Min(320, _viewportWidth * .4) : 0;
    private void ToggleCompactPane(string pane)
    {
        _compactPane = _compactPane == pane ? null : pane;
        RaiseLayoutGeometryChanged();
    }
    private void RaiseAdaptivePresentationChanged()
    {
        (SelectCompactAuxiliaryCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
        foreach (var property in new[] { nameof(IsAiWorkspace), nameof(IsMultiviewWorkspace),
            nameof(EffectiveViewerMode), nameof(ViewerModeLabel), nameof(PreviewViewerWidth), nameof(ProgramViewerWidth),
            nameof(PreviewViewerVisibility), nameof(ProgramViewerVisibility), nameof(ViewerGapWidth),
            nameof(ProductionDetailsHeight), nameof(ProductionDetailsGap), nameof(ProductionDetailsVisibility),
            nameof(AuxiliaryWorkspaceColumnWidth), nameof(AuxiliaryNavigationVisibility), nameof(PrimaryWorkspaceWidth), nameof(LiveActionVisibility), nameof(RundownColumnWidth), nameof(LayoutPersistenceStatus) })
            OnPropertyChanged(property);
    }
}
