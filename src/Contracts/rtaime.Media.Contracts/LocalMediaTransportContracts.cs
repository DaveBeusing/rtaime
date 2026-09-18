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

public enum MediaDeckEndBehavior
{
	HoldLastFrame = 1,
	Stop = 2,
	Loop = 3,
	ReturnToIn = 4
}

public enum MediaTransportCommandKind
{
	Play = 1,
	Pause = 2,
	Stop = 3,
	Seek = 4,
	JumpToStart = 5,
	StepBackward = 6,
	StepForward = 7,
	ConfigurePlayback = 8
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
		Failure? failure,
		bool autoPlayOnProgram = true,
		MediaDeckEndBehavior endBehavior = MediaDeckEndBehavior.HoldLastFrame,
		bool isOnProgram = false,
		long? effectiveStartFrame = null,
		long? effectiveEndFrame = null,
		long? effectiveRemainingFrames = null,
		TimeSpan? effectiveRemaining = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaTransportState), state))
			throw new ArgumentOutOfRangeException(nameof(state));
		if (!Enum.IsDefined(typeof(MediaDeckEndBehavior), endBehavior))
			throw new ArgumentOutOfRangeException(nameof(endBehavior));
		if (state == MediaTransportState.Error && failure is null)
			throw new ArgumentException("Error transport state requires failure details.", nameof(failure));
		if (state != MediaTransportState.Error && failure is not null)
			throw new ArgumentException("Failure details are only valid for the error transport state.", nameof(failure));

		var rangeStart = effectiveStartFrame ?? 0;
		var rangeEnd = effectiveEndFrame ?? checked(position.TotalFrames - 1);
		if (rangeStart < 0 || rangeStart >= position.TotalFrames)
			throw new ArgumentOutOfRangeException(nameof(effectiveStartFrame));
		if (rangeEnd < rangeStart || rangeEnd >= position.TotalFrames)
			throw new ArgumentOutOfRangeException(nameof(effectiveEndFrame));
		var remainingFrames = effectiveRemainingFrames ?? Math.Max(0, rangeEnd - Math.Max(position.CurrentFrame, rangeStart));
		if (remainingFrames < 0 || remainingFrames > rangeEnd - rangeStart)
			throw new ArgumentOutOfRangeException(nameof(effectiveRemainingFrames));
		var remainingTime = effectiveRemaining ?? FrameDuration(remainingFrames, position.FrameRate);
		if (remainingTime < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(effectiveRemaining));

		Version = version;
		AssetId = assetId;
		SourceId = sourceId;
		State = state;
		Position = position;
		Failure = failure;
		AutoPlayOnProgram = autoPlayOnProgram;
		EndBehavior = endBehavior;
		IsOnProgram = isOnProgram;
		EffectiveStartFrame = rangeStart;
		EffectiveEndFrame = rangeEnd;
		EffectiveRemainingFrames = remainingFrames;
		EffectiveRemaining = remainingTime;
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public MediaSourceId SourceId { get; }
	public MediaTransportState State { get; }
	public MediaTransportPosition Position { get; }
	public Failure? Failure { get; }
	public bool AutoPlayOnProgram { get; }
	public MediaDeckEndBehavior EndBehavior { get; }
	public bool IsOnProgram { get; }
	public long EffectiveStartFrame { get; }
	public long EffectiveEndFrame { get; }
	public long EffectiveRemainingFrames { get; }
	public TimeSpan EffectiveRemaining { get; }

	private static TimeSpan FrameDuration(long frameCount, FrameRate frameRate) =>
		TimeSpan.FromSeconds(frameCount * frameRate.Denominator / (double)frameRate.Numerator);
}

public sealed record MediaTransportCommand
{
	public MediaTransportCommand(
		CompatibilityVersion version,
		MediaAssetId assetId,
		MediaTransportCommandKind kind,
		long? targetFrame = null,
		bool? autoPlayOnProgram = null,
		MediaDeckEndBehavior? endBehavior = null,
		long? inPointFrame = null,
		long? outPointFrame = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaTransportCommandKind), kind))
			throw new ArgumentOutOfRangeException(nameof(kind));
		if (kind == MediaTransportCommandKind.Seek && targetFrame is null)
			throw new ArgumentException("Seek requires a target frame.", nameof(targetFrame));
		if (kind != MediaTransportCommandKind.Seek && targetFrame is not null)
			throw new ArgumentException("Only seek accepts a target frame.", nameof(targetFrame));

		if (kind == MediaTransportCommandKind.ConfigurePlayback)
		{
			if (autoPlayOnProgram is null)
				throw new ArgumentException("Playback configuration requires Auto Play on Program.", nameof(autoPlayOnProgram));
			if (endBehavior is null || !Enum.IsDefined(typeof(MediaDeckEndBehavior), endBehavior.Value))
				throw new ArgumentException("Playback configuration requires a valid end behavior.", nameof(endBehavior));
			if (inPointFrame < 0)
				throw new ArgumentOutOfRangeException(nameof(inPointFrame));
			if (outPointFrame < 0)
				throw new ArgumentOutOfRangeException(nameof(outPointFrame));
			if (inPointFrame.HasValue && outPointFrame.HasValue && inPointFrame.Value > outPointFrame.Value)
				throw new ArgumentException("Playback IN point must not be after OUT point.");
		}
		else if (autoPlayOnProgram is not null || endBehavior is not null || inPointFrame is not null || outPointFrame is not null)
		{
			throw new ArgumentException("Playback policy fields are valid only for ConfigurePlayback.");
		}

		Version = version;
		AssetId = assetId;
		Kind = kind;
		TargetFrame = targetFrame;
		AutoPlayOnProgram = autoPlayOnProgram;
		EndBehavior = endBehavior;
		InPointFrame = inPointFrame;
		OutPointFrame = outPointFrame;
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public MediaTransportCommandKind Kind { get; }
	public long? TargetFrame { get; }
	public bool? AutoPlayOnProgram { get; }
	public MediaDeckEndBehavior? EndBehavior { get; }
	public long? InPointFrame { get; }
	public long? OutPointFrame { get; }
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
