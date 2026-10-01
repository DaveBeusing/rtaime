// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Text.Json;
using rtaime.Core;

namespace rtaime.Control.Contracts;

public static class ProductionMacroContractVersion
{
	public static CompatibilityVersion Current { get; } = new(1, 0);

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (version != Current)
			throw new NotSupportedException($"Unsupported production-macro contract version '{version}'. Supported version is '{Current}'.");
	}
}

public readonly record struct ProductionMacroId
{
	public ProductionMacroId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Production Macro identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ProductionMacroId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct ProductionMacroActionId
{
	public ProductionMacroActionId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Production Macro action identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ProductionMacroActionId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct ProductionMacroExecutionId
{
	public ProductionMacroExecutionId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Production Macro execution identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ProductionMacroExecutionId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public sealed record ProductionMacroAction
{
	public ProductionMacroAction(ProductionMacroActionId actionId, ShowControlAction command)
	{
		ArgumentNullException.ThrowIfNull(command);
		if (command.ActionId.Value != actionId.Value)
			throw new ArgumentException("Macro action identity must match the governed action identity.", nameof(command));
		ActionId = actionId;
		Command = command;
	}

	public ProductionMacroAction(
		ProductionMacroActionId actionId,
		ShowControlActionKind kind,
		string? sceneId = null,
		string? sourceId = null,
		uint? durationFrames = null,
		string? mediaAssetId = null,
		string? mediaCuePointId = null,
		string? layerId = null,
		bool? visible = null,
		string? recordingDestinationDirectory = null,
		string? recordingFileName = null,
		uint? waitFrames = null,
		int? audioRoutingMode = null,
		string? outputRoleId = null)
		: this(
			actionId,
			new ShowControlAction(
				new ShowControlActionId(actionId.Value),
				kind,
				sceneId,
				sourceId,
				durationFrames,
				mediaAssetId,
				mediaCuePointId,
				layerId,
				visible,
				recordingDestinationDirectory,
				recordingFileName,
				waitFrames,
				audioRoutingMode,
				outputRoleId))
	{
	}

	public ProductionMacroActionId ActionId { get; }
	public ShowControlAction Command { get; }
	public ShowControlActionKind Kind => Command.Kind;
}

public sealed record ProductionMacroDefinition
{
	public const int MaximumNameLength = 128;
	public const int MaximumDescriptionLength = 512;
	public const int MaximumActions = ShowControlCue.MaximumActions;
	private readonly ReadOnlyCollection<ProductionMacroAction> _actions;

	public ProductionMacroDefinition(
		CompatibilityVersion version,
		ProductionMacroId macroId,
		string name,
		IReadOnlyList<ProductionMacroAction> actions,
		string? description = null)
	{
		ProductionMacroContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Production Macro name is required.", nameof(name));
		var normalizedName = name.Trim();
		if (normalizedName.Length > MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name), $"Production Macro names are limited to {MaximumNameLength} characters.");
		var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
		if (normalizedDescription is { Length: > MaximumDescriptionLength })
			throw new ArgumentOutOfRangeException(nameof(description), $"Production Macro descriptions are limited to {MaximumDescriptionLength} characters.");
		ArgumentNullException.ThrowIfNull(actions);
		if (actions.Count is 0 or > MaximumActions)
			throw new ArgumentOutOfRangeException(nameof(actions), $"Production Macros must contain between 1 and {MaximumActions} actions.");
		if (actions.Any(action => action is null))
			throw new ArgumentException("Production Macro actions must not contain null values.", nameof(actions));
		if (actions.Select(action => action.ActionId).Distinct().Count() != actions.Count)
			throw new ArgumentException("Production Macro action identities must be unique.", nameof(actions));

		Version = version;
		MacroId = macroId;
		Name = normalizedName;
		Description = normalizedDescription;
		_actions = Array.AsReadOnly(actions.ToArray());
	}

	public CompatibilityVersion Version { get; }
	public ProductionMacroId MacroId { get; }
	public string Name { get; }
	public string? Description { get; }
	public IReadOnlyList<ProductionMacroAction> Actions => _actions;

	public ProductionMacroDefinition Reorder(IReadOnlyList<ProductionMacroActionId> orderedActionIds)
	{
		ArgumentNullException.ThrowIfNull(orderedActionIds);
		if (orderedActionIds.Count != _actions.Count || orderedActionIds.Distinct().Count() != _actions.Count)
			throw new ArgumentException("Macro action reorder must contain every action identity exactly once.", nameof(orderedActionIds));
		var byId = _actions.ToDictionary(action => action.ActionId);
		if (orderedActionIds.Any(id => !byId.ContainsKey(id)))
			throw new ArgumentException("Macro action reorder contains an unknown action identity.", nameof(orderedActionIds));
		return new ProductionMacroDefinition(Version, MacroId, Name, orderedActionIds.Select(id => byId[id]).ToArray(), Description);
	}
}

public enum ProductionMacroExecutionState
{
	Idle = 1,
	Armed = 2,
	Executing = 3,
	Waiting = 4,
	Completed = 5,
	Failed = 6,
	Cancelled = 7,
	RecoveryRequired = 8
}

public sealed record ProductionMacroExecutionSnapshot(
	ProductionMacroExecutionState State,
	ProductionMacroExecutionId? ExecutionId,
	ProductionMacroId? MacroId,
	int? ActionIndex,
	ProductionMacroActionId? CurrentActionId,
	ProductionMacroActionId? LastCompletedActionId,
	ulong Revision,
	ulong? WaitTargetFrameSequence,
	string? RuntimeHostInstanceId,
	bool RequiresAcknowledgement,
	Failure? Failure)
{
	public static ProductionMacroExecutionSnapshot Idle { get; } = new(
		ProductionMacroExecutionState.Idle,
		null,
		null,
		null,
		null,
		null,
		0,
		null,
		null,
		false,
		null);
}

public sealed record ProductionMacroWorkspaceSnapshot(
	IReadOnlyList<ProductionMacroDefinition> Macros,
	ulong StorageVersion,
	ProductionMacroExecutionSnapshot Execution);

public sealed record ProductionMacroValidationIssue(
	string Code,
	string Message,
	ProductionMacroActionId? ActionId = null);

public sealed record ProductionMacroValidationResult(
	bool IsValid,
	IReadOnlyList<ProductionMacroValidationIssue> Issues)
{
	public static ProductionMacroValidationResult Valid { get; } =
		new(true, Array.Empty<ProductionMacroValidationIssue>());
}

public static class ProductionMacroCanonicalSerializer
{
	public const int MaximumMacros = 128;
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	public static string Serialize(IReadOnlyList<ProductionMacroDefinition> macros)
	{
		ArgumentNullException.ThrowIfNull(macros);
		ValidateMacroSet(macros);
		return JsonSerializer.Serialize(
			new LibraryDocument(
				ProductionMacroContractVersion.Current.ToString(),
				macros.Select(ToDocument).ToArray()),
			Options);
	}

	public static IReadOnlyList<ProductionMacroDefinition> Deserialize(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			throw new ArgumentException("Production Macro JSON is required.", nameof(json));
		var document = JsonSerializer.Deserialize<LibraryDocument>(json, Options)
			?? throw new InvalidDataException("Production Macro JSON did not contain a library.");
		var version = CompatibilityVersion.Parse(document.Version);
		ProductionMacroContractVersion.EnsureSupported(version);
		if (document.Macros is null)
			throw new InvalidDataException("Production Macro JSON requires a macro collection.");
		var macros = document.Macros.Select(FromDocument).ToArray();
		ValidateMacroSet(macros);
		return macros;
	}

	private static void ValidateMacroSet(IReadOnlyList<ProductionMacroDefinition> macros)
	{
		if (macros.Count > MaximumMacros)
			throw new ArgumentOutOfRangeException(nameof(macros), $"Production Macro libraries are limited to {MaximumMacros} definitions.");
		if (macros.Any(macro => macro is null))
			throw new ArgumentException("Production Macro libraries must not contain null values.", nameof(macros));
		if (macros.Select(macro => macro.MacroId).Distinct().Count() != macros.Count)
			throw new ArgumentException("Production Macro identities must be unique.", nameof(macros));
	}

	private static MacroDocument ToDocument(ProductionMacroDefinition macro) =>
		new(
			macro.Version.ToString(),
			macro.MacroId.ToString(),
			macro.Name,
			macro.Description,
			macro.Actions.Select(action => ToDocument(action.Command)).ToArray());

	private static ProductionMacroDefinition FromDocument(MacroDocument document)
	{
		var version = CompatibilityVersion.Parse(document.Version);
		ProductionMacroContractVersion.EnsureSupported(version);
		if (document.Actions is null)
			throw new InvalidDataException("Production Macro JSON requires an action collection.");
		return new ProductionMacroDefinition(
			version,
			new ProductionMacroId(Identity.Parse(document.MacroId)),
			document.Name,
			document.Actions.Select(FromDocument).ToArray(),
			document.Description);
	}

	private static ActionDocument ToDocument(ShowControlAction action) =>
		new(
			action.ActionId.ToString(),
			action.Kind.ToString(),
			action.SceneId,
			action.SourceId,
			action.DurationFrames,
			action.MediaAssetId,
			action.MediaCuePointId,
			action.LayerId,
			action.Visible,
			action.RecordingDestinationDirectory,
			action.RecordingFileName,
			action.WaitFrames,
			action.AudioRoutingMode,
			action.OutputRoleId);

	private static ProductionMacroAction FromDocument(ActionDocument document)
	{
		if (!Enum.TryParse<ShowControlActionKind>(document.Kind, false, out var kind) || !Enum.IsDefined(kind))
			throw new InvalidDataException($"Production Macro action kind '{document.Kind}' is unsupported.");
		var actionId = new ProductionMacroActionId(Identity.Parse(document.ActionId));
		return new ProductionMacroAction(
			actionId,
			kind,
			document.SceneId,
			document.SourceId,
			document.DurationFrames,
			document.MediaAssetId,
			document.MediaCuePointId,
			document.LayerId,
			document.Visible,
			document.RecordingDestinationDirectory,
			document.RecordingFileName,
			document.WaitFrames,
			document.AudioRoutingMode,
			document.OutputRoleId);
	}

	private sealed record LibraryDocument(string Version, MacroDocument[] Macros);
	private sealed record MacroDocument(string Version, string MacroId, string Name, string? Description, ActionDocument[] Actions);
	private sealed record ActionDocument(
		string ActionId,
		string Kind,
		string? SceneId,
		string? SourceId,
		uint? DurationFrames,
		string? MediaAssetId,
		string? MediaCuePointId,
		string? LayerId,
		bool? Visible,
		string? RecordingDestinationDirectory,
		string? RecordingFileName,
		uint? WaitFrames,
		int? AudioRoutingMode = null,
		string? OutputRoleId = null);
}

public static class ProductionMacroExecutionSerializer
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	public static string Serialize(ProductionMacroExecutionSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		return JsonSerializer.Serialize(
			new ExecutionDocument(
				(int)snapshot.State,
				snapshot.ExecutionId?.ToString(),
				snapshot.MacroId?.ToString(),
				snapshot.ActionIndex,
				snapshot.CurrentActionId?.ToString(),
				snapshot.LastCompletedActionId?.ToString(),
				snapshot.Revision,
				snapshot.WaitTargetFrameSequence,
				snapshot.RuntimeHostInstanceId,
				snapshot.RequiresAcknowledgement,
				snapshot.Failure?.Code,
				snapshot.Failure?.Message),
			Options);
	}

	public static ProductionMacroExecutionSnapshot Deserialize(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			throw new ArgumentException("Production Macro execution JSON is required.", nameof(json));
		var document = JsonSerializer.Deserialize<ExecutionDocument>(json, Options)
			?? throw new InvalidDataException("Production Macro execution JSON did not contain a snapshot.");
		if (!Enum.IsDefined(typeof(ProductionMacroExecutionState), document.State))
			throw new InvalidDataException($"Unsupported Production Macro execution state '{document.State}'.");
		return new ProductionMacroExecutionSnapshot(
			(ProductionMacroExecutionState)document.State,
			string.IsNullOrWhiteSpace(document.ExecutionId) ? null : new ProductionMacroExecutionId(Identity.Parse(document.ExecutionId)),
			string.IsNullOrWhiteSpace(document.MacroId) ? null : new ProductionMacroId(Identity.Parse(document.MacroId)),
			document.ActionIndex,
			string.IsNullOrWhiteSpace(document.CurrentActionId) ? null : new ProductionMacroActionId(Identity.Parse(document.CurrentActionId)),
			string.IsNullOrWhiteSpace(document.LastCompletedActionId) ? null : new ProductionMacroActionId(Identity.Parse(document.LastCompletedActionId)),
			document.Revision,
			document.WaitTargetFrameSequence,
			document.RuntimeHostInstanceId,
			document.RequiresAcknowledgement,
			string.IsNullOrWhiteSpace(document.FailureCode)
				? null
				: new Failure(document.FailureCode, document.FailureMessage ?? "Production Macro execution failed."));
	}

	private sealed record ExecutionDocument(
		int State,
		string? ExecutionId,
		string? MacroId,
		int? ActionIndex,
		string? CurrentActionId,
		string? LastCompletedActionId,
		ulong Revision,
		ulong? WaitTargetFrameSequence,
		string? RuntimeHostInstanceId,
		bool RequiresAcknowledgement,
		string? FailureCode,
		string? FailureMessage);
}
