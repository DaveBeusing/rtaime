// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

public static class ReplayContractVersion
{
	public static CompatibilityVersion Current => new(1, 0);

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (version != Current)
			throw new NotSupportedException($"Unsupported replay contract version {version}.");
	}
}

public readonly record struct ReplaySegmentId
{
	public ReplaySegmentId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Replay segment identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ReplaySegmentId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public readonly record struct ReplayClipId
{
	public ReplayClipId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Replay clip identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static ReplayClipId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public sealed record ReplayBufferPolicy
{
	public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(2);
	public const long DefaultMaximumStorageBytes = 4L * 1024 * 1024 * 1024;
	public static readonly TimeSpan DefaultSegmentDuration = TimeSpan.FromSeconds(2);

	public ReplayBufferPolicy(TimeSpan retentionDuration, long maximumStorageBytes, TimeSpan segmentDuration)
	{
		if (retentionDuration <= TimeSpan.Zero || retentionDuration > TimeSpan.FromMinutes(30))
			throw new ArgumentOutOfRangeException(nameof(retentionDuration), "Replay retention must be between zero and 30 minutes.");
		if (maximumStorageBytes <= 0 || maximumStorageBytes > 256L * 1024 * 1024 * 1024)
			throw new ArgumentOutOfRangeException(nameof(maximumStorageBytes), "Replay storage quota must be positive and bounded.");
		if (segmentDuration < TimeSpan.FromMilliseconds(250) || segmentDuration > TimeSpan.FromSeconds(30))
			throw new ArgumentOutOfRangeException(nameof(segmentDuration), "Replay segment duration must be between 250 ms and 30 seconds.");
		if (segmentDuration > retentionDuration)
			throw new ArgumentOutOfRangeException(nameof(segmentDuration), "Replay segment duration cannot exceed retention.");

		RetentionDuration = retentionDuration;
		MaximumStorageBytes = maximumStorageBytes;
		SegmentDuration = segmentDuration;
	}

	public TimeSpan RetentionDuration { get; }
	public long MaximumStorageBytes { get; }
	public TimeSpan SegmentDuration { get; }

	public static ReplayBufferPolicy Default { get; } =
		new(DefaultRetention, DefaultMaximumStorageBytes, DefaultSegmentDuration);
}

public sealed record ReplaySegmentDescriptor
{
	public ReplaySegmentDescriptor(
		ReplaySegmentId segmentId,
		string path,
		ulong firstProgramSequence,
		ulong lastProgramSequence,
		TimeSpan start,
		TimeSpan end,
		long bytes,
		bool hasAudio,
		bool discontinuityBefore)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("Replay segment path is required.", nameof(path));
		if (lastProgramSequence < firstProgramSequence)
			throw new ArgumentOutOfRangeException(nameof(lastProgramSequence));
		if (start < TimeSpan.Zero || end <= start)
			throw new ArgumentOutOfRangeException(nameof(end), "Replay segment time range must be positive and ordered.");
		if (bytes <= 0)
			throw new ArgumentOutOfRangeException(nameof(bytes));

		SegmentId = segmentId;
		Path = System.IO.Path.GetFullPath(path);
		FirstProgramSequence = firstProgramSequence;
		LastProgramSequence = lastProgramSequence;
		Start = start;
		End = end;
		Bytes = bytes;
		HasAudio = hasAudio;
		DiscontinuityBefore = discontinuityBefore;
	}

	public ReplaySegmentId SegmentId { get; }
	public string Path { get; }
	public ulong FirstProgramSequence { get; }
	public ulong LastProgramSequence { get; }
	public TimeSpan Start { get; }
	public TimeSpan End { get; }
	public long Bytes { get; }
	public bool HasAudio { get; }
	public bool DiscontinuityBefore { get; }
	public TimeSpan Duration => End - Start;
}

public sealed record ReplayRange
{
	public ReplayRange(TimeSpan @in, TimeSpan @out)
	{
		if (@in < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(@in));
		if (@out <= @in)
			throw new ArgumentOutOfRangeException(nameof(@out), "Replay OUT must be later than IN.");
		In = @in;
		Out = @out;
	}

	public TimeSpan In { get; }
	public TimeSpan Out { get; }
	public TimeSpan Duration => Out - In;
}

public enum ReplayEnqueueStatus
{
	Accepted = 1,
	Dropped = 2,
	Rejected = 3
}

public sealed record ReplayEnqueueResult(ReplayEnqueueStatus Status, Failure? Failure)
{
	public bool Accepted => Status == ReplayEnqueueStatus.Accepted;

	public static ReplayEnqueueResult AcceptedSample() => new(ReplayEnqueueStatus.Accepted, null);
	public static ReplayEnqueueResult Dropped(Failure failure) => new(ReplayEnqueueStatus.Dropped, failure);
	public static ReplayEnqueueResult Rejected(Failure failure) => new(ReplayEnqueueStatus.Rejected, failure);
}

public enum ReplayCaptureState
{
	Disabled = 1,
	Starting = 2,
	Capturing = 3,
	Degraded = 4,
	Failed = 5,
	Stopping = 6
}

public enum ReplayClipState
{
	Idle = 1,
	Marked = 2,
	Finalizing = 3,
	Ready = 4,
	Failed = 5
}

public sealed record ReplayStatistics(
	ulong AcceptedSamples,
	ulong DroppedSamples,
	ulong FinalizedSegments,
	ulong EvictedSegments,
	ulong Discontinuities,
	long RetainedBytes);

public sealed record ReplayObservation(
	UtcTimestamp Timestamp,
	string Code,
	string Message,
	ulong? ProgramSequence = null,
	ReplaySegmentId? SegmentId = null);

public sealed record ReplayBufferSnapshot(
	CompatibilityVersion Version,
	ReplayCaptureState CaptureState,
	ReplayClipState ClipState,
	ReplayBufferPolicy Policy,
	IReadOnlyList<ReplaySegmentDescriptor> Segments,
	ReplayRange? Selection,
	ReplayStatistics Statistics,
	Failure? Failure)
{
	public TimeSpan RetainedDuration =>
		Segments.Count == 0 ? TimeSpan.Zero : Segments[^1].End - Segments[0].Start;
}

public sealed record ReplayClipRequest
{
	public const int MaximumNameLength = 96;

	public ReplayClipRequest(CompatibilityVersion version, ReplayClipId clipId, ReplayRange range, string name)
	{
		ReplayContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
			throw new ArgumentException($"Replay clip name is required and limited to {MaximumNameLength} characters.", nameof(name));

		Version = version;
		ClipId = clipId;
		Range = range ?? throw new ArgumentNullException(nameof(range));
		Name = name.Trim();
	}

	public CompatibilityVersion Version { get; }
	public ReplayClipId ClipId { get; }
	public ReplayRange Range { get; }
	public string Name { get; }
}

public sealed record ReplayClipResult(
	ReplayClipId ClipId,
	string FinalPath,
	ReplayRange SourceRange,
	MediaAssetId? AssetId,
	string Sha256,
	bool Succeeded,
	Failure? Failure);
