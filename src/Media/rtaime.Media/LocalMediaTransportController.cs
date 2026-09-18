// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Media;

public sealed class LocalMediaTransportController
{
	private readonly LocalMediaProbe _probe;
	private readonly Func<long, Failure?> _seekFrame;
	private readonly long _totalFrames;
	private MediaTransportState _state;
	private long _currentFrame;
	private Failure? _failure;

	public LocalMediaTransportController(LocalMediaProbe probe, Func<long, Failure?> seekFrame)
	{
		_probe = probe ?? throw new ArgumentNullException(nameof(probe));
		_seekFrame = seekFrame ?? throw new ArgumentNullException(nameof(seekFrame));
		_totalFrames = LocalMediaFrameMath.GetTotalFrames(probe);
		_state = MediaTransportState.Ready;
	}

	public MediaTransportSnapshot Snapshot => BuildSnapshot();
	public long TotalFrames => _totalFrames;

	public MediaTransportCommandResult Apply(MediaTransportCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		if (command.AssetId != _probe.AssetId)
		{
			return MediaTransportCommandResult.Rejected(
				Snapshot,
				"media.transport.asset_mismatch",
				"Transport command targets a different media asset.");
		}
		if (_state == MediaTransportState.Error)
		{
			return MediaTransportCommandResult.Rejected(
				Snapshot,
				"media.transport.error_state",
				"Transport commands are rejected while the media source is in the error state.");
		}

		return command.Kind switch
		{
			MediaTransportCommandKind.Play => Play(),
			MediaTransportCommandKind.Pause => Pause(),
			MediaTransportCommandKind.Stop => Stop(),
			MediaTransportCommandKind.Seek => Seek(command.TargetFrame!.Value),
			MediaTransportCommandKind.JumpToStart => JumpToStart(),
			MediaTransportCommandKind.StepBackward => Step(-1),
			MediaTransportCommandKind.StepForward => Step(1),
			_ => MediaTransportCommandResult.Rejected(
				Snapshot,
				"media.transport.command_unsupported",
				$"Unsupported media transport command '{command.Kind}'.")
		};
	}

	public void ObserveDecodedTimestamp(long presentationTimestamp, Timebase timebase)
	{
		if (presentationTimestamp < 0)
			return;

		_currentFrame = Math.Clamp(
			LocalMediaFrameMath.TimestampToFrame(presentationTimestamp, timebase, _probe.VideoFormat.FrameRate),
			0,
			_totalFrames - 1);
	}

	public void MarkEnded()
	{
		if (_state == MediaTransportState.Error)
			return;

		_currentFrame = _totalFrames - 1;
		_state = MediaTransportState.Ended;
		_failure = null;
	}

	public void MarkError(Failure failure)
	{
		_state = MediaTransportState.Error;
		_failure = failure;
	}

	private MediaTransportCommandResult Play()
	{
		if (_state == MediaTransportState.Ended)
		{
			var seek = SeekDecoder(0);
			if (seek is not null)
				return seek;
			_currentFrame = 0;
		}
		else if (_state is not (MediaTransportState.Ready or MediaTransportState.Paused))
		{
			return InvalidTransition(MediaTransportCommandKind.Play);
		}

		_state = MediaTransportState.Playing;
		return MediaTransportCommandResult.Accepted(Snapshot);
	}

	private MediaTransportCommandResult Pause()
	{
		if (_state != MediaTransportState.Playing)
			return InvalidTransition(MediaTransportCommandKind.Pause);

		_state = MediaTransportState.Paused;
		return MediaTransportCommandResult.Accepted(Snapshot);
	}

	private MediaTransportCommandResult Stop()
	{
		if (_state is not (MediaTransportState.Ready or MediaTransportState.Playing or MediaTransportState.Paused or MediaTransportState.Ended))
			return InvalidTransition(MediaTransportCommandKind.Stop);

		var seek = SeekDecoder(0);
		if (seek is not null)
			return seek;

		_currentFrame = 0;
		_state = MediaTransportState.Ready;
		return MediaTransportCommandResult.Accepted(Snapshot);
	}

