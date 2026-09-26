// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class RundownShowControlAdapterTests
{
	[Fact]
	public void Media_item_maps_to_open_preview_transition_and_play()
	{
		var item = new RundownMediaItem(
			new RundownItemId(Identity.Parse("82000000-0000-0000-0000-000000000001")),
			"Clip",
			Identity.Parse("82000000-0000-0000-0000-000000000002"),
			new ProductionSourceId(Identity.Parse("82000000-0000-0000-0000-000000000003")),
			RundownTransition.Dissolve(20),
			RundownAdvanceMode.AutoOnMediaEnd);
		var rundown = new RundownDefinition(
			RundownContractVersion.Current,
			new RundownId(Identity.Parse("82000000-0000-0000-0000-000000000004")),
			"Main",
			[item]);

		var cueList = RundownShowControlAdapter.BuildCueList(rundown);
		var cue = Assert.Single(cueList.Cues);

		Assert.Equal(item.ItemId.Value, cue.CueId.Value);
		Assert.Collection(
			cue.Actions,
			action => Assert.Equal(ShowControlActionKind.MediaOpen, action.Kind),
			action => Assert.Equal(ShowControlActionKind.SetPreview, action.Kind),
			action =>
			{
				Assert.Equal(ShowControlActionKind.Dissolve, action.Kind);
				Assert.Equal(20U, action.DurationFrames);
			},
			action => Assert.Equal(ShowControlActionKind.MediaPlay, action.Kind));
	}

	[Fact]
	public void Stable_item_identity_produces_stable_cue_and_action_identity()
	{
		var itemId = new RundownItemId(Identity.Parse("82000000-0000-0000-0000-000000000011"));
		var sourceId = new ProductionSourceId(Identity.Parse("82000000-0000-0000-0000-000000000012"));
		var assetId = Identity.Parse("82000000-0000-0000-0000-000000000013");
		var first = RundownShowControlAdapter.BuildCueList(Create(
			new RundownMediaItem(itemId, "Clip", assetId, sourceId, RundownTransition.Cut)));
		var second = RundownShowControlAdapter.BuildCueList(Create(
			new RundownMediaItem(itemId, "Renamed", assetId, sourceId, RundownTransition.Cut)));

		Assert.Equal(first.Cues[0].CueId, second.Cues[0].CueId);
		Assert.Equal(
			first.Cues[0].Actions.Select(action => action.ActionId),
			second.Cues[0].Actions.Select(action => action.ActionId));
	}

	[Fact]
	public void Audio_and_hold_items_map_to_existing_show_control_execution_primitives()
	{
		var sourceId = new ProductionSourceId(Identity.Parse("82000000-0000-0000-0000-000000000021"));
		var cueList = RundownShowControlAdapter.BuildCueList(Create(
			new RundownAudioRoutingItem(
				RundownItemId.New(),
				"Breakaway",
				RundownAudioRoutingItem.BreakawayMode,
				sourceId),
			new RundownHoldItem(RundownItemId.New(), "Hold", 50)));

		Assert.Equal(ShowControlActionKind.SetAudioRouting, cueList.Cues[0].Actions[0].Kind);
		Assert.Equal(sourceId.ToString(), cueList.Cues[0].Actions[0].SourceId);
		Assert.Equal(ShowControlActionKind.WaitFrames, cueList.Cues[1].Actions[0].Kind);
		Assert.Equal(50U, cueList.Cues[1].Actions[0].WaitFrames);
	}

	private static RundownDefinition Create(params RundownItem[] items) =>
		new(
			RundownContractVersion.Current,
			RundownId.New(),
			"Test rundown",
			items);
}
