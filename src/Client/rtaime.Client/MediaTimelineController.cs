// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public delegate ValueTask<MediaTransportCommandResult> MediaTransportCommandSender(
	MediaTransportCommand command,
	CancellationToken cancellationToken);

public sealed record MediaTimelineState(
	bool IsLoaded,
	string TransportState,
	long ConfirmedFrame,
	long DisplayFrame,
	long TotalFrames,
	double Progress,
	string CurrentTimecode,
	string DurationTimecode,
	string RemainingTimecode,
	string FrameRate,
	bool CanSeek,
	bool HasPendingSeek,
	string? Failure)
{
	public static MediaTimelineState Empty { get; } = new(
		false,
		"UNLOADED",
		0,
		0,
		0,
		0,
		"--:--:--:--",
		"--:--:--:--",
		"--:--:--:--",
		"—",
		false,
		false,
		null);
}

public static class MediaTimelineTimecode
{
	public static string FormatFrame(long frameNumber, FrameRate frameRate)
	{
		if (frameNumber < 0)
			throw new ArgumentOutOfRangeException(nameof(frameNumber));

		var nominalFramesPerSecond = GetNominalFramesPerSecond(frameRate);
		var frames = frameNumber % nominalFramesPerSecond;
		var totalSeconds = frameNumber / nominalFramesPerSecond;
		var seconds = totalSeconds % 60;
		var totalMinutes = totalSeconds / 60;
		var minutes = totalMinutes % 60;
		var hours = totalMinutes / 60;
		return $"{hours:00}:{minutes:00}:{seconds:00}:{frames:00}";
	}

	public static string FormatDuration(TimeSpan duration, FrameRate frameRate)
	{
		if (duration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));

		var frames = TimeSpanToNearestFrame(duration, frameRate);
		return FormatFrame(frames, frameRate);
	}

	public static long TimeSpanToNearestFrame(TimeSpan time, FrameRate frameRate)
	{
		if (time < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(time));

		var exactFrames = (decimal)time.Ticks * frameRate.Numerator /
			(TimeSpan.TicksPerSecond * (decimal)frameRate.Denominator);
		return checked((long)decimal.Round(exactFrames, 0, MidpointRounding.AwayFromZero));
	}

	public static int GetNominalFramesPerSecond(FrameRate frameRate)
	{
		var value = (decimal)frameRate.Numerator / frameRate.Denominator;
		var nominal = checked((int)decimal.Round(value, 0, MidpointRounding.AwayFromZero));
		if (nominal <= 0 || nominal > 120)
			throw new ArgumentOutOfRangeException(nameof(frameRate), "Timeline timecode supports nominal frame rates from 1 through 120 fps.");
		return nominal;
	}
}

public static class MediaTimelineGeometry
{
	public static long FrameFromLogicalPosition(double logicalX, double logicalWidth, long totalFrames)
	{
		if (!double.IsFinite(logicalX))
			throw new ArgumentOutOfRangeException(nameof(logicalX));
		if (!double.IsFinite(logicalWidth) || logicalWidth <= 0)
			throw new ArgumentOutOfRangeException(nameof(logicalWidth));
		if (totalFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(totalFrames));

		var fraction = Math.Clamp(logicalX / logicalWidth, 0, 1);
		return checked((long)Math.Round(fraction * (totalFrames - 1), MidpointRounding.AwayFromZero));
	}

	public static long FrameFromPhysicalPosition(
		double physicalX,
		double physicalWidth,
		double dpiScale,
		long totalFrames)
	{
		if (!double.IsFinite(dpiScale) || dpiScale <= 0)
			throw new ArgumentOutOfRangeException(nameof(dpiScale));
		return FrameFromLogicalPosition(physicalX / dpiScale, physicalWidth / dpiScale, totalFrames);
	}
}

