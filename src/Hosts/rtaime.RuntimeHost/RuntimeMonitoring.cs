// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Pipes;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

public readonly record struct RuntimeMonitoringHubStatistics(
	ulong Published,
	ulong DroppedBySubscribers,
	int Subscribers);

public sealed class RuntimeMonitoringHub : IDisposable
{
	private readonly object _gate = new();
	private readonly HashSet<RuntimeMonitoringSubscription> _subscriptions = new();
	private ulong _published;
	private bool _disposed;

	public event Action<bool>? SubscriberAvailabilityChanged;

	public bool HasSubscribers
	{
		get
		{
			lock (_gate)
				return _subscriptions.Count != 0;
		}
	}

	public bool RequiresCpuFallback
	{
		get
		{
			lock (_gate)
				return _subscriptions.Any(subscription => subscription.RequiresCpuFallback);
		}
	}

	public RuntimeMonitoringHubStatistics Statistics
	{
		get
		{
			lock (_gate)
				return new RuntimeMonitoringHubStatistics(
					_published,
					_subscriptions.Aggregate(0UL, (value, subscription) => value + subscription.DroppedFrames),
					_subscriptions.Count);
		}
	}

	public RuntimeMonitoringSubscription Subscribe(int capacity = 2, bool requiresCpuFallback = true)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		RuntimeMonitoringSubscription subscription;
		var becameAvailable = false;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			becameAvailable = _subscriptions.Count == 0;
			subscription = new RuntimeMonitoringSubscription(this, capacity, requiresCpuFallback);
			_subscriptions.Add(subscription);
		}
		if (becameAvailable)
			SubscriberAvailabilityChanged?.Invoke(true);
		return subscription;
	}

	public void Publish(MonitoringFrame frame)
	{
		ArgumentNullException.ThrowIfNull(frame);
		RuntimeMonitoringSubscription[] subscriptions;
		lock (_gate)
		{
			if (_disposed) return;
			_published++;
			subscriptions = _subscriptions.ToArray();
		}

		foreach (var subscription in subscriptions)
			subscription.Offer(frame);
	}

	internal void Remove(RuntimeMonitoringSubscription subscription)
	{
		var becameUnavailable = false;
		lock (_gate)
		{
			if (_subscriptions.Remove(subscription))
				becameUnavailable = _subscriptions.Count == 0;
		}
		if (becameUnavailable)
			SubscriberAvailabilityChanged?.Invoke(false);
	}

	public void Dispose()
	{
		RuntimeMonitoringSubscription[] subscriptions;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			subscriptions = _subscriptions.ToArray();
			_subscriptions.Clear();
		}
		foreach (var subscription in subscriptions)
			subscription.DisposeFromHub();
		if (subscriptions.Length != 0)
			SubscriberAvailabilityChanged?.Invoke(false);
	}
}

public sealed class RuntimeMonitoringSubscription : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly RuntimeMonitoringHub _hub;
	private readonly Queue<MonitoringFrame> _frames = new();
	private readonly SemaphoreSlim _available = new(0);
	private readonly CancellationTokenSource _stop = new();
	private readonly int _capacity;
	private ulong _dropped;
	private bool _disposed;

	internal RuntimeMonitoringSubscription(RuntimeMonitoringHub hub, int capacity, bool requiresCpuFallback)
	{
		_hub = hub;
		_capacity = capacity;
		RequiresCpuFallback = requiresCpuFallback;
	}

	public bool RequiresCpuFallback { get; }

	public ulong DroppedFrames
	{
		get { lock (_gate) return _dropped; }
	}

	internal void Offer(MonitoringFrame frame)
	{
		var signal = false;
		lock (_gate)
		{
			if (_disposed) return;
			if (_frames.Count == _capacity)
			{
				_frames.Dequeue();
				_dropped++;
			}
			else
			{
				signal = true;
			}
			_frames.Enqueue(frame);
		}
		if (signal) _available.Release();
	}

	public async ValueTask<MonitoringFrame> ReadAsync(CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
		while (true)
		{
			await _available.WaitAsync(linked.Token).ConfigureAwait(false);
			lock (_gate)
			{
				if (_frames.Count != 0)
					return _frames.Dequeue();
				ObjectDisposedException.ThrowIf(_disposed, this);
			}
		}
	}

	public ValueTask DisposeAsync()
	{
		DisposeCore(removeFromHub: true);
		return ValueTask.CompletedTask;
	}

	internal void DisposeFromHub() => DisposeCore(removeFromHub: false);

	private void DisposeCore(bool removeFromHub)
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_frames.Clear();
		}
		_stop.Cancel();
		if (removeFromHub) _hub.Remove(this);
		_available.Dispose();
		_stop.Dispose();
	}
}

