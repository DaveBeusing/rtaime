// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Pipes;
using rtaime.Media.Contracts;

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

	public bool HasSubscribers
	{
		get
		{
			lock (_gate)
				return _subscriptions.Count != 0;
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

	public RuntimeMonitoringSubscription Subscribe(int capacity = 2)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var subscription = new RuntimeMonitoringSubscription(this, capacity);
			_subscriptions.Add(subscription);
			return subscription;
		}
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
		lock (_gate)
			_subscriptions.Remove(subscription);
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

	internal RuntimeMonitoringSubscription(RuntimeMonitoringHub hub, int capacity)
	{
		_hub = hub;
		_capacity = capacity;
	}

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
	ulong Processed);

public sealed class RuntimeMonitoringTap : IAsyncDisposable
{
	public const uint MonitorWidth = 320;
	public const uint MonitorHeight = 180;
	public const ulong SampleStride = 4;

	private readonly object _gate = new();
	private readonly RuntimeMonitoringHub _hub;
	private readonly SemaphoreSlim _available = new(0);
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _worker;
	private MonitoringBoundarySample? _pending;
	private ulong _captured;
	private ulong _dropped;
	private ulong _processed;
	private bool _disposed;

	public RuntimeMonitoringTap(RuntimeMonitoringHub hub)
	{
		_hub = hub ?? throw new ArgumentNullException(nameof(hub));
		_worker = Task.Run(() => RunAsync(_stop.Token));
	}

	public RuntimeMonitoringTapStatistics Statistics
	{
		get { lock (_gate) return new RuntimeMonitoringTapStatistics(_captured, _dropped, _processed); }
	}

	public bool TryCapture(
		MediaSourceId sourceAId,
		ReadOnlyMemory<byte> sourceA,
		MediaSourceId sourceBId,
		ReadOnlyMemory<byte> sourceB,
		MediaSourceId committedProgramSourceId,
		ReadOnlyMemory<byte> program,
		VideoFormat sourceFormat,
		FrameTiming timing)
	{
		if (!_hub.HasSubscribers || timing.SequenceNumber % SampleStride != 0) return false;
		var sample = new MonitoringBoundarySample(
			sourceAId,
			sourceA,
			sourceBId,
			sourceB,
			committedProgramSourceId,
			program,
			sourceFormat,
			timing);
		var signal = false;
		lock (_gate)
		{
			if (_disposed) return false;
			_captured++;
			if (_pending is not null)
				_dropped++;
			else
				signal = true;
			_pending = sample;
		}
		if (signal) _available.Release();
		return true;
	}

	public async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_pending = null;
		}
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
			if (sample is null || !_hub.HasSubscribers) continue;

			Publish(sample.SourceAId, MonitoringStreamKind.Source, sample.SourceA.Span, sample.Format, sample.Timing);
			Publish(sample.SourceBId, MonitoringStreamKind.Source, sample.SourceB.Span, sample.Format, sample.Timing);
			Publish(sample.ProgramSourceId, MonitoringStreamKind.Program, sample.Program.Span, sample.Format, sample.Timing);
			lock (_gate) _processed++;
		}
	}

	private void Publish(
		MediaSourceId sourceId,
		MonitoringStreamKind kind,
		ReadOnlySpan<byte> pixels,
		VideoFormat format,
		FrameTiming timing)
	{
		var downscaled = DownscaleRgbaNearest(pixels, format.Width, format.Height, MonitorWidth, MonitorHeight);
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			kind,
			sourceId,
			MonitorWidth,
			MonitorHeight,
			PixelFormat.Rgba8,
			timing);
		_hub.Publish(new MonitoringFrame(descriptor, downscaled));
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
		ReadOnlyMemory<byte> SourceA,
		MediaSourceId SourceBId,
		ReadOnlyMemory<byte> SourceB,
		MediaSourceId ProgramSourceId,
		ReadOnlyMemory<byte> Program,
		VideoFormat Format,
		FrameTiming Timing);
}

public sealed class RuntimeHostMonitoringServer : IAsyncDisposable
{
	private readonly string _endpoint;
	private readonly RuntimeMonitoringHub _hub;
	private readonly CancellationTokenSource _stop = new();
	private Task? _acceptLoop;

	public RuntimeHostMonitoringServer(string endpoint, RuntimeMonitoringHub hub)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Monitoring endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_hub = hub ?? throw new ArgumentNullException(nameof(hub));
	}

	public string Endpoint => _endpoint;
	public bool Running => _acceptLoop is { IsCompleted: false };

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_acceptLoop is not null) throw new InvalidOperationException("Monitoring server has already been started.");
		var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
		_acceptLoop = AcceptLoopAsync(linked.Token);
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync()
	{
		_stop.Cancel();
		if (_acceptLoop is not null)
		{
			try { await _acceptLoop.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_stop.Dispose();
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			var pipe = new NamedPipeServerStream(
				_endpoint,
				PipeDirection.Out,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			try
			{
				await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
				_ = StreamAsync(pipe, cancellationToken);
			}
			catch
			{
				pipe.Dispose();
				if (!cancellationToken.IsCancellationRequested) throw;
			}
		}
	}

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
