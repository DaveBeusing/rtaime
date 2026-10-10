// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Ndi;

public readonly record struct NdiSendResult(ulong MediaFramesSubmitted, ulong MediaBytesSubmitted);

public interface INdiSender : IAsyncDisposable
{
	bool IsReady { get; }
	ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken);
}

public sealed class NdiNetworkOutputProvider
{
	internal static readonly ProviderId ProviderIdentity =
		new(new Identity(new Guid("cf78d139-f89e-43dc-b44c-0fb48874c2ad")));
	private static readonly CapabilityId OutputCapabilityIdentity =
		new(new Identity(new Guid("55e56775-2d57-4ab5-8c78-86395ca2d711")));
	private static readonly CapabilityId DiscoveryCapabilityIdentity =
		new(new Identity(new Guid("2d8995f1-62e5-4a08-9250-0438679f81cd")));
	private static readonly CapabilityId InputCapabilityIdentity =
		new(new Identity(new Guid("696b82d4-b118-44a2-82f6-d60f09770619")));
	private static readonly ProviderResourceId OutputResourceIdentity =
		new(new Identity(new Guid("21b8b9e2-89b3-43fb-bdc9-c94183982a9d")));
	private static readonly ProviderResourceId DiscoveryResourceIdentity =
		new(new Identity(new Guid("1c133a24-b999-42b6-ac58-42ac51b8058c")));
	private static readonly ProviderResourceId InputResourceIdentity =
		new(new Identity(new Guid("9189a29a-0394-4aca-9ec8-335a9e2dc8d8")));

	private readonly string? _runtimeLibraryPath;

	public static IReadOnlyList<VideoFormat> SupportedFormats { get; } =
		Array.AsReadOnly(new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 });

	public NdiNetworkOutputProvider(string? nativeLibraryPath = null)
	{
		_runtimeLibraryPath = NdiRuntimeDiscovery.ResolveLibraryPath(nativeLibraryPath);
		var availability = _runtimeLibraryPath is null
			? new ProviderAvailability(
				ProviderAvailabilityState.Unavailable,
				new Failure(
					"network.output.ndi_runtime_unavailable",
					"NDI runtime is unavailable. Install a compatible NDI runtime or configure RTAIME_NDI_LIBRARY_PATH."))
			: new ProviderAvailability(ProviderAvailabilityState.Available);

		Descriptor = new ProviderDescriptor(
			ProviderContractVersion.Current,
			ProviderIdentity,
			"NDI Network Output",
			availability,
			new[]
			{
				new ProviderCapabilityDescriptor(
					OutputCapabilityIdentity,
					NetworkOutputCapabilityKinds.Output,
					SupportedFormats),
				new ProviderCapabilityDescriptor(
					DiscoveryCapabilityIdentity,
					MediaSourceCapabilityKinds.Discovery,
					SupportedFormats),
				new ProviderCapabilityDescriptor(
					InputCapabilityIdentity,
					MediaSourceCapabilityKinds.Input,
					SupportedFormats)
			},
			new[]
			{
				new ProviderResourceDescriptor(
					OutputResourceIdentity,
					ProviderIdentity,
					NetworkOutputCapabilityKinds.Output,
					2,
					true),
				new ProviderResourceDescriptor(
					DiscoveryResourceIdentity,
					ProviderIdentity,
					MediaSourceCapabilityKinds.Discovery,
					1,
					false),
				new ProviderResourceDescriptor(
					InputResourceIdentity,
					ProviderIdentity,
					MediaSourceCapabilityKinds.Input,
					8,
					true)
			});
	}

	public ProviderDescriptor Descriptor { get; }

	public NdiDiscoveryService CreateDiscoveryService(
		INdiDiscoveryBackend? backend = null,
		int maximumRetainedResults = NdiDiscoveryService.DefaultMaximumRetainedResults,
		TimeSpan? expiry = null) =>
		new(
			backend ?? new NativeNdiDiscoveryBackend(_runtimeLibraryPath),
			maximumRetainedResults,
			expiry);

	public NdiInputSession CreateInputSession(
		NdiInputConfiguration configuration,
		Func<INdiReceiveBackend>? backendFactory = null,
		Func<DateTimeOffset>? clock = null)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		backendFactory ??= () => new NativeNdiReceiveBackend(
			configuration.Endpoint,
			$"rtaime {configuration.SourceId}",
			_runtimeLibraryPath);
		return new NdiInputSession(configuration, backendFactory, clock);
	}

	public NdiNetworkOutputSession CreateSession(
		NetworkOutputConfiguration configuration,
		Func<NdiNetworkOutputSettings, INdiSender>? senderFactory = null)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		if (configuration.Protocol != NetworkOutputProtocolFamily.Ndi || configuration.NdiSettings is null)
			throw new ArgumentException("NDI provider requires typed NDI network-output settings.", nameof(configuration));

		senderFactory ??= settings =>
		{
			var libraryPath = _runtimeLibraryPath
				?? throw new DllNotFoundException("NDI runtime is unavailable.");
			return new NativeNdiSender(configuration, libraryPath);
		};

		return new NdiNetworkOutputSession(configuration, senderFactory);
	}
}