public readonly record struct RuntimeMonitoringTapStatistics(
	ulong Captured,
	ulong DroppedBeforeProcessing,
	ulong Processed,
	int ActiveSharedResources);

internal sealed record RuntimeMonitoringSourceSnapshot(
	MediaSourceId SourceAId,
	byte[] SourceA,
	MediaSourceId SourceBId,
	byte[] SourceB,
	FrameTiming Timing);

public sealed class RuntimeMonitoringTap : IAsyncDisposable
{
	public const uint MonitorWidth = 320;
	public const uint MonitorHeight = 180;
	public const ulong SampleStride = 4;

	private readonly object _gate = new();
	private readonly RuntimeMonitoringHub _hub;
	private readonly MonitoringSharedResourceCapabilityState _sharedResourceCapability;
	private readonly SemaphoreSlim _available = new(0);
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _worker;
	private MonitoringBoundarySample? _pending;
	private GpuSharedMonitoringResourceLease? _publishedSharedResource;
	private ulong _captured;
	private ulong _dropped;
	private ulong _processed;
	private bool _disposed;

	public RuntimeMonitoringTap(
		RuntimeMonitoringHub hub,
		MonitoringSharedResourceCapabilityState sharedResourceCapability = MonitoringSharedResourceCapabilityState.Unavailable)
	{
		_hub = hub ?? throw new ArgumentNullException(nameof(hub));
		if (!Enum.IsDefined(typeof(MonitoringSharedResourceCapabilityState), sharedResourceCapability))
			throw new ArgumentOutOfRangeException(nameof(sharedResourceCapability));
		_sharedResourceCapability = sharedResourceCapability;
		_hub.SubscriberAvailabilityChanged += SubscriberAvailabilityChanged;
		_worker = Task.Run(() => RunAsync(_stop.Token));
	}

	public RuntimeMonitoringTapStatistics Statistics
	{
		get
		{
			lock (_gate)
				return new RuntimeMonitoringTapStatistics(
					_captured,
					_dropped,
					_processed,
					_publishedSharedResource is null ? 0 : 1);
		}
	}

	internal RuntimeMonitoringSourceSnapshot? CaptureSources(
		MediaSourceId sourceAId,
		ReadOnlyMemory<byte> sourceA,
		MediaSourceId sourceBId,
		ReadOnlyMemory<byte> sourceB,
		VideoFormat sourceFormat,
		FrameTiming timing)
	{
		if (!_hub.HasSubscribers || timing.SequenceNumber % SampleStride != 0)
			return null;

		return new RuntimeMonitoringSourceSnapshot(
			sourceAId,
			DownscaleRgbaNearest(sourceA.Span, sourceFormat.Width, sourceFormat.Height, MonitorWidth, MonitorHeight),
			sourceBId,
			DownscaleRgbaNearest(sourceB.Span, sourceFormat.Width, sourceFormat.Height, MonitorWidth, MonitorHeight),
			timing);
	}

	public bool TryCapture(
		MediaSourceId sourceAId,
		ReadOnlyMemory<byte> sourceA,
		MediaSourceId sourceBId,
		ReadOnlyMemory<byte> sourceB,
		MediaSourceId committedProgramSourceId,
		GpuReadbackLease program,
		VideoFormat sourceFormat,
		FrameTiming timing,
		GpuSharedMonitoringResourceLease? sharedResource = null)
	{
		var sources = CaptureSources(sourceAId, sourceA, sourceBId, sourceB, sourceFormat, timing);
		return TryCapture(sources, committedProgramSourceId, program, sourceFormat, timing, sharedResource);
	}

