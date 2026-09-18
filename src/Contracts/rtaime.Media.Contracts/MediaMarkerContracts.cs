// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Core;

namespace rtaime.Media.Contracts;

public readonly record struct MediaCuePointId
{
	public MediaCuePointId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Cue-point identity must not be empty.", nameof(value));

		Value = value;
	}

	public Identity Value { get; }
	public static MediaCuePointId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public sealed record MediaCuePoint
{
	public const int MaximumNameLength = 64;

	public MediaCuePoint(MediaCuePointId id, string name, long positionFrame)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Cue-point name is required.", nameof(name));
		var normalizedName = name.Trim();
		if (normalizedName.Length > MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name), $"Cue-point names are limited to {MaximumNameLength} characters.");
		if (positionFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(positionFrame));

		Id = id;
		Name = normalizedName;
		PositionFrame = positionFrame;
	}

	public MediaCuePointId Id { get; }
	public string Name { get; }
	public long PositionFrame { get; }
}

public sealed record MediaMarkerSnapshot
{
	public MediaMarkerSnapshot(
		CompatibilityVersion version,
		MediaAssetId assetId,
		long totalFrames,
		long? inPointFrame = null,
		long? outPointFrame = null,
		IEnumerable<MediaCuePoint>? cuePoints = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (totalFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(totalFrames), "Marker snapshots require at least one frame.");

		ValidateFrame(inPointFrame, totalFrames, nameof(inPointFrame));
		ValidateFrame(outPointFrame, totalFrames, nameof(outPointFrame));
		if (inPointFrame.HasValue && outPointFrame.HasValue && inPointFrame.Value > outPointFrame.Value)
			throw new ArgumentException("IN point must not be after OUT point.");

		var normalizedCuePoints = (cuePoints ?? Array.Empty<MediaCuePoint>()).ToArray();
		if (normalizedCuePoints.Any(cue => cue.PositionFrame >= totalFrames))
			throw new ArgumentOutOfRangeException(nameof(cuePoints), "Cue-point positions must be inside the media asset.");
		if (normalizedCuePoints.GroupBy(cue => cue.Id).Any(group => group.Count() != 1))
			throw new ArgumentException("Cue-point identities must be unique.", nameof(cuePoints));

		Version = version;
		AssetId = assetId;
		TotalFrames = totalFrames;
		InPointFrame = inPointFrame;
		OutPointFrame = outPointFrame;
		CuePoints = new ReadOnlyCollection<MediaCuePoint>(
			normalizedCuePoints
				.OrderBy(cue => cue.PositionFrame)
				.ThenBy(cue => cue.Name, StringComparer.Ordinal)
				.ThenBy(cue => cue.Id.ToString(), StringComparer.Ordinal)
				.ToArray());
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public long TotalFrames { get; }
	public long? InPointFrame { get; }
	public long? OutPointFrame { get; }
	public IReadOnlyList<MediaCuePoint> CuePoints { get; }

	private static void ValidateFrame(long? frame, long totalFrames, string parameterName)
	{
		if (frame.HasValue && (frame.Value < 0 || frame.Value >= totalFrames))
			throw new ArgumentOutOfRangeException(parameterName, $"Marker frame must be in range 0..{totalFrames - 1}.");
	}
}

public enum MediaMarkerCommandKind
{
	SetInPoint = 1,
	ClearInPoint = 2,
	SetOutPoint = 3,
	ClearOutPoint = 4,
	AddCuePoint = 5,
	RenameCuePoint = 6,
	DeleteCuePoint = 7
}

public sealed record MediaMarkerCommand
{
	public MediaMarkerCommand(
		CompatibilityVersion version,
		MediaAssetId assetId,
		MediaMarkerCommandKind kind,
		long? positionFrame = null,
		MediaCuePointId? cuePointId = null,
		string? name = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaMarkerCommandKind), kind))
			throw new ArgumentOutOfRangeException(nameof(kind));

		switch (kind)
		{
			case MediaMarkerCommandKind.SetInPoint:
			case MediaMarkerCommandKind.SetOutPoint:
				if (!positionFrame.HasValue || positionFrame.Value < 0)
					throw new ArgumentOutOfRangeException(nameof(positionFrame));
				if (cuePointId.HasValue || name is not null)
					throw new ArgumentException("IN/OUT commands do not accept cue-point fields.");
				break;

			case MediaMarkerCommandKind.ClearInPoint:
			case MediaMarkerCommandKind.ClearOutPoint:
				if (positionFrame.HasValue || cuePointId.HasValue || name is not null)
					throw new ArgumentException("Clear-marker commands do not accept additional fields.");
				break;

			case MediaMarkerCommandKind.AddCuePoint:
				if (!positionFrame.HasValue || positionFrame.Value < 0)
					throw new ArgumentOutOfRangeException(nameof(positionFrame));
				if (!cuePointId.HasValue)
					throw new ArgumentException("Add-cue command requires a cue-point identity.", nameof(cuePointId));
				ValidateName(name);
				break;

			case MediaMarkerCommandKind.RenameCuePoint:
				if (positionFrame.HasValue)
					throw new ArgumentException("Rename-cue command does not accept a frame position.", nameof(positionFrame));
				if (!cuePointId.HasValue)
					throw new ArgumentException("Rename-cue command requires a cue-point identity.", nameof(cuePointId));
				ValidateName(name);
				break;

			case MediaMarkerCommandKind.DeleteCuePoint:
				if (positionFrame.HasValue || name is not null)
					throw new ArgumentException("Delete-cue command accepts only a cue-point identity.");
				if (!cuePointId.HasValue)
					throw new ArgumentException("Delete-cue command requires a cue-point identity.", nameof(cuePointId));
				break;
		}

		Version = version;
		AssetId = assetId;
		Kind = kind;
		PositionFrame = positionFrame;
		CuePointId = cuePointId;
		Name = name?.Trim();
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public MediaMarkerCommandKind Kind { get; }
	public long? PositionFrame { get; }
	public MediaCuePointId? CuePointId { get; }
	public string? Name { get; }

	private static void ValidateName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Cue-point name is required.", nameof(name));
		if (name.Trim().Length > MediaCuePoint.MaximumNameLength)
			throw new ArgumentOutOfRangeException(nameof(name), $"Cue-point names are limited to {MediaCuePoint.MaximumNameLength} characters.");
	}
}

public sealed record MediaMarkerCommandResult(
	bool Succeeded,
	MediaMarkerSnapshot Snapshot,
	Failure? Failure)
{
	public static MediaMarkerCommandResult Applied(MediaMarkerSnapshot snapshot) =>
		new(true, snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null);

	public static MediaMarkerCommandResult Rejected(MediaMarkerSnapshot snapshot, string code, string message) =>
		new(false, snapshot ?? throw new ArgumentNullException(nameof(snapshot)), new Failure(code, message));
}
