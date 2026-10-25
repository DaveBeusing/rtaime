// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Srt;
using Xunit;
using Xunit.Abstractions;

namespace rtaime.Tests.Unit;

public sealed class SrtNativeLoopbackQualificationTests(ITestOutputHelper output)
{
	[Fact]
	public async Task Native_live_transport_and_media_encoder_pass_when_explicitly_enabled()
	{
		if (Environment.GetEnvironmentVariable("RTAIME_SRT_NATIVE_QUALIFICATION") != "1")
		{
			output.WriteLine("Native SRT/MF loopback UNVERIFIED: explicit qualification profile is not enabled.");
			return;
		}
		Assert.True(OperatingSystem.IsWindows(), "Native H.264/AAC qualification requires Windows Media Foundation.");
		var library = Path.GetFullPath(RequireEnvironment("RTAIME_SRT_NATIVE_LIBRARY"));
		var evidenceRoot = Path.GetFullPath(RequireEnvironment("RTAIME_SRT_NATIVE_EVIDENCE"));
		var source = RequireEnvironment("RTAIME_SRT_NATIVE_SOURCE");
		var embeddedSource = typeof(NativeSrtTransport).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Single(attribute => attribute.Key == "RtaimeSourceRevision").Value;
		Assert.Equal(source, embeddedSource);
		Assert.True(File.Exists(library));
		var nativeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library)));
		Directory.CreateDirectory(evidenceRoot);
		foreach (var use5994 in new[] { false, true })
			await QualifyAsync(use5994, library, evidenceRoot, source, nativeHash);
	}

	private async Task QualifyAsync(bool use5994, string library, string evidenceRoot, string source, string nativeHash)
	{
		var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
		var formatName = use5994 ? "1080p59.94" : "1080p50";
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		await using var receiver = new LoopbackReceiver(library);
		var encoder = new MeasuredEncoder(receiver.ExpectCompletion);
		var configuration = new NetworkOutputConfiguration("native-loopback", new Uri($"srt://127.0.0.1:{receiver.Port}"),
			NetworkOutputProtocolFamily.Srt, NetworkOutputConnectionMode.Caller, format, AudioFormat.Stereo48kFloat32,
			NetworkOutputVideoCodec.H264, NetworkOutputAudioCodec.AacLc, 12_000_000, 192_000, 120,
			queueCapacity: 2, reconnectInitialDelayMilliseconds: 50, reconnectMaximumDelayMilliseconds: 1000,
			reconnectMaximumAttempts: 1);
		await using var session = new SrtNetworkOutputSession(configuration, () => new NativeSrtTransport(library), () => encoder);
		var pixels = new byte[1920 * 1080 * 4];
		new Random(579).NextBytes(pixels);
		for (var pixel = 3; pixel < pixels.Length; pixel += 4) pixels[pixel] = 255;
		const int frames = 90;
		for (ulong sequence = 0; sequence < frames; sequence++)
		{
			var units = (Int128)48000 * format.FrameRate.Denominator;
			var start = checked((ulong)((Int128)sequence * units / format.FrameRate.Numerator));
			var end = checked((ulong)(((Int128)sequence + 1) * units / format.FrameRate.Numerator));
			var count = checked((uint)(end - start));
			var pcm = new float[checked((int)count * 2)];
			for (var frame = 0; frame < count; frame++)
			{
				var value = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * (start + (ulong)frame) / 48000));
				pcm[frame * 2] = pcm[frame * 2 + 1] = value;
			}
			var lease = new SampleLease(pixels);
			var sample = new NetworkOutputProgramSample(sequence,
				new FrameTiming(sequence, checked((long)sequence), new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator)),
				new AudioBufferTiming(start, count, checked((long)start), new Timebase(1, 48000)),
				lease, MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray());
			Assert.True(session.TrySubmit(sample).Accepted);
			await lease.Returned.Task.WaitAsync(timeout.Token);
			var current = session.Snapshot;
			if (current.Statistics.DroppedSamples != 0)
			{
				// These bytes come exclusively from the synthetic qualification fixture, never live Program input.
				var diagnostic = JsonSerializer.Serialize(new
				{
					schemaVersion = "1.0", status = "FAIL", sourceCommit = source, capturedAtUtc = DateTimeOffset.UtcNow,
					format = formatName, sequence, runtimeVersion = $"0x{receiver.RuntimeVersion:X6}", nativeLibrarySha256 = nativeHash,
					failure = current.Failure?.ToString(), encoderFailure = encoder.LastFailure?.ToString(),
					encoder.EncodedBytes, encoder.MaximumDrain, encoder.EncodeCalls,
					firstNonEmptyDrain = encoder.FirstNonEmptyDrain, latestNonEmptyDrain = encoder.LatestNonEmptyDrain,
					current.Statistics
				}, new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(Path.Combine(evidenceRoot, $"srt-native-{formatName}-failure.json"), diagnostic);
				output.WriteLine(diagnostic);
			}
			Assert.True(current.Statistics.DroppedSamples == 0,
				$"Native output failed: {current.Failure}; encoder: {encoder.LastFailure}");
		}
		Assert.True(encoder.MaximumDrain > 1316, "Native encoder never produced a drain larger than one SRT payload.");
		Assert.True(encoder.EncodedBytes > 1316);
		Assert.Equal(0L, encoder.EncodedBytes % 188);
		var statistics = session.Snapshot.Statistics;
		Assert.Equal((ulong)encoder.EncodedBytes, statistics.BytesSent);
		await receiver.WaitForBytesAsync(encoder.EncodedBytes, timeout.Token);
		Assert.Equal(encoder.EncodedBytes, receiver.BytesReceived);
		Assert.Equal(statistics.PacketsSent, receiver.MessagesReceived);
		var encodedHash = encoder.PayloadHash;
		Assert.Equal(encodedHash, receiver.PayloadHash);
		Assert.Equal(0UL, statistics.DroppedSamples);
		Assert.Equal(0UL, statistics.ReconnectCount);
		var liveBytes = encoder.EncodedBytes;
		var liveHash = encodedHash;
		// Keep the receiver alive while Finalize produces delayed media and the session sends every tail byte.
		await session.DisposeAsync().AsTask().WaitAsync(timeout.Token);
		Assert.Equal(1, encoder.CompletionCalls);
		Assert.True(encoder.CompletionBytes > 0, "The native fixture did not exercise encoder/mux tail output.");
		Assert.Equal(liveBytes + encoder.CompletionBytes, encoder.EncodedBytes);
		Assert.Equal(0L, encoder.EncodedBytes % 188);
		statistics = session.Snapshot.Statistics;
		Assert.Equal((ulong)encoder.EncodedBytes, statistics.BytesSent);
		await receiver.WaitForBytesAsync(encoder.EncodedBytes, timeout.Token);
		Assert.Equal(encoder.EncodedBytes, receiver.BytesReceived);
		Assert.Equal(statistics.PacketsSent, receiver.MessagesReceived);
		encodedHash = encoder.PayloadHash;
		Assert.Equal(encodedHash, receiver.PayloadHash);
		Assert.Null(session.Snapshot.Failure);
		Assert.Equal(NetworkOutputLifecycleState.Disabled, session.Snapshot.Lifecycle);
		Assert.Equal(0UL, statistics.DroppedSamples);
		Assert.Equal(0UL, statistics.RejectedSamples);
		Assert.Equal(0UL, statistics.ReconnectCount);
		var evidence = new
		{
			schemaVersion = "1.0", status = "PASS", sourceCommit = source, capturedAtUtc = DateTimeOffset.UtcNow,
			scope = "Live and orderly-completion output: Windows Media Foundation H.264/AAC to native SRT caller and local native receiver",
			format = formatName, frames, runtimeVersion = $"0x{receiver.RuntimeVersion:X6}", nativeLibrarySha256 = nativeHash,
			maximumEncoderDrain = encoder.MaximumDrain, encodedBytes = encoder.EncodedBytes,
			liveBytes, livePayloadSha256 = liveHash, completionBytes = encoder.CompletionBytes,
			completionCalls = encoder.CompletionCalls, completionStatus = "PASS",
			receivedBytes = receiver.BytesReceived,
			messagesReceived = receiver.MessagesReceived, maximumMessage = receiver.MaximumMessage,
			payloadSha256 = encodedHash, statistics,
			limits = "Frame count is submitted input, not decoded frame coverage; bytes/hash cover live output plus complete Finalize output. External decode, WAN, encryption and physical latency remain UNVERIFIED."
		};
		await receiver.DisposeAsync();
		File.WriteAllText(Path.Combine(evidenceRoot, $"srt-native-{formatName}.json"),
			JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
		output.WriteLine($"Native SRT/MF {formatName}: {statistics.PacketsSent} messages, {statistics.BytesSent} bytes, SHA-256 {encodedHash}.");
	}

	private static string RequireEnvironment(string name) =>
		Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
			? value : throw new InvalidOperationException($"Native SRT qualification requires {name}.");

	private sealed class SampleLease(ReadOnlyMemory<byte> pixels) : INetworkOutputPayloadLease
	{
		public ReadOnlyMemory<byte> Memory => pixels;
		public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public void Dispose() => Returned.TrySetResult();
	}

	private sealed class MeasuredEncoder(Action<long> expectCompletion) : ISrtPayloadEncoder
	{
		private readonly WindowsMediaFoundationSrtEncoder _inner = new();
		private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		private bool _disposed;
		private string? _completedHash;
		public long EncodedBytes { get; private set; }
		public long CompletionBytes { get; private set; }
		public int CompletionCalls { get; private set; }
		public int MaximumDrain { get; private set; }
		public int EncodeCalls { get; private set; }
		public DrainDiagnostic? FirstNonEmptyDrain { get; private set; }
		public DrainDiagnostic? LatestNonEmptyDrain { get; private set; }
		public Exception? LastFailure { get; private set; }
		public string PayloadHash => _completedHash ?? Convert.ToHexString(_hash.GetCurrentHash());
		public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken token) => _inner.InitializeAsync(configuration, token);
		public async ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken token)
		{
			try
			{
				var encoded = await _inner.EncodeAsync(sample, token);
				EncodeCalls++;
				if (!encoded.IsEmpty)
				{
					LatestNonEmptyDrain = DescribeDrain(encoded.Span);
					FirstNonEmptyDrain ??= LatestNonEmptyDrain;
				}
				EncodedBytes += encoded.Length;
				MaximumDrain = Math.Max(MaximumDrain, encoded.Length);
				_hash.AppendData(encoded.Span);
				return encoded;
			}
			catch (Exception exception) { LastFailure = exception; throw; }
		}
		public async ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken token)
		{
			try
			{
				CompletionCalls++;
				var tail = await _inner.DrainAsync(token);
				CompletionBytes += tail.Length;
				EncodedBytes += tail.Length;
				MaximumDrain = Math.Max(MaximumDrain, tail.Length);
				_hash.AppendData(tail.Span);
				expectCompletion(EncodedBytes);
				return tail;
			}
			catch (Exception exception) { LastFailure = exception; throw; }
		}
		public async ValueTask DisposeAsync()
		{
			if (_disposed) return;
			_disposed = true;
			try { await _inner.DisposeAsync(); }
			finally { _completedHash = Convert.ToHexString(_hash.GetCurrentHash()); _hash.Dispose(); }
		}
	}

	private sealed record DrainDiagnostic(int Length, int ScannedBytes, string PrefixHex, int[] SyncOffsets, SyncStride[] Strides);
	private sealed record SyncStride(int PacketBytes, int FirstSyncOffset, int CheckedPositions, int MatchingSyncBytes);

	private static DrainDiagnostic DescribeDrain(ReadOnlySpan<byte> bytes)
	{
		var scanned = Math.Min(bytes.Length, 8192);
		var offsets = new List<int>();
		for (var offset = 0; offset < scanned && offsets.Count < 32; offset++)
			if (bytes[offset] == 0x47) offsets.Add(offset);
		var strides = new List<SyncStride>();
		foreach (var (size, first) in new[] { (188, 0), (192, 0), (192, 4), (204, 0) })
		{
			var positions = 0;
			var matches = 0;
			for (var offset = first; offset < scanned; offset += size)
			{
				positions++;
				if (bytes[offset] == 0x47) matches++;
			}
			strides.Add(new SyncStride(size, first, positions, matches));
		}
		return new DrainDiagnostic(bytes.Length, scanned, Convert.ToHexString(bytes[..Math.Min(bytes.Length, 256)]), offsets.ToArray(), strides.ToArray());
	}

	private sealed class LoopbackReceiver : IAsyncDisposable
	{
		private readonly object _gate = new();
		private readonly IntPtr _library;
		private readonly CloseSocket _close;
		private readonly Cleanup _cleanup;
		private readonly AcceptSocket _accept;
		private readonly Receive _receive;
		private readonly LastError _lastError;
		private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		private readonly CancellationTokenSource _shutdown = new();
		private readonly Task _worker;
		private int _listener = -1;
		private int _connection = -1;
		private long _bytes;
		private ulong _messages;
		private int _maximumMessage;
		private long _expectedTotal = -1;
		private bool _disposed;
		public int Port { get; }
		public uint RuntimeVersion { get; }
		public long BytesReceived { get { lock (_gate) return _bytes; } }
		public ulong MessagesReceived { get { lock (_gate) return _messages; } }
		public int MaximumMessage { get { lock (_gate) return _maximumMessage; } }
		public string PayloadHash { get { lock (_gate) return Convert.ToHexString(_hash.GetCurrentHash()); } }

		public void ExpectCompletion(long expectedTotal)
		{
			lock (_gate) _expectedTotal = expectedTotal;
		}

		public LoopbackReceiver(string library)
		{
			_library = NativeLibrary.Load(library);
			var started = false;
			try
			{
				_close = Load<CloseSocket>("srt_close");
				_cleanup = Load<Cleanup>("srt_cleanup");
				_accept = Load<AcceptSocket>("srt_accept");
				_receive = Load<Receive>("srt_recv");
				_lastError = Load<LastError>("srt_getlasterror_str");
				Check(Load<Startup>("srt_startup")(), "startup");
				started = true;
				RuntimeVersion = Load<GetVersion>("srt_getversion")();
				if (RuntimeVersion < 0x010507)
					throw new NotSupportedException("Native SRT loopback requires runtime 1.5.7 or newer.");
				_listener = Load<CreateSocket>("srt_create_socket")();
				Check(_listener, "create");
				var set = Load<SetSocketOption>("srt_setsockflag");
				Set(set, 50, 0); // Live transmission.
				Set(set, 48, 1); // Message API.
				Set(set, 49, 1316);
				Set(set, 23, 120);
				Set(set, 45, 0x010507);
				Set(set, 14, 10_000); // Bound each blocking receive.
				var address = Marshal.AllocHGlobal(16);
				try
				{
					Marshal.Copy(new byte[16], 0, address, 16);
					Marshal.WriteInt16(address, (short)AddressFamily.InterNetwork);
					Marshal.Copy(IPAddress.Loopback.GetAddressBytes(), 0, IntPtr.Add(address, 4), 4);
					Check(Load<BindSocket>("srt_bind")(_listener, address, 16), "bind");
					Check(Load<ListenSocket>("srt_listen")(_listener, 1), "listen");
					var length = 16;
					Check(Load<GetSocketName>("srt_getsockname")(_listener, address, ref length), "getsockname");
					Port = (Marshal.ReadByte(address, 2) << 8) | Marshal.ReadByte(address, 3);
				}
				finally { Marshal.FreeHGlobal(address); }
				_worker = Task.Run(ReceiveLoop);
			}
			catch
			{
				if (_listener != -1) _close!(_listener);
				if (started) _cleanup!();
				NativeLibrary.Free(_library);
				_hash.Dispose();
				_shutdown.Dispose();
				throw;
			}
		}

		private void ReceiveLoop()
		{
			var bytes = new byte[1316];
			var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
			try
			{
				var socket = _accept(_listener, IntPtr.Zero, IntPtr.Zero);
				if (_shutdown.IsCancellationRequested) { if (socket != -1) _close(socket); return; }
				Check(socket, "accept");
				_connection = socket;
				while (!_shutdown.IsCancellationRequested)
				{
					var count = _receive(socket, handle.AddrOfPinnedObject(), bytes.Length);
					if (_shutdown.IsCancellationRequested) return;
					if (count == -1 && HasReceivedCompleteOutput()) return;
					Check(count, "receive");
					Assert.InRange(count, 188, 1316);
					Assert.Equal(0, count % 188);
					for (var offset = 0; offset < count; offset += 188) Assert.Equal(0x47, bytes[offset]);
					lock (_gate)
					{
						_hash.AppendData(bytes.AsSpan(0, count));
						_bytes += count;
						_messages++;
						_maximumMessage = Math.Max(_maximumMessage, count);
					}
					if (HasReceivedCompleteOutput())
					{
						// Do not close the receiver before the sender has observed native buffer completion.
						_shutdown.Token.WaitHandle.WaitOne();
						return;
					}
				}
			}
			finally
			{
				handle.Free();
				Close(ref _connection);
			}
		}

		public async Task WaitForBytesAsync(long expected, CancellationToken token)
		{
			while (BytesReceived < expected)
			{
				if (_worker.IsCompleted) await _worker;
				await Task.Delay(10, token);
			}
		}

		private bool HasReceivedCompleteOutput()
		{
			lock (_gate) return _expectedTotal >= 0 && _bytes == _expectedTotal;
		}

		private void Set(SetSocketOption set, int option, int value)
		{
			var pointer = Marshal.AllocHGlobal(sizeof(int));
			try { Marshal.WriteInt32(pointer, value); Check(set(_listener, option, pointer, sizeof(int)), $"set option {option}"); }
			finally { Marshal.FreeHGlobal(pointer); }
		}
		private void Check(int result, string operation)
		{
			if (result == -1) throw new IOException($"SRT receiver {operation}: {Marshal.PtrToStringUTF8(_lastError())}");
		}
		private T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
		private void Close(ref int socket)
		{
			var owned = Interlocked.Exchange(ref socket, -1);
			if (owned != -1) _close(owned);
		}
		public async ValueTask DisposeAsync()
		{
			if (_disposed) return;
			_disposed = true;
			_shutdown.Cancel();
			Close(ref _listener);
			Close(ref _connection);
			try { await _worker; }
			finally
			{
				_cleanup();
				NativeLibrary.Free(_library);
				_hash.Dispose();
				_shutdown.Dispose();
			}
		}

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Startup();
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Cleanup();
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetVersion();
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSocket();
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseSocket(int socket);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSocketOption(int socket, int option, IntPtr value, int length);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindSocket(int socket, IntPtr address, int length);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ListenSocket(int socket, int backlog);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSocketName(int socket, IntPtr address, ref int length);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AcceptSocket(int socket, IntPtr address, IntPtr length);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Receive(int socket, IntPtr buffer, int length);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LastError();
	}
}