	internal bool TryCapture(
		RuntimeMonitoringSourceSnapshot? sources,
		MediaSourceId committedProgramSourceId,
		GpuReadbackLease program,
		VideoFormat sourceFormat,
		FrameTiming timing,
		GpuSharedMonitoringResourceLease? sharedResource = null)
	{
		if (sources is null)
		{
			sharedResource?.Dispose();
			return false;
		}
		var retainedProgram = program.Retain();
		MonitoringBoundarySample? replaced = null;
		var sample = new MonitoringBoundarySample(
			sources.SourceAId,
			sources.SourceA,
			sources.SourceBId,
			sources.SourceB,
			committedProgramSourceId,
			retainedProgram,
			sourceFormat,
			timing,
			sharedResource);
		var signal = false;
		lock (_gate)
		{
			if (_disposed)
			{
				retainedProgram.Dispose();
				sharedResource?.Dispose();
				return false;
			}
			_captured++;
			if (_pending is not null)
			{
				_dropped++;
				replaced = _pending;
			}
			else
			{
				signal = true;
			}
			_pending = sample;
		}
		replaced?.Program.Dispose();
		replaced?.SharedResource?.Dispose();
		if (signal) _available.Release();
		return true;
	}

	public async ValueTask DisposeAsync()
	{
		MonitoringBoundarySample? pending;
		GpuSharedMonitoringResourceLease? published;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			pending = _pending;
			_pending = null;
			published = _publishedSharedResource;
			_publishedSharedResource = null;
		}
		_hub.SubscriberAvailabilityChanged -= SubscriberAvailabilityChanged;
		pending?.Program.Dispose();
		pending?.SharedResource?.Dispose();
		published?.Dispose();
		_stop.Cancel();
		try { await _worker.ConfigureAwait(false); }
		catch (OperationCanceledException) { }
		_available.Dispose();
		_stop.Dispose();
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
			MonitoringBoundarySample? sample;
			lock (_gate)
			{
				sample = _pending;
				_pending = null;
			}
			if (sample is null) continue;

			using (sample.Program)
			{
				if (!_hub.HasSubscribers)
				{
					sample.SharedResource?.Dispose();
					continue;
				}

				PublishPrepared(sample.SourceAId, MonitoringStreamKind.Source, sample.SourceA, sample.Timing, sample.Format.Color);
				PublishPrepared(sample.SourceBId, MonitoringStreamKind.Source, sample.SourceB, sample.Timing, sample.Format.Color);
				Publish(
					sample.ProgramSourceId,
					MonitoringStreamKind.Program,
					sample.Program.Memory.Span,
					sample.Format,
					sample.Timing,
					sample.SharedResource?.Descriptor);

				GpuSharedMonitoringResourceLease? replacedResource;
				var retainPublishedResource = false;
				lock (_gate)
				{
					replacedResource = _publishedSharedResource;
					if (!_disposed && _hub.HasSubscribers)
					{
						_publishedSharedResource = sample.SharedResource;
						retainPublishedResource = true;
					}
					else
					{
						_publishedSharedResource = null;
					}
					_processed++;
				}

				replacedResource?.Dispose();
				if (!retainPublishedResource)
					sample.SharedResource?.Dispose();
			}
		}
	}

	private void PublishPrepared(
		MediaSourceId sourceId,
		MonitoringStreamKind kind,
		byte[] pixels,
		FrameTiming timing,
		ColorDescription color)
	{
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			kind,
			sourceId,
			MonitorWidth,
			MonitorHeight,
			PixelFormat.Rgba8,
			timing,
			color);
		_hub.Publish(new MonitoringFrame(descriptor, pixels));
	}

	private void Publish(
		MediaSourceId sourceId,
		MonitoringStreamKind kind,
		ReadOnlySpan<byte> pixels,
		VideoFormat format,
		FrameTiming timing,
		MonitoringSharedResourceDescriptor? sharedResource)
	{
		var requiresCpuFallback = _hub.RequiresCpuFallback || sharedResource is null;
		var payload = requiresCpuFallback
			? DownscaleRgbaNearest(pixels, format.Width, format.Height, MonitorWidth, MonitorHeight)
			: Array.Empty<byte>();
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			kind,
			sourceId,
			MonitorWidth,
			MonitorHeight,
			PixelFormat.Rgba8,
			timing,
			format.Color,
			_sharedResourceCapability,
			sharedResource);
		_hub.Publish(new MonitoringFrame(descriptor, payload));
	}

	private void SubscriberAvailabilityChanged(bool available)
	{
		if (available) return;

		MonitoringBoundarySample? pending;
		GpuSharedMonitoringResourceLease? published;
		lock (_gate)
		{
			if (_disposed) return;
			pending = _pending;
			_pending = null;
			published = _publishedSharedResource;
			_publishedSharedResource = null;
		}

		pending?.Program.Dispose();
		pending?.SharedResource?.Dispose();
		published?.Dispose();
	}

	internal static byte[] DownscaleRgbaNearest(
		ReadOnlySpan<byte> source,
		uint sourceWidth,
		uint sourceHeight,
		uint targetWidth,
		uint targetHeight)
	{
		var expected = checked((int)((ulong)sourceWidth * sourceHeight * 4UL));
		if (source.Length != expected) throw new ArgumentException("Source RGBA payload length is invalid.", nameof(source));
		if (targetWidth == 0 || targetHeight == 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
		var output = new byte[checked((int)((ulong)targetWidth * targetHeight * 4UL))];
		for (uint y = 0; y < targetHeight; y++)
		{
			var sourceY = (uint)((ulong)y * sourceHeight / targetHeight);
			for (uint x = 0; x < targetWidth; x++)
			{
				var sourceX = (uint)((ulong)x * sourceWidth / targetWidth);
				var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4UL));
				var targetOffset = checked((int)(((ulong)y * targetWidth + x) * 4UL));
				source.Slice(sourceOffset, 4).CopyTo(output.AsSpan(targetOffset, 4));
			}
		}
		return output;
	}

	private sealed record MonitoringBoundarySample(
		MediaSourceId SourceAId,
		byte[] SourceA,
		MediaSourceId SourceBId,
		byte[] SourceB,
		MediaSourceId ProgramSourceId,
		GpuReadbackLease Program,
		VideoFormat Format,
		FrameTiming Timing,
		GpuSharedMonitoringResourceLease? SharedResource);
}

