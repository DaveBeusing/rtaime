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
internal sealed class LocalMediaDeckSlot : IDisposable
{
	private readonly object _gate = new();
    private readonly object _openGate = new();
	private readonly LocalMediaFileProvider _provider;
	private LocalMediaRuntimeSession? _session;
    private LocalMediaRuntimeSession? _quarantinedSession;
	private LocalMediaRuntimeBoundaryResult? _latestBoundary;
	private Failure? _failure;
	private bool _autoPlayOnProgram = true;
    private bool _preparedNextPlayback;
	private MediaDeckEndBehavior _endBehavior = MediaDeckEndBehavior.HoldLastFrame;
	private long? _inPointFrame;
	private long? _outPointFrame;
	private bool _isOnProgram;
	private bool _disposed;
    private bool _opening;
    private long _openGeneration;

	public LocalMediaDeckSlot()
		: this(VideoFormat.Hd1080p50Rgba8)
	{
	}

	public LocalMediaDeckSlot(VideoFormat outputFormat)
	{
		_provider = new LocalMediaFileProvider(outputFormat);
	}

    internal LocalMediaDeckSlot(LocalMediaFileProvider provider, LocalMediaRuntimeSession session)
    {
        _provider = provider;
        _session = session;
    }

    internal bool HasPreparedAudio { get { lock (_gate) return _session?.HasPreparedAudio == true; } }
    internal void PrepareNext()
    {
        lock (_openGate)
        {
            LocalMediaRuntimeSession session;
            lock (_gate)
            {
                if (_isOnProgram || _session is null || _opening || _quarantinedSession is not null ||
                    _session.Transport.State == MediaTransportState.Playing)
                    throw new InvalidOperationException("Preload requires an inactive qualified owner.");
                session = _session;
                _autoPlayOnProgram = true;
                _endBehavior = MediaDeckEndBehavior.HoldLastFrame;
                var result = ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, session.Probe.AssetId,
                    MediaTransportCommandKind.Seek, EffectiveStartFrameUnsafe()));
                if (!result.Succeeded) throw new InvalidOperationException(result.Failure?.Message);
                _preparedNextPlayback = true;
            }
            if (!session.WaitForPreroll(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("Prepared-next preroll failed or timed out.");
        }
    }

	public ProviderDescriptor ProviderDescriptor => _provider.Descriptor;
    public LocalMediaDecodeSnapshot? DecodeSnapshot { get { lock (_gate) return _session?.DecodeSnapshot; } }
    public LocalMediaPlaybackMeasurements? PlaybackMeasurements { get { lock (_gate) return _session?.Measurements; } }

    public MediaAssetProbeResult Probe(string path, MediaAssetId assetId)
    {
        lock (_openGate)
        {
            lock (_gate)
                if (_quarantinedSession is not null)
                    return MediaAssetProbeResult.Rejected(_failure!.Value.Code, _failure.Value.Message);
            return ProbeCore(path, assetId);
        }
    }