public sealed class NdiNetworkOutputSession : INetworkOutputSession
{
	private readonly object _gate = new();
	private readonly NetworkOutputConfiguration _configuration;
	private readonly Func<NdiNetworkOutputSettings, INdiSender> _senderFactory;
	private readonly Queue<NetworkOutputProgramSample> _queue = new();
	private readonly SemaphoreSlim _queueSignal = new(0);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Task _worker;

	private INdiSender? _sender;
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

	public NdiNetworkOutputSession(
		NetworkOutputConfiguration configuration,
		Func<NdiNetworkOutputSettings, INdiSender> senderFactory)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		if (_configuration.Protocol != NetworkOutputProtocolFamily.Ndi || _configuration.NdiSettings is null)
			throw new ArgumentException("NDI session requires typed NDI network-output settings.", nameof(configuration));
		if (!NdiNetworkOutputProvider.SupportedFormats.Contains(_configuration.VideoFormat))
			throw new NotSupportedException("NDI output is currently qualified only for the 1080p50 and 1080p59.94 RGBA8 Runtime formats.");
		_senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
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
					"ndi",
					NetworkOutputProtocolFamily.Ndi,
					_configuration.SafeTargetIdentity,
					_lifecycle,
					_connected,
					_configuration.VideoFormat,
					_configuration.AudioFormat,
					NetworkOutputVideoCodec.NdiHighBandwidth,
					NetworkOutputAudioCodec.Float32,
					0,
					0,
					0,
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
				NetworkOutputProgramSample? sample;
				lock (_gate)
					sample = _queue.Count == 0 ? null : _queue.Dequeue();
				if (sample is null)
					continue;

				try
				{
					if (!await EnsureSenderAsync(cancellationToken).ConfigureAwait(false))
					{
						lock (_gate) _dropped++;
						continue;
					}

					var result = await _sender!.SendAsync(sample, cancellationToken).ConfigureAwait(false);
					lock (_gate)
					{
						_sent++;
						_packetsSent += result.MediaFramesSubmitted;
						_bytesSent += result.MediaBytesSubmitted;
						_lastSuccessfulSendUtc = DateTimeOffset.UtcNow;
						_lifecycle = NetworkOutputLifecycleState.Connected;
						_connected = true;
						_failure = null;
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception exception)
				{
					lock (_gate)
					{
						_dropped++;
						_failure = new Failure(
							"network.output.send_failed",
							$"NDI output sample failed without interrupting Program: {exception.GetType().Name}.");
					}
					await ResetSenderAsync(
						new Failure("network.output.transport_failed", $"NDI sender failed: {exception.GetType().Name}."))
						.ConfigureAwait(false);
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
		finally
		{
			await ReleaseSenderAsync().ConfigureAwait(false);
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

	private async ValueTask<bool> EnsureSenderAsync(CancellationToken cancellationToken)
	{
		if (_sender is { IsReady: true })
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
					_failure = new Failure("network.output.reconnect_exhausted", "Configured NDI sender restart attempts were exhausted.");
				}
				return false;
			}

			try
			{
				await ReleaseSenderAsync().ConfigureAwait(false);
				lock (_gate)
				{
					_lifecycle = attempt == 1 && _reconnects == 0
						? NetworkOutputLifecycleState.Connecting
						: NetworkOutputLifecycleState.Reconnecting;
					_connected = false;
				}

				var sender = _senderFactory(_configuration.NdiSettings!);
				if (!sender.IsReady)
					throw new InvalidOperationException("NDI sender did not enter a ready state.");
				_sender = sender;

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
			catch (Exception exception) when (
				exception is DllNotFoundException or
				EntryPointNotFoundException or
				BadImageFormatException or
				PlatformNotSupportedException)
			{
				lock (_gate)
				{
					_connected = false;
					_lifecycle = NetworkOutputLifecycleState.Faulted;
					_failure = new Failure(
						"network.output.ndi_runtime_unavailable",
						$"NDI runtime is unavailable or incompatible: {exception.GetType().Name}.");
				}
				return false;
			}
			catch (Exception exception)
			{
				lock (_gate)
				{
					_connected = false;
					_lifecycle = NetworkOutputLifecycleState.Reconnecting;
					_failure = new Failure(
						"network.output.connect_failed",
						$"NDI sender initialization failed: {exception.GetType().Name}.");
				}
				await ReleaseSenderAsync().ConfigureAwait(false);
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				delay = Math.Min(checked(delay * 2), _configuration.ReconnectMaximumDelayMilliseconds);
			}
		}
		return false;
	}

	private async ValueTask ResetSenderAsync(Failure failure)
	{
		lock (_gate)
		{
			_connected = false;
			_lifecycle = NetworkOutputLifecycleState.Reconnecting;
			_failure = failure;
			_reconnects++;
		}
		await ReleaseSenderAsync().ConfigureAwait(false);
	}

	private async ValueTask ReleaseSenderAsync()
	{
		var sender = Interlocked.Exchange(ref _sender, null);
		if (sender is not null)
			await sender.DisposeAsync().ConfigureAwait(false);
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
