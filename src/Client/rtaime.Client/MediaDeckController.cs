// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public sealed class MediaDeckController : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly OperatorControlClient _client;
	private bool _disposed;
	private MediaDeckSnapshot _snapshot = MediaDeckSnapshot.Unloaded;

	public MediaDeckController(OperatorControlClient client)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		Timeline = new MediaTimelineController(SendTransportAsync);
		Markers = new MediaTimelineMarkerController(Timeline, SendMarkerAsync);
	}

	public event EventHandler? StateChanged;

	public MediaTimelineController Timeline { get; }
	public MediaTimelineMarkerController Markers { get; }

	public MediaDeckSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return _snapshot;
		}
	}

	public async ValueTask<MediaDeckSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		var snapshot = await _client.GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
		ApplySnapshot(snapshot);
		return snapshot;
	}

	public async ValueTask<MediaDeckSnapshot> OpenAsync(
		string path,
		MediaSourceId sourceId,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		var snapshot = await _client.OpenMediaDeckAsync(path, sourceId, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(snapshot);
		return snapshot;
	}

	public ValueTask<MediaDeckSnapshot> PlayAsync(CancellationToken cancellationToken = default) =>
		SendTransportKindAsync(MediaTransportCommandKind.Play, cancellationToken);

	public ValueTask<MediaDeckSnapshot> PauseAsync(CancellationToken cancellationToken = default) =>
		SendTransportKindAsync(MediaTransportCommandKind.Pause, cancellationToken);

	public ValueTask<MediaDeckSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
		SendTransportKindAsync(MediaTransportCommandKind.Stop, cancellationToken);

	public async ValueTask<MediaDeckSnapshot> CloseAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		var snapshot = await _client.CloseMediaDeckAsync(cancellationToken).ConfigureAwait(false);
		ApplySnapshot(snapshot);
		return snapshot;
	}

	public void ApplyConfirmedSnapshot(MediaDeckSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ThrowIfDisposed();
		ApplySnapshot(snapshot);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		_disposed = true;
		await Markers.DisposeAsync().ConfigureAwait(false);
		await Timeline.DisposeAsync().ConfigureAwait(false);
	}

	private async ValueTask<MediaDeckSnapshot> SendTransportKindAsync(
		MediaTransportCommandKind kind,
		CancellationToken cancellationToken)
	{
		var snapshot = RequireLoaded();
		var command = new MediaTransportCommand(
			MediaContractVersion.Current,
			snapshot.Probe!.AssetId,
			kind);
		var response = await _client.ApplyMediaDeckTransportAsync(command, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(response);
		return response;
	}

	private async ValueTask<MediaTransportCommandResult> SendTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken)
	{
		MediaTransportSnapshot previous;
		lock (_gate)
			previous = _snapshot.Transport ?? throw new InvalidOperationException("Media deck transport is not loaded.");

		var response = await _client.ApplyMediaDeckTransportAsync(command, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(response);
		if (response.Transport is not null && response.State != MediaDeckState.Error)
			return MediaTransportCommandResult.Accepted(response.Transport);

		var failure = response.Failure ?? new Failure(
			"client.media_deck.transport_failed",
			"Media-deck transport command failed.");
		return MediaTransportCommandResult.Rejected(previous, failure.Code, failure.Message);
	}

	private async ValueTask<MediaMarkerCommandResult> SendMarkerAsync(
		MediaMarkerCommand command,
		CancellationToken cancellationToken)
	{
		MediaMarkerSnapshot previous;
		lock (_gate)
			previous = _snapshot.Markers ?? throw new InvalidOperationException("Media deck markers are not loaded.");

		var response = await _client.ApplyMediaDeckMarkerAsync(command, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(response);
		if (response.Markers is not null && response.State != MediaDeckState.Error)
			return MediaMarkerCommandResult.Applied(response.Markers);

		var failure = response.Failure ?? new Failure(
			"client.media_deck.marker_failed",
			"Media-deck marker command failed.");
		return MediaMarkerCommandResult.Rejected(previous, failure.Code, failure.Message);
	}

	private void ApplySnapshot(MediaDeckSnapshot snapshot)
	{
		lock (_gate)
			_snapshot = snapshot;

		if (snapshot.Transport is not null)
			Timeline.ApplyConfirmedSnapshot(snapshot.Transport);
		if (snapshot.Markers is not null)
			Markers.ApplyConfirmedSnapshot(snapshot.Markers);
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	private MediaDeckSnapshot RequireLoaded()
	{
		ThrowIfDisposed();
		lock (_gate)
		{
			if (!_snapshot.IsLoaded || _snapshot.Probe is null)
				throw new InvalidOperationException("Media deck has no loaded asset.");
			return _snapshot;
		}
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
