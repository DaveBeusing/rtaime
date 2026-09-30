// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Srt;

public sealed class NativeSrtTransport : ISrtTransport
{
	private const int InvalidSocket = -1;
	private const int SrtError = -1;
	private const int SrtLive = 0;
	private const int SrtoSndSyn = 1;
	private const int SrtoSender = 21;
	private const int SrtoTsbpdMode = 22;
	private const int SrtoLatency = 23;
	private const int SrtoPassphrase = 26;
	private const int SrtoConnTimeout = 36;
	private const int SrtoMinVersion = 45;
	private const int SrtoMessageApi = 48;
	private const int SrtoPayloadSize = 49;
	private const int SrtoTranType = 50;
	private const uint MinimumSrtVersion = 0x010507;

	private readonly string? _configuredLibraryPath;
	private SrtNativeApi? _api;
	private int _socket = InvalidSocket;
	private bool _started;
	private bool _disposed;

	public NativeSrtTransport(string? nativeLibraryPath = null)
	{
		_configuredLibraryPath = string.IsNullOrWhiteSpace(nativeLibraryPath)
			? null
			: Path.GetFullPath(nativeLibraryPath);
	}

	public bool IsConnected => _socket != InvalidSocket;

	public async ValueTask ConnectAsync(
		NetworkOutputConfiguration configuration,
		string? passphrase,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ThrowIfDisposed();
		if (configuration.Mode != NetworkOutputConnectionMode.Caller)
			throw new NotSupportedException("The first SRT reference transport qualifies caller mode only. Listener and rendezvous remain explicit unverified modes.");

		await DisposeSocketAsync().ConfigureAwait(false);
		var api = _api ??= SrtNativeApi.Load(ResolveLibraryPath());
		if (!_started)
		{
			api.ThrowIfError(api.Startup(), "srt_startup");
			_started = true;
			var version = api.GetVersion();
			if (version < MinimumSrtVersion)
				throw new NotSupportedException($"SRT runtime 0x{version:X6} is older than the required 1.5.7 baseline.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		var socket = api.CreateSocket();
		if (socket == InvalidSocket)
			throw api.CreateException("srt_create_socket");

		try
		{
			SetIntOption(api, socket, SrtoTranType, SrtLive);
			SetIntOption(api, socket, SrtoSender, 1);
			SetIntOption(api, socket, SrtoTsbpdMode, 1);
			SetIntOption(api, socket, SrtoLatency, configuration.LatencyMilliseconds);
			SetIntOption(api, socket, SrtoConnTimeout, Math.Min(configuration.ReconnectMaximumDelayMilliseconds, 10_000));
			SetIntOption(api, socket, SrtoMinVersion, checked((int)MinimumSrtVersion));
			SetIntOption(api, socket, SrtoMessageApi, 0);
			SetIntOption(api, socket, SrtoPayloadSize, 1316);
			SetIntOption(api, socket, SrtoSndSyn, 1);
			if (!string.IsNullOrEmpty(passphrase))
				SetStringOption(api, socket, SrtoPassphrase, passphrase);

			var address = await ResolveEndpointAsync(configuration.Endpoint, cancellationToken).ConfigureAwait(false);
			using var nativeAddress = new NativeIpv4SocketAddress(address, configuration.Endpoint.Port);
			api.ThrowIfError(api.Connect(socket, nativeAddress.Pointer, nativeAddress.Length), "srt_connect");
			_socket = socket;
			socket = InvalidSocket;
		}
		finally
		{
			if (socket != InvalidSocket)
				api.Close(socket);
		}
	}

	public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();
		if (_api is null || _socket == InvalidSocket)
			throw new InvalidOperationException("SRT transport is not connected.");
		if (payload.IsEmpty)
			return ValueTask.FromResult(0);

		byte[]? copy = null;
		ArraySegment<byte> segment;
		if (!MemoryMarshal.TryGetArray(payload, out segment) || segment.Array is null)
		{
			copy = payload.ToArray();
			segment = new ArraySegment<byte>(copy);
		}

		var handle = GCHandle.Alloc(segment.Array!, GCHandleType.Pinned);
		try
		{
			var pointer = IntPtr.Add(handle.AddrOfPinnedObject(), segment.Offset);
			var sent = _api.Send(_socket, pointer, segment.Count);
			if (sent == SrtError)
				throw _api.CreateException("srt_send");
			return ValueTask.FromResult(sent);
		}
		finally
		{
			handle.Free();
			GC.KeepAlive(copy);
		}
	}

	private static void SetIntOption(SrtNativeApi api, int socket, int option, int value)
	{
		var buffer = Marshal.AllocHGlobal(sizeof(int));
		try
		{
			Marshal.WriteInt32(buffer, value);
			api.ThrowIfError(api.SetSockFlag(socket, option, buffer, sizeof(int)), $"srt_setsockflag({option})");
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static void SetStringOption(SrtNativeApi api, int socket, int option, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		var buffer = Marshal.AllocHGlobal(bytes.Length);
		try
		{
			Marshal.Copy(bytes, 0, buffer, bytes.Length);
			api.ThrowIfError(api.SetSockFlag(socket, option, buffer, bytes.Length), $"srt_setsockflag({option})");
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private string ResolveLibraryPath()
	{
		if (_configuredLibraryPath is not null)
			return _configuredLibraryPath;
		var environment = Environment.GetEnvironmentVariable("RTAIME_SRT_LIBRARY_PATH");
		if (!string.IsNullOrWhiteSpace(environment))
			return Path.GetFullPath(environment);
		return OperatingSystem.IsWindows() ? "srt.dll" : "libsrt.so.1.5";
	}

	private static async ValueTask<IPAddress> ResolveEndpointAsync(Uri endpoint, CancellationToken cancellationToken)
	{
		if (IPAddress.TryParse(endpoint.Host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
			return parsed;
		var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
		return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
			?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);
	}

	private async ValueTask DisposeSocketAsync()
	{
		var socket = Interlocked.Exchange(ref _socket, InvalidSocket);
		var api = _api;
		if (socket != InvalidSocket && api is not null)
			api.Close(socket);
		await ValueTask.CompletedTask;
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed) return;
		_disposed = true;
		await DisposeSocketAsync().ConfigureAwait(false);
		if (_started && _api is not null)
		{
			_api.Cleanup();
			_started = false;
		}
		_api?.Dispose();
		_api = null;
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private sealed class NativeIpv4SocketAddress : IDisposable
	{
		public NativeIpv4SocketAddress(IPAddress address, int port)
		{
			var bytes = address.GetAddressBytes();
			if (bytes.Length != 4)
				throw new ArgumentException("SRT reference transport currently requires an IPv4 endpoint.", nameof(address));
			Length = 16;
			Pointer = Marshal.AllocHGlobal(Length);
			for (var index = 0; index < Length; index++)
				Marshal.WriteByte(Pointer, index, 0);
			Marshal.WriteInt16(Pointer, 0, (short)AddressFamily.InterNetwork);
			Marshal.WriteByte(Pointer, 2, checked((byte)(port >> 8)));
			Marshal.WriteByte(Pointer, 3, checked((byte)(port & 0xff)));
			for (var index = 0; index < 4; index++)
				Marshal.WriteByte(Pointer, 4 + index, bytes[index]);
		}

		public IntPtr Pointer { get; }
		public int Length { get; }
		public void Dispose() => Marshal.FreeHGlobal(Pointer);
	}
}

internal sealed class SrtNativeApi : IDisposable
{
	private const int SrtError = -1;
	private readonly IntPtr _library;
	private readonly SrtStartup _startup;
	private readonly SrtCleanup _cleanup;
	private readonly SrtGetVersion _getVersion;
	private readonly SrtCreateSocket _createSocket;
	private readonly SrtClose _close;
	private readonly SrtSetSockFlag _setSockFlag;
	private readonly SrtConnect _connect;
	private readonly SrtSend _send;
	private readonly SrtGetLastErrorString _getLastErrorString;
	private bool _disposed;

	private SrtNativeApi(IntPtr library)
	{
		_library = library;
		_startup = Load<SrtStartup>("srt_startup");
		_cleanup = Load<SrtCleanup>("srt_cleanup");
		_getVersion = Load<SrtGetVersion>("srt_getversion");
		_createSocket = Load<SrtCreateSocket>("srt_create_socket");
		_close = Load<SrtClose>("srt_close");
		_setSockFlag = Load<SrtSetSockFlag>("srt_setsockflag");
		_connect = Load<SrtConnect>("srt_connect");
		_send = Load<SrtSend>("srt_send");
		_getLastErrorString = Load<SrtGetLastErrorString>("srt_getlasterror_str");
	}

	public static SrtNativeApi Load(string libraryPath)
	{
		if (!NativeLibrary.TryLoad(libraryPath, out var handle))
			throw new DllNotFoundException($"SRT native library could not be loaded from '{libraryPath}'.");
		return new SrtNativeApi(handle);
	}

	public int Startup() => _startup();
	public int Cleanup() => _cleanup();
	public uint GetVersion() => _getVersion();
	public int CreateSocket() => _createSocket();
	public int Close(int socket) => _close(socket);
	public int SetSockFlag(int socket, int option, IntPtr value, int length) => _setSockFlag(socket, option, value, length);
	public int Connect(int socket, IntPtr address, int length) => _connect(socket, address, length);
	public int Send(int socket, IntPtr buffer, int length) => _send(socket, buffer, length);

	public void ThrowIfError(int result, string operation)
	{
		if (result == SrtError)
			throw CreateException(operation);
	}

	public IOException CreateException(string operation)
	{
		var pointer = _getLastErrorString();
		var detail = pointer == IntPtr.Zero ? "unknown SRT error" : Marshal.PtrToStringUTF8(pointer) ?? "unknown SRT error";
		return new IOException($"{operation} failed: {detail}");
	}

	private T Load<T>(string export) where T : Delegate =>
		Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, export));

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		NativeLibrary.Free(_library);
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtStartup();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtCleanup();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SrtGetVersion();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtCreateSocket();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtClose(int socket);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtSetSockFlag(int socket, int option, IntPtr value, int length);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtConnect(int socket, IntPtr address, int length);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SrtSend(int socket, IntPtr buffer, int length);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr SrtGetLastErrorString();
}
