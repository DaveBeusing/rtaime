// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Text.Json;
using rtaime.Core;

namespace rtaime.Control.Contracts;

public static class RundownContractVersion
{
	public static CompatibilityVersion Current { get; } = new(1, 0);

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (version != Current)
			throw new NotSupportedException($"Unsupported rundown contract version '{version}'. Supported version is '{Current}'.");
	}
}

public readonly record struct RundownId
{
	public RundownId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Rundown identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static RundownId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct RundownItemId
{
	public RundownItemId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Rundown item identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static RundownItemId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public enum RundownItemKind
{
	MediaClip = 1,
	Scene = 2,
	Graphics = 3,
	AudioRouting = 4,
	Hold = 5
}

public enum RundownTransitionKind
{
	Cut = 1,
	Dissolve = 2
}

public sealed record RundownTransition
{
	public const uint MaximumDissolveFrames = ShowControlAction.MaximumDissolveFrames;

	public RundownTransition(RundownTransitionKind kind, uint? durationFrames = null)
	{
		if (!Enum.IsDefined(kind))
			throw new ArgumentOutOfRangeException(nameof(kind));
		if (kind == RundownTransitionKind.Cut && durationFrames is not null)
			throw new ArgumentException("CUT transition must not declare a duration.", nameof(durationFrames));
		if (kind == RundownTransitionKind.Dissolve && durationFrames is < 2 or > MaximumDissolveFrames)
			throw new ArgumentOutOfRangeException(nameof(durationFrames), $"DISSOLVE duration must be between 2 and {MaximumDissolveFrames} frames.");

		Kind = kind;
		DurationFrames = durationFrames;
	}

	public RundownTransitionKind Kind { get; }
	public uint? DurationFrames { get; }

	public static RundownTransition Cut { get; } = new(RundownTransitionKind.Cut);
	public static RundownTransition Dissolve(uint durationFrames) => new(RundownTransitionKind.Dissolve, durationFrames);
}

public enum RundownAdvanceMode
{
	Manual = 1,
	AutoOnMediaEnd = 2
}

public enum RundownRepeatMode
{
	None = 1,
	RepeatItem = 2,
	RepeatRundown = 3
}

public sealed record RundownRepeatPolicy
{
	public const ushort MaximumRepeatCount = 100;

	public RundownRepeatPolicy(RundownRepeatMode mode, ushort repeatCount = 0)
	{
		if (!Enum.IsDefined(mode))
			throw new ArgumentOutOfRangeException(nameof(mode));
		if (mode == RundownRepeatMode.None && repeatCount != 0)
			throw new ArgumentException("Non-repeating policy must use repeat count zero.", nameof(repeatCount));
		if (mode != RundownRepeatMode.None && repeatCount is 0 or > MaximumRepeatCount)
			throw new ArgumentOutOfRangeException(nameof(repeatCount), $"Repeat count must be between 1 and {MaximumRepeatCount}.");

		Mode = mode;
		RepeatCount = repeatCount;
	}

	public RundownRepeatMode Mode { get; }
	public ushort RepeatCount { get; }

	public static RundownRepeatPolicy None { get; } = new(RundownRepeatMode.None);
}

public abstract record RundownItem
{
	public const int MaximumNameLength = 128;

	protected RundownItem(
		RundownItemId itemId,
		string name,
		RundownItemKind kind,
		RundownTransition? transition,
		RundownAdvanceMode advanceMode,
		RundownRepeatPolicy? repeat)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Rundown item name is required.", nameof(name));
		var normalized = name.Trim();
		if (normalized.Length > MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name), $"Rundown item names are limited to {MaximumNameLength} characters.");
		if (!Enum.IsDefined(kind))
			throw new ArgumentOutOfRangeException(nameof(kind));
		if (!Enum.IsDefined(advanceMode))
			throw new ArgumentOutOfRangeException(nameof(advanceMode));

		ItemId = itemId;
		Name = normalized;
		Kind = kind;
		Transition = transition;
		AdvanceMode = advanceMode;
		Repeat = repeat ?? RundownRepeatPolicy.None;
	}

	public RundownItemId ItemId { get; }
	public string Name { get; }
	public RundownItemKind Kind { get; }
	public RundownTransition? Transition { get; }
	public RundownAdvanceMode AdvanceMode { get; }
	public RundownRepeatPolicy Repeat { get; }
}

