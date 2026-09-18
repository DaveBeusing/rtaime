// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Runtime-owned single local-media deck. ControlHost supplies the prepared execution and remains authoritative;
/// this service owns only decode, transport and frame advancement.
/// </summary>
public sealed class LocalMediaDeckRuntimeService : IDisposable
{
	private readonly object _gate = new();
	private readonly LocalMediaFileProvider _provider = new();
	private LocalMediaRuntimeSession? _session;
	private Failure? _failure;
	private bool _disposed;

	public ProviderDescriptor ProviderDescriptor => _provider.Descriptor;

	public MediaDeckRuntimeSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return CreateSnapshot();
		}
	}

	public MediaDeckRuntimeSnapshot Open(
		MediaDeckOpenRequest request,
		PreparedExecutionContract preparedExecution)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(preparedExecution);
		ObjectDisposedException.ThrowIf(_disposed, this);

		lock (_gate)
		{
			DisposeSession();
			_failure = null;

			var open = _provider.TryOpen(request.Path, request.SourceId);
			if (!open.Succeeded || open.Source is null)
			{
				_failure = open.Failure ?? new Failure(
					"runtime.media_deck.open_failed",
					"Local media deck could not open the requested file.");
				return CreateSnapshot();
			}

			var session = new LocalMediaRuntimeSession(_provider, open.Source);
			var applied = session.ApplyExecution(preparedExecution);
			if (!applied.Committed)
			{
				var failure = applied.Commit?.Failure ??
					applied.Prepare.Failure ??
					new Failure("runtime.media_deck.commit_failed", "Local media deck execution was not committed.");
				session.Dispose();
				_failure = failure;
				return CreateSnapshot();
			}

			_session = session;
			return CreateSnapshot();
		}
	}

	public MediaTransportCommandResult ApplyTransport(MediaTransportCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_session is null)
			{
				return MediaTransportCommandResult.Rejected(
					CreateEmptyTransport(command.AssetId),
					"runtime.media_deck.unloaded",
					"Local media deck has no loaded asset.");
			}

			var result = _session.ApplyTransport(command);
			if (!result.Succeeded && result.Failure is { } failure)
				_failure = failure;
			else if (result.Succeeded)
				_failure = null;
			return result;
		}
	}

	public MediaDeckRuntimeSnapshot ProcessBoundary()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_session is null || _session.Transport.State != MediaTransportState.Playing)
				return CreateSnapshot();

			var result = _session.ProcessNextBoundary();
			if (result.Status == LocalMediaRuntimeBoundaryStatus.Failed)
				_failure = result.Failure;
			return CreateSnapshot();
		}
	}

	public MediaDeckRuntimeSnapshot Close()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			DisposeSession();
			_failure = null;
			return MediaDeckRuntimeSnapshot.Unloaded;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		lock (_gate)
			DisposeSession();
	}

	private MediaDeckRuntimeSnapshot CreateSnapshot()
	{
		if (_session is null)
		{
			return _failure is null
				? MediaDeckRuntimeSnapshot.Unloaded
				: new MediaDeckRuntimeSnapshot(
					MediaContractVersion.Current,
					MediaDeckState.Error,
					failure: _failure);
		}

		var transport = _session.Transport;
		var state = transport.State switch
		{
			MediaTransportState.Ready => MediaDeckState.Ready,
			MediaTransportState.Playing => MediaDeckState.Playing,
			MediaTransportState.Paused => MediaDeckState.Paused,
			MediaTransportState.Ended => MediaDeckState.Ended,
			MediaTransportState.Error => MediaDeckState.Error,
			_ => MediaDeckState.Ready
		};
		var failure = state == MediaDeckState.Error
			? transport.Failure ?? _failure ?? new Failure("runtime.media_deck.failed", "Local media deck entered an error state.")
			: null;
		return new MediaDeckRuntimeSnapshot(
			MediaContractVersion.Current,
			state,
			_session.Probe.SourceId,
			_session.Probe,
			transport,
			failure);
	}

	private void DisposeSession()
	{
		_session?.Dispose();
		_session = null;
	}

	private static MediaTransportSnapshot CreateEmptyTransport(MediaAssetId assetId) =>
		new(
			MediaContractVersion.Current,
			assetId,
			MediaSourceId.New(),
			MediaTransportState.Unloaded,
			new MediaTransportPosition(
				0,
				1,
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(20),
				TimeSpan.FromMilliseconds(20),
				FrameRate.Fps50),
			null);
}
