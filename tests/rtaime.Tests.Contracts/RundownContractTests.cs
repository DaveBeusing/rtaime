// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Contracts;

public sealed class RundownContractTests
{
	[Fact]
	public void Rundown_round_trips_stable_item_identity_and_order()
	{
		var assetId = Identity.Parse("81000000-0000-0000-0000-000000000001");
		var sourceId = new ProductionSourceId(Identity.Parse("81000000-0000-0000-0000-000000000002"));
		var sceneId = new SceneId(Identity.Parse("81000000-0000-0000-0000-000000000003"));
		var mediaId = new RundownItemId(Identity.Parse("81000000-0000-0000-0000-000000000004"));
		var sceneItemId = new RundownItemId(Identity.Parse("81000000-0000-0000-0000-000000000005"));
		var rundown = new RundownDefinition(
			RundownContractVersion.Current,
			new RundownId(Identity.Parse("81000000-0000-0000-0000-000000000006")),
			"Evening Show",
			new RundownItem[]
			{
				new RundownMediaItem(
					mediaId,
					"Open",
					assetId,
					sourceId,
					RundownTransition.Dissolve(25),
					RundownAdvanceMode.AutoOnMediaEnd),
				new RundownSceneItem(sceneItemId, "Studio", sceneId)
			});

		var restored = RundownCanonicalSerializer.Deserialize(RundownCanonicalSerializer.Serialize(rundown));

		Assert.Equal(rundown.RundownId, restored.RundownId);
		Assert.Equal(new[] { mediaId, sceneItemId }, restored.Items.Select(item => item.ItemId));
		var media = Assert.IsType<RundownMediaItem>(restored.Items[0]);
		Assert.Equal(assetId, media.AssetId);
		Assert.Equal(sourceId, media.SourceId);
		Assert.Equal(RundownTransitionKind.Dissolve, media.Transition!.Kind);
		Assert.Equal(25U, media.Transition.DurationFrames);
		Assert.Equal(RundownAdvanceMode.AutoOnMediaEnd, media.AdvanceMode);
		Assert.IsType<RundownSceneItem>(restored.Items[1]);
	}

	[Fact]
	public void Reorder_preserves_identity_and_content()
	{
		var first = new RundownHoldItem(RundownItemId.New(), "Hold A", 10);
		var second = new RundownHoldItem(RundownItemId.New(), "Hold B", 20);
		var rundown = Create(first, second);

		var reordered = rundown.Reorder(new[] { second.ItemId, first.ItemId });

		Assert.Equal(second.ItemId, reordered.Items[0].ItemId);
		Assert.Equal(first.ItemId, reordered.Items[1].ItemId);
		Assert.Equal(20U, Assert.IsType<RundownHoldItem>(reordered.Items[0]).Frames);
	}

	[Fact]
	public void Duplicate_item_identity_is_rejected()
	{
		var id = RundownItemId.New();
		Assert.Throws<ArgumentException>(() => Create(
			new RundownHoldItem(id, "A", 10),
			new RundownHoldItem(id, "B", 20)));
	}

	[Fact]
	public void Auto_advance_is_limited_to_media_items()
	{
		Assert.Throws<ArgumentException>(() =>
			new RundownSceneItem(
				RundownItemId.New(),
				"Scene",
				SceneId.New(),
				RundownAdvanceMode.AutoOnMediaEnd));
	}

	[Fact]
	public void Repeat_policy_is_strictly_bounded()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new RundownRepeatPolicy(
				RundownRepeatMode.RepeatItem,
				checked((ushort)(RundownRepeatPolicy.MaximumRepeatCount + 1))));
	}

	[Fact]
	public void Audio_breakaway_requires_source_and_follow_video_rejects_source()
	{
		Assert.Throws<ArgumentException>(() =>
			new RundownAudioRoutingItem(
				RundownItemId.New(),
				"Breakaway",
				RundownAudioRoutingItem.BreakawayMode));
		Assert.Throws<ArgumentException>(() =>
			new RundownAudioRoutingItem(
				RundownItemId.New(),
				"Follow",
				RundownAudioRoutingItem.FollowVideoMode,
				ProductionSourceId.New()));
	}

	private static RundownDefinition Create(params RundownItem[] items) =>
		new(
			RundownContractVersion.Current,
			RundownId.New(),
			"Test",
			items);
}