    private MediaAssetProbeResult ProbeCore(string path, MediaAssetId assetId)
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
        lock (_openGate) return OpenCore(request, preparedExecution);
    }

    private MediaDeckRuntimeSnapshot OpenCore(MediaDeckOpenRequest request, PreparedExecutionContract preparedExecution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preparedExecution);
        ObjectDisposedException.ThrowIf(_disposed, this);

        LocalMediaRuntimeSession? previous;
        long generation;
        lock (_gate)
        {
            if (_quarantinedSession is not null) return CreateSnapshot();
            generation = ++_openGeneration;
            previous = _session ?? _quarantinedSession;
            _session = null;
            _opening = true;
            _latestBoundary?.Dispose();
            _latestBoundary = null;
            _failure = null;
            _inPointFrame = null;
            _outPointFrame = null;
            _isOnProgram = false;
        }
        // Probing, allocation, preroll and worker join run outside the admission lock.
        if (!ReleaseSession(previous)) { lock (_gate) return CreateSnapshot(); }
        LocalMediaRuntimeSession? candidate = null;
        Failure? failure = null;
        try
        {
            var open = _provider.TryOpen(request.Path, request.SourceId, request.AssetId);
            if (open.Source is null) failure = open.Failure;
            else
            {
                candidate = new LocalMediaRuntimeSession(_provider, open.Source);
                var applied = candidate.ApplyExecution(preparedExecution);
                if (!applied.Committed) failure = applied.Commit?.Failure ?? applied.Prepare.Failure ??
                    new Failure("runtime.media_deck.commit_failed", "Local media deck execution was not committed.");
            }
        }
        catch (Exception exception)
        {
            failure = new Failure("runtime.media_deck.open_failed", $"Local media preparation failed: {exception.Message}");
        }
        var accepted = false;
        MediaDeckRuntimeSnapshot snapshot;
        lock (_gate)
        {
            if (generation == _openGeneration && !_disposed)
            {
                _opening = false;
                _failure = failure;
                if (failure is null) { _session = candidate; accepted = true; }
            }
            snapshot = CreateSnapshot();
        }
        if (!accepted && !ReleaseSession(candidate)) { lock (_gate) return CreateSnapshot(); }
        return snapshot;
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

            if (command.Kind != MediaTransportCommandKind.Play) _preparedNextPlayback = false;
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
            if (command.Kind is MediaTransportCommandKind.Seek or MediaTransportCommandKind.Stop or
                MediaTransportCommandKind.JumpToStart or MediaTransportCommandKind.StepBackward or MediaTransportCommandKind.StepForward)
            {
                _latestBoundary?.Dispose();
                _latestBoundary = null;
            }
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

	public MediaDeckRuntimeSnapshot ProcessBoundary(ulong? programSlot = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_session is null || _session.Transport.State != MediaTransportState.Playing)
			{
				_latestBoundary?.Dispose();
            _latestBoundary = null;
				return CreateSnapshot();
			}

            var previousPosition = _session.Transport.Position.CurrentFrame;
			var result = _session.ProcessNextBoundary(programSlot);
			_latestBoundary?.Dispose();
            _latestBoundary = result.Retain();
			if (result.Status == LocalMediaRuntimeBoundaryStatus.Failed)
			{
				_failure = result.Failure;
				return CreateSnapshot();
			}

			if (result.Succeeded && result.Transport.Position.CurrentFrame >= EffectiveEndFrameUnsafe())
				ApplyEndBehaviorUnsafe();
            else if (result.Status == LocalMediaRuntimeBoundaryStatus.Ended)
            {
                if (_preparedNextPlayback && previousPosition < EffectiveEndFrameUnsafe())
                    _failure = new Failure("runtime.media_preload.early_eof", "Prepared clip ended before its confirmed OUT. Automatic follow is blocked.");
                else ApplyEndBehaviorUnsafe();
            }

			return CreateSnapshot();
		}
	}

    // The caller receives its own lease while the deck/session retain their current admission.
    public MediaDeckRuntimeSnapshot ProcessBoundary(out LocalMediaRuntimeBoundaryResult? boundary, ulong? programSlot = null)
    {
        lock (_gate)
        {
            var snapshot = ProcessBoundary(programSlot);
            boundary = _latestBoundary?.Retain();
            return snapshot;
        }
    }

    public MediaDeckRuntimeSnapshot Close()
    {
        lock (_openGate) return CloseCore();
    }

    private MediaDeckRuntimeSnapshot CloseCore()
    {
        LocalMediaRuntimeSession? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ++_openGeneration;
            previous = _session ?? _quarantinedSession;
            _session = null;
            _opening = false;
            _latestBoundary?.Dispose();
            _latestBoundary = null;
            _failure = null;
            _inPointFrame = null;
            _outPointFrame = null;
            _isOnProgram = false;
        }
        if (!ReleaseSession(previous)) { lock (_gate) return CreateSnapshot(); }
        return MediaDeckRuntimeSnapshot.Unloaded;
    }

    public void Dispose()
    {
        lock (_openGate) DisposeCore();
    }

    private void DisposeCore()
    {
        LocalMediaRuntimeSession? previous;
        lock (_gate)
        {
            if (_disposed && _quarantinedSession is null) return;
            _disposed = true;
            ++_openGeneration;
            previous = _session ?? _quarantinedSession;
            _session = null;
            _latestBoundary?.Dispose();
            _latestBoundary = null;
        }
        if (!ReleaseSession(previous)) throw new IOException(_failure!.Value.Message);
    }

    private bool ReleaseSession(LocalMediaRuntimeSession? session)
    {
        if (session is null) return true;
        try { session.Dispose(); return true; }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _quarantinedSession = session;
                _opening = false;
                _failure = new Failure("runtime.media_deck.cleanup_failed",
                    $"Local media cleanup failed; reopen is blocked until a supported RuntimeHost restart: {exception.Message}");
            }
            return false;
        }
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
            if (_opening) return new MediaDeckRuntimeSnapshot(MediaContractVersion.Current, MediaDeckState.Loading);
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
			MediaTransportState.Ready when _session.IsBuffering => MediaDeckState.Loading,
            MediaTransportState.Playing when _session.IsBuffering => MediaDeckState.Loading,
            MediaTransportState.Paused when _session.IsBuffering => MediaDeckState.Loading,
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
