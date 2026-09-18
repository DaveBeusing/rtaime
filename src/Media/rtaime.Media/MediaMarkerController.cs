// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

public sealed class MediaMarkerController
{
	private MediaMarkerSnapshot _snapshot;

	public MediaMarkerController(MediaMarkerSnapshot snapshot)
	{
		_snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
	}

	public MediaMarkerSnapshot Snapshot => _snapshot;

	public MediaMarkerCommandResult Apply(MediaMarkerCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		if (command.AssetId != _snapshot.AssetId)
		{
			return MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.asset_mismatch",
				"Marker command asset does not match the active media asset.");
		}

		return command.Kind switch
		{
			MediaMarkerCommandKind.SetInPoint => SetInPoint(command.PositionFrame!.Value),
			MediaMarkerCommandKind.ClearInPoint => Replace(inPointFrame: null, keepInPoint: false),
			MediaMarkerCommandKind.SetOutPoint => SetOutPoint(command.PositionFrame!.Value),
			MediaMarkerCommandKind.ClearOutPoint => Replace(outPointFrame: null, keepOutPoint: false),
			MediaMarkerCommandKind.AddCuePoint => AddCue(command),
			MediaMarkerCommandKind.RenameCuePoint => RenameCue(command),
			MediaMarkerCommandKind.DeleteCuePoint => DeleteCue(command.CuePointId!.Value),
			_ => MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.command_unsupported",
				$"Unsupported marker command '{command.Kind}'.")
		};
	}

	private MediaMarkerCommandResult SetInPoint(long frame)
	{
		if (!FrameIsValid(frame))
			return FrameOutOfRange(frame);
		if (_snapshot.OutPointFrame.HasValue && frame > _snapshot.OutPointFrame.Value)
		{
			return MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.in_after_out",
				"IN point must not be after the current OUT point.");
		}

		return Replace(inPointFrame: frame, keepInPoint: false);
	}

	private MediaMarkerCommandResult SetOutPoint(long frame)
	{
		if (!FrameIsValid(frame))
			return FrameOutOfRange(frame);
		if (_snapshot.InPointFrame.HasValue && frame < _snapshot.InPointFrame.Value)
		{
			return MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.out_before_in",
				"OUT point must not be before the current IN point.");
		}

		return Replace(outPointFrame: frame, keepOutPoint: false);
	}

	private MediaMarkerCommandResult AddCue(MediaMarkerCommand command)
	{
		var frame = command.PositionFrame!.Value;
		var cueId = command.CuePointId!.Value;
		if (!FrameIsValid(frame))
			return FrameOutOfRange(frame);
		if (_snapshot.CuePoints.Any(cue => cue.Id == cueId))
		{
			return MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.cue_id_conflict",
				$"Cue point '{cueId}' already exists.");
		}

		var cues = _snapshot.CuePoints
			.Append(new MediaCuePoint(cueId, command.Name!, frame))
			.ToArray();
		return Replace(cuePoints: cues);
	}

	private MediaMarkerCommandResult RenameCue(MediaMarkerCommand command)
	{
		var cueId = command.CuePointId!.Value;
		var existing = _snapshot.CuePoints.FirstOrDefault(cue => cue.Id == cueId);
		if (existing is null)
		{
			return MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.cue_not_found",
				$"Cue point '{cueId}' does not exist.");
		}

		var cues = _snapshot.CuePoints
			.Select(cue => cue.Id == cueId
				? new MediaCuePoint(cue.Id, command.Name!, cue.PositionFrame)
				: cue)
			.ToArray();
		return Replace(cuePoints: cues);
	}

	private MediaMarkerCommandResult DeleteCue(MediaCuePointId cueId)
	{
		if (!_snapshot.CuePoints.Any(cue => cue.Id == cueId))
		{
			return MediaMarkerCommandResult.Rejected(
				_snapshot,
				"media.marker.cue_not_found",
				$"Cue point '{cueId}' does not exist.");
		}

		return Replace(cuePoints: _snapshot.CuePoints.Where(cue => cue.Id != cueId).ToArray());
	}

	private MediaMarkerCommandResult Replace(
		long? inPointFrame = null,
		long? outPointFrame = null,
		IEnumerable<MediaCuePoint>? cuePoints = null,
		bool keepInPoint = true,
		bool keepOutPoint = true)
	{
		_snapshot = new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			_snapshot.AssetId,
			_snapshot.TotalFrames,
			keepInPoint ? _snapshot.InPointFrame : inPointFrame,
			keepOutPoint ? _snapshot.OutPointFrame : outPointFrame,
			cuePoints ?? _snapshot.CuePoints);
		return MediaMarkerCommandResult.Applied(_snapshot);
	}

	private MediaMarkerCommandResult FrameOutOfRange(long frame) =>
		MediaMarkerCommandResult.Rejected(
			_snapshot,
			"media.marker.frame_out_of_range",
			$"Marker frame '{frame}' is outside range 0..{_snapshot.TotalFrames - 1}.");

	private bool FrameIsValid(long frame) => frame >= 0 && frame < _snapshot.TotalFrames;
}
