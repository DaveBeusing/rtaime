// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

// A menu link identifies an existing catalogue command, not a second command owner.
// Null IDs are visual group separators. The special read-only show identity and
// existing Demo/Scopes owners are resolved by the MainWindow composition.
public sealed record OperatorShellMenuSlot(string? CommandId, string? Label = null);
public sealed record OperatorShellMenuSection(string Name, IReadOnlyList<OperatorShellMenuSlot> Slots);

public static class OperatorCommandCatalog
{
    public static IReadOnlyList<OperatorShellMenuSection> GlobalShellMenus { get; } =
    [
        new("Project",
        [
            new("show-identity"),
            new("show-checks"),
            new("recent-favorites"),
            new(null),
            new("demo-open"),
            new(null),
            new("sync")
        ]),
        new("Edit",
        [
            new("timeline-undo"),
            new("timeline-redo"),
            new(null),
            new("inspector-copy"),
            new("inspector-paste")
        ]),
        new("View",
        [
            new("pane-library"),
            new("pane-inspector"),
            new("view-scopes"),
            new(null),
            new("preview-view"),
            new("program-view"),
            new("dual-view"),
            new(null),
            new("workspace-live"),
            new("workspace-media"),
            new("workspace-edit"),
            new("workspace-outputs")
        ]),
        new("Media",
        [
            new("workspace-media"),
            new("media-search"),
            new(null),
            new("media-import"),
            new("media-refresh"),
            new("media-clear-selection"),
            new(null),
            new("media-grid"),
            new("media-list"),
            new("media-filters"),
            new("media-save-bin"),
            new("media-delete-bin")
        ]),
        new("Tools",
        [
            new("workspace-scenes"),
            new("workspace-multiview"),
            new("workspace-compositing"),
            new("workspace-outputs"),
            new(null),
            new("workspace-ai"),
            new("workspace-health"),
            new("workspace-settings")
        ]),
        new("Window",
        [
            new("dock-toggle"),
            new("dock-float"),
            new("dock-focus"),
            new("dock-cinema"),
            new("dock-next"),
            new(null),
            new("workspace-restore"),
            new("workspace-templates"),
            new(null),
            new("fullscreen"),
            new("exit-fullscreen")
        ]),
        new("Help",
        [
            new("inline-help"),
            new("command-palette"),
            new(null),
            new("show-checks"),
            new("recent-favorites")
        ])
    ];

	private static readonly HashSet<string> Presentation = new(StringComparer.Ordinal)
	{ "command-palette", "media-search", "fullscreen", "exit-fullscreen", "preview-view", "program-view", "dual-view" };
	private static readonly HashSet<string> Transport = new(StringComparer.Ordinal)
	{ "play-pause", "j-step", "l-play", "pause", "stop", "set-in", "set-out", "add-cue", "delete-cue", "previous-cue", "next-cue" };

