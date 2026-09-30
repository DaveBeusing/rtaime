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
}

public interface ISrtPayloadEncoder : IAsyncDisposable
{
	ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken cancellationToken);
	ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken);
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

public sealed class SrtNetworkOutputSession : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly NetworkOutputConfiguration _configuration;
	private readonly Func<ISrtTransport> _transportFactory;
	private readonly Func<ISrtPayloadEncoder> _encoderFactory;
	private readonly Queue<NetworkOutputProgramSample> _queue = new();
	private readonly SemaphoreSlim _queueSignal = new(0);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Task _worker;

	private ISrtTransport? _transport;
	private ISrtPayloadEncoder? _encoder;
	private NetworkOutputLifecycleState _lifecycle = NetworkOutputLifecycleState.Disabled;
	private bool _connected;
	private ulong _accepted;
	private ulong _sent;
	private ulong _dropped;
	private ulong _rejected;
	private ulong _reconnects;
	private ulong _packetsSent;
	private ulong _bytesSent;
	private DateTimeOffset? _lastSuccessfulSendUtc;
	private Failure? _failure;
	private bool _disposed;

	public SrtNetworkOutputSession(
		NetworkOutputConfiguration configuration,
		Func<ISrtTransport> transportFactory,
		Func<ISrtPayloadEncoder> encoderFactory)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		_transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
		_encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
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
					_lifecycle,
					_connected,
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
						_queue.Count),
					_lastSuccessfulSendUtc,
					_failure);
			}
		}
	}

	public NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample)
	{
		ArgumentNullException.ThrowIfNull(sample);
		lock (_gate)
		{
			if (_disposed || _shutdown.IsCancellationRequested)
			{
				_rejected++;
				sample.Video.Dispose();
				return NetworkOutputEnqueueResult.Rejected(
					new Failure("network.output.stopped", "Network output session is stopping or stopped."));
			}

			if (sample.VideoTiming.Timebase != sample.AudioTiming.Timebase)
			{
				_rejected++;
				sample.Video.Dispose();
				return NetworkOutputEnqueueResult.Rejected(
					new Failure("network.output.av_timebase_mismatch", "Video and audio must use the same Runtime media timebase."));
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
			_accepted++;
			_queueSignal.Release();
			return drop is null
				? NetworkOutputEnqueueResult.AcceptedSample()
				: NetworkOutputEnqueueResult.AcceptedAfterDroppingOldest(drop.Value);
		}
	}

	private async Task WorkerAsync()
	{
		var cancellationToken = _shutdown.Token;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await _queueSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
				NetworkOutputProgramSample? queuedSample;
				lock (_gate)
					queuedSample = _queue.Count == 0 ? null : _queue.Dequeue();
				if (queuedSample is null)
					continue;
				var sample = queuedSample;

				try
				{
					var reconnecting = !await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
					if (reconnecting)
					{
						DropSample(sample, "network.output.reconnect.cancelled", "Network output reconnect did not complete.");
						continue;
					}

					lock (_gate)
					{
						if (_queue.Count > 0 && _reconnects > 0)
						{
							_dropped++;
							sample.Video.Dispose();
							sample = null;
						}
					}
					if (sample is null)
						continue;

					var encoded = await _encoder!.EncodeAsync(sample, cancellationToken).ConfigureAwait(false);
					if (encoded.IsEmpty)
						throw new InvalidDataException("Network encoder returned an empty transport payload.");
					var sent = await _transport!.SendAsync(encoded, cancellationToken).ConfigureAwait(false);
					if (sent != encoded.Length)
						throw new IOException($"SRT transport accepted {sent} of {encoded.Length} encoded bytes.");

					lock (_gate)
					{
						_sent++;
						_packetsSent++;
						_bytesSent += checked((ulong)sent);
						_lastSuccessfulSendUtc = DateTimeOffset.UtcNow;
						_lifecycle = NetworkOutputLifecycleState.Connected;
						_connected = true;
						_failure = null;
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					sample.Video.Dispose();
					break;
				}
				catch (Exception exception)
				{
					DropSample(
						sample,
						"network.output.send_failed",
						$"Network output sample failed without interrupting Program: {exception.GetType().Name}.");
					await ResetConnectionAsync(
						new Failure("network.output.transport_failed", $"SRT transport failed: {exception.GetType().Name}."))
						.ConfigureAwait(false);
				}
				finally
				{
					sample?.Video.Dispose();
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		finally
		{
			await ReleaseConnectionAsync().ConfigureAwait(false);
			lock (_gate)
			{
				while (_queue.Count > 0)
					_queue.Dequeue().Video.Dispose();
				_connected = false;
				if (_lifecycle != NetworkOutputLifecycleState.Faulted)
					_lifecycle = NetworkOutputLifecycleState.Disabled;
			}
		}
	}

	private async ValueTask<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
	{
		if (_transport is { IsConnected: true } && _encoder is not null)
			return true;

		var attempt = 0;
		var delay = _configuration.ReconnectInitialDelayMilliseconds;
		while (!cancellationToken.IsCancellationRequested)
		{
			attempt++;
			if (_configuration.ReconnectMaximumAttempts > 0 && attempt > _configuration.ReconnectMaximumAttempts)
			{
				lock (_gate)
				{
					_connected = false;
					_lifecycle = NetworkOutputLifecycleState.Faulted;
					_failure = new Failure("network.output.reconnect_exhausted", "Configured SRT reconnect attempts were exhausted.");
				}
				return false;
			}

			try
			{
				await ReleaseConnectionAsync().ConfigureAwait(false);
				var transport = _transportFactory();
				var encoder = _encoderFactory();
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
				_transport = transport;
				_encoder = encoder;
				lock (_gate)
				{
					if (attempt > 1 || _reconnects > 0)
						_reconnects++;
					_lifecycle = NetworkOutputLifecycleState.Connected;
					_connected = true;
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
				await ReleaseConnectionAsync().ConfigureAwait(false);
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				delay = Math.Min(checked(delay * 2), _configuration.ReconnectMaximumDelayMilliseconds);
			}
		}
		return false;
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
		await ReleaseConnectionAsync().ConfigureAwait(false);
	}

	private async ValueTask ReleaseConnectionAsync()
	{
		var encoder = Interlocked.Exchange(ref _encoder, null);
		var transport = Interlocked.Exchange(ref _transport, null);
		if (encoder is not null)
		{
			try { _ = await encoder.DrainAsync(CancellationToken.None).ConfigureAwait(false); }
			catch { }
			await encoder.DisposeAsync().ConfigureAwait(false);
		}
		if (transport is not null)
			await transport.DisposeAsync().ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_lifecycle = NetworkOutputLifecycleState.Stopping;
		}
		_shutdown.Cancel();
		_queueSignal.Release();
		try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
		_queueSignal.Dispose();
		_shutdown.Dispose();
	}
}
