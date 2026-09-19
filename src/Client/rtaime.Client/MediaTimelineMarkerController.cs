// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Client;

public delegate ValueTask<MediaMarkerCommandResult> MediaMarkerCommandSender(
	MediaMarkerCommand command,
	CancellationToken cancellationToken);

public sealed record MediaTimelineMarkerState(
	bool IsLoaded,
	long? InPointFrame,
	long? OutPointFrame,
	IReadOnlyList<MediaCuePoint> CuePoints,
	string? Failure)
{
	public static MediaTimelineMarkerState Empty { get; } =
		new(false, null, null, Array.Empty<MediaCuePoint>(), null);
}

public sealed class MediaTimelineMarkerController : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly SemaphoreSlim _sendGate = new(1, 1);
	private readonly MediaTimelineController _timeline;
	private readonly MediaMarkerCommandSender? _sender;
	private readonly CancellationTokenSource _dispose = new();
	private MediaMarkerSnapshot? _confirmed;
	private bool _disposed;

	public MediaTimelineMarkerController(
		MediaTimelineController timeline,
		MediaMarkerCommandSender? sender = null)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_sender = sender;
	}

	public event EventHandler? StateChanged;

	public MediaTimelineMarkerState State
	{
		get
		{
			lock (_gate)
			{
				return _confirmed is null
					? MediaTimelineMarkerState.Empty
					: new MediaTimelineMarkerState(
						true,
						_confirmed.InPointFrame,
						_confirmed.OutPointFrame,
						_confirmed.CuePoints,
						null);
			}
		}
	}

	public void ApplyConfirmedSnapshot(MediaMarkerSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ThrowIfDisposed();
		lock (_gate)
			_confirmed = snapshot;
		RaiseStateChanged();
	}

	internal void ClearConfirmedSnapshot()
	{
		ThrowIfDisposed();
		lock (_gate)
			_confirmed = null;
		RaiseStateChanged();
	}

	public ValueTask<bool> SetInAtCurrentFrameAsync(CancellationToken cancellationToken = default) =>
		SetInAtFrameAsync(_timeline.State.ConfirmedFrame, cancellationToken);

	public ValueTask<bool> SetInAtFrameAsync(long frame, CancellationToken cancellationToken = default) =>
		SendAtFrameAsync(MediaMarkerCommandKind.SetInPoint, frame, cancellationToken);

	public ValueTask<bool> ClearInAsync(CancellationToken cancellationToken = default) =>
		SendAsync(
			snapshot => new MediaMarkerCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				MediaMarkerCommandKind.ClearInPoint),
			cancellationToken);

	public ValueTask<bool> SetOutAtCurrentFrameAsync(CancellationToken cancellationToken = default) =>
		SetOutAtFrameAsync(_timeline.State.ConfirmedFrame, cancellationToken);

	public ValueTask<bool> SetOutAtFrameAsync(long frame, CancellationToken cancellationToken = default) =>
		SendAtFrameAsync(MediaMarkerCommandKind.SetOutPoint, frame, cancellationToken);

	public ValueTask<bool> ClearOutAsync(CancellationToken cancellationToken = default) =>
		SendAsync(
			snapshot => new MediaMarkerCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				MediaMarkerCommandKind.ClearOutPoint),
			cancellationToken);

	public async ValueTask<MediaCuePointId?> AddCueAtCurrentFrameAsync(
		string name,
		CancellationToken cancellationToken = default)
	{
		var cueId = MediaCuePointId.New();
		var sent = await SendAsync(
			snapshot => new MediaMarkerCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				MediaMarkerCommandKind.AddCuePoint,
				_timeline.State.ConfirmedFrame,
				cueId,
				name),
			cancellationToken).ConfigureAwait(false);
		return sent ? cueId : null;
	}

	public ValueTask<bool> RenameCueAsync(
		MediaCuePointId cueId,
		string name,
		CancellationToken cancellationToken = default) =>
		SendAsync(
			snapshot => new MediaMarkerCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				MediaMarkerCommandKind.RenameCuePoint,
				cuePointId: cueId,
				name: name),
			cancellationToken);

	public ValueTask<bool> DeleteCueAsync(
		MediaCuePointId cueId,
		CancellationToken cancellationToken = default) =>
		SendAsync(
			snapshot => new MediaMarkerCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				MediaMarkerCommandKind.DeleteCuePoint,
				cuePointId: cueId),
			cancellationToken);

	public async ValueTask JumpToInAsync(CancellationToken cancellationToken = default)
	{
		var frame = GetMarkerFrame(snapshot => snapshot.InPointFrame);
		if (frame.HasValue)
			await _timeline.SeekToFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask JumpToOutAsync(CancellationToken cancellationToken = default)
	{
		var frame = GetMarkerFrame(snapshot => snapshot.OutPointFrame);
		if (frame.HasValue)
			await _timeline.SeekToFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask JumpToCueAsync(
		MediaCuePointId cueId,
		CancellationToken cancellationToken = default)
	{
		long? frame;
		lock (_gate)
			frame = _confirmed?.CuePoints.FirstOrDefault(cue => cue.Id == cueId)?.PositionFrame;
		if (frame.HasValue)
			await _timeline.SeekToFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<bool> JumpToPreviousCueAsync(CancellationToken cancellationToken = default)
	{
		var currentFrame = _timeline.State.ConfirmedFrame;
		long? frame;
		lock (_gate)
			frame = _confirmed?.CuePoints
				.Where(cue => cue.PositionFrame < currentFrame)
				.Select(cue => (long?)cue.PositionFrame)
				.LastOrDefault();
		if (!frame.HasValue)
			return false;

		await _timeline.SeekToFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
		return true;
	}

	public async ValueTask<bool> JumpToNextCueAsync(CancellationToken cancellationToken = default)
	{
		var currentFrame = _timeline.State.ConfirmedFrame;
		long? frame;
		lock (_gate)
			frame = _confirmed?.CuePoints
				.Where(cue => cue.PositionFrame > currentFrame)
				.Select(cue => (long?)cue.PositionFrame)
				.FirstOrDefault();
		if (!frame.HasValue)
			return false;

		await _timeline.SeekToFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
		return true;
	}

	public ValueTask DisposeAsync()
	{
		if (_disposed)
			return ValueTask.CompletedTask;

		_disposed = true;
		_dispose.Cancel();
		_dispose.Dispose();
		_sendGate.Dispose();
		return ValueTask.CompletedTask;
	}

	private ValueTask<bool> SendAtFrameAsync(
		MediaMarkerCommandKind kind,
		long frame,
		CancellationToken cancellationToken)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));

		return SendAsync(
			snapshot => new MediaMarkerCommand(
				MediaContractVersion.Current,
				snapshot.AssetId,
				kind,
				frame),
			cancellationToken);
	}

	private async ValueTask<bool> SendAsync(
		Func<MediaMarkerSnapshot, MediaMarkerCommand> commandFactory,
		CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		MediaMarkerSnapshot? snapshot;
		lock (_gate)
			snapshot = _confirmed;
		if (snapshot is null || _sender is null || !_timeline.State.IsLoaded)
			return false;

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dispose.Token);
		await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
		try
		{
			MediaMarkerCommandResult result;
			try
			{
				result = await _sender(commandFactory(snapshot), linked.Token).ConfigureAwait(false);
			}
			catch (MediaDeckMarkersUnavailableException)
			{
				ClearConfirmedSnapshot();
				return false;
			}
			lock (_gate)
				_confirmed = result.Snapshot;
			RaiseStateChanged();
			return result.Succeeded;
		}
		finally
		{
			_sendGate.Release();
		}
	}

	private long? GetMarkerFrame(Func<MediaMarkerSnapshot, long?> selector)
	{
		lock (_gate)
			return _confirmed is null ? null : selector(_confirmed);
	}

	private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
