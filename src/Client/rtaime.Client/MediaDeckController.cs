// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

internal sealed class MediaDeckTransportUnavailableException : InvalidOperationException
{
	public MediaDeckTransportUnavailableException() : base("Media deck transport is not loaded.") { }
}

internal sealed class MediaDeckMarkersUnavailableException : InvalidOperationException
{
	public MediaDeckMarkersUnavailableException() : base("Media deck markers are not loaded.") { }
}

public sealed class MediaDeckController : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly OperatorControlClient _client;
	private bool _disposed;
    private MediaSourceId? _selectedSource;
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

	public ValueTask<MediaDeckSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
		ReadSnapshotAsync(_selectedSource, select: false, cancellationToken);

	public ValueTask<MediaDeckSnapshot> SelectAsync(MediaSourceId source, CancellationToken cancellationToken = default) =>
		ReadSnapshotAsync(source, select: true, cancellationToken);

	private async ValueTask<MediaDeckSnapshot> ReadSnapshotAsync(MediaSourceId? source, bool select, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		var synchronization = Timeline.CaptureSynchronizationState();
		try
		{
			if (synchronization.Required)
				await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
			var snapshot = source is { } selected
				? await _client.GetMediaDeckSnapshotAsync(selected, cancellationToken).ConfigureAwait(false)
				: await _client.GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (ApplySnapshot(snapshot, synchronization.Generation, synchronization.Required) && select)
				_selectedSource = source;
			return snapshot;
		}
		catch (Exception exception) when (MediaTimelineController.IsRecoverableTransportFailure(exception))
		{
			Timeline.SuspendUntilSynchronization(exception.Message);
			throw;
		}
	}

	public ValueTask<MediaDeckSnapshot> OpenAsync(
		string path,
		MediaSourceId sourceId,
		CancellationToken cancellationToken = default) =>
		OpenAsync(path, sourceId, null, cancellationToken);

	public async ValueTask<MediaDeckSnapshot> OpenAsync(
		string path,
		MediaSourceId sourceId,
		MediaAssetId? assetId,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
        _selectedSource = sourceId;
		var generation = Timeline.CaptureSynchronizationState().Generation;
		var snapshot = await RequestMutationAsync(
			() => _client.OpenMediaDeckAsync(path, sourceId, assetId, cancellationToken),
			generation, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(snapshot, generation);
		return snapshot;
	}

	public ValueTask<MediaDeckSnapshot> PlayAsync(CancellationToken cancellationToken = default) =>
		SendTransportKindAsync(MediaTransportCommandKind.Play, cancellationToken);

	public ValueTask<MediaDeckSnapshot> PauseAsync(CancellationToken cancellationToken = default) =>
		SendTransportKindAsync(MediaTransportCommandKind.Pause, cancellationToken);

	public ValueTask<MediaDeckSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
		SendTransportKindAsync(MediaTransportCommandKind.Stop, cancellationToken);

	public async ValueTask<MediaDeckSnapshot> ConfigurePlaybackAsync(
		bool autoPlayOnProgram,
		MediaDeckEndBehavior endBehavior,
		CancellationToken cancellationToken = default)
	{
		if (!Enum.IsDefined(typeof(MediaDeckEndBehavior), endBehavior))
			throw new ArgumentOutOfRangeException(nameof(endBehavior));
		var snapshot = RequireLoaded();
		var generation = Timeline.CaptureSynchronizationState().Generation;
		var command = new MediaTransportCommand(
			MediaContractVersion.Current,
			snapshot.Probe!.AssetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: autoPlayOnProgram,
			endBehavior: endBehavior) { SourceId = snapshot.SourceId };
		var response = await RequestMutationAsync(
			() => _client.ApplyMediaDeckTransportAsync(command with { SourceId = _selectedSource ?? command.SourceId }, cancellationToken),
			generation, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(response, generation);
		return response;
	}

	public async ValueTask<MediaDeckSnapshot> CloseAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		var generation = Timeline.CaptureSynchronizationState().Generation;
		var snapshot = await RequestMutationAsync(
			() => _selectedSource is { } source
				? _client.CloseMediaDeckAsync(source, cancellationToken)
				: _client.CloseMediaDeckAsync(cancellationToken),
			generation, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(snapshot, generation);
		return snapshot;
	}

	public void ApplyConfirmedSnapshot(MediaDeckSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ThrowIfDisposed();
        if (_selectedSource is { } source && snapshot.SourceId != source) return;
		ApplySnapshot(snapshot, Timeline.CaptureSynchronizationState().Generation);
	}

	public async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
		}
		await Markers.DisposeAsync().ConfigureAwait(false);
		await Timeline.DisposeAsync().ConfigureAwait(false);
	}

	private async ValueTask<MediaDeckSnapshot> SendTransportKindAsync(
		MediaTransportCommandKind kind,
		CancellationToken cancellationToken)
	{
		var snapshot = RequireLoaded();
		var generation = Timeline.CaptureSynchronizationState().Generation;
		var command = new MediaTransportCommand(
			MediaContractVersion.Current,
			snapshot.Probe!.AssetId,
			kind) { SourceId = snapshot.SourceId };
		var response = await RequestMutationAsync(
			() => _client.ApplyMediaDeckTransportAsync(command with { SourceId = _selectedSource ?? command.SourceId }, cancellationToken),
			generation, cancellationToken).ConfigureAwait(false);
		ApplySnapshot(response, generation);
		return response;
	}

	private async ValueTask<MediaTransportCommandResult> SendTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken)
	{
		MediaTransportSnapshot previous;
		lock (_gate)
			previous = _snapshot.Transport ?? throw new MediaDeckTransportUnavailableException();
		var generation = Timeline.CaptureSynchronizationState().Generation;

		var response = await _client.ApplyMediaDeckTransportAsync(command with { SourceId = _selectedSource ?? command.SourceId }, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		ApplySnapshot(response, generation);
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
			previous = _snapshot.Markers ?? throw new MediaDeckMarkersUnavailableException();
		var generation = Timeline.CaptureSynchronizationState().Generation;

		var response = await _client.ApplyMediaDeckMarkerAsync(command with { SourceId = _selectedSource ?? command.SourceId }, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		ApplySnapshot(response, generation);
		if (response.Markers is not null && response.State != MediaDeckState.Error)
			return MediaMarkerCommandResult.Applied(response.Markers);

		var failure = response.Failure ?? new Failure(
			"client.media_deck.marker_failed",
			"Media-deck marker command failed.");
		return MediaMarkerCommandResult.Rejected(previous, failure.Code, failure.Message);
	}

	private async ValueTask<MediaDeckSnapshot> RequestMutationAsync(
		Func<ValueTask<MediaDeckSnapshot>> send, long generation, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!Timeline.IsCommandGenerationCurrent(generation))
			throw new ControlSnapshotRequiredException("Media state must be synchronized before another command.");
		try
		{
			var response = await send().ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return response;
		}
		catch (OperationCanceledException)
		{
			Timeline.SuspendUntilSynchronization("The media action was cancelled after dispatch; its outcome is unknown.");
			throw;
		}
		catch (Exception exception) when (MediaTimelineController.IsRecoverableTransportFailure(exception))
		{
			Timeline.SuspendUntilSynchronization(exception.Message);
			throw;
		}
	}

	private bool ApplySnapshot(MediaDeckSnapshot snapshot, long generation, bool synchronized = false)
	{
		lock (_gate)
			if (_disposed) return false;
		if (!Timeline.ApplyObservedSnapshot(snapshot.Transport, generation, synchronized)) return false;
		lock (_gate)
		{
			if (_disposed) return false;
			_snapshot = snapshot;
		}
		if (!Markers.ApplyObservedSnapshot(snapshot.Markers)) return false;
		StateChanged?.Invoke(this, EventArgs.Empty);
		return true;
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