public sealed class MediaTimelineController : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly SemaphoreSlim _sendGate = new(1, 1);
	private readonly MediaTransportCommandSender? _sender;
	private readonly TimeSpan _dragThrottle;
	private readonly CancellationTokenSource _dispose = new();
	private MediaTransportSnapshot? _confirmed;
	private long? _previewFrame;
	private long? _queuedFrame;
	private Task? _drainTask;
	private bool _isDragging;
	private bool _disposed;

	public MediaTimelineController(
		MediaTransportCommandSender? sender = null,
		TimeSpan? dragThrottle = null)
	{
		_sender = sender;
		_dragThrottle = dragThrottle ?? TimeSpan.FromMilliseconds(40);
		if (_dragThrottle <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(dragThrottle));
	}

	public event EventHandler? StateChanged;

	public MediaTimelineState State
	{
		get
		{
			lock (_gate)
				return CreateState();
		}
	}

	public void ApplyConfirmedSnapshot(MediaTransportSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ThrowIfDisposed();
		lock (_gate)
		{
			_confirmed = snapshot;
			if (!_isDragging)
				_previewFrame = null;
		}
		RaiseStateChanged();
	}

	public void BeginPointerSeek()
	{
		ThrowIfDisposed();
		lock (_gate)
		{
			if (_confirmed is null || _sender is null)
				return;
			_isDragging = true;
			_previewFrame = _confirmed.Position.CurrentFrame;
		}
		RaiseStateChanged();
	}

	public void PreviewPointerSeek(long targetFrame)
	{
		ThrowIfDisposed();
		var shouldStartDrain = false;
		lock (_gate)
		{
			if (!_isDragging || _confirmed is null || _sender is null)
				return;

			var clamped = ClampFrame(targetFrame, _confirmed.Position.TotalFrames);
			_previewFrame = clamped;
			_queuedFrame = clamped;
			if (_drainTask is null || _drainTask.IsCompleted)
			{
				shouldStartDrain = true;
				_drainTask = DrainQueuedSeekAsync(_dispose.Token);
			}
		}

		RaiseStateChanged();
		if (shouldStartDrain)
			_ = ObserveDrainAsync();
	}

	public async ValueTask CompletePointerSeekAsync(long targetFrame, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		long clamped;
		lock (_gate)
		{
			if (_confirmed is null || _sender is null)
			{
				_isDragging = false;
				_previewFrame = null;
				_queuedFrame = null;
				return;
			}

			clamped = ClampFrame(targetFrame, _confirmed.Position.TotalFrames);
			_previewFrame = clamped;
			_queuedFrame = null;
			_isDragging = false;
		}

		RaiseStateChanged();
		await SendSeekAsync(clamped, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask SeekToFrameAsync(long targetFrame, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		await SendSeekAsync(targetFrame, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask SeekRelativeAsync(long frameDelta, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		long target;
		lock (_gate)
		{
			if (_confirmed is null || _sender is null)
				return;
			target = ClampFrame(checked(_confirmed.Position.CurrentFrame + frameDelta), _confirmed.Position.TotalFrames);
		}
		await SendSeekAsync(target, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask SeekToStartAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (_sender is null)
			return;
		await SendSeekAsync(0, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask SeekToEndAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		long target;
		lock (_gate)
		{
			if (_confirmed is null || _sender is null)
				return;
			target = _confirmed.Position.TotalFrames - 1;
		}
		await SendSeekAsync(target, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask FlushPendingSeekAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		Task? drain;
		lock (_gate)
			drain = _drainTask;
		if (drain is not null)
			await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		_disposed = true;
		_dispose.Cancel();
		Task? drain;
		lock (_gate)
			drain = _drainTask;
		if (drain is not null)
		{
			try { await drain.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_dispose.Dispose();
		_sendGate.Dispose();
	}

	private async Task DrainQueuedSeekAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(_dragThrottle, cancellationToken).ConfigureAwait(false);
			long? target;
			lock (_gate)
			{
				target = _queuedFrame;
				_queuedFrame = null;
			}
			if (target is null)
				return;

			await SendSeekAsync(target.Value, cancellationToken).ConfigureAwait(false);
			lock (_gate)
			{
				if (_queuedFrame is null)
					return;
			}
		}
	}

	private async Task ObserveDrainAsync()
	{
		Task? drain;
		lock (_gate)
			drain = _drainTask;
		if (drain is null)
			return;
		try { await drain.ConfigureAwait(false); }
		catch (OperationCanceledException) when (_dispose.IsCancellationRequested) { }
	}

	private async ValueTask SendSeekAsync(long targetFrame, CancellationToken cancellationToken)
	{
		MediaTransportSnapshot? snapshot;
		MediaTransportCommandSender? sender;
		lock (_gate)
		{
			snapshot = _confirmed;
			sender = _sender;
		}
		if (snapshot is null || sender is null)
			return;

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dispose.Token);
		await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			var command = new MediaTransportCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				MediaTransportCommandKind.Seek,
				ClampFrame(targetFrame, snapshot.Position.TotalFrames));
			var result = await sender(command, linked.Token).ConfigureAwait(false);
			lock (_gate)
			{
				_confirmed = result.Snapshot;
				if (!_isDragging)
					_previewFrame = null;
			}
			RaiseStateChanged();
		}
		finally
		{
			_sendGate.Release();
		}
	}

	private MediaTimelineState CreateState()
	{
		if (_confirmed is null)
			return MediaTimelineState.Empty;

		var position = _confirmed.Position;
		var displayFrame = _previewFrame ?? position.CurrentFrame;
		var progress = position.TotalFrames <= 1
			? 0
			: (double)displayFrame / (position.TotalFrames - 1);
		var canSeek = _sender is not null &&
			_confirmed.State is MediaTransportState.Ready or MediaTransportState.Playing or MediaTransportState.Paused or MediaTransportState.Ended;
		return new MediaTimelineState(
			true,
			_confirmed.State.ToString().ToUpperInvariant(),
			position.CurrentFrame,
			displayFrame,
			position.TotalFrames,
			Math.Clamp(progress, 0, 1),
			MediaTimelineTimecode.FormatFrame(displayFrame, position.FrameRate),
			MediaTimelineTimecode.FormatDuration(position.Duration, position.FrameRate),
			MediaTimelineTimecode.FormatDuration(position.Remaining, position.FrameRate),
			position.FrameRate.ToString(),
			canSeek,
			_previewFrame is not null && _previewFrame.Value != position.CurrentFrame,
			_confirmed.Failure?.Message);
	}

	private static long ClampFrame(long frame, long totalFrames) =>
		Math.Clamp(frame, 0, totalFrames - 1);

	private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
