// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Recording;

/// <summary>
/// Bounded index for finalized encoded replay segments. The store retains file metadata only;
/// full-resolution Program pixels are never retained here.
/// </summary>
public sealed class RollingReplaySegmentStore : IDisposable
{
	private readonly object _gate = new();
	private readonly List<ReplaySegmentDescriptor> _segments = [];
	private readonly Dictionary<ReplaySegmentId, int> _pins = [];
	private readonly Dictionary<string, PendingDeletion> _pendingDeletion = new(PathComparer);
	private readonly ReplayBufferPolicy _policy;
	private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	private string? _rootDirectory;
	private FileStream? _directoryLease;
	private Failure? _storageFailure;
	private ulong _evicted;
	private long _retainedBytes;
	private bool _disposed;
	private StoreSnapshot _publishedSnapshot = new(Array.Empty<ReplaySegmentDescriptor>(), 0, 0, null);

	public RollingReplaySegmentStore(ReplayBufferPolicy? policy = null) =>
		_policy = policy ?? ReplayBufferPolicy.Default;

	public ReplayBufferPolicy Policy => _policy;

	public Failure? StorageFailure => Volatile.Read(ref _publishedSnapshot).StorageFailure;

	/// <summary>Reclaims prior-session history and abandoned writer files before a new writer opens.</summary>
	public void PrepareForCapture(string rootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
		lock (_gate)
		{
			ThrowIfDisposed();
			try
			{
				if (_rootDirectory is not null && !PathComparer.Equals(root, _rootDirectory))
					throw new InvalidOperationException("A replay store cannot change its owned directory.");
				if (_directoryLease is null)
				{
					Directory.CreateDirectory(root);
					_directoryLease = new FileStream(Path.Combine(root, ".replay-owner.lock"),
						FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
					_rootDirectory = root;
				}

				var retainedPaths = _segments.Select(segment => segment.Path).ToHashSet(PathComparer);
				foreach (var path in Directory.EnumerateFiles(root, "replay-*", SearchOption.TopDirectoryOnly))
				{
					if (!IsOwnedSegmentFile(Path.GetFileName(path)) || retainedPaths.Contains(path))
						continue;
					TrackPendingDeletionUnsafe(path, new FileInfo(path).Length, countAsEviction: false);
				}

				RetryPendingDeletionUnsafe();
				if (_pendingDeletion.Count > 0)
					throw new IOException($"Replay capture is blocked by {_pendingDeletion.Count} unreclaimed owned files ({_retainedBytes} retained bytes). {_storageFailure?.Message}");
				_storageFailure = null;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				_storageFailure = new Failure("replay.storage.unavailable", exception.Message);
				throw;
			}
			finally
			{
				PublishSnapshotUnsafe();
			}
		}
	}

	public IReadOnlyList<ReplaySegmentDescriptor> Snapshot() => CaptureSnapshot().Segments;

	internal StoreSnapshot CaptureSnapshot()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
		return Volatile.Read(ref _publishedSnapshot);
	}

	public long RetainedBytes => Volatile.Read(ref _publishedSnapshot).RetainedBytes;

	public ulong EvictedSegments => Volatile.Read(ref _publishedSnapshot).EvictedSegments;

