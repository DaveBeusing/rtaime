// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaMarkerControllerTests
{
	[Fact]
	public void State_machine_sets_clears_and_orders_in_out_and_cues()
	{
		var assetId = new MediaAssetId(Id(100));
		var controller = new MediaMarkerController(new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			assetId,
			50));

		Assert.True(controller.Apply(Command(assetId, MediaMarkerCommandKind.SetInPoint, 5)).Succeeded);
		Assert.True(controller.Apply(Command(assetId, MediaMarkerCommandKind.SetOutPoint, 40)).Succeeded);

		var cueB = new MediaCuePointId(Id(2));
		var cueA = new MediaCuePointId(Id(1));
		Assert.True(controller.Apply(Cue(assetId, MediaMarkerCommandKind.AddCuePoint, cueB, 20, "Bravo")).Succeeded);
		Assert.True(controller.Apply(Cue(assetId, MediaMarkerCommandKind.AddCuePoint, cueA, 10, "Alpha")).Succeeded);
		Assert.Equal(new[] { "Alpha", "Bravo" }, controller.Snapshot.CuePoints.Select(cue => cue.Name));

		Assert.True(controller.Apply(Cue(assetId, MediaMarkerCommandKind.RenameCuePoint, cueB, name: "Beta")).Succeeded);
		Assert.Equal("Beta", controller.Snapshot.CuePoints.Single(cue => cue.Id == cueB).Name);
		Assert.True(controller.Apply(Cue(assetId, MediaMarkerCommandKind.DeleteCuePoint, cueA)).Succeeded);
		Assert.Single(controller.Snapshot.CuePoints);

		Assert.True(controller.Apply(Command(assetId, MediaMarkerCommandKind.ClearInPoint)).Succeeded);
		Assert.True(controller.Apply(Command(assetId, MediaMarkerCommandKind.ClearOutPoint)).Succeeded);
		Assert.Null(controller.Snapshot.InPointFrame);
		Assert.Null(controller.Snapshot.OutPointFrame);
	}

	[Fact]
	public void State_machine_rejects_invalid_ranges_identity_and_cue_conflicts()
	{
		var assetId = new MediaAssetId(Id(100));
		var controller = new MediaMarkerController(new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			assetId,
			50,
			10,
			30));
		var cue = new MediaCuePointId(Id(1));

		Assert.Equal(
			"media.marker.in_after_out",
			controller.Apply(Command(assetId, MediaMarkerCommandKind.SetInPoint, 31)).Failure?.Code);
		Assert.Equal(
			"media.marker.out_before_in",
			controller.Apply(Command(assetId, MediaMarkerCommandKind.SetOutPoint, 9)).Failure?.Code);
		Assert.Equal(
			"media.marker.frame_out_of_range",
			controller.Apply(Command(assetId, MediaMarkerCommandKind.SetOutPoint, 50)).Failure?.Code);
		Assert.Equal(
			"media.marker.asset_mismatch",
			controller.Apply(Command(new MediaAssetId(Id(200)), MediaMarkerCommandKind.ClearInPoint)).Failure?.Code);

		Assert.True(controller.Apply(Cue(assetId, MediaMarkerCommandKind.AddCuePoint, cue, 20, "Cue")).Succeeded);
		Assert.Equal(
			"media.marker.cue_id_conflict",
			controller.Apply(Cue(assetId, MediaMarkerCommandKind.AddCuePoint, cue, 21, "Cue 2")).Failure?.Code);
	}

	[Fact]
	public async Task Client_controller_uses_confirmed_frame_and_jumps_through_timeline_seek()
	{
		var assetId = new MediaAssetId(Id(100));
		var domain = new MediaMarkerController(new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			assetId,
			50));
		var seekTargets = new List<long>();

		await using var timeline = new MediaTimelineController((command, _) =>
		{
			var target = command.TargetFrame!.Value;
			seekTargets.Add(target);
			return ValueTask.FromResult(MediaTransportCommandResult.Accepted(TransportSnapshot(target, 50)));
		});
		timeline.ApplyConfirmedSnapshot(TransportSnapshot(12, 50));

		await using var markers = new MediaTimelineMarkerController(
			timeline,
			(command, _) => ValueTask.FromResult(domain.Apply(command)));
		markers.ApplyConfirmedSnapshot(domain.Snapshot);

		Assert.True(await markers.SetInAtCurrentFrameAsync());
		Assert.Equal(12, markers.State.InPointFrame);

		var cueId = await markers.AddCueAtCurrentFrameAsync("Interview");
		Assert.True(cueId.HasValue);
		Assert.Equal(12, markers.State.CuePoints.Single().PositionFrame);

		await timeline.SeekToFrameAsync(25);
		Assert.True(await markers.SetOutAtCurrentFrameAsync());
		Assert.Equal(25, markers.State.OutPointFrame);

		await markers.JumpToInAsync();
		await markers.JumpToCueAsync(cueId!.Value);

		Assert.Equal(new long[] { 25, 12, 12 }, seekTargets);
	}

	[Fact]
	public async Task Absolute_trim_and_cue_navigation_use_authoritative_command_paths()
	{
		var assetId = new MediaAssetId(Id(100));
		var initial = new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			assetId,
			100,
			cuePoints:
			[
				new MediaCuePoint(new MediaCuePointId(Id(10)), "Intro", 10),
				new MediaCuePoint(new MediaCuePointId(Id(30)), "Guest", 30),
				new MediaCuePoint(new MediaCuePointId(Id(70)), "Outro", 70)
			]);
		var domain = new MediaMarkerController(initial);
		var markerCommands = new List<MediaMarkerCommand>();
		var seekTargets = new List<long>();

		await using var timeline = new MediaTimelineController((command, _) =>
		{
			var target = command.TargetFrame!.Value;
			seekTargets.Add(target);
			return ValueTask.FromResult(MediaTransportCommandResult.Accepted(TransportSnapshot(target, 100)));
		});
		timeline.ApplyConfirmedSnapshot(TransportSnapshot(50, 100));

		await using var markers = new MediaTimelineMarkerController(
			timeline,
			(command, _) =>
			{
				markerCommands.Add(command);
				return ValueTask.FromResult(domain.Apply(command));
			});
		markers.ApplyConfirmedSnapshot(initial);

		Assert.True(await markers.SetInAtFrameAsync(8));
		Assert.True(await markers.SetOutAtFrameAsync(80));
		Assert.True(await markers.JumpToPreviousCueAsync());
		Assert.True(await markers.JumpToNextCueAsync());

		Assert.Collection(
			markerCommands,
			command =>
			{
				Assert.Equal(MediaMarkerCommandKind.SetInPoint, command.Kind);
				Assert.Equal(8, command.PositionFrame);
			},
			command =>
			{
				Assert.Equal(MediaMarkerCommandKind.SetOutPoint, command.Kind);
				Assert.Equal(80, command.PositionFrame);
			});
		Assert.Equal(new long[] { 30, 70 }, seekTargets);
		Assert.Equal(8, markers.State.InPointFrame);
		Assert.Equal(80, markers.State.OutPointFrame);
	}

	private static MediaMarkerCommand Command(
		MediaAssetId assetId,
		MediaMarkerCommandKind kind,
		long? frame = null) =>
		new(MediaContractVersion.Current, assetId, kind, frame);

	private static MediaMarkerCommand Cue(
		MediaAssetId assetId,
		MediaMarkerCommandKind kind,
		MediaCuePointId cueId,
		long? frame = null,
		string? name = null) =>
		new(MediaContractVersion.Current, assetId, kind, frame, cueId, name);

	private static MediaTransportSnapshot TransportSnapshot(long frame, long totalFrames)
	{
		var duration = TimeSpan.FromMilliseconds(totalFrames * 20);
		var position = TimeSpan.FromMilliseconds(frame * 20);
		return new MediaTransportSnapshot(
			MediaContractVersion.Current,
			new MediaAssetId(Id(100)),
			new MediaSourceId(Id(101)),
			MediaTransportState.Paused,
			new MediaTransportPosition(
				frame,
				totalFrames,
				position,
				duration,
				duration - position,
				FrameRate.Fps50),
			null);
	}

	private static Identity Id(int value) =>
		Identity.Parse($"44000000-0000-0000-0000-{value:000000000000}");
}