public sealed class RuntimeHostMonitoringServer : IAsyncDisposable
{
	private readonly string _endpoint;
	private readonly RuntimeMonitoringHub _hub;
	private readonly RuntimePipeListener _listener;

	public RuntimeHostMonitoringServer(string endpoint, RuntimeMonitoringHub hub)
		: this(endpoint, hub, null, null)
	{
	}

	internal RuntimeHostMonitoringServer(
		string endpoint,
		RuntimeMonitoringHub hub,
		Func<NamedPipeServerStream>? pipeFactory,
		TimeSpan? sessionDrainTimeout = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Monitoring endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_hub = hub ?? throw new ArgumentNullException(nameof(hub));
		_listener = new RuntimePipeListener(
			_endpoint,
			"monitoring-output",
			pipeFactory ?? (() => OperatorPipeServerFactory.Create(_endpoint, PipeDirection.Out)),
			StreamAsync,
			sessionDrainTimeout);
	}

	public string Endpoint => _endpoint;
	public bool Running => _listener.Running;
	public RuntimePipeListenerSnapshot Listener => _listener.Snapshot;
	public HostIpcSessionSnapshot Sessions => _listener.Sessions;
	internal Task ListenerCompletion => _listener.Completion;
	internal event Action<RuntimePipeListenerSnapshot, Exception>? ListenerFaulted
	{
		add => _listener.TerminalFaulted += value;
		remove => _listener.TerminalFaulted -= value;
	}

	public Task StartAsync(CancellationToken cancellationToken = default) =>
		_listener.StartAsync(cancellationToken);

	public ValueTask DisposeAsync() => _listener.DisposeAsync();

	private async Task StreamAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
	{
		await using (pipe.ConfigureAwait(false))
		await using (var subscription = _hub.Subscribe(capacity: 2))
		{
			try
			{
				var header = new byte[MonitoringFrameWire.HeaderSize];
				while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
				{
					var frame = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
					MonitoringFrameWire.WriteHeader(header, frame.Descriptor, frame.Pixels.Length);
					await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
					await pipe.WriteAsync(frame.Pixels, cancellationToken).ConfigureAwait(false);
					await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
			catch (IOException) { }
		}
	}
}