	public void AddFinalizedSegment(ReplaySegmentDescriptor segment)
	{
		ArgumentNullException.ThrowIfNull(segment);
		lock (_gate)
		{
			try
			{
				ThrowIfDisposed();
				var info = new FileInfo(segment.Path);
				if (!info.Exists)
					throw new FileNotFoundException("Finalized replay segment does not exist.", segment.Path);
				if (info.Length != segment.Bytes)
					throw new InvalidDataException("Replay segment byte count must match the finalized file.");
				if (_rootDirectory is not null && (!PathComparer.Equals(Path.GetDirectoryName(segment.Path), _rootDirectory) ||
					!IsOwnedSegmentFile(Path.GetFileName(segment.Path))))
					throw new InvalidDataException("Replay segment must belong to the exclusively owned rolling directory.");
				if (_segments.Any(item => item.SegmentId == segment.SegmentId || PathComparer.Equals(item.Path, segment.Path)) ||
					_pendingDeletion.ContainsKey(segment.Path))
					throw new InvalidOperationException($"Replay segment '{segment.SegmentId}' already exists.");
				if (_segments.Count > 0)
				{
					var previous = _segments[^1];
					if (segment.FirstProgramSequence <= previous.LastProgramSequence)
						throw new InvalidDataException("Replay segments require strictly increasing Program sequence ranges.");
					if (segment.Start < previous.End)
						throw new InvalidDataException("Replay segment time ranges must not overlap.");
				}

				_retainedBytes = checked(_retainedBytes + segment.Bytes);
				EvictUnsafe(segment.End);

				if (_retainedBytes > _policy.MaximumStorageBytes)
				{
					_pendingDeletion.Add(segment.Path, new PendingDeletion(segment.Bytes, false));
					ReclaimUnsafe(segment.Path);
					throw new IOException("Replay storage quota is exhausted by pinned history or files that could not be reclaimed.");
				}
				_segments.Add(segment);
			}
			finally
			{
				PublishSnapshotUnsafe();
			}
		}
	}

	public ReplaySegmentPin PinRange(ReplayRange range)
	{
		ArgumentNullException.ThrowIfNull(range);
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_segments.Count == 0)
				throw new InvalidOperationException("Replay history is empty.");
			if (range.In < _segments[0].Start || range.Out > _segments[^1].End)
				throw new InvalidOperationException("Requested replay range is no longer fully retained.");

			var selected = _segments
				.Where(segment => segment.End > range.In && segment.Start < range.Out)
				.ToArray();
			if (selected.Length == 0)
				throw new InvalidOperationException("Requested replay range has no retained encoded segments.");

			for (var index = 1; index < selected.Length; index++)
			{
				if (selected[index].DiscontinuityBefore)
					throw new InvalidOperationException("Requested replay range crosses a capture discontinuity.");
			}

			foreach (var segment in selected)
				_pins[segment.SegmentId] = _pins.GetValueOrDefault(segment.SegmentId) + 1;

