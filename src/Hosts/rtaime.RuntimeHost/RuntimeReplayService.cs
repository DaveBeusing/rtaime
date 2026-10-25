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
	private readonly Func<ReplayClipRequest, CancellationToken, ValueTask<ReplayClipResult>> _materialize;
	private ClipJob? _activeClip;
	private ReplayClipState _clipState = ReplayClipState.Idle;
	private Failure? _clipFailure;
	private ReplayClipResult? _lastClip;
	private ReplayBufferSnapshot? _finalSnapshot;
	private bool _disposed;
	private Task? _disposeTask;

	public RuntimeReplayService(
		string rootDirectory,
		VideoFormat format,
		ReplayBufferPolicy? policy = null,
		Func<IReplaySegmentWriter>? writerFactory = null,
		int queueCapacity = ReplayCaptureEngine.DefaultQueueCapacity)
		: this(rootDirectory, format, policy, writerFactory, queueCapacity, null)
	{
	}

	internal RuntimeReplayService(
		string rootDirectory,
		VideoFormat format,
		ReplayBufferPolicy? policy,
		Func<IReplaySegmentWriter>? writerFactory,
		int queueCapacity,
		Func<ReplayClipRequest, CancellationToken, ValueTask<ReplayClipResult>>? materialize)
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
		_materialize = materialize ?? new ReplayClipMaterializer(_capture.Store, clips, format).MaterializeAsync;
	}

	public int QueueCapacity => _capture.QueueCapacity;
	public ReplayCaptureQueueStatistics QueueStatistics => _capture.QueueStatistics;
	public ReplayClipResult? LastClip
	{
		get { lock (_gate) return _lastClip; }
	}

	public ReplayBufferSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return _finalSnapshot ?? ProjectSnapshotUnsafe(_capture.Snapshot);
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
		lock (_gate)
		{
			EnsureSelectionEditableUnsafe();
			var range = _capture.MarkIn(lookback);
			_clipState = ReplayClipState.Marked;
			_clipFailure = null;
			return range;
		}
	}

	public ReplayRange MarkOut()
	{
		lock (_gate)
		{
			EnsureSelectionEditableUnsafe();
			var range = _capture.MarkOut();
			_clipState = ReplayClipState.Marked;
			_clipFailure = null;
			return range;
		}
	}

	public ReplayRange SelectRange(ReplayRange range)
	{
		lock (_gate)
		{
			EnsureSelectionEditableUnsafe();
			var selected = _capture.SelectRange(range);
			_clipState = ReplayClipState.Marked;
			_clipFailure = null;
			return selected;
		}
	}

	public void ClearSelection()
	{
		lock (_gate)
		{
			EnsureSelectionEditableUnsafe();
			_capture.ClearSelection();
			_clipState = ReplayClipState.Idle;
			_clipFailure = null;
		}
	}

	public async ValueTask<ReplayClipResult> CreateClipAsync(
		string name,
		CancellationToken cancellationToken = default)
	{
		ClipJob job;
		lock (_gate)
		{
			EnsureSelectionEditableUnsafe();
			var selection = _capture.Snapshot.Selection;
			if (selection is null)
			{
				var failure = new Failure("replay.clip.selection_required", "MARK IN and MARK OUT are required before clip creation.");
				_clipState = ReplayClipState.Failed;
				_clipFailure = failure;
				return new ReplayClipResult(ReplayClipId.New(), string.Empty, new ReplayRange(TimeSpan.Zero, TimeSpan.FromTicks(1)), null, string.Empty, false, failure);
			}
			var request = new ReplayClipRequest(ReplayContractVersion.Current, ReplayClipId.New(), selection, name);
			job = new ClipJob(request);
			_activeClip = job;
			_clipState = ReplayClipState.Finalizing;
			_clipFailure = null;
		}

		ReplayClipResult? result = null;
		try
		{
			result = await _materialize(job.Request, cancellationToken).ConfigureAwait(false);
			return result;
		}
		catch (OperationCanceledException)
		{
			result = FailedClip(job.Request, "replay.clip.cancelled", "Replay clip materialization was cancelled before completion.");
			throw;
		}
		catch (Exception exception)
		{
			result = FailedClip(job.Request, "replay.clip.materialization_failed",
				string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message);
			throw;
		}
		finally
		{
			lock (_gate)
			{
				// Ownership ends only after the actual materializer terminates, even
				// when the requesting IPC connection stopped waiting much earlier.
				_lastClip = result;
				_clipState = result?.Succeeded == true ? ReplayClipState.Ready : ReplayClipState.Failed;
				_clipFailure = result?.Failure;
				_activeClip = null;
				job.Completion.TrySetResult();
			}
		}
	}

	private void EnsureSelectionEditableUnsafe()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_activeClip is { } job)
			throw new InvalidOperationException($"Replay clip '{job.Request.ClipId}' is still finalizing; selection edits and additional materializations are unavailable until its outcome is confirmed.");
	}

	private static ReplayClipResult FailedClip(ReplayClipRequest request, string code, string message) =>
		new(request.ClipId, string.Empty, request.Range, null, string.Empty, false, new Failure(code, message));

	private sealed class ClipJob(ReplayClipRequest request)
	{
		public ReplayClipRequest Request { get; } = request;
		public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			_disposed = true;
			return new ValueTask(_disposeTask ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync()
	{
		Task? activeClip;
		lock (_gate)
			activeClip = _activeClip?.Completion.Task;
		var failures = new List<Exception>();
		try { await _capture.StopAsync(CancellationToken.None).ConfigureAwait(false); }
		catch (Exception exception) { failures.Add(exception); }
		if (activeClip is not null)
			await activeClip.ConfigureAwait(false);
		try
		{
			var finalCapture = _capture.Snapshot;
			if (failures.Count > 0)
				finalCapture = finalCapture with
				{
					CaptureState = ReplayCaptureState.Failed,
					Failure = new Failure("replay.capture.shutdown_failed", "Replay capture did not drain successfully; inspect the retained shutdown failure.")
				};
			lock (_gate)
				_finalSnapshot = ProjectSnapshotUnsafe(finalCapture);
		}
		catch (Exception exception) { failures.Add(exception); }
		try { await _capture.DisposeAsync().ConfigureAwait(false); }
		catch (Exception exception)
		{
			// Capture disposal rejoins the same worker; retain one instance of
			// its original failure while preserving distinct cleanup failures.
			if (!failures.Any(failure => ReferenceEquals(failure, exception))) failures.Add(exception);
		}
		if (failures.Count == 1)
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
		if (failures.Count > 1)
			throw new AggregateException("Replay shutdown failed after all owned cleanup was attempted.", failures);
	}

	private ReplayBufferSnapshot ProjectSnapshotUnsafe(ReplayBufferSnapshot capture)
	{
		var state = capture.Selection is null && _clipState == ReplayClipState.Marked
			? ReplayClipState.Idle
			: _clipState;
		return capture with
		{
			ClipState = state,
			Failure = _clipFailure ?? capture.Failure,
			ActiveClipId = _activeClip?.Request.ClipId,
			LastClip = _lastClip
		};
	}
}
