// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Contracts;

public sealed class ShowControlContractTests
{
	[Fact]
	public void Cue_list_round_trip_preserves_stable_identity_and_action_order()
	{
		var cueList = Fixture();

		var json = ShowControlCanonicalSerializer.Serialize(cueList);
		var copy = ShowControlCanonicalSerializer.Deserialize(json);

		Assert.Equal(cueList.Version, copy.Version);
		Assert.Equal(cueList.CueListId, copy.CueListId);
		Assert.Equal(cueList.Name, copy.Name);
		Assert.Equal(cueList.Cues.Select(cue => cue.CueId), copy.Cues.Select(cue => cue.CueId));
		Assert.Equal(
			cueList.Cues.SelectMany(cue => cue.Actions).Select(action => action.ActionId),
			copy.Cues.SelectMany(cue => cue.Actions).Select(action => action.ActionId));
		Assert.Equal(json, ShowControlCanonicalSerializer.Serialize(copy));
	}

	[Fact]
	public void Unsupported_version_and_invalid_action_shapes_fail_closed()
	{
		Assert.Throws<NotSupportedException>(() => new ShowControlCueList(
			new CompatibilityVersion(99, 0),
			new ShowControlCueListId(Id(1)),
			"Unsupported",
			new[] { Cue(10, new ShowControlAction(new ShowControlActionId(Id(11)), ShowControlActionKind.Cut)) }));

		Assert.Throws<ArgumentException>(() => new ShowControlAction(
			new ShowControlActionId(Id(12)),
			ShowControlActionKind.ActivateScene,
			sourceId: Id(2).ToString()));

		Assert.Throws<ArgumentOutOfRangeException>(() => new ShowControlAction(
			new ShowControlActionId(Id(13)),
			ShowControlActionKind.WaitFrames,
			waitFrames: ShowControlAction.MaximumWaitFrames + 1));
	}

	[Fact]
	public void Duplicate_cue_or_action_identity_is_rejected()
	{
		var actionId = new ShowControlActionId(Id(20));
		var first = new ShowControlCue(
			new ShowControlCueId(Id(21)),
			"First",
			new[] { new ShowControlAction(actionId, ShowControlActionKind.Cut) });
		var second = new ShowControlCue(
			new ShowControlCueId(Id(22)),
			"Second",
			new[] { new ShowControlAction(actionId, ShowControlActionKind.StopRecording) });

		Assert.Throws<ArgumentException>(() => new ShowControlCueList(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(Id(23)),
			"Duplicate actions",
			new[] { first, second }));
	}

	private static ShowControlCueList Fixture()
	{
		var sceneId = Id(100).ToString();
		var sourceId = Id(101).ToString();
		var assetId = Id(102).ToString();
		var cuePointId = Id(103).ToString();
		return new ShowControlCueList(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(Id(104)),
			"Opening",
			new[]
			{
				new ShowControlCue(
					new ShowControlCueId(Id(105)),
					"Preset",
					new[]
					{
						new ShowControlAction(new ShowControlActionId(Id(106)), ShowControlActionKind.ActivateScene, sceneId: sceneId),
						new ShowControlAction(new ShowControlActionId(Id(107)), ShowControlActionKind.SetPreview, sourceId: sourceId),
						new ShowControlAction(new ShowControlActionId(Id(108)), ShowControlActionKind.Dissolve, durationFrames: 12)
					}),
				new ShowControlCue(
					new ShowControlCueId(Id(109)),
					"Media",
					new[]
					{
						new ShowControlAction(new ShowControlActionId(Id(110)), ShowControlActionKind.JumpMediaCue, mediaAssetId: assetId, mediaCuePointId: cuePointId),
						new ShowControlAction(new ShowControlActionId(Id(111)), ShowControlActionKind.MediaPlay, mediaAssetId: assetId),
						new ShowControlAction(new ShowControlActionId(Id(112)), ShowControlActionKind.WaitFrames, waitFrames: 30),
						new ShowControlAction(new ShowControlActionId(Id(113)), ShowControlActionKind.MediaStop, mediaAssetId: assetId)
					})
			});
	}

	private static ShowControlCue Cue(int cueSeed, params ShowControlAction[] actions) =>
		new(new ShowControlCueId(Id(cueSeed)), $"Cue {cueSeed}", actions);

	private static Identity Id(int value) =>
		new(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));
}
