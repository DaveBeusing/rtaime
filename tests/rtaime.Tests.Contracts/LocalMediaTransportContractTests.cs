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
	public void Playback_configuration_requires_explicit_policy_and_valid_effective_range()
	{
		var assetId = new MediaAssetId(Id(1));

		Assert.Throws<ArgumentException>(() => new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback));

		Assert.Throws<ArgumentException>(() => new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: true,
			endBehavior: MediaDeckEndBehavior.Loop,
			inPointFrame: 8,
			outPointFrame: 4));

		var command = new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: false,
			endBehavior: MediaDeckEndBehavior.ReturnToIn,
			inPointFrame: 2,
			outPointFrame: 8);

		Assert.False(command.AutoPlayOnProgram);
		Assert.Equal(MediaDeckEndBehavior.ReturnToIn, command.EndBehavior);
		Assert.Equal(2, command.InPointFrame);
		Assert.Equal(8, command.OutPointFrame);
	}

	[Fact]
	public void Snapshot_exposes_effective_range_program_state_and_countdown()
	{
		var position = new MediaTransportPosition(
			5,
			10,
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(200),
			TimeSpan.FromMilliseconds(100),
			FrameRate.Fps50);

		var snapshot = new MediaTransportSnapshot(
			MediaContractVersion.Current,
			new MediaAssetId(Id(1)),
			new MediaSourceId(Id(2)),
			MediaTransportState.Playing,
			position,
			null,
			autoPlayOnProgram: true,
			endBehavior: MediaDeckEndBehavior.Loop,
			isOnProgram: true,
			effectiveStartFrame: 3,
			effectiveEndFrame: 8,
			effectiveRemainingFrames: 3,
			effectiveRemaining: TimeSpan.FromMilliseconds(60));

		Assert.True(snapshot.AutoPlayOnProgram);
		Assert.Equal(MediaDeckEndBehavior.Loop, snapshot.EndBehavior);
		Assert.True(snapshot.IsOnProgram);
		Assert.Equal(3, snapshot.EffectiveStartFrame);
		Assert.Equal(8, snapshot.EffectiveEndFrame);
		Assert.Equal(3, snapshot.EffectiveRemainingFrames);
		Assert.Equal(TimeSpan.FromMilliseconds(60), snapshot.EffectiveRemaining);
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
