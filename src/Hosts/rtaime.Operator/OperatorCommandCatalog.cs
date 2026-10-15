// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public static class OperatorCommandCatalog
{
	private static readonly HashSet<string> Presentation = new(StringComparer.Ordinal)
	{ "command-palette", "media-search", "fullscreen", "exit-fullscreen", "preview-view", "program-view", "dual-view" };
	private static readonly HashSet<string> Transport = new(StringComparer.Ordinal)
	{ "play-pause", "j-step", "l-play", "pause", "stop", "set-in", "set-out", "add-cue", "delete-cue", "previous-cue", "next-cue" };

	public static IReadOnlyList<OperatorPaletteCommand> Create(OperatorKeyboardCommandRegistry registry,
		OperatorViewModel owner, MediaDeckViewModel deck, OperatorShellViewModel shell, MediaPoolInspectorViewModel pool,
		ICommand? openShowChecks = null, ICommand? openLibrary = null, ICommand? openHelp = null)
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
		foreach (var definition in registry.CanonicalDefinitions.Where(item => item.Id != "command-palette"))
		{
			var local = Presentation.Contains(definition.Id) || definition.Id.StartsWith("dock-", StringComparison.Ordinal);
			commands.Add(new(definition.Id, definition.Label,
				local ? "Navigation" : definition.Id == "sync" ? "Connection" : "Production",
				definition.Command, RequiresExplicitButton: !local && definition.Id != "sync",
				UnavailableReason: local || definition.Id == "sync" ? null : Transport.Contains(definition.Id) ? PreviewReason : ProductionReason));
		}
		foreach (var workspace in OperatorWorkspaceNames.All)
			commands.Add(new("workspace-" + workspace.ToLowerInvariant(), "Workspace " + workspace, "Workspaces", shell.SelectWorkspaceCommand, workspace));
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
        return commands;
	}
}
