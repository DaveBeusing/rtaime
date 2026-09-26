// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Control;

public static class RundownShowControlAdapter
{
	public static ShowControlCueList BuildCueList(RundownDefinition rundown)
	{
		ArgumentNullException.ThrowIfNull(rundown);
		return new ShowControlCueList(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(rundown.RundownId.Value),
			rundown.Name,
			rundown.Items.Select(ToCue).ToArray());
	}

	private static ShowControlCue ToCue(RundownItem item) =>
		new(
			new ShowControlCueId(item.ItemId.Value),
			item.Name,
			Actions(item));

	private static IReadOnlyList<ShowControlAction> Actions(RundownItem item) => item switch
	{
		RundownMediaItem media => MediaActions(media),
		RundownSceneItem scene =>
			[
				new ShowControlAction(
					ActionId(scene.ItemId, 0),
					ShowControlActionKind.ActivateScene,
					sceneId: scene.SceneId.ToString())
			],
		RundownGraphicsItem graphics =>
			[
				new ShowControlAction(
					ActionId(graphics.ItemId, 0),
					ShowControlActionKind.SetLayerVisibility,
					layerId: graphics.LayerId,
					visible: graphics.Visible)
			],
		RundownAudioRoutingItem audio =>
			[
				new ShowControlAction(
					ActionId(audio.ItemId, 0),
					ShowControlActionKind.SetAudioRouting,
					sourceId: audio.BreakawaySourceId?.ToString(),
					audioRoutingMode: audio.RoutingMode)
			],
		RundownHoldItem hold =>
			[
				new ShowControlAction(
					ActionId(hold.ItemId, 0),
					ShowControlActionKind.WaitFrames,
					waitFrames: hold.Frames)
			],
		_ => throw new NotSupportedException($"Unsupported rundown item type '{item.GetType().Name}'.")
	};

	private static IReadOnlyList<ShowControlAction> MediaActions(RundownMediaItem media)
	{
		var actions = new List<ShowControlAction>(4)
		{
			new(
				ActionId(media.ItemId, 0),
				ShowControlActionKind.MediaOpen,
				sourceId: media.SourceId.ToString(),
				mediaAssetId: media.AssetId.ToString()),
			new(
				ActionId(media.ItemId, 1),
				ShowControlActionKind.SetPreview,
				sourceId: media.SourceId.ToString())
		};

		actions.Add(media.Transition!.Kind switch
		{
			RundownTransitionKind.Cut => new ShowControlAction(
				ActionId(media.ItemId, 2),
				ShowControlActionKind.Cut),
			RundownTransitionKind.Dissolve => new ShowControlAction(
				ActionId(media.ItemId, 2),
				ShowControlActionKind.Dissolve,
				durationFrames: media.Transition.DurationFrames),
			_ => throw new NotSupportedException($"Unsupported rundown transition '{media.Transition.Kind}'.")
		});

		actions.Add(new ShowControlAction(
			ActionId(media.ItemId, 3),
			ShowControlActionKind.MediaPlay,
			mediaAssetId: media.AssetId.ToString()));
		return actions;
	}

	private static ShowControlActionId ActionId(RundownItemId itemId, int ordinal)
	{
		var payload = Encoding.UTF8.GetBytes($"rtaime:rundown-action:{itemId}:{ordinal}");
		var hash = SHA256.HashData(payload);
		return new ShowControlActionId(new Identity(new Guid(hash.AsSpan(0, 16))));
	}
}
