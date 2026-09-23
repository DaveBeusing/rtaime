// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Text.Json;
using rtaime.Core;

namespace rtaime.Control.Contracts;

public static class ShowControlContractVersion
{
	public static CompatibilityVersion Current { get; } = new(1, 0);

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (version != Current)
			throw new NotSupportedException($"Unsupported show-control contract version '{version}'. Supported version is '{Current}'.");
	}
}

public readonly record struct ShowControlCueListId
{
	public ShowControlCueListId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Show-control cue-list identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ShowControlCueListId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct ShowControlCueId
{
	public ShowControlCueId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Show-control cue identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ShowControlCueId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct ShowControlActionId
{
	public ShowControlActionId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Show-control action identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ShowControlActionId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct ShowControlExecutionId
{
	public ShowControlExecutionId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Show-control execution identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ShowControlExecutionId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public enum ShowControlActionKind
{
	ActivateScene = 1,
	SetPreview = 2,
	Cut = 3,
	Dissolve = 4,
	JumpMediaCue = 5,
	MediaPlay = 6,
	MediaPause = 7,
	MediaStop = 8,
	SetLayerVisibility = 9,
	StartRecording = 10,
	StopRecording = 11,
	WaitFrames = 12
}

public sealed record ShowControlAction
{
	public const uint MaximumDissolveFrames = 600;
	public const uint MaximumWaitFrames = 216_000;
	public const int MaximumReferenceLength = 512;

	public ShowControlAction(
		ShowControlActionId actionId,
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
		uint? waitFrames = null)
	{
		if (!Enum.IsDefined(kind))
			throw new ArgumentOutOfRangeException(nameof(kind));

		SceneId = Normalize(sceneId, nameof(sceneId));
		SourceId = Normalize(sourceId, nameof(sourceId));
		MediaAssetId = Normalize(mediaAssetId, nameof(mediaAssetId));
		MediaCuePointId = Normalize(mediaCuePointId, nameof(mediaCuePointId));
		LayerId = Normalize(layerId, nameof(layerId));
		RecordingDestinationDirectory = Normalize(recordingDestinationDirectory, nameof(recordingDestinationDirectory));
		RecordingFileName = Normalize(recordingFileName, nameof(recordingFileName));
		ActionId = actionId;
		Kind = kind;
		DurationFrames = durationFrames;
		Visible = visible;
		WaitFrames = waitFrames;

		ValidateShape();
	}

	public ShowControlActionId ActionId { get; }
	public ShowControlActionKind Kind { get; }
	public string? SceneId { get; }
	public string? SourceId { get; }
	public uint? DurationFrames { get; }
	public string? MediaAssetId { get; }
	public string? MediaCuePointId { get; }
	public string? LayerId { get; }
	public bool? Visible { get; }
	public string? RecordingDestinationDirectory { get; }
	public string? RecordingFileName { get; }
	public uint? WaitFrames { get; }

	public bool ReplaySafeAfterUncertainCompletion => Kind is
		ShowControlActionKind.ActivateScene or
		ShowControlActionKind.SetPreview or
		ShowControlActionKind.JumpMediaCue or
		ShowControlActionKind.MediaPlay or
		ShowControlActionKind.MediaPause or
		ShowControlActionKind.MediaStop or
		ShowControlActionKind.SetLayerVisibility or
		ShowControlActionKind.StopRecording or
		ShowControlActionKind.WaitFrames;

	private void ValidateShape()
	{
		switch (Kind)
		{
			case ShowControlActionKind.ActivateScene:
				Require(SceneId, nameof(SceneId));
				RequireOnly(scene: true);
				break;
			case ShowControlActionKind.SetPreview:
				Require(SourceId, nameof(SourceId));
				RequireOnly(source: true);
				break;
			case ShowControlActionKind.Cut:
				RequireOnly();
				break;
			case ShowControlActionKind.Dissolve:
				if (DurationFrames is < 2 or > MaximumDissolveFrames)
					throw new ArgumentOutOfRangeException(nameof(DurationFrames), $"Dissolve duration must be between 2 and {MaximumDissolveFrames} frames.");
				RequireOnly(duration: true);
				break;
			case ShowControlActionKind.JumpMediaCue:
				Require(MediaAssetId, nameof(MediaAssetId));
				Require(MediaCuePointId, nameof(MediaCuePointId));
				RequireOnly(mediaAsset: true, mediaCue: true);
				break;
			case ShowControlActionKind.MediaPlay:
			case ShowControlActionKind.MediaPause:
			case ShowControlActionKind.MediaStop:
				Require(MediaAssetId, nameof(MediaAssetId));
				RequireOnly(mediaAsset: true);
				break;
			case ShowControlActionKind.SetLayerVisibility:
				Require(LayerId, nameof(LayerId));
				if (!Visible.HasValue)
					throw new ArgumentException("Layer visibility action requires an explicit visibility value.", nameof(Visible));
				RequireOnly(layer: true, visible: true);
				break;
			case ShowControlActionKind.StartRecording:
				Require(RecordingDestinationDirectory, nameof(RecordingDestinationDirectory));
				Require(RecordingFileName, nameof(RecordingFileName));
				RequireOnly(recordingDestination: true, recordingFile: true);
				break;
			case ShowControlActionKind.StopRecording:
				RequireOnly();
				break;
			case ShowControlActionKind.WaitFrames:
				if (WaitFrames is null or 0 or > MaximumWaitFrames)
					throw new ArgumentOutOfRangeException(nameof(WaitFrames), $"Frame wait must be between 1 and {MaximumWaitFrames} frames.");
				RequireOnly(wait: true);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(Kind));
		}
	}

	private void RequireOnly(
		bool scene = false,
		bool source = false,
		bool duration = false,
		bool mediaAsset = false,
		bool mediaCue = false,
		bool layer = false,
		bool visible = false,
		bool recordingDestination = false,
		bool recordingFile = false,
		bool wait = false)
	{
		if ((!scene && SceneId is not null) ||
			(!source && SourceId is not null) ||
			(!duration && DurationFrames.HasValue) ||
			(!mediaAsset && MediaAssetId is not null) ||
			(!mediaCue && MediaCuePointId is not null) ||
			(!layer && LayerId is not null) ||
			(!visible && Visible.HasValue) ||
			(!recordingDestination && RecordingDestinationDirectory is not null) ||
			(!recordingFile && RecordingFileName is not null) ||
			(!wait && WaitFrames.HasValue))
		{
			throw new ArgumentException($"Show-control action '{Kind}' contains fields that do not belong to that action kind.");
		}
	}

	private static string? Normalize(string? value, string parameterName)
	{
		if (value is null)
			return null;
		var normalized = value.Trim();
		if (normalized.Length == 0)
			return null;
		if (normalized.Length > MaximumReferenceLength)
			throw new ArgumentOutOfRangeException(parameterName, $"Show-control references are limited to {MaximumReferenceLength} characters.");
		return normalized;
	}

	private static void Require(string? value, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Required show-control action reference is missing.", parameterName);
	}
}

public sealed record ShowControlCue
{
	public const int MaximumNameLength = 96;
	public const int MaximumActions = 32;
	private readonly ReadOnlyCollection<ShowControlAction> _actions;

	public ShowControlCue(ShowControlCueId cueId, string name, IReadOnlyList<ShowControlAction> actions)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Show-control cue name is required.", nameof(name));
		var normalizedName = name.Trim();
		if (normalizedName.Length > MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name), $"Cue names are limited to {MaximumNameLength} characters.");
		ArgumentNullException.ThrowIfNull(actions);
		if (actions.Count is 0 or > MaximumActions)
			throw new ArgumentOutOfRangeException(nameof(actions), $"A cue must contain between 1 and {MaximumActions} actions.");
		if (actions.Any(action => action is null))
			throw new ArgumentException("Cue actions must not contain null entries.", nameof(actions));
		if (actions.Select(action => action.ActionId).Distinct().Count() != actions.Count)
			throw new ArgumentException("Action identities must be unique inside a cue.", nameof(actions));

		CueId = cueId;
		Name = normalizedName;
		_actions = Array.AsReadOnly(actions.ToArray());
	}

	public ShowControlCueId CueId { get; }
	public string Name { get; }
	public IReadOnlyList<ShowControlAction> Actions => _actions;
}

public sealed record ShowControlCueList
{
	public const int MaximumNameLength = 96;
	public const int MaximumCues = 256;
	private readonly ReadOnlyCollection<ShowControlCue> _cues;

	public ShowControlCueList(
		CompatibilityVersion version,
		ShowControlCueListId cueListId,
		string name,
		IReadOnlyList<ShowControlCue> cues)
	{
		ShowControlContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Show-control cue-list name is required.", nameof(name));
		var normalizedName = name.Trim();
		if (normalizedName.Length > MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name), $"Cue-list names are limited to {MaximumNameLength} characters.");
		ArgumentNullException.ThrowIfNull(cues);
		if (cues.Count is 0 or > MaximumCues)
			throw new ArgumentOutOfRangeException(nameof(cues), $"A cue list must contain between 1 and {MaximumCues} cues.");
		if (cues.Any(cue => cue is null))
			throw new ArgumentException("Cue list must not contain null cues.", nameof(cues));
		if (cues.Select(cue => cue.CueId).Distinct().Count() != cues.Count)
			throw new ArgumentException("Cue identities must be unique inside a cue list.", nameof(cues));
		var allActionIds = cues.SelectMany(cue => cue.Actions).Select(action => action.ActionId).ToArray();
		if (allActionIds.Distinct().Count() != allActionIds.Length)
			throw new ArgumentException("Action identities must be unique across the complete cue list.", nameof(cues));

		Version = version;
		CueListId = cueListId;
		Name = normalizedName;
		_cues = Array.AsReadOnly(cues.ToArray());
	}

	public CompatibilityVersion Version { get; }
	public ShowControlCueListId CueListId { get; }
	public string Name { get; }
	public IReadOnlyList<ShowControlCue> Cues => _cues;
}