	private MediaTransportCommandResult Seek(long requestedFrame)
	{
		if (_state is not (MediaTransportState.Ready or MediaTransportState.Playing or MediaTransportState.Paused or MediaTransportState.Ended))
			return InvalidTransition(MediaTransportCommandKind.Seek);

		var target = Math.Clamp(requestedFrame, 0, _totalFrames - 1);
		var seek = SeekDecoder(target);
		if (seek is not null)
			return seek;

		_currentFrame = target;
		if (_state == MediaTransportState.Ended)
			_state = MediaTransportState.Paused;
		return MediaTransportCommandResult.Accepted(Snapshot);
	}

	private MediaTransportCommandResult JumpToStart()
	{
		if (_state is not (MediaTransportState.Ready or MediaTransportState.Playing or MediaTransportState.Paused or MediaTransportState.Ended))
			return InvalidTransition(MediaTransportCommandKind.JumpToStart);

		var seek = SeekDecoder(0);
		if (seek is not null)
			return seek;

		_currentFrame = 0;
		if (_state == MediaTransportState.Ended)
			_state = MediaTransportState.Paused;
		return MediaTransportCommandResult.Accepted(Snapshot);
	}

	private MediaTransportCommandResult Step(long delta)
	{
		var kind = delta < 0 ? MediaTransportCommandKind.StepBackward : MediaTransportCommandKind.StepForward;
		if (_state is not (MediaTransportState.Ready or MediaTransportState.Paused or MediaTransportState.Ended))
			return InvalidTransition(kind);

		var target = Math.Clamp(_currentFrame + delta, 0, _totalFrames - 1);
		var seek = SeekDecoder(target);
		if (seek is not null)
			return seek;

		_currentFrame = target;
		_state = MediaTransportState.Paused;
		return MediaTransportCommandResult.Accepted(Snapshot);
	}

	private MediaTransportCommandResult? SeekDecoder(long targetFrame)
	{
		var failure = _seekFrame(targetFrame);
		if (failure is null)
			return null;

		MarkError(failure.Value);
		return new MediaTransportCommandResult(false, Snapshot, failure);
	}

	private MediaTransportCommandResult InvalidTransition(MediaTransportCommandKind command) =>
		MediaTransportCommandResult.Rejected(
			Snapshot,
			"media.transport.transition_invalid",
			$"Transport command '{command}' is not valid while state is '{_state}'.");

	private MediaTransportSnapshot BuildSnapshot()
	{
		var position = LocalMediaFrameMath.FrameToTimeSpan(_currentFrame, _probe.VideoFormat.FrameRate);
		if (position > _probe.Duration)
			position = _probe.Duration;
		var remaining = _probe.Duration - position;
		if (remaining < TimeSpan.Zero)
			remaining = TimeSpan.Zero;

		return new MediaTransportSnapshot(
			MediaContractVersion.Current,
			_probe.AssetId,
			_probe.SourceId,
			_state,
			new MediaTransportPosition(
				_currentFrame,
				_totalFrames,
				position,
				_probe.Duration,
				remaining,
				_probe.VideoFormat.FrameRate),
			_failure);
	}
}

internal static class LocalMediaFrameMath
{
	private const decimal TicksPerSecond = TimeSpan.TicksPerSecond;

	public static long GetTotalFrames(LocalMediaProbe probe)
	{
		ArgumentNullException.ThrowIfNull(probe);
		var rate = probe.VideoFormat.FrameRate;
		var exactFrames = (decimal)probe.Duration.Ticks * rate.Numerator /
			(TicksPerSecond * rate.Denominator);
		return Math.Max(1, checked((long)Math.Ceiling(exactFrames)));
	}

	public static TimeSpan FrameToTimeSpan(long frameNumber, FrameRate frameRate)
	{
		if (frameNumber < 0)
			throw new ArgumentOutOfRangeException(nameof(frameNumber));

		var ticks = decimal.Round(
			(decimal)frameNumber * TimeSpan.TicksPerSecond * frameRate.Denominator / frameRate.Numerator,
			0,
			MidpointRounding.AwayFromZero);
		return TimeSpan.FromTicks(checked((long)ticks));
	}

	public static long TimestampToFrame(long timestamp, Timebase timebase, FrameRate frameRate)
	{
		if (timestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timestamp));

		var frame = decimal.Round(
			(decimal)timestamp * timebase.Numerator * frameRate.Numerator /
			(timebase.Denominator * frameRate.Denominator),
			0,
			MidpointRounding.AwayFromZero);
		return checked((long)frame);
	}
}
