// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Srt;

public interface ISrtTransport : IAsyncDisposable
{
	bool IsConnected { get; }
	ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken cancellationToken);
	ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
	/// <summary>Waits for buffered sends before orderly close; unbuffered transports need no extra work.</summary>
	ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public interface ISrtPayloadEncoder : IAsyncDisposable
{
	ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken cancellationToken);
	ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken);
	/// <summary>Completes encoding and returns all remaining mux bytes. No further input is accepted.</summary>
	ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken cancellationToken);
}

public sealed class SrtNetworkOutputProvider
{
	private static readonly ProviderId ProviderIdentity =
		new(new Identity(new Guid("9db66bf4-c189-4e0e-aa27-93083986aa10")));
	private static readonly CapabilityId CapabilityIdentity =
		new(new Identity(new Guid("bc86199c-fd43-4ee2-bc4c-87be56541e6a")));
	private static readonly ProviderResourceId ResourceIdentity =
		new(new Identity(new Guid("f2f17143-f487-44bc-90bf-77bbd86a7b3c")));

	public static IReadOnlyList<VideoFormat> SupportedFormats { get; } =
		Array.AsReadOnly(new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 });

	public ProviderDescriptor Descriptor { get; } = new(
		ProviderContractVersion.Current,
		ProviderIdentity,
		"SRT Network Output",
		new ProviderAvailability(ProviderAvailabilityState.Available),
		new[]
		{
			new ProviderCapabilityDescriptor(
				CapabilityIdentity,
				NetworkOutputCapabilityKinds.Output,
				SupportedFormats)
		},
		new[]
		{
			new ProviderResourceDescriptor(
				ResourceIdentity,
				ProviderIdentity,
				NetworkOutputCapabilityKinds.Output,
				2,
				true)
		});

	public SrtNetworkOutputSession CreateSession(
		NetworkOutputConfiguration configuration,
		string? nativeLibraryPath = null) =>
		new(
			configuration,
			() => new NativeSrtTransport(nativeLibraryPath),
			() => new WindowsMediaFoundationSrtEncoder());
}

public sealed class SrtNetworkOutputSession : INetworkOutputSession
{
	private static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromSeconds(10);
	private readonly object _gate = new();
	private readonly NetworkOutputConfiguration _configuration;
	private readonly Func<ISrtTransport> _transportFactory;
	private readonly Func<ISrtPayloadEncoder> _encoderFactory;
	private readonly TimeSpan _completionTimeout;
	private readonly SrtTransportPacketizer _packetizer = new();
	private readonly Action<int> _messageSent;
	private readonly Queue<NetworkOutputProgramSample> _queue = new();
	private readonly SemaphoreSlim _queueSignal = new(0);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly CancellationTokenSource _connectionStop = new();
	private readonly Task _worker;

	private ISrtTransport? _transport;
	private ISrtPayloadEncoder? _encoder;
	private bool _encoderInitialized;
	// A failed connection episode persists across Program samples. Only a
	// confirmed connection or a newly constructed session resets this budget.
	private int _retryEpisodeAttempts;
	private bool _retryEpisodeExhausted;
	private NetworkOutputLifecycleState _lifecycle = NetworkOutputLifecycleState.Disabled;
	private bool _connected;
	private ulong _accepted;
	private ulong _sent;
	private ulong _dropped;
	private ulong _rejected;
	private ulong _reconnects;
	private ulong _packetsSent;
	private ulong _bytesSent;
	private int _maximumQueueDepth;
	private DateTimeOffset? _lastSuccessfulSendUtc;
	private Failure? _failure;
	private Failure? _completionFailure;
	private volatile bool _disposed;
	private bool _workerStopped;
	private Task? _disposeTask;
	private int _synchronizationDisposed;

	public SrtNetworkOutputSession(
		NetworkOutputConfiguration configuration,
		Func<ISrtTransport> transportFactory,
		Func<ISrtPayloadEncoder> encoderFactory)
		: this(configuration, transportFactory, encoderFactory, DefaultCompletionTimeout)
	{
	}

