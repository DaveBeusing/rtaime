// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Runtime-owned single local-media deck. ControlHost supplies the prepared execution and remains authoritative;
/// this service owns decode, transport, Program-edge autoplay and deterministic clip-end behavior.
/// </summary>
public sealed class LocalMediaDeckRuntimeService : IDisposable
{
	private readonly object _gate = new();
	private readonly LocalMediaFileProvider _provider;
	private LocalMediaRuntimeSession? _session;
	private LocalMediaRuntimeBoundaryResult? _latestBoundary;
	private Failure? _failure;
	private bool _autoPlayOnProgram = true;
	private MediaDeckEndBehavior _endBehavior = MediaDeckEndBehavior.HoldLastFrame;
	private long? _inPointFrame;
	private long? _outPointFrame;
	private bool _isOnProgram;
	private bool _disposed;

	public LocalMediaDeckRuntimeService()
		: this(VideoFormat.Hd1080p50Rgba8)
	{
	}

	public LocalMediaDeckRuntimeService(VideoFormat outputFormat)
	{
		_provider = new LocalMediaFileProvider(outputFormat);
	}

	public ProviderDescriptor ProviderDescriptor => _provider.Descriptor;

	public MediaAssetProbeResult Probe(string path, MediaAssetId assetId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var open = _provider.TryOpen(path, new MediaSourceId(Identity.New()), assetId);
		if (!open.Succeeded || open.Source is null)
		{
			var failure = open.Failure ?? new Failure(
				"runtime.media_asset.probe_failed",
				"Local media could not be probed.");
			return MediaAssetProbeResult.Rejected(failure.Code, failure.Message);
		}

		using var source = open.Source;
		return MediaAssetProbeResult.Ready(source.Probe);
	}

	public LocalMediaRuntimeBoundaryResult? LatestBoundary
	{
		get
		{
			lock (_gate)
				return _latestBoundary;
		}
	}

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
			_latestBoundary = null;
			_failure = null;
			_inPointFrame = null;
			_outPointFrame = null;
			_isOnProgram = false;

			var open = _provider.TryOpen(request.Path, request.SourceId, request.AssetId);
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
			if (command.AssetId != _session.Probe.AssetId)
			{
				return MediaTransportCommandResult.Rejected(
					Decorate(_session.Transport),
					"runtime.media_deck.asset_mismatch",
					"Media-deck command targets a different media asset.");
			}

			if (command.Kind == MediaTransportCommandKind.ConfigurePlayback)
			{
				var totalFrames = _session.Transport.Position.TotalFrames;
				var inPoint = command.InPointFrame;
				var outPoint = command.OutPointFrame;
				if (inPoint.HasValue && inPoint.Value >= totalFrames)
					return ConfigurationRejected("runtime.media_deck.in_out_of_range", "Playback IN point is outside the loaded clip.");
				if (outPoint.HasValue && outPoint.Value >= totalFrames)
					return ConfigurationRejected("runtime.media_deck.out_out_of_range", "Playback OUT point is outside the loaded clip.");

				_autoPlayOnProgram = command.AutoPlayOnProgram!.Value;
				_endBehavior = command.EndBehavior!.Value;
				_inPointFrame = inPoint;
				_outPointFrame = outPoint;
				_failure = null;
				return MediaTransportCommandResult.Accepted(Decorate(_session.Transport));
			}

