// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class MediaMarkerContractTests
{
	[Fact]
	public void Snapshot_sorts_cues_deterministically_and_preserves_frame_markers()
	{
		var cues = new[]
		{
			new MediaCuePoint(new MediaCuePointId(Id(3)), "Bravo", 20),
			new MediaCuePoint(new MediaCuePointId(Id(2)), "Alpha", 20),
			new MediaCuePoint(new MediaCuePointId(Id(1)), "Opening", 5)
		};

		var snapshot = new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			new MediaAssetId(Id(100)),
			50,
			3,
			45,
			cues);

		Assert.Equal(3, snapshot.InPointFrame);
		Assert.Equal(45, snapshot.OutPointFrame);
		Assert.Equal(new[] { "Opening", "Alpha", "Bravo" }, snapshot.CuePoints.Select(cue => cue.Name));
		Assert.Equal(new long[] { 5, 20, 20 }, snapshot.CuePoints.Select(cue => cue.PositionFrame));
	}

	[Fact]
	public void Snapshot_rejects_invalid_ranges_and_duplicate_cue_ids()
	{
		var asset = new MediaAssetId(Id(100));
		var cueId = new MediaCuePointId(Id(1));

		Assert.Throws<ArgumentException>(() => new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			asset,
			50,
			40,
			20));

		Assert.Throws<ArgumentOutOfRangeException>(() => new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			asset,
			50,
			cuePoints: new[] { new MediaCuePoint(cueId, "Outside", 50) }));

		Assert.Throws<ArgumentException>(() => new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			asset,
			50,
			cuePoints: new[]
			{
				new MediaCuePoint(cueId, "One", 5),
				new MediaCuePoint(cueId, "Two", 10)
			}));
	}

	[Fact]
	public void Commands_validate_fields_by_operation()
	{
		var asset = new MediaAssetId(Id(100));
		var cue = new MediaCuePointId(Id(1));

		Assert.Throws<ArgumentOutOfRangeException>(() => new MediaMarkerCommand(
			MediaContractVersion.Current,
			asset,
			MediaMarkerCommandKind.SetInPoint));

		Assert.Throws<ArgumentException>(() => new MediaMarkerCommand(
			MediaContractVersion.Current,
			asset,
			MediaMarkerCommandKind.AddCuePoint,
			10,
			cue,
			"   "));

		Assert.Throws<ArgumentException>(() => new MediaMarkerCommand(
			MediaContractVersion.Current,
			asset,
			MediaMarkerCommandKind.DeleteCuePoint,
			10,
			cue));
	}

	private static Identity Id(int value) =>
		Identity.Parse($"44000000-0000-0000-0000-{value:000000000000}");
}