	public static IReadOnlyList<OperatorPaletteCommand> Create(OperatorKeyboardCommandRegistry registry,
		OperatorViewModel owner, MediaDeckViewModel deck, OperatorShellViewModel shell, MediaPoolInspectorViewModel pool,
		ICommand? openShowChecks = null, ICommand? openLibrary = null, ICommand? openHelp = null,
        ICommand? openWorkspaceTemplates = null, SequenceOperatorPlayoutViewModel? nativeSequence = null)
	{
		string? ProductionReason()
		{
			if (!owner.IsConnected) return "Disconnected: synchronize authoritative state.";
			if (owner.IsStale) return "Stale state: synchronize before production actions.";
			if (owner.IsBusy) return "A command is pending authoritative confirmation.";
			if (owner.ProgramSafety == OperatorProgramSafetyStates.Blocked) return "Program safety is blocked.";
			if (!string.Equals(owner.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase)) return "Runtime is not ready.";
			return null;
		}
		string? PreviewReason()
		{
			var reason = ProductionReason();
			if (reason is not null) return reason;
			if (!deck.IsLoaded) return "Open media in Preview first.";
			if (owner.PreviewSourceId != deck.SourceId) return "Loaded media is not the confirmed Preview source.";
			return null;
		}
		var commands = new List<OperatorPaletteCommand>();
		foreach (var definition in registry.CanonicalDefinitions)
		{
			var local = Presentation.Contains(definition.Id) || definition.Id.StartsWith("dock-", StringComparison.Ordinal);
			commands.Add(new(definition.Id, definition.Label,
				local ? "Navigation" : definition.Id == "sync" ? "Connection" : "Production",
				definition.Command, RequiresExplicitButton: !local && definition.Id != "sync",
				UnavailableReason: local || definition.Id == "sync" ? null : Transport.Contains(definition.Id) ? PreviewReason : ProductionReason));
		}
		commands.Add(new("pane-library", "Show or hide Library pane", "Navigation", shell.ToggleLeftPanelCommand));
        commands.Add(new("pane-inspector", "Show or hide Inspector pane", "Navigation", shell.ToggleRightPanelCommand));
        commands.Add(new("workspace-restore", "Restore current workspace defaults", "Navigation", shell.ResetLayoutCommand));
        foreach (var destination in OperatorWorkspaceRegistry.Destinations)
        {
            // The EDIT ID remains stable; Timeline is the operator-facing label.
            var label = destination.Id == OperatorWorkspaceNames.Edit
                ? destination.Label + " (EDIT)" : destination.Label;
            commands.Add(new("workspace-" + destination.Id.ToLowerInvariant(),
                "Workspace " + label, "Workspaces", shell.SelectWorkspaceCommand, destination.Id));
        }
		void Add(string id, string label, ICommand command, bool management = false) =>
			commands.Add(new(id, label, "Media Library", command, RequiresExplicitButton: management,
				UnavailableReason: management ? () => !owner.IsConnected ? "Disconnected: catalogue action unavailable." :
					owner.IsStale ? "Stale state: synchronize before catalogue actions." : owner.IsBusy ? "A command is pending." : null : null));
		Add("media-grid", "Media Library grid view", pool.GridViewCommand);
		Add("media-list", "Media Library list view", pool.ListViewCommand);
		Add("media-filters", "Show duration and readiness filters", pool.ToggleMetadataFiltersCommand);
		Add("media-clear-selection", "Clear Media Library selection", pool.ClearLibrarySelectionCommand);
		Add("media-refresh", "Refresh catalogue availability", pool.RefreshCatalogCommand, true);
		Add("media-import", "Import media into catalogue", pool.ImportCommand, true);
		Add("media-save-bin", "Save current Smart Bin", pool.SaveSmartBinCommand);
		commands.Add(new("media-delete-bin", "Delete selected Smart Bin", "Media Library", pool.DeleteSmartBinCommand, RequiresExplicitButton: true));
		commands.Add(new("timeline-undo", "Undo local timeline draft", "Draft editing", deck.Timeline.UndoDraftCommand));
		commands.Add(new("timeline-redo", "Redo local timeline draft", "Draft editing", deck.Timeline.RedoDraftCommand));
		commands.Add(new("inspector-copy", "Copy compatible Inspector attributes", "Draft editing", pool.CopyAttributesCommand));
		commands.Add(new("inspector-paste", "Paste compatible Inspector attribute draft", "Draft editing", pool.PasteAttributesCommand));
		if (openShowChecks is not null) commands.Add(new("show-checks", "Show preflight, action history and notifications", "Navigation", openShowChecks));
		if (openLibrary is not null) commands.Add(new("recent-favorites", "Recent items and favorites", "Navigation", openLibrary));
        if (openHelp is not null) commands.Add(new("inline-help", "Contextual help and searchable shortcut hints", "Help", openHelp));
        if (openWorkspaceTemplates is not null) commands.Add(new("workspace-templates", "Named workspace layout templates and portable import/export", "Workspaces", openWorkspaceTemplates));
        if (nativeSequence is not null)
        {
            void Native(string id, string label, ICommand command, bool confirmation = false) =>
                commands.Add(new(id, label, "Sequence playout", command, RequiresExplicitButton: confirmation));
            Native("sequence-prepare", "Prepare saved native sequence", nativeSequence.PrepareCommand, true);
            Native("sequence-preview", "Activate native sequence in confirmed Preview", nativeSequence.ActivatePreviewCommand, true);
            Native("sequence-observe", "Observe actual sequence Preview / Program output", nativeSequence.ObserveCommand);
            Native("sequence-cue", "Cue native shuttle to local edit cursor", nativeSequence.CueCommand, true);
            Native("sequence-play", "Play native sequence shuttle", nativeSequence.PlayCommand, true);
            Native("sequence-pause", "Pause native sequence shuttle", nativeSequence.PauseCommand);
            Native("sequence-stop", "Stop native sequence shuttle", nativeSequence.StopCommand);
            Native("sequence-arm", "Arm confirmed sequence Take", nativeSequence.ArmCommand, true);
            Native("sequence-take", "Take armed native sequence to Program", nativeSequence.TakeCommand, true);
            Native("sequence-recover", "Recover exact ambiguous sequence Take", nativeSequence.RecoverCommand, true);
            Native("sequence-release", "Release prepared native sequence", nativeSequence.CancelCommand, true);
        }
        return commands;
	}
}