public sealed record RundownMediaItem : RundownItem
{
	public RundownMediaItem(
		RundownItemId itemId,
		string name,
		Identity assetId,
		ProductionSourceId sourceId,
		RundownTransition transition,
		RundownAdvanceMode advanceMode = RundownAdvanceMode.Manual,
		RundownRepeatPolicy? repeat = null)
		: base(itemId, name, RundownItemKind.MediaClip, transition ?? throw new ArgumentNullException(nameof(transition)), advanceMode, repeat)
	{
		if (assetId.IsEmpty)
			throw new ArgumentException("Persistent media asset identity must not be empty.", nameof(assetId));
		AssetId = assetId;
		SourceId = sourceId;
	}

	public Identity AssetId { get; }
	public ProductionSourceId SourceId { get; }
}

public sealed record RundownSceneItem : RundownItem
{
	public RundownSceneItem(
		RundownItemId itemId,
		string name,
		SceneId sceneId,
		RundownAdvanceMode advanceMode = RundownAdvanceMode.Manual,
		RundownRepeatPolicy? repeat = null)
		: base(itemId, name, RundownItemKind.Scene, null, advanceMode, repeat)
	{
		if (advanceMode == RundownAdvanceMode.AutoOnMediaEnd)
			throw new ArgumentException("Scene items cannot auto-advance on media completion.", nameof(advanceMode));
		SceneId = sceneId;
	}

	public SceneId SceneId { get; }
}

public sealed record RundownGraphicsItem : RundownItem
{
	public const int MaximumLayerIdLength = 128;

	public RundownGraphicsItem(
		RundownItemId itemId,
		string name,
		string layerId,
		bool visible,
		RundownRepeatPolicy? repeat = null)
		: base(itemId, name, RundownItemKind.Graphics, null, RundownAdvanceMode.Manual, repeat)
	{
		if (string.IsNullOrWhiteSpace(layerId))
			throw new ArgumentException("Graphics layer identity is required.", nameof(layerId));
		var normalized = layerId.Trim();
		if (normalized.Length > MaximumLayerIdLength)
			throw new ArgumentOutOfRangeException(nameof(layerId));
		LayerId = normalized;
		Visible = visible;
	}

	public string LayerId { get; }
	public bool Visible { get; }
}

public sealed record RundownAudioRoutingItem : RundownItem
{
	public const int FollowVideoMode = 1;
	public const int BreakawayMode = 2;

	public RundownAudioRoutingItem(
		RundownItemId itemId,
		string name,
		int routingMode,
		ProductionSourceId? breakawaySourceId = null,
		RundownRepeatPolicy? repeat = null)
		: base(itemId, name, RundownItemKind.AudioRouting, null, RundownAdvanceMode.Manual, repeat)
	{
		if (routingMode is not (FollowVideoMode or BreakawayMode))
			throw new ArgumentOutOfRangeException(nameof(routingMode));
		if (routingMode == FollowVideoMode && breakawaySourceId is not null)
			throw new ArgumentException("FOLLOW_VIDEO routing must not declare a breakaway source.", nameof(breakawaySourceId));
		if (routingMode == BreakawayMode && breakawaySourceId is null)
			throw new ArgumentException("BREAKAWAY routing requires an explicit source.", nameof(breakawaySourceId));

		RoutingMode = routingMode;
		BreakawaySourceId = breakawaySourceId;
	}

	public int RoutingMode { get; }
	public ProductionSourceId? BreakawaySourceId { get; }
}

public sealed record RundownHoldItem : RundownItem
{
	public const uint MaximumHoldFrames = ShowControlAction.MaximumWaitFrames;

	public RundownHoldItem(
		RundownItemId itemId,
		string name,
		uint frames,
		RundownRepeatPolicy? repeat = null)
		: base(itemId, name, RundownItemKind.Hold, null, RundownAdvanceMode.Manual, repeat)
	{
		if (frames is 0 or > MaximumHoldFrames)
			throw new ArgumentOutOfRangeException(nameof(frames), $"Hold duration must be between 1 and {MaximumHoldFrames} frames.");
		Frames = frames;
	}

	public uint Frames { get; }
}

public sealed record RundownDefinition
{
	public const int MaximumItems = 256;
	public const int MaximumNameLength = 128;
	private readonly ReadOnlyCollection<RundownItem> _items;

	public RundownDefinition(
		CompatibilityVersion version,
		RundownId rundownId,
		string name,
		IReadOnlyList<RundownItem> items)
	{
		RundownContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Rundown name is required.", nameof(name));
		var normalized = name.Trim();
		if (normalized.Length > MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name));
		ArgumentNullException.ThrowIfNull(items);
		if (items.Count is 0 or > MaximumItems)
			throw new ArgumentOutOfRangeException(nameof(items), $"Rundown must contain between 1 and {MaximumItems} items.");
		if (items.Any(item => item is null))
			throw new ArgumentException("Rundown items must not contain null values.", nameof(items));
		if (items.Select(item => item.ItemId).Distinct().Count() != items.Count)
			throw new ArgumentException("Rundown item identities must be unique.", nameof(items));

