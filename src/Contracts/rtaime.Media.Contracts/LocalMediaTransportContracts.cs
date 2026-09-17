// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Media.Contracts;

public enum MediaTransportState
{
	Unloaded = 1,
	Loading = 2,
	Ready = 3,
	Playing = 4,
	Paused = 5,
	Ended = 6,
	Error = 7
}

public enum MediaTransportCommandKind
{
	Play = 1,
	Pause = 2,
	Stop = 3,
	Seek = 4,
	JumpToStart = 5,
	StepBackward = 6,
	StepForward = 7
}

public readonly record struct MediaTransportPosition
{
	public MediaTransportPosition(
		long currentFrame,
		long totalFrames,
		TimeSpan position,
		TimeSpan duration,
		TimeSpan remaining,
		FrameRate frameRate)
	{
		if (currentFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(currentFrame));
		if (totalFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(totalFrames));
		if (currentFrame >= totalFrames)
			throw new ArgumentOutOfRangeException(nameof(currentFrame), "Current frame must be inside the clip frame range.");
		if (duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));
		if (position < TimeSpan.Zero || position > duration)
			throw new ArgumentOutOfRangeException(nameof(position));
		if (remaining < TimeSpan.Zero || remaining > duration)
			throw new ArgumentOutOfRangeException(nameof(remaining));

		CurrentFrame = currentFrame;
		TotalFrames = totalFrames;
		Position = position;
		Duration = duration;
		Remaining = remaining;
		FrameRate = frameRate;
	}

	public long CurrentFrame { get; }
	public long TotalFrames { get; }
	public TimeSpan Position { get; }
	public TimeSpan Duration { get; }
	public TimeSpan Remaining { get; }
	public FrameRate FrameRate { get; }
}

public sealed record MediaTransportSnapshot
{
	public MediaTransportSnapshot(
		CompatibilityVersion version,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		MediaTransportState state,
		MediaTransportPosition position,
		Failure? failure)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaTransportState), state))
			throw new ArgumentOutOfRangeException(nameof(state));
		if (state == MediaTransportState.Error && failure is null)
			throw new ArgumentException("Error transport state requires failure details.", nameof(failure));
		if (state != MediaTransportState.Error && failure is not null)
			throw new ArgumentException("Failure details are only valid for the error transport state.", nameof(failure));

		Version = version;
		AssetId = assetId;
		SourceId = sourceId;
		State = state;
		Position = position;
		Failure = failure;
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public MediaSourceId SourceId { get; }
	public MediaTransportState State { get; }
	public MediaTransportPosition Position { get; }
	public Failure? Failure { get; }
}

public sealed record MediaTransportCommand
{
	public MediaTransportCommand(
		CompatibilityVersion version,
		MediaAssetId assetId,
		MediaTransportCommandKind kind,
		long? targetFrame = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaTransportCommandKind), kind))
			throw new ArgumentOutOfRangeException(nameof(kind));
		if (kind == MediaTransportCommandKind.Seek && targetFrame is null)
			throw new ArgumentException("Seek requires a target frame.", nameof(targetFrame));
		if (kind != MediaTransportCommandKind.Seek && targetFrame is not null)
			throw new ArgumentException("Only seek accepts a target frame.", nameof(targetFrame));

		Version = version;
		AssetId = assetId;
		Kind = kind;
		TargetFrame = targetFrame;
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public MediaTransportCommandKind Kind { get; }
	public long? TargetFrame { get; }
}

public sealed record MediaTransportCommandResult(
	bool Succeeded,
	MediaTransportSnapshot Snapshot,
	Failure? Failure)
{
	public static MediaTransportCommandResult Accepted(MediaTransportSnapshot snapshot) =>
		new(true, snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null);

	public static MediaTransportCommandResult Rejected(MediaTransportSnapshot snapshot, string code, string message) =>
		new(false, snapshot ?? throw new ArgumentNullException(nameof(snapshot)), new Failure(code, message));
}