			return new ReplaySegmentPin(this, range, selected);
		}
	}

	public bool ContainsRange(ReplayRange range)
	{
		ArgumentNullException.ThrowIfNull(range);
		if (Volatile.Read(ref _disposed))
			return false;
		// This is advisory visibility. PinRange revalidates under the ownership
		// gate before a materializer can acquire any segment.
		var segments = Volatile.Read(ref _publishedSnapshot).Segments;
		if (segments.Count == 0 || range.In < segments[0].Start || range.Out > segments[^1].End)
			return false;
		var selected = segments.Where(segment => segment.End > range.In && segment.Start < range.Out).ToArray();
		return selected.Length > 0 && selected.Skip(1).All(segment => !segment.DiscontinuityBefore);
	}

	public void Trim()
	{
		lock (_gate)
		{
			try
			{
				ThrowIfDisposed();
				RetryPendingDeletionUnsafe();
				if (_segments.Count > 0)
					EvictUnsafe(_segments[^1].End);
			}
			finally
			{
				PublishSnapshotUnsafe();
			}
		}
	}

	private void EvictUnsafe(TimeSpan newestEnd)
	{
		while (_segments.Count > 0)
		{
			var oldest = _segments[0];
			var overDuration = newestEnd - oldest.Start > _policy.RetentionDuration;
			var overStorage = _retainedBytes > _policy.MaximumStorageBytes;
			if (!overDuration && !overStorage)
				break;
			if (_pins.GetValueOrDefault(oldest.SegmentId) > 0)
				break;

			_segments.RemoveAt(0);
			_pendingDeletion.Add(oldest.Path, new PendingDeletion(oldest.Bytes, true));
			ReclaimUnsafe(oldest.Path);
		}
	}

	private void Release(IReadOnlyList<ReplaySegmentDescriptor> segments)
	{
		lock (_gate)
		{
			try
			{
				foreach (var segment in segments)
				{
					var count = _pins.GetValueOrDefault(segment.SegmentId);
					if (count <= 1)
						_pins.Remove(segment.SegmentId);
					else
						_pins[segment.SegmentId] = count - 1;
				}
				if (_disposed)
				{
					ReleaseDirectoryIfUnpinnedUnsafe();
					return;
				}
				RetryPendingDeletionUnsafe();
				if (_segments.Count > 0)
					EvictUnsafe(_segments[^1].End);
			}
			finally
			{
				PublishSnapshotUnsafe();
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			Volatile.Write(ref _disposed, true);
			ReleaseDirectoryIfUnpinnedUnsafe();
		}
	}

	private void ReleaseDirectoryIfUnpinnedUnsafe()
	{
		if (_pins.Count != 0)
			return;
		_directoryLease?.Dispose();
		_directoryLease = null;
	}

	private void TrackPendingDeletionUnsafe(string path, long bytes, bool countAsEviction)
	{
		if (_pendingDeletion.TryGetValue(path, out var previous))
		{
			_retainedBytes = checked(_retainedBytes - previous.Bytes + bytes);
			_pendingDeletion[path] = previous with { Bytes = bytes };
			return;
		}
		_retainedBytes = checked(_retainedBytes + bytes);
		_pendingDeletion.Add(path, new PendingDeletion(bytes, countAsEviction));
	}

	private void RetryPendingDeletionUnsafe()
	{
		foreach (var path in _pendingDeletion.Keys.ToArray())
			ReclaimUnsafe(path);
		if (_pendingDeletion.Count == 0)
			_storageFailure = null;
	}

	private void ReclaimUnsafe(string path)
	{
		try
		{
			// File.Delete also succeeds for an absent file; File.Exists would hide access failures.
			File.Delete(path);
			var pending = _pendingDeletion[path];
			_retainedBytes -= pending.Bytes;
			if (pending.CountAsEviction)
				_evicted++;
			_pendingDeletion.Remove(path);
			if (_pendingDeletion.Count == 0)
				_storageFailure = null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_storageFailure = new Failure("replay.storage.cleanup_pending", $"Replay file '{Path.GetFileName(path)}' remains accounted until deletion succeeds: {exception.Message}");
		}
	}

	private static bool IsOwnedSegmentFile(string name)
	{
		if (!name.StartsWith("replay-", StringComparison.Ordinal))
			return false;
		var suffix = name.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase) ? ".partial.mp4" :
			name.EndsWith(".mp4.lock", StringComparison.OrdinalIgnoreCase) ? ".mp4.lock" :
			name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : null;
		if (suffix is null || name.Length <= 7 + suffix.Length)
			return false;
		var identity = name.AsSpan(7, name.Length - 7 - suffix.Length);
		return Guid.TryParseExact(identity, "N", out _) || Guid.TryParseExact(identity, "D", out _);
	}

	private sealed record PendingDeletion(long Bytes, bool CountAsEviction);

	private void PublishSnapshotUnsafe() => Volatile.Write(ref _publishedSnapshot,
		new StoreSnapshot(Array.AsReadOnly(_segments.ToArray()), _retainedBytes, _evicted, _storageFailure));

	internal sealed record StoreSnapshot(
		IReadOnlyList<ReplaySegmentDescriptor> Segments,
		long RetainedBytes,
		ulong EvictedSegments,
		Failure? StorageFailure);

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	public sealed class ReplaySegmentPin : IDisposable
	{
		private RollingReplaySegmentStore? _owner;

		internal ReplaySegmentPin(
			RollingReplaySegmentStore owner,
			ReplayRange range,
			IReadOnlyList<ReplaySegmentDescriptor> segments)
		{
			_owner = owner;
			Range = range;
			Segments = segments;
		}

		public ReplayRange Range { get; }
		public IReadOnlyList<ReplaySegmentDescriptor> Segments { get; }

		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(Segments);
	}
}