			var result = _session.ApplyTransport(command);
			if (!result.Succeeded && result.Failure is { } failure)
				_failure = failure;
			else if (result.Succeeded)
				_failure = null;
			return new MediaTransportCommandResult(
				result.Succeeded,
				Decorate(result.Snapshot),
				result.Failure);
		}
	}

	public MediaDeckRuntimeSnapshot ObserveProgramSource(MediaSourceId? programSourceId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_session is null)
			{
				_isOnProgram = false;
				return CreateSnapshot();
			}

			var wasOnProgram = _isOnProgram;
			_isOnProgram = programSourceId.HasValue && programSourceId.Value == _session.Probe.SourceId;
			if (_isOnProgram && !wasOnProgram && _autoPlayOnProgram)
				StartForProgramUnsafe();

			return CreateSnapshot();
		}
	}

	public MediaDeckRuntimeSnapshot ProcessBoundary()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_session is null || _session.Transport.State != MediaTransportState.Playing)
			{
				_latestBoundary = null;
				return CreateSnapshot();
			}

			var result = _session.ProcessNextBoundary();
			_latestBoundary = result;
			if (result.Status == LocalMediaRuntimeBoundaryStatus.Failed)
			{
				_failure = result.Failure;
				return CreateSnapshot();
			}

			if (result.Succeeded && result.Transport.Position.CurrentFrame >= EffectiveEndFrameUnsafe())
				ApplyEndBehaviorUnsafe();
			else if (result.Status == LocalMediaRuntimeBoundaryStatus.Ended)
				ApplyEndBehaviorUnsafe();

			return CreateSnapshot();
		}
	}

	public MediaDeckRuntimeSnapshot Close()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			DisposeSession();
			_latestBoundary = null;
			_failure = null;
			_inPointFrame = null;
			_outPointFrame = null;
			_isOnProgram = false;
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

	private void StartForProgramUnsafe()
	{
		if (_session is null)
			return;

		var transport = _session.Transport;
		var start = EffectiveStartFrameUnsafe();
		var end = EffectiveEndFrameUnsafe();
		if (transport.Position.CurrentFrame < start ||
			transport.Position.CurrentFrame > end ||
			transport.State == MediaTransportState.Ended)
		{
			var seek = _session.ApplyTransport(new MediaTransportCommand(
				MediaContractVersion.Current,
				_session.Probe.AssetId,
				MediaTransportCommandKind.Seek,
				start));
			if (!seek.Succeeded)
			{
				_failure = seek.Failure;
				return;
			}
			transport = seek.Snapshot;
		}

		if (transport.State is MediaTransportState.Ready or MediaTransportState.Paused)
		{
			var play = _session.ApplyTransport(new MediaTransportCommand(
				MediaContractVersion.Current,
				_session.Probe.AssetId,
				MediaTransportCommandKind.Play));
			if (!play.Succeeded)
				_failure = play.Failure;
		}
	}

	private void ApplyEndBehaviorUnsafe()
	{
		if (_session is null)
			return;

		var assetId = _session.Probe.AssetId;
		MediaTransportCommandResult result;
		switch (_endBehavior)
		{
			case MediaDeckEndBehavior.HoldLastFrame:
				if (_session.Transport.State != MediaTransportState.Ended)
				_session.MarkEnded(EffectiveEndFrameUnsafe());
				return;

			case MediaDeckEndBehavior.Stop:
				result = _session.ApplyTransport(new MediaTransportCommand(
					MediaContractVersion.Current,
					assetId,
					MediaTransportCommandKind.Stop));
				break;

			case MediaDeckEndBehavior.Loop:
				result = _session.ApplyTransport(new MediaTransportCommand(
					MediaContractVersion.Current,
					assetId,
					MediaTransportCommandKind.Seek,
					EffectiveStartFrameUnsafe()));
				if (result.Succeeded && result.Snapshot.State != MediaTransportState.Playing)
				{
					result = _session.ApplyTransport(new MediaTransportCommand(
						MediaContractVersion.Current,
						assetId,
						MediaTransportCommandKind.Play));
				}
				break;

			case MediaDeckEndBehavior.ReturnToIn:
				if (_session.Transport.State == MediaTransportState.Playing)
				{
					result = _session.ApplyTransport(new MediaTransportCommand(
						MediaContractVersion.Current,
						assetId,
						MediaTransportCommandKind.Pause));
					if (!result.Succeeded)
						break;
				}
				result = _session.ApplyTransport(new MediaTransportCommand(
					MediaContractVersion.Current,
					assetId,
					MediaTransportCommandKind.Seek,
					EffectiveStartFrameUnsafe()));
				break;

			default:
				throw new InvalidOperationException($"Unsupported media deck end behavior '{_endBehavior}'.");
		}

		if (!result.Succeeded)
			_failure = result.Failure;
	}

	private long EffectiveStartFrameUnsafe()
	{
		if (_session is null)
			return 0;
		return Math.Clamp(_inPointFrame ?? 0, 0, _session.Transport.Position.TotalFrames - 1);
	}

	private long EffectiveEndFrameUnsafe()
	{
		if (_session is null)
			return 0;
		var last = _session.Transport.Position.TotalFrames - 1;
		return Math.Clamp(_outPointFrame ?? last, EffectiveStartFrameUnsafe(), last);
	}

	private MediaTransportSnapshot Decorate(MediaTransportSnapshot transport)
	{
		var start = Math.Clamp(_inPointFrame ?? 0, 0, transport.Position.TotalFrames - 1);
		var end = Math.Clamp(_outPointFrame ?? transport.Position.TotalFrames - 1, start, transport.Position.TotalFrames - 1);
		var effectiveCurrent = Math.Clamp(transport.Position.CurrentFrame, start, end);
		var remainingFrames = Math.Max(0, end - effectiveCurrent);
		var remaining = TimeSpan.FromSeconds(
			remainingFrames * transport.Position.FrameRate.Denominator /
			(double)transport.Position.FrameRate.Numerator);
		return new MediaTransportSnapshot(
			transport.Version,
			transport.AssetId,
			transport.SourceId,
			transport.State,
			transport.Position,
			transport.Failure,
			_autoPlayOnProgram,
			_endBehavior,
			_isOnProgram,
			start,
			end,
			remainingFrames,
			remaining);
	}

	private MediaTransportCommandResult ConfigurationRejected(string code, string message) =>
		MediaTransportCommandResult.Rejected(
			Decorate(_session!.Transport),
			code,
			message);

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

		var transport = Decorate(_session.Transport);
		var state = transport.State switch
		{
			MediaTransportState.Ready => MediaDeckState.Ready,
			MediaTransportState.Playing => MediaDeckState.Playing,
			MediaTransportState.Paused => MediaDeckState.Paused,
			MediaTransportState.Ended => MediaDeckState.Ended,
			MediaTransportState.Error => MediaDeckState.Error,
			_ => MediaDeckState.Ready
		};
		Failure? failure = state == MediaDeckState.Error
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
		_latestBoundary = null;
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
