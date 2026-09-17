// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class LocalMediaTransportContractTests
{
	[Fact]
	public void Snapshot_exposes_frame_position_duration_remaining_and_timecode_rate()
	{
		var position = new MediaTransportPosition(
			2,
			10,
			TimeSpan.FromMilliseconds(40),
			TimeSpan.FromMilliseconds(200),
			TimeSpan.FromMilliseconds(160),
			FrameRate.Fps50);
		var snapshot = new MediaTransportSnapshot(
			MediaContractVersion.Current,
			new MediaAssetId(Id(1)),
			new MediaSourceId(Id(2)),
			MediaTransportState.Paused,
			position,
			null);

		Assert.Equal(MediaTransportState.Paused, snapshot.State);
		Assert.Equal(2, snapshot.Position.CurrentFrame);
		Assert.Equal(10, snapshot.Position.TotalFrames);
		Assert.Equal(TimeSpan.FromMilliseconds(40), snapshot.Position.Position);
		Assert.Equal(TimeSpan.FromMilliseconds(160), snapshot.Position.Remaining);
		Assert.Equal(FrameRate.Fps50, snapshot.Position.FrameRate);
	}

	[Fact]
	public void Seek_requires_target_frame_and_other_commands_reject_one()
	{
		var assetId = new MediaAssetId(Id(1));
		Assert.Throws<ArgumentException>(() => new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Seek));
		Assert.Throws<ArgumentException>(() => new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Play,
			0));
	}

	[Fact]
	public void Error_snapshot_requires_failure_details()
	{
		var position = new MediaTransportPosition(
			0,
			1,
			TimeSpan.Zero,
			TimeSpan.FromMilliseconds(20),
			TimeSpan.FromMilliseconds(20),
			FrameRate.Fps50);

		Assert.Throws<ArgumentException>(() => new MediaTransportSnapshot(
			MediaContractVersion.Current,
			new MediaAssetId(Id(1)),
			new MediaSourceId(Id(2)),
			MediaTransportState.Error,
			position,
			null));
	}

	private static Identity Id(int value) =>
		Identity.Parse($"42000000-0000-0000-0000-{value:000000000000}");
}