		Version = version;
		RundownId = rundownId;
		Name = normalized;
		_items = Array.AsReadOnly(items.ToArray());
	}

	public CompatibilityVersion Version { get; }
	public RundownId RundownId { get; }
	public string Name { get; }
	public IReadOnlyList<RundownItem> Items => _items;

	public RundownDefinition Insert(int index, RundownItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (index < 0 || index > _items.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
		var items = _items.ToList();
		items.Insert(index, item);
		return new RundownDefinition(Version, RundownId, Name, items);
	}

	public RundownDefinition Remove(RundownItemId itemId)
	{
		var items = _items.Where(item => item.ItemId != itemId).ToArray();
		if (items.Length == _items.Count)
			throw new KeyNotFoundException($"Rundown item '{itemId}' does not exist.");
		if (items.Length == 0)
			throw new InvalidOperationException("A rundown must retain at least one item.");
		return new RundownDefinition(Version, RundownId, Name, items);
	}

	public RundownDefinition Reorder(IReadOnlyList<RundownItemId> orderedItemIds)
	{
		ArgumentNullException.ThrowIfNull(orderedItemIds);
		if (orderedItemIds.Count != _items.Count ||
			orderedItemIds.Distinct().Count() != _items.Count)
		{
			throw new ArgumentException("Rundown reorder must contain every item identity exactly once.", nameof(orderedItemIds));
		}
		var byId = _items.ToDictionary(item => item.ItemId);
		if (orderedItemIds.Any(id => !byId.ContainsKey(id)))
			throw new ArgumentException("Rundown reorder contains an unknown item identity.", nameof(orderedItemIds));
		return new RundownDefinition(Version, RundownId, Name, orderedItemIds.Select(id => byId[id]).ToArray());
	}
}

public enum RundownExecutionState
{
	Idle = 1,
	Prepared = 2,
	Executing = 3,
	Held = 4,
	Completed = 5,
	Failed = 6,
	RecoveryRequired = 7
}

public sealed record RundownExecutionSnapshot(
	RundownExecutionState State,
	RundownId? RundownId,
	RundownItemId? SelectedItemId,
	RundownItemId? PreparedItemId,
	RundownItemId? CurrentItemId,
	RundownItemId? NextItemId,
	ulong Revision,
	Identity? CausalActionId,
	bool AutoAdvanceArmed,
	bool RequiresAcknowledgement,
	Failure? Failure)
{
	public static RundownExecutionSnapshot Idle { get; } = new(
		RundownExecutionState.Idle,
		null,
		null,
		null,
		null,
		null,
		0,
		null,
		false,
		false,
		null);
}

public sealed record RundownWorkspaceSnapshot(
	RundownDefinition? Rundown,
	RundownExecutionSnapshot Execution,
	ulong StorageVersion);