	internal SrtNetworkOutputSession(
		NetworkOutputConfiguration configuration,
		Func<ISrtTransport> transportFactory,
		Func<ISrtPayloadEncoder> encoderFactory,
		TimeSpan completionTimeout)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		if (_configuration.Protocol != NetworkOutputProtocolFamily.Srt || _configuration.SrtSettings is null)
			throw new ArgumentException("SRT session requires typed SRT network-output settings.", nameof(configuration));
		_transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
		_encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
		if (completionTimeout <= TimeSpan.Zero || completionTimeout > DefaultCompletionTimeout)
			throw new ArgumentOutOfRangeException(nameof(completionTimeout));
		_completionTimeout = completionTimeout;
		_messageSent = ObserveSentMessage;
		_worker = Task.Run(WorkerAsync);
	}

	public NetworkOutputConfiguration Configuration => _configuration;

	public NetworkOutputHealthSnapshot Snapshot
	{
		get
		{
			lock (_gate)
			{
				return new NetworkOutputHealthSnapshot(
					_configuration.TargetId,
					"srt",
					NetworkOutputProtocolFamily.Srt,
					_configuration.SafeTargetIdentity,
					_completionFailure is null ? _lifecycle : NetworkOutputLifecycleState.Faulted,
					_completionFailure is null && _connected,
					_configuration.VideoFormat,
					_configuration.AudioFormat,
					_configuration.VideoCodec,
					_configuration.AudioCodec,
					_configuration.VideoBitRate,
					_configuration.AudioBitRate,
					_configuration.LatencyMilliseconds,
					new NetworkOutputStatistics(
						_accepted,
						_sent,
						_dropped,
						_rejected,
						_reconnects,
						_packetsSent,
						_bytesSent,
						_queue.Count,
						_maximumQueueDepth,
						_configuration.QueueCapacity),
					_lastSuccessfulSendUtc,
					_completionFailure ?? _failure);
			}
		}
	}

	public NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample)
	{
		ArgumentNullException.ThrowIfNull(sample);
		lock (_gate)
		{
			if (_disposed || _workerStopped || _shutdown.IsCancellationRequested)
			{
				_rejected++;
				sample.Video.Dispose();
				return NetworkOutputEnqueueResult.Rejected(
					new Failure("network.output.stopped", "Network output session is stopping or stopped."));
			}
			if (_retryEpisodeExhausted)
			{
				_rejected++;
				sample.Video.Dispose();
				return NetworkOutputEnqueueResult.Rejected(_failure ??
					new Failure("network.output.reconnect_exhausted", "SRT reconnect budget is exhausted."));
			}

			if (!sample.HasCompatibleRuntimeTiming(_configuration.VideoFormat, _configuration.AudioFormat))
			{
				_rejected++;
				sample.Video.Dispose();
				return NetworkOutputEnqueueResult.Rejected(
					new Failure("network.output.av_timebase_mismatch", "Video and audio must use a shared clock or the aligned Runtime frame/sample clocks."));
			}

			Failure? drop = null;
			if (_queue.Count >= _configuration.QueueCapacity)
			{
				var oldest = _queue.Dequeue();
				oldest.Video.Dispose();
				_dropped++;
				drop = new Failure(
					"network.output.backpressure.drop_oldest",
					"Network output queue was saturated; the oldest complete A/V sample was dropped to preserve live output.");
			}

			_queue.Enqueue(sample);
			_maximumQueueDepth = Math.Max(_maximumQueueDepth, _queue.Count);
			_accepted++;
			// Replacing an already signaled queued sample must not accumulate empty wakeups.
			if (drop is null)
				_queueSignal.Release();
			return drop is null
				? NetworkOutputEnqueueResult.AcceptedSample()
				: NetworkOutputEnqueueResult.AcceptedAfterDroppingOldest(drop.Value);
		}
	}

	private async Task WorkerAsync()
	{
		var cancellationToken = _shutdown.Token;
		Exception? workerFailure = null;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await _queueSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
				NetworkOutputProgramSample? queuedSample;
				lock (_gate)
				{
					if (_disposed && _queue.Count == 0)
						break;
					queuedSample = _queue.Count == 0 ? null : _queue.Dequeue();
				}
				if (queuedSample is null)
					continue;
				var sample = queuedSample;

				try
				{
					if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
					{
						// Preserve the terminal reconnect_exhausted or provider failure.
						// A shutdown interruption is not a newly failed retry episode.
						lock (_gate) _dropped++;
						continue;
					}

					lock (_gate)
					{
						if (_queue.Count > 0 && _reconnects > 0)
						{
							_dropped++;
							continue;
						}
					}
					var encoded = await _encoder!.EncodeAsync(sample, cancellationToken).ConfigureAwait(false);
					var messages = await _packetizer.SendAsync(_transport!, encoded, _messageSent, cancellationToken).ConfigureAwait(false);
					cancellationToken.ThrowIfCancellationRequested();
					if (messages == 0)
						continue;

					lock (_gate)
					{
						_sent++;
						_lifecycle = NetworkOutputLifecycleState.Connected;
						_connected = true;
						_failure = null;
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					lock (_gate) _dropped++;
					break;
				}
				catch (Exception exception)
				{
					var alignmentContext = _packetizer.AlignmentFailure is { } alignmentFailure ? $" {alignmentFailure}" : string.Empty;
					DropSample(
						sample,
						"network.output.send_failed",
						$"Network output sample failed without interrupting Program: {exception.GetType().Name}.");
					try
					{
						await ResetConnectionAsync(
							new Failure("network.output.transport_failed", $"SRT transport failed: {exception.GetType().Name}.{alignmentContext}"))
							.ConfigureAwait(false);
					}
					catch (Exception cleanupFailure)
					{
						throw new AggregateException("SRT sample execution and aborted-connection cleanup failed.", exception, cleanupFailure);
					}
					if (_disposed)
						throw new IOException("SRT output failed while completing the accepted Program samples.", exception);
				}
				finally
				{
					sample.Video.Dispose();
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			workerFailure = exception;
		}
		finally
		{
			try
			{
				await ReleaseConnectionAsync(
					orderly: _disposed && workerFailure is null && !cancellationToken.IsCancellationRequested,
					cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				workerFailure = workerFailure is null ? exception
					: new AggregateException("SRT worker and resource cleanup failed.", workerFailure, exception);
			}
			finally
			{
				lock (_gate)
				{
					_workerStopped = true;
					while (_queue.Count > 0)
					{
						_queue.Dequeue().Video.Dispose();
						_dropped++;
					}
					_connected = false;
					if (_lifecycle != NetworkOutputLifecycleState.Faulted)
						_lifecycle = NetworkOutputLifecycleState.Disabled;
				}
			}
		}
		if (workerFailure is not null)
		{
			ObserveCompletionFailure(workerFailure);
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(workerFailure).Throw();
		}
	}

	private void ObserveSentMessage(int bytes)
	{
		lock (_gate)
		{
			_packetsSent++;
			_bytesSent += checked((ulong)bytes);
			_lastSuccessfulSendUtc = DateTimeOffset.UtcNow;
		}
	}

	private async ValueTask<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
	{
		if (_transport is { IsConnected: true } && _encoder is not null && _encoderInitialized)
			return true;
		using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionStop.Token);
		cancellationToken = attemptCancellation.Token;

		var delay = _configuration.ReconnectInitialDelayMilliseconds;
		while (!cancellationToken.IsCancellationRequested)
		{
			int attempt;
			lock (_gate)
			{
				if (_retryEpisodeExhausted) return false;
				if (_configuration.ReconnectMaximumAttempts > 0 &&
					_retryEpisodeAttempts >= _configuration.ReconnectMaximumAttempts)
				{
					ExhaustRetryEpisodeLocked();
					return false;
				}
				attempt = _retryEpisodeAttempts < int.MaxValue
					? ++_retryEpisodeAttempts : int.MaxValue;
			}

			try
			{
				await ReleaseConnectionAsync(orderly: false, cancellationToken).ConfigureAwait(false);
				var transport = _transportFactory();
				_transport = transport;
				var encoder = _encoderFactory();
				_encoder = encoder;
				var passphrase = ResolvePassphrase();
				lock (_gate)
				{
					_lifecycle = attempt == 1 && _reconnects == 0
						? NetworkOutputLifecycleState.Connecting
						: NetworkOutputLifecycleState.Reconnecting;
					_connected = false;
				}
				await transport.ConnectAsync(_configuration, passphrase, cancellationToken).ConfigureAwait(false);
				await encoder.InitializeAsync(_configuration, cancellationToken).ConfigureAwait(false);
				_encoderInitialized = true;
				cancellationToken.ThrowIfCancellationRequested();
				lock (_gate)
				{
					if (attempt > 1 || _reconnects > 0)
						_reconnects++;
					_lifecycle = NetworkOutputLifecycleState.Connected;
					_connected = true;
					_retryEpisodeAttempts = 0;
					_retryEpisodeExhausted = false;
					_failure = null;
				}
				return true;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return false;
			}
			catch (Exception exception)
			{
				lock (_gate)
				{
					_connected = false;
					_lifecycle = NetworkOutputLifecycleState.Reconnecting;
					_failure = new Failure("network.output.connect_failed", $"SRT connection failed: {exception.GetType().Name}.");
				}
				await ReleaseConnectionAsync(orderly: false, cancellationToken).ConfigureAwait(false);
				lock (_gate)
				{
					if (_configuration.ReconnectMaximumAttempts > 0 &&
						_retryEpisodeAttempts >= _configuration.ReconnectMaximumAttempts)
					{
						ExhaustRetryEpisodeLocked();
						return false;
					}
				}
				try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
				delay = Math.Min(checked(delay * 2), _configuration.ReconnectMaximumDelayMilliseconds);
			}
		}
		return false;
	}

	// Must be invoked with _gate held. The cause remains observable until
	// an explicit new target session, not the next Program frame.
	private void ExhaustRetryEpisodeLocked()
	{
		_retryEpisodeExhausted = true;
		_connected = false;
		_lifecycle = NetworkOutputLifecycleState.Faulted;
		_failure = new Failure("network.output.reconnect_exhausted",
			$"Configured SRT reconnect attempts ({_configuration.ReconnectMaximumAttempts}) were exhausted; explicitly restart the target.");
	}

	private string? ResolvePassphrase()
	{
		if (string.IsNullOrWhiteSpace(_configuration.PassphraseEnvironmentVariable))
			return null;
		var passphrase = Environment.GetEnvironmentVariable(_configuration.PassphraseEnvironmentVariable);
		if (string.IsNullOrEmpty(passphrase))
			throw new InvalidOperationException("Configured SRT passphrase secret reference is unavailable.");
		if (passphrase.Length is < 10 or > 79)
			throw new InvalidOperationException("SRT passphrase must contain between 10 and 79 characters.");
		return passphrase;
	}

	private void DropSample(NetworkOutputProgramSample sample, string code, string message)
	{
		lock (_gate)
		{
			_dropped++;
			_failure = new Failure(code, message);
		}
	}

	private async ValueTask ResetConnectionAsync(Failure failure)
	{
		lock (_gate)
		{
			_connected = false;
			_lifecycle = NetworkOutputLifecycleState.Reconnecting;
			_failure = failure;
			_reconnects++;
		}
		await ReleaseConnectionAsync(orderly: false, CancellationToken.None).ConfigureAwait(false);
	}

	private async ValueTask ReleaseConnectionAsync(bool orderly, CancellationToken cancellationToken)
	{
		var encoder = Interlocked.Exchange(ref _encoder, null);
		var transport = Interlocked.Exchange(ref _transport, null);
		var encoderInitialized = _encoderInitialized;
		_encoderInitialized = false;
		List<Exception>? failures = null;
		if (orderly && encoderInitialized && encoder is not null && transport is not null)
		{
			try
			{
				var tail = await encoder.DrainAsync(cancellationToken).ConfigureAwait(false);
				await _packetizer.SendAsync(transport, tail, _messageSent, cancellationToken).ConfigureAwait(false);
				_packetizer.Complete();
				await transport.CompleteAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				(failures ??= []).Add(exception);
				ObserveCompletionFailure(exception, detail: _packetizer.AlignmentFailure);
			}
		}
		// An aborted connection never drains or carries old-stream bytes into its replacement.
		_packetizer.Reset();
		if (encoder is not null)
		{
			try { await encoder.DisposeAsync().ConfigureAwait(false); }
			catch (Exception exception) { (failures ??= []).Add(exception); }
		}
		if (transport is not null)
		{
			try { await transport.DisposeAsync().ConfigureAwait(false); }
			catch (Exception exception) { (failures ??= []).Add(exception); }
		}
		if (failures is { Count: > 0 })
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures.Count == 1
				? failures[0] : new AggregateException("SRT completion or resource cleanup failed.", failures)).Throw();
	}

	private void ObserveCompletionFailure(Exception exception, bool timedOut = false, string? detail = null)
	{
		lock (_gate)
		{
			_connected = false;
			_lifecycle = NetworkOutputLifecycleState.Faulted;
			if (_completionFailure is null || timedOut)
				_completionFailure = new Failure(timedOut ? "network.output.completion_timeout" : "network.output.completion_failed",
					timedOut ? "SRT completion exceeded its deadline; pending native work retains resource ownership until it returns."
						: $"SRT completion or cleanup failed: {exception.GetType().Name}.{(detail is null ? string.Empty : $" {detail}")}");
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposeTask is not null)
				return new ValueTask(_disposeTask);
			_disposed = true;
			if (_lifecycle != NetworkOutputLifecycleState.Faulted)
				_lifecycle = NetworkOutputLifecycleState.Stopping;
			// The worker owns native calls and cleanup. Starting disposal must never run a blocking native call inline.
			return new ValueTask(_disposeTask = Task.Run(DisposeCoreAsync));
		}
	}

	private async Task DisposeCoreAsync()
	{
		using var deadlineCancellation = new CancellationTokenSource();
		try
		{
			// There is no completed stream to preserve while a connection is still being established.
			_connectionStop.Cancel();
			_queueSignal.Release();
			var deadline = Task.Delay(_completionTimeout, deadlineCancellation.Token);
			if (await Task.WhenAny(_worker, deadline).ConfigureAwait(false) != _worker)
			{
				var exception = new TimeoutException("SRT orderly completion did not finish within its deadline.");
				ObserveCompletionFailure(exception, timedOut: true);
				_shutdown.Cancel();
				// A native call may ignore cancellation. Its worker remains the sole cleanup owner;
				// releasing its COM objects or library while it is in flight would be unsafe.
				_ = ReleaseSynchronizationAfterWorkerAsync();
				throw exception;
			}
			await _worker.ConfigureAwait(false);
		}
		finally
		{
			await deadlineCancellation.CancelAsync().ConfigureAwait(false);
			if (_worker.IsCompleted)
				ReleaseSynchronization();
		}
	}

	private async Task ReleaseSynchronizationAfterWorkerAsync()
	{
		try { await _worker.ConfigureAwait(false); }
		catch (Exception exception) { ObserveCompletionFailure(exception); }
		finally { ReleaseSynchronization(); }
	}

	private void ReleaseSynchronization()
	{
		if (Interlocked.Exchange(ref _synchronizationDisposed, 1) != 0)
			return;
		_queueSignal.Dispose();
		_shutdown.Dispose();
		_connectionStop.Dispose();
	}
}
