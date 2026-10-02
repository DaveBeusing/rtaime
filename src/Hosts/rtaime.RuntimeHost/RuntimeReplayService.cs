// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.RuntimeHost;

/// <summary>
/// Runtime-owned replay composition. Capture is continuous and failure-isolated; clip creation is
/// non-realtime and works only from pinned finalized encoded segments.
/// </summary>
public sealed class RuntimeReplayService : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly ReplayCaptureEngine _capture;
	private readonly ReplayClipMaterializer _materializer;
	private ReplayClipState _clipState = ReplayClipState.Idle;
	private Failure? _clipFailure;
	private ReplayClipResult? _lastClip;
	private bool _disposed;

	public RuntimeReplayService(
		string rootDirectory,
		VideoFormat format,
		ReplayBufferPolicy? policy = null,
		Func<IReplaySegmentWriter>? writerFactory = null,
		int queueCapacity = ReplayCaptureEngine.DefaultQueueCapacity)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory))
			throw new ArgumentException("Replay root directory is required.", nameof(rootDirectory));
		var root = Path.GetFullPath(rootDirectory);
		var segments = Path.Combine(root, "segments");
		var clips = Path.Combine(root, "clips");
		_capture = new ReplayCaptureEngine(
			segments,
			writerFactory ?? (() => new WindowsMediaFoundationMp4RecordingWriter(segments)),
			policy,
			queueCapacity);
		_materializer = new ReplayClipMaterializer(_capture.Store, clips, format);
	}

	public int QueueCapacity => _capture.QueueCapacity;
	public ReplayClipResult? LastClip
	{
		get { lock (_gate) return _lastClip; }
	}

	public ReplayBufferSnapshot Snapshot
	{
		get
		{
			var capture = _capture.Snapshot;
			lock (_gate)
			{
				var state = capture.Selection is null && _clipState == ReplayClipState.Marked
					? ReplayClipState.Idle
					: _clipState;
				return capture with
				{
					ClipState = state,
					Failure = _clipFailure ?? capture.Failure
				};
			}
		}
	}

	public ReplayEnqueueResult TryCapture(
		MediaSinkId programSinkId,
		FrameDescriptor video,
		AudioBufferDescriptor audio,
		IProgramRecordingPayloadLease videoPayload,
		ReadOnlyMemory<byte> audioPayload)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _capture.TryEnqueue(programSinkId, video, audio, videoPayload, audioPayload);
	}

	public ReplayRange MarkIn(TimeSpan? lookback = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var range = _capture.MarkIn(lookback);
		lock (_gate)
		{
			_clipState = ReplayClipState.Marked;
			_clipFailure = null;
		}
		return range;
	}

	public ReplayRange MarkOut()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var range = _capture.MarkOut();
		lock (_gate)
		{
			_clipState = ReplayClipState.Marked;
			_clipFailure = null;
		}
		return range;
	}

	public ReplayRange SelectRange(ReplayRange range)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var selected = _capture.SelectRange(range);
		lock (_gate)
		{
			_clipState = ReplayClipState.Marked;
			_clipFailure = null;
		}
		return selected;
	}

	public void ClearSelection()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_capture.ClearSelection();
		lock (_gate)
		{
			_clipState = ReplayClipState.Idle;
			_clipFailure = null;
		}
	}

	public async ValueTask<ReplayClipResult> CreateClipAsync(
		string name,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var selection = _capture.Snapshot.Selection;
		if (selection is null)
		{
			var failure = new Failure("replay.clip.selection_required", "MARK IN and MARK OUT are required before clip creation.");
			lock (_gate)
			{
				_clipState = ReplayClipState.Failed;
				_clipFailure = failure;
			}
			return new ReplayClipResult(ReplayClipId.New(), string.Empty, new ReplayRange(TimeSpan.Zero, TimeSpan.FromTicks(1)), null, string.Empty, false, failure);
		}

		var request = new ReplayClipRequest(
			ReplayContractVersion.Current,
			ReplayClipId.New(),
			selection,
			name);
		lock (_gate)
		{
			if (_clipState == ReplayClipState.Finalizing)
				throw new InvalidOperationException("Replay clip finalization is already active.");
			_clipState = ReplayClipState.Finalizing;
			_clipFailure = null;
		}

		var result = await _materializer.MaterializeAsync(request, cancellationToken).ConfigureAwait(false);
		lock (_gate)
		{
			_lastClip = result;
			_clipState = result.Succeeded ? ReplayClipState.Ready : ReplayClipState.Failed;
			_clipFailure = result.Failure;
		}
		return result;
	}

	public async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
		}
		await _capture.DisposeAsync().ConfigureAwait(false);
	}
}
