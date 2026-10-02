// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

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
	private readonly ReplayBufferPolicy _policy;
	private ulong _evicted;
	private long _retainedBytes;
	private bool _disposed;

	public RollingReplaySegmentStore(ReplayBufferPolicy? policy = null) =>
		_policy = policy ?? ReplayBufferPolicy.Default;

	public ReplayBufferPolicy Policy => _policy;

	public IReadOnlyList<ReplaySegmentDescriptor> Snapshot()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return Array.AsReadOnly(_segments.ToArray());
		}
	}

	public long RetainedBytes
	{
		get { lock (_gate) return _retainedBytes; }
	}

	public ulong EvictedSegments
	{
		get { lock (_gate) return _evicted; }
	}

	public void AddFinalizedSegment(ReplaySegmentDescriptor segment)
	{
		ArgumentNullException.ThrowIfNull(segment);
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!File.Exists(segment.Path))
				throw new FileNotFoundException("Finalized replay segment does not exist.", segment.Path);
			if (_segments.Any(item => item.SegmentId == segment.SegmentId))
				throw new InvalidOperationException($"Replay segment '{segment.SegmentId}' already exists.");
			if (_segments.Count > 0)
			{
				var previous = _segments[^1];
				if (segment.FirstProgramSequence <= previous.LastProgramSequence)
					throw new InvalidDataException("Replay segments require strictly increasing Program sequence ranges.");
				if (segment.Start < previous.End)
					throw new InvalidDataException("Replay segment time ranges must not overlap.");
			}

			_segments.Add(segment);
			_retainedBytes = checked(_retainedBytes + segment.Bytes);
			EvictUnsafe(segment.End);

			if (_retainedBytes > _policy.MaximumStorageBytes)
			{
				_segments.Remove(segment);
				_retainedBytes -= segment.Bytes;
				TryDelete(segment.Path);
				throw new IOException("Replay storage quota is exhausted because older retained segments are currently pinned.");
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
		lock (_gate)
		{
			if (_disposed || _segments.Count == 0)
				return false;
			if (range.In < _segments[0].Start || range.Out > _segments[^1].End)
				return false;
			var selected = _segments.Where(segment => segment.End > range.In && segment.Start < range.Out).ToArray();
			return selected.Length > 0 && selected.Skip(1).All(segment => !segment.DiscontinuityBefore);
		}
	}

	public void Trim()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_segments.Count > 0)
				EvictUnsafe(_segments[^1].End);
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
			_retainedBytes -= oldest.Bytes;
			_evicted++;
			TryDelete(oldest.Path);
		}
	}

	private void Release(IReadOnlyList<ReplaySegmentDescriptor> segments)
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			foreach (var segment in segments)
			{
				var count = _pins.GetValueOrDefault(segment.SegmentId);
				if (count <= 1)
					_pins.Remove(segment.SegmentId);
				else
					_pins[segment.SegmentId] = count - 1;
			}
			if (_segments.Count > 0)
				EvictUnsafe(_segments[^1].End);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			_pins.Clear();
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

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
