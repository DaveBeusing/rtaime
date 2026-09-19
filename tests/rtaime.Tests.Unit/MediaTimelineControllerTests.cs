// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaTimelineControllerTests
{
	[Theory]
	[InlineData(25, 1, 24, "00:00:00:24")]
	[InlineData(25, 1, 25, "00:00:01:00")]
	[InlineData(50, 1, 49, "00:00:00:49")]
	[InlineData(50, 1, 50, "00:00:01:00")]
	[InlineData(60000, 1001, 59, "00:00:00:59")]
	[InlineData(60000, 1001, 60, "00:00:01:00")]
	[InlineData(240, 1, 239, "00:00:00:239")]
	[InlineData(240, 1, 240, "00:00:01:00")]
	[InlineData(240000, 1001, 240, "00:00:01:00")]
	public void Timecode_uses_nominal_frame_field_for_supported_rates(
		long numerator,
		long denominator,
		long frame,
		string expected)
	{
		Assert.Equal(expected, MediaTimelineTimecode.FormatFrame(frame, new FrameRate(numerator, denominator)));
	}

	[Theory]
	[InlineData(1.0)]
	[InlineData(1.25)]
	[InlineData(1.5)]
	public void Pointer_mapping_is_dpi_independent(double dpiScale)
	{
		var frame = MediaTimelineGeometry.FrameFromPhysicalPosition(
			750 * dpiScale,
			1000 * dpiScale,
			dpiScale,
			101);

		Assert.Equal(75, frame);
	}

	[Fact]
	public async Task Confirmed_snapshot_reconciles_optimistic_drag_position()
	{
		var sent = new List<long>();
		await using var controller = new MediaTimelineController(
			(command, _) =>
			{
				var target = command.TargetFrame!.Value;
				sent.Add(target);
				return ValueTask.FromResult(MediaTransportCommandResult.Accepted(Snapshot(target, 100, MediaTransportState.Paused)));
			},
			TimeSpan.FromSeconds(1));

		controller.ApplyConfirmedSnapshot(Snapshot(10, 100, MediaTransportState.Paused));
		controller.BeginPointerSeek();
		controller.PreviewPointerSeek(20);
		Assert.Equal(20, controller.State.DisplayFrame);
		Assert.Equal(10, controller.State.ConfirmedFrame);

		controller.ApplyConfirmedSnapshot(Snapshot(12, 100, MediaTransportState.Paused));
		Assert.Equal(20, controller.State.DisplayFrame);
		Assert.Equal(12, controller.State.ConfirmedFrame);

		await controller.CompletePointerSeekAsync(20);
		Assert.Equal(new long[] { 20 }, sent);
		Assert.Equal(20, controller.State.ConfirmedFrame);
		Assert.Equal(20, controller.State.DisplayFrame);
		Assert.False(controller.State.HasPendingSeek);
	}

	[Fact]
	public async Task Rapid_drag_updates_are_coalesced_and_final_position_is_exact()
	{
		var sent = new List<long>();
		await using var controller = new MediaTimelineController(
			(command, _) =>
			{
				var target = command.TargetFrame!.Value;
				sent.Add(target);
				return ValueTask.FromResult(MediaTransportCommandResult.Accepted(Snapshot(target, 100, MediaTransportState.Playing)));
			},
			TimeSpan.FromMilliseconds(100));

		controller.ApplyConfirmedSnapshot(Snapshot(0, 100, MediaTransportState.Playing));
		controller.BeginPointerSeek();
		for (var frame = 1; frame <= 80; frame++)
			controller.PreviewPointerSeek(frame);

		await controller.CompletePointerSeekAsync(80);
		await controller.FlushPendingSeekAsync();

		Assert.InRange(sent.Count, 1, 2);
		Assert.Equal(80, sent[^1]);
		Assert.Equal(80, controller.State.ConfirmedFrame);
	}

	[Theory]
	[InlineData(0, 0.0)]
	[InlineData(49, 1.0)]
	public async Task Timeline_clamps_and_reports_clip_boundaries(long frame, double expectedProgress)
	{
		await using var controller = new MediaTimelineController(
			(command, _) => ValueTask.FromResult(MediaTransportCommandResult.Accepted(
				Snapshot(command.TargetFrame!.Value, 50, MediaTransportState.Ready))));
		controller.ApplyConfirmedSnapshot(Snapshot(frame, 50, MediaTransportState.Ready));

		Assert.Equal(expectedProgress, controller.State.Progress, 6);
		Assert.Equal("00:00:01:00", controller.State.DurationTimecode);
	}

	[Theory]
	[InlineData(3, 2, 5)]
	[InlineData(0, -1, 0)]
	[InlineData(49, 1, 49)]
	public async Task Keyboard_relative_seek_uses_confirmed_state_and_clamps(
		long current,
		long delta,
		long expected)
	{
		MediaTransportCommand? observed = null;
		await using var controller = new MediaTimelineController(
			(command, _) =>
			{
				observed = command;
				return ValueTask.FromResult(MediaTransportCommandResult.Accepted(
					Snapshot(command.TargetFrame!.Value, 50, MediaTransportState.Paused)));
			});
		controller.ApplyConfirmedSnapshot(Snapshot(current, 50, MediaTransportState.Paused));

		await controller.SeekRelativeAsync(delta);

		Assert.NotNull(observed);
		Assert.Equal(MediaTransportCommandKind.Seek, observed!.Kind);
		Assert.Equal(expected, observed.TargetFrame);
		Assert.Equal(expected, controller.State.ConfirmedFrame);
	}

	[Fact]
	public void Zoomed_visible_range_preserves_frame_boundaries_without_fractional_time_state()
	{
		var centered = MediaTimelineGeometry.CalculateVisibleRange(1000, 4.0, 500);

		Assert.Equal(250, centered.FrameCount);
		Assert.InRange(500, centered.StartFrame, centered.EndFrame);

		var clampedStart = MediaTimelineGeometry.CalculateVisibleRangeFromStart(1000, 4.0, 990);
		Assert.Equal(750, clampedStart.StartFrame);
		Assert.Equal(999, clampedStart.EndFrame);
	}

	[Theory]
	[InlineData(0, 200, 400)]
	[InlineData(500, 300, 400)]
	[InlineData(1000, 400, 400)]
	public void Visible_pointer_mapping_is_frame_accurate(double x, long expected, long visibleEnd)
	{
		var range = new MediaTimelineVisibleRange(200, visibleEnd);
		var frame = MediaTimelineGeometry.FrameFromVisiblePosition(x, 1000, range);

		Assert.Equal(expected, frame);
		Assert.Equal(x, MediaTimelineGeometry.LogicalPositionFromFrame(frame, 1000, range), 6);
	}

	[Fact]
	public void Snap_uses_nearest_frame_inside_bounded_threshold()
	{
		var snapFrames = new long[] { 10, 25, 40 };

		Assert.Equal(25, MediaTimelineGeometry.SnapFrame(23, snapFrames, 3));
		Assert.Equal(23, MediaTimelineGeometry.SnapFrame(23, snapFrames, 1));
		Assert.Equal(40, MediaTimelineGeometry.SnapFrame(38, snapFrames, 2));
	}

	private static MediaTransportSnapshot Snapshot(
		long currentFrame,
		long totalFrames,
		MediaTransportState state)
	{
		var frameRate = FrameRate.Fps50;
		var duration = FrameTime(totalFrames, frameRate);
		var position = FrameTime(currentFrame, frameRate);
		return new MediaTransportSnapshot(
			MediaContractVersion.Current,
			new MediaAssetId(Id(1)),
			new MediaSourceId(Id(2)),
			state,
			new MediaTransportPosition(
				currentFrame,
				totalFrames,
				position,
				duration,
				duration - position,
				frameRate),
			null);
	}

	private static TimeSpan FrameTime(long frame, FrameRate frameRate)
	{
		var ticks = decimal.Round(
			(decimal)frame * TimeSpan.TicksPerSecond * frameRate.Denominator / frameRate.Numerator,
			0,
			MidpointRounding.AwayFromZero);
		return TimeSpan.FromTicks(checked((long)ticks));
	}

	private static Identity Id(int value) =>
		Identity.Parse($"43000000-0000-0000-0000-{value:000000000000}");
}
