// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;

namespace rtaime.Operator;

// Typed connections to existing feature owners, not a registry or a lifetime owner.
public sealed class OperatorComposition
{
    internal OperatorComposition(MainWindow shell)
    {
        Library = new(shell.MediaPool, shell.MediaDeck, shell.Timeline, shell.WorkspaceStates,
            shell.NdiSources, shell.Shell.ToggleLeftPanelCommand);
        InspectorView = shell.InspectorView;
        Inspector = shell.MediaPool;
        Viewer = shell.Monitoring;
        Live = (OperatorViewModel)shell.DataContext;
        Timeline = shell.Timeline;
        Outputs = shell.OutputHealth;
        Health = shell.HealthCenter;
        Ai = Live;
    }
    public OperatorMediaLibraryContext Library { get; }
    public OperatorInspectorControl InspectorView { get; }
    public MediaPoolInspectorViewModel Inspector { get; }
    public MediaSelectionContext Selection => Inspector.Selection;
    public OperatorMonitoringViewModel Viewer { get; }
    public OperatorViewModel Live { get; }
    public MediaTimelineViewModel Timeline { get; }
    public OutputRoutingHealthViewModel Outputs { get; }
    public HealthCenterViewModel Health { get; }
    // Existing governed AI properties/commands; an independent workspace comes later.
    public OperatorViewModel Ai { get; }
}