public enum ShowControlExecutionState
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

public sealed record ShowControlExecutionSnapshot(
	CompatibilityVersion Version,
	ShowControlExecutionId? ExecutionId,
	ShowControlCueListId? CueListId,
	ShowControlExecutionState State,
	int? CueIndex,
	int? ActionIndex,
	ShowControlCueId? CurrentCueId,
	ShowControlActionId? CurrentActionId,
	ulong ExecutionRevision,
	ulong? WaitTargetFrameSequence,
	string? RuntimeHostInstanceId,
	bool RequiresAcknowledgement,
	Failure? Failure)
{
	public static ShowControlExecutionSnapshot Idle { get; } = new(
		ShowControlContractVersion.Current,
		null,
		null,
		ShowControlExecutionState.Idle,
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

public static class ShowControlCanonicalSerializer
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	public static string Serialize(ShowControlCueList cueList)
	{
		ArgumentNullException.ThrowIfNull(cueList);
		return JsonSerializer.Serialize(cueList, Options);
	}

	public static ShowControlCueList Deserialize(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			throw new ArgumentException("Show-control cue-list JSON is required.", nameof(json));
		return JsonSerializer.Deserialize<ShowControlCueList>(json, Options)
			?? throw new InvalidDataException("Show-control cue-list JSON did not contain a cue list.");
	}
}
