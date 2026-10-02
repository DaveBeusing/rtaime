// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Control.Contracts;

public static class ReplayControlContractVersion
{
	public static CompatibilityVersion Current => new(1, 0);

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (version != Current)
			throw new NotSupportedException($"Unsupported replay control contract version {version}.");
	}
}

public enum ReplayControlCaptureState
{
	Disabled = 1,
	Starting = 2,
	Capturing = 3,
	Degraded = 4,
	Failed = 5,
	Stopping = 6
}

public enum ReplayControlClipState
{
	Idle = 1,
	Marked = 2,
	Finalizing = 3,
	Ready = 4,
	Failed = 5
}

public sealed record ReplayControlSnapshot(
	CompatibilityVersion Version,
	ReplayControlCaptureState CaptureState,
	ReplayControlClipState ClipState,
	TimeSpan Retention,
	TimeSpan RetainedDuration,
	long MaximumStorageBytes,
	long RetainedBytes,
	TimeSpan SegmentDuration,
	int RetainedSegmentCount,
	TimeSpan? MarkIn,
	TimeSpan? MarkOut,
	ulong AcceptedSamples,
	ulong DroppedSamples,
	ulong FinalizedSegments,
	ulong EvictedSegments,
	ulong Discontinuities,
	Failure? Failure)
{
	public static ReplayControlSnapshot Unavailable { get; } = new(
		ReplayControlContractVersion.Current,
		ReplayControlCaptureState.Disabled,
		ReplayControlClipState.Idle,
		TimeSpan.Zero,
		TimeSpan.Zero,
		0,
		0,
		TimeSpan.Zero,
		0,
		null,
		null,
		0,
		0,
		0,
		0,
		0,
		new Failure("replay.unavailable", "Replay capture is unavailable."));

	public bool HasSelection => MarkIn is not null && MarkOut is not null && MarkOut > MarkIn;
	public TimeSpan? SelectedDuration => HasSelection ? MarkOut - MarkIn : null;
}

public sealed record ReplayClipAssetResult(
	CompatibilityVersion Version,
	bool Succeeded,
	string ClipId,
	MediaAssetId? AssetId,
	string? SourceLocation,
	TimeSpan SourceIn,
	TimeSpan SourceOut,
	string? Sha256,
	Failure? Failure)
{
	public TimeSpan Duration => SourceOut - SourceIn;
}