public static class RundownCanonicalSerializer
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

	public static string Serialize(RundownDefinition rundown)
	{
		ArgumentNullException.ThrowIfNull(rundown);
		return JsonSerializer.Serialize(new Document(
			rundown.Version.ToString(),
			rundown.RundownId.ToString(),
			rundown.Name,
			rundown.Items.Select(ToDocument).ToArray()), Options);
	}

	public static RundownDefinition Deserialize(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			throw new ArgumentException("Rundown JSON is required.", nameof(json));
		var document = JsonSerializer.Deserialize<Document>(json, Options)
			?? throw new InvalidDataException("Rundown JSON did not contain a document.");
		var version = CompatibilityVersion.Parse(document.Version);
		RundownContractVersion.EnsureSupported(version);
		if (document.Items is null)
			throw new InvalidDataException("Rundown JSON requires an item collection.");
		return new RundownDefinition(
			version,
			new RundownId(Identity.Parse(document.RundownId)),
			document.Name,
			document.Items.Select(FromDocument).ToArray());
	}

	private static ItemDocument ToDocument(RundownItem item) => item switch
	{
		RundownMediaItem media => new(
			media.ItemId.ToString(), media.Name, media.Kind.ToString(),
			media.AssetId.ToString(), media.SourceId.ToString(), null, null, null, null,
			media.Transition!.Kind.ToString(), media.Transition.DurationFrames,
			media.AdvanceMode.ToString(), media.Repeat.Mode.ToString(), media.Repeat.RepeatCount),
		RundownSceneItem scene => new(
			scene.ItemId.ToString(), scene.Name, scene.Kind.ToString(),
			null, null, scene.SceneId.ToString(), null, null, null,
			null, null, scene.AdvanceMode.ToString(), scene.Repeat.Mode.ToString(), scene.Repeat.RepeatCount),
		RundownGraphicsItem graphics => new(
			graphics.ItemId.ToString(), graphics.Name, graphics.Kind.ToString(),
			null, null, null, graphics.LayerId, graphics.Visible, null,
			null, null, graphics.AdvanceMode.ToString(), graphics.Repeat.Mode.ToString(), graphics.Repeat.RepeatCount),
		RundownAudioRoutingItem audio => new(
			audio.ItemId.ToString(), audio.Name, audio.Kind.ToString(),
			null, audio.BreakawaySourceId?.ToString(), null, null, null, audio.RoutingMode,
			null, null, audio.AdvanceMode.ToString(), audio.Repeat.Mode.ToString(), audio.Repeat.RepeatCount),
		RundownHoldItem hold => new(
			hold.ItemId.ToString(), hold.Name, hold.Kind.ToString(),
			null, null, null, null, null, checked((int)hold.Frames),
			null, null, hold.AdvanceMode.ToString(), hold.Repeat.Mode.ToString(), hold.Repeat.RepeatCount),
		_ => throw new NotSupportedException($"Unsupported rundown item type '{item.GetType().Name}'.")
	};

	private static RundownItem FromDocument(ItemDocument item)
	{
		if (!Enum.TryParse<RundownItemKind>(item.Kind, false, out var kind) || !Enum.IsDefined(kind))
			throw new InvalidDataException($"Unsupported rundown item kind '{item.Kind}'.");
		if (!Enum.TryParse<RundownAdvanceMode>(item.AdvanceMode, false, out var advance) || !Enum.IsDefined(advance))
			throw new InvalidDataException($"Unsupported rundown advance mode '{item.AdvanceMode}'.");
		if (!Enum.TryParse<RundownRepeatMode>(item.RepeatMode, false, out var repeatMode) || !Enum.IsDefined(repeatMode))
			throw new InvalidDataException($"Unsupported rundown repeat mode '{item.RepeatMode}'.");
		var repeat = new RundownRepeatPolicy(repeatMode, item.RepeatCount);
		var itemId = new RundownItemId(Identity.Parse(item.ItemId));

		return kind switch
		{
			RundownItemKind.MediaClip => new RundownMediaItem(
				itemId,
				item.Name,
				Identity.Parse(Require(item.AssetId, nameof(item.AssetId))),
				new ProductionSourceId(Identity.Parse(Require(item.SourceId, nameof(item.SourceId)))),
				ReadTransition(item),
				advance,
				repeat),
			RundownItemKind.Scene => new RundownSceneItem(
				itemId,
				item.Name,
				new SceneId(Identity.Parse(Require(item.SceneId, nameof(item.SceneId)))),
				advance,
				repeat),
			RundownItemKind.Graphics => new RundownGraphicsItem(
				itemId,
				item.Name,
				Require(item.LayerId, nameof(item.LayerId)),
				item.Visible ?? throw new InvalidDataException("Graphics rundown item requires visibility."),
				repeat),
			RundownItemKind.AudioRouting => new RundownAudioRoutingItem(
				itemId,
				item.Name,
				item.NumericValue ?? throw new InvalidDataException("Audio routing rundown item requires a mode."),
				string.IsNullOrWhiteSpace(item.SourceId) ? null : new ProductionSourceId(Identity.Parse(item.SourceId)),
				repeat),
			RundownItemKind.Hold => new RundownHoldItem(
				itemId,
				item.Name,
				checked((uint)(item.NumericValue ?? throw new InvalidDataException("Hold rundown item requires a frame count."))),
				repeat),
			_ => throw new InvalidDataException($"Unsupported rundown item kind '{kind}'.")
		};
	}

	private static RundownTransition ReadTransition(ItemDocument item)
	{
		if (!Enum.TryParse<RundownTransitionKind>(Require(item.TransitionKind, nameof(item.TransitionKind)), false, out var kind) ||
			!Enum.IsDefined(kind))
		{
			throw new InvalidDataException($"Unsupported rundown transition '{item.TransitionKind}'.");
		}
		return new RundownTransition(kind, item.TransitionFrames);
	}

	private static string Require(string? value, string field) =>
		string.IsNullOrWhiteSpace(value)
			? throw new InvalidDataException($"Rundown field '{field}' is required.")
			: value;

	private sealed record Document(string Version, string RundownId, string Name, ItemDocument[] Items);
	private sealed record ItemDocument(
		string ItemId,
		string Name,
		string Kind,
		string? AssetId,
		string? SourceId,
		string? SceneId,
		string? LayerId,
		bool? Visible,
		int? NumericValue,
		string? TransitionKind,
		uint? TransitionFrames,
		string AdvanceMode,
		string RepeatMode,
		ushort RepeatCount);
}
