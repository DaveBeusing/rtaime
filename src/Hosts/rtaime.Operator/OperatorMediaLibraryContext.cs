// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;

namespace rtaime.Operator;

// Borrowed references only: MainWindow remains the single lifetime owner.
public sealed record OperatorMediaLibraryContext(
    MediaPoolInspectorViewModel MediaPool,
    MediaDeckViewModel MediaDeck,
    MediaTimelineViewModel Timeline,
    OperatorWorkspaceStateViewModel WorkspaceStates,
    NdiSourceDiscoveryViewModel NdiSources,
    ICommand CollapseCommand);
