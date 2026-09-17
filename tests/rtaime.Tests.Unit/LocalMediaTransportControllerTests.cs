// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class LocalMediaTransportControllerTests
{
	[Fact]
	public void Play_pause_resume_stop_follow_deterministic_state_transitions()
	{
		var seeks = new List<long>();
		var controller = new LocalMediaTransportController(Probe(), frame =>
		{
			seeks.Add(frame);
			return null;
		});

		Assert.Equal(MediaTransportState.Ready, controller.Snapshot.State);
		Assert.True(controller.Apply(Command(MediaTransportCommandKind.Play)).Succeeded);
		Assert.Equal(MediaTransportState.Playing, controller.Snapshot.State);
		Assert.True(controller.Apply(Command(MediaTransportCommandKind.Pause)).Succeeded);
		Assert.Equal(MediaTransportState.Paused, controller.Snapshot.State);
		Assert.True(controller.Apply(Command(MediaTransportCommandKind.Play)).Succeeded);
		Assert.Equal(MediaTransportState.Playing, controller.Snapshot.State);
		Assert.True(controller.Apply(Command(MediaTransportCommandKind.Stop)).Succeeded);
		Assert.Equal(MediaTransportState.Ready, controller.Snapshot.State);
		Assert.Equal(0, controller.Snapshot.Position.CurrentFrame);
		Assert.Equal(new long[] { 0 }, seeks);
	}

	[Fact]
	public void Seek_clamps_to_clip_range_and_preserves_play_pause_semantics()
	{
		var seeks = new List<long>();
		var controller = new LocalMediaTransportController(Probe(), frame =>
		{
			seeks.Add(frame);
			return null;
		});

		var beforeStart = controller.Apply(Seek(-5));
		Assert.True(beforeStart.Succeeded);
		Assert.Equal(0, beforeStart.Snapshot.Position.CurrentFrame);
		Assert.Equal(MediaTransportState.Ready, beforeStart.Snapshot.State);

		Assert.True(controller.Apply(Command(MediaTransportCommandKind.Play)).Succeeded);
		var beyondEnd = controller.Apply(Seek(999));
		Assert.True(beyondEnd.Succeeded);
		Assert.Equal(9, beyondEnd.Snapshot.Position.CurrentFrame);
		Assert.Equal(MediaTransportState.Playing, beyondEnd.Snapshot.State);
		Assert.Equal(new long[] { 0, 9 }, seeks);
	}

	[Fact]
	public void Frame_step_moves_exactly_one_frame_and_is_rejected_while_playing()
	{
		var controller = new LocalMediaTransportController(Probe(), _ => null);
		Assert.True(controller.Apply(Seek(4)).Succeeded);

		var forward = controller.Apply(Command(MediaTransportCommandKind.StepForward));
		Assert.True(forward.Succeeded);
		Assert.Equal(5, forward.Snapshot.Position.CurrentFrame);
		Assert.Equal(TimeSpan.FromMilliseconds(100), forward.Snapshot.Position.Position);
		Assert.Equal(MediaTransportState.Paused, forward.Snapshot.State);

		var backward = controller.Apply(Command(MediaTransportCommandKind.StepBackward));
		Assert.True(backward.Succeeded);
		Assert.Equal(4, backward.Snapshot.Position.CurrentFrame);
		Assert.Equal(TimeSpan.FromMilliseconds(80), backward.Snapshot.Position.Position);

		Assert.True(controller.Apply(Command(MediaTransportCommandKind.Play)).Succeeded);
		var rejected = controller.Apply(Command(MediaTransportCommandKind.StepForward));
		Assert.False(rejected.Succeeded);
		Assert.Equal("media.transport.transition_invalid", rejected.Failure?.Code);
	}

	[Fact]
	public void Illegal_transition_returns_controlled_failure_without_mutating_state()
	{
		var controller = new LocalMediaTransportController(Probe(), _ => null);
		var result = controller.Apply(Command(MediaTransportCommandKind.Pause));

		Assert.False(result.Succeeded);
		Assert.Equal("media.transport.transition_invalid", result.Failure?.Code);
		Assert.Equal(MediaTransportState.Ready, result.Snapshot.State);
	}

	[Fact]
	public void Decoder_seek_failure_moves_transport_to_error()
	{
		var controller = new LocalMediaTransportController(
			Probe(),
			_ => new Failure("media.file.seek_failed", "seek failed"));

		var result = controller.Apply(Seek(2));
		Assert.False(result.Succeeded);
		Assert.Equal(MediaTransportState.Error, result.Snapshot.State);
		Assert.Equal("media.file.seek_failed", result.Snapshot.Failure?.Code);
	}

	private static LocalMediaProbe Probe() =>
		new(
			MediaContractVersion.Current,
			AssetId,
			new MediaSourceId(Id(2)),
			"transport.mp4",
			MediaContainerFormat.Mp4,
			MediaVideoCodec.H264,
			MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			TimeSpan.FromMilliseconds(200));

	private static MediaTransportCommand Command(MediaTransportCommandKind kind) =>
		new(MediaContractVersion.Current, AssetId, kind);

	private static MediaTransportCommand Seek(long frame) =>
		new(MediaContractVersion.Current, AssetId, MediaTransportCommandKind.Seek, frame);

	private static MediaAssetId AssetId => new(Id(1));

	private static Identity Id(int value) =>
		Identity.Parse($"42000000-0000-0000-0000-{value:000000000000}");
}
