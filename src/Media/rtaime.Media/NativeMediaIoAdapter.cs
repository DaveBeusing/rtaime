// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Media;

/// <summary>
/// Managed bridge over the stable rtaime Media I/O C ABI. Loading is explicit: construction fails closed when
/// the native provider module is absent or rejects the requested ABI.
/// </summary>
public sealed class NativeMediaIoProviderAdapter : IMediaIoProviderAdapter
{
	private static readonly ProviderId ProviderIdentity =
		new(Identity.Parse("95000000-0000-0000-0000-000000000001"));

	private readonly IntPtr _provider;
	private readonly Dictionary<MediaIoPortId, MediaIoPortDescriptor> _ports;
	private int _disposed;

	public NativeMediaIoProviderAdapter()
	{
		var result = NativeMethods.CreateProvider(1, 0, out _provider);
		Ensure(result, "create provider");
		try
		{
			Descriptor = BuildDescriptor(_provider, out _ports);
		}
		catch
		{
			NativeMethods.DestroyProvider(_provider);
			throw;
		}
	}

	public MediaIoProviderDescriptor Descriptor { get; }

	public MediaIoPortStatus GetPortStatus(MediaIoPortId portId)
	{
		ThrowIfDisposed();
		if (!_ports.ContainsKey(portId))
			throw new KeyNotFoundException($"Unknown Media I/O port '{portId}'.");

		return new MediaIoPortStatus(
			MediaIoContractVersion.Current,
			portId,
			MediaIoSignalState.Unknown,
			UtcTimestamp.Now());
	}

	public IMediaIoInputSession OpenInput(MediaIoSessionRequest request)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(request);
		if (request.Direction != MediaIoDirection.Input)
			throw new ArgumentException("Input session requires MediaIoDirection.Input.", nameof(request));
		ValidateAdmission(request);
		var port = _ports[request.PortId];
		var session = OpenNativeSession(request);
		return new NativeInputSession(session, port, request.VideoFormat);
	}

	public IMediaIoOutputSession OpenOutput(MediaIoSessionRequest request)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(request);
		if (request.Direction != MediaIoDirection.Output)
			throw new ArgumentException("Output session requires MediaIoDirection.Output.", nameof(request));
		ValidateAdmission(request);
		var port = _ports[request.PortId];
		var session = OpenNativeSession(request);
		return new NativeOutputSession(session, port, request.VideoFormat);
	}

	public ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			NativeMethods.DestroyProvider(_provider);
		return ValueTask.CompletedTask;
	}

	private IntPtr OpenNativeSession(MediaIoSessionRequest request)
	{
		var config = new NativeSessionConfig
		{
			AbiVersionMajor = 1,
			AbiVersionMinor = 0,
			PortId = NativeIdentity.From(request.PortId.Value),
			Direction = (uint)request.Direction,
			NormalizedFormat = NativeVideoFormat.From(request.VideoFormat),
			TransferMode = (uint)request.TransferMode,
			EmbeddedAudioEnabled = request.AudioFormat is null ? 0u : 1u,
			ExternalReferenceRequired = request.RequireExternalReference ? 1u : 0u
		};
		var result = NativeMethods.OpenSession(_provider, in config, out var session);
		Ensure(result, $"open {request.Direction} session");
		return session;
	}

	private void ValidateAdmission(MediaIoSessionRequest request)
	{
		var validation = MediaIoAdmission.Validate(Descriptor, request);
		if (!validation.IsValid)
		{
			throw new InvalidOperationException(
				$"Media I/O admission rejected: {string.Join(" | ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");
		}
	}

	private static MediaIoProviderDescriptor BuildDescriptor(
		IntPtr provider,
		out Dictionary<MediaIoPortId, MediaIoPortDescriptor> portsById)
	{
		Ensure(NativeMethods.GetPortCount(provider, out var count), "get port count");
		if (count == 0)
			throw new InvalidOperationException("Native Media I/O provider exposed no ports.");

		var ports = new List<MediaIoPortDescriptor>(checked((int)count));
		var resources = new List<ProviderResourceDescriptor>(checked((int)count));
		for (uint index = 0; index < count; index++)
		{
			Ensure(NativeMethods.GetPort(provider, index, out var native), $"get port {index}");
			var portId = new MediaIoPortId(native.PortId.ToIdentity());
			var resourceId = new ProviderResourceId(native.ResourceId.ToIdentity());
			var direction = (MediaIoDirection)native.Direction;
			var capabilityKind = direction == MediaIoDirection.Input
				? MediaIoCapabilityKinds.VideoInput
				: MediaIoCapabilityKinds.VideoOutput;
			var port = new MediaIoPortDescriptor(
				MediaIoContractVersion.Current,
				ProviderIdentity,
				resourceId,
				portId,
				$"SDI {(direction == MediaIoDirection.Input ? "Input" : "Output")} {index + 1}",
				direction,
				MediaIoTransportKind.Sdi,
				new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
				new[]
				{
					new MediaIoNativeVideoFormat(1920, 1080, FrameRate.Fps50, MediaIoNativePixelFormat.Rgba8, ScanMode.Progressive),
					new MediaIoNativeVideoFormat(1920, 1080, FrameRate.Fps59_94, MediaIoNativePixelFormat.Rgba8, ScanMode.Progressive)
				},
				new[] { AudioFormat.Stereo48kFloat32 },
				new[] { MediaIoTransferMode.PinnedHostLease },
				native.SupportsExternalReference != 0);
			ports.Add(port);
			resources.Add(new ProviderResourceDescriptor(resourceId, ProviderIdentity, capabilityKind, 1, reservable: true));
		}

		var formats = new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 };
		var capabilities = new List<ProviderCapabilityDescriptor>();
		if (ports.Any(port => port.Direction == MediaIoDirection.Input))
			capabilities.Add(new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoInput, formats));
		if (ports.Any(port => port.Direction == MediaIoDirection.Output))
			capabilities.Add(new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoOutput, formats));

		var generic = new ProviderDescriptor(
			ProviderContractVersion.Current,
			ProviderIdentity,
			"rtaime Native Media I/O",
			new ProviderAvailability(ProviderAvailabilityState.Available),
			capabilities,
			resources);
		portsById = ports.ToDictionary(port => port.PortId);
		return new MediaIoProviderDescriptor(MediaIoContractVersion.Current, generic, ports);
	}

	private static void Ensure(NativeResult result, string operation)
	{
		if (result == NativeResult.Ok)
			return;
		throw new InvalidOperationException($"Native Media I/O operation '{operation}' failed with '{result}' ({(int)result}).");
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

	private sealed class NativeInputSession : IMediaIoInputSession
	{
		private readonly IntPtr _session;
		private readonly VideoFormat _format;
		private int _disposed;

		public NativeInputSession(IntPtr session, MediaIoPortDescriptor port, VideoFormat format)
		{
			_session = session;
			Port = port;
			_format = format;
		}

		public MediaIoPortDescriptor Port { get; }
		public MediaIoPortStatus Status => ReadStatus(_session, Port.PortId);

		public bool TryAcquire(out MediaIoInputFrameLease? lease)
		{
			ThrowIfDisposed();
			var result = NativeMethods.InputTryAcquire(_session, out var native);
			if (result == NativeResult.WouldBlock)
			{
				lease = null;
				return false;
			}
			Ensure(result, "input acquire");

			var leaseId = native.Video.LeaseId.ToIdentity();
			var surface = new SurfaceDescriptor(
				new SurfaceId(leaseId),
				_format,
				SurfaceStorageDomain.Host,
				SurfaceOwnership.SharedLease,
				new SurfaceLifetimeDescriptor(new Generation(native.SequenceNumber), leaseId),
				new OpaqueSurfaceHandle(
					PinnedHostMediaIoMemory.VideoHandleKind,
					native.Video.OpaqueHandle.ToString(CultureInfo.InvariantCulture)));
			var frame = new FrameDescriptor(
				MediaContractVersion.Current,
				new MediaSourceId(native.SourceId.ToIdentity()),
				surface,
				new FrameTiming(
					native.SequenceNumber,
					native.PresentationTimestamp,
					new Timebase(native.TimebaseNumerator, native.TimebaseDenominator)));

			AudioBufferDescriptor? audio = null;
			if (native.AudioOpaqueHandle != 0 && native.AudioSampleCount > 0 && native.AudioChannelCount == 2)
			{
				audio = new AudioBufferDescriptor(
					MediaContractVersion.Current,
					new AudioStreamId(leaseId),
					AudioFormat.Stereo48kFloat32,
					leaseId,
					new AudioBufferTiming(
						native.SequenceNumber * native.AudioSampleCount,
						native.AudioSampleCount,
						native.PresentationTimestamp,
						new Timebase(1, 48_000)),
					new OpaqueAudioHandle(
						PinnedHostMediaIoMemory.AudioHandleKind,
						native.AudioOpaqueHandle.ToString(CultureInfo.InvariantCulture)));
			}

			var descriptor = new MediaIoInputFrameDescriptor(MediaIoContractVersion.Current, Port.PortId, frame, audio);
			lease = new MediaIoInputFrameLease(descriptor, _ =>
			{
				var identity = NativeIdentity.From(leaseId);
				Ensure(NativeMethods.InputRelease(_session, in identity), "input release");
			});
			return true;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				NativeMethods.CloseSession(_session);
		}

		private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
	}

	private sealed class NativeOutputSession : IMediaIoOutputSession
	{
		private readonly IntPtr _session;
		private readonly VideoFormat _format;
		private int _disposed;

		public NativeOutputSession(IntPtr session, MediaIoPortDescriptor port, VideoFormat format)
		{
			_session = session;
			Port = port;
			_format = format;
		}

		public MediaIoPortDescriptor Port { get; }
		public MediaIoPortStatus Status => ReadStatus(_session, Port.PortId);

		public MediaIoOutputSubmitResult TrySubmit(MediaIoOutputFrameDescriptor frame)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(frame);
			if (frame.PortId != Port.PortId)
				return MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.port_mismatch", "Output descriptor targets a different port."));
			if (frame.Video.Surface.Format != _format)
				return MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.format_mismatch", "Output descriptor format differs from the opened session."));
			if (!string.Equals(frame.Video.Surface.Handle?.Kind, PinnedHostMediaIoMemory.VideoHandleKind, StringComparison.Ordinal))
				return MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.handle_unsupported", "AP-33 native output requires a pinned-host Media I/O handle."));
			if (!ulong.TryParse(frame.Video.Surface.Handle.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pointer) || pointer == 0)
				return MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.handle_invalid", "Output surface handle is not a valid pinned-host address."));

			ulong audioPointer = 0;
			if (frame.EmbeddedAudio?.Handle is { } audioHandle &&
				string.Equals(audioHandle.Kind, PinnedHostMediaIoMemory.AudioHandleKind, StringComparison.Ordinal))
			{
				ulong.TryParse(audioHandle.Value, NumberStyles.None, CultureInfo.InvariantCulture, out audioPointer);
			}

			var native = new NativeOutputFrame
			{
				PortId = NativeIdentity.From(Port.PortId.Value),
				SequenceNumber = frame.Video.Timing.SequenceNumber,
				PresentationTimestamp = frame.Video.Timing.PresentationTimestamp,
				TimebaseNumerator = checked((uint)frame.Video.Timing.Timebase.Numerator),
				TimebaseDenominator = checked((uint)frame.Video.Timing.Timebase.Denominator),
				Format = NativeVideoFormat.From(frame.Video.Surface.Format),
				OpaqueSurfaceHandle = pointer,
				SurfaceLeaseId = NativeIdentity.From(frame.Video.Surface.Lifetime.LeaseId ?? Identity.New()),
				AudioOpaqueHandle = audioPointer
			};
			var result = NativeMethods.OutputTrySubmit(_session, in native);
			return result switch
			{
				NativeResult.Ok => MediaIoOutputSubmitResult.Success(),
				NativeResult.WouldBlock => MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.backpressure", "Native output is not ready for another frame.")),
				_ => MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.native_failure", $"Native output rejected frame with '{result}'."))
			};
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				NativeMethods.CloseSession(_session);
		}

		private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
	}

	private static MediaIoPortStatus ReadStatus(IntPtr session, MediaIoPortId portId)
	{
		Ensure(NativeMethods.GetStatus(session, out var native), "get session status");
		Failure? failure = native.SignalState == (uint)MediaIoSignalState.Faulted
			? new Failure("media.io.native.device_error", $"Native provider status code {native.VendorStatusCode}.")
			: null;
		return new MediaIoPortStatus(
			MediaIoContractVersion.Current,
			portId,
			(MediaIoSignalState)native.SignalState,
			UtcTimestamp.FromUnixMilliseconds(native.ObservedAtUnixMilliseconds),
			null,
			failure);
	}

	private enum NativeResult
	{
		Ok = 0,
		WouldBlock = 1,
		InvalidArgument = 2,
		Unavailable = 3,
		FormatUnsupported = 4,
		DeviceError = 5,
		StateError = 6
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
	private struct NativeIdentity
	{
		public ulong Low;
		public ulong High;

		public static NativeIdentity From(Identity identity)
		{
			var bytes = identity.Value.ToByteArray();
			return new NativeIdentity
			{
				Low = BitConverter.ToUInt64(bytes, 0),
				High = BitConverter.ToUInt64(bytes, 8)
			};
		}

		public Identity ToIdentity()
		{
			var bytes = new byte[16];
			BitConverter.GetBytes(Low).CopyTo(bytes, 0);
			BitConverter.GetBytes(High).CopyTo(bytes, 8);
			return new Identity(new Guid(bytes));
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeVideoFormat
	{
		public uint Width;
		public uint Height;
		public uint FrameRateNumerator;
		public uint FrameRateDenominator;
		public uint PixelFormat;
		public uint Progressive;

		public static NativeVideoFormat From(VideoFormat format) => new()
		{
			Width = format.Width,
			Height = format.Height,
			FrameRateNumerator = checked((uint)format.FrameRate.Numerator),
			FrameRateDenominator = checked((uint)format.FrameRate.Denominator),
			PixelFormat = 1,
			Progressive = format.ScanMode == ScanMode.Progressive ? 1u : 0u
		};
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePortInfo
	{
		public NativeIdentity PortId;
		public NativeIdentity ResourceId;
		public uint Direction;
		public uint Transport;
		public uint SupportsExternalReference;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeSessionConfig
	{
		public uint AbiVersionMajor;
		public uint AbiVersionMinor;
		public NativeIdentity PortId;
		public uint Direction;
		public NativeVideoFormat NormalizedFormat;
		public uint TransferMode;
		public uint EmbeddedAudioEnabled;
		public uint ExternalReferenceRequired;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeBufferLease
	{
		public NativeIdentity LeaseId;
		public ulong OpaqueHandle;
		public ulong ByteLength;
		public uint RowBytes;
		public uint TransferMode;
		public uint StorageDomain;
		public uint NativePixelFormat;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeInputFrame
	{
		public NativeIdentity PortId;
		public NativeIdentity SourceId;
		public ulong SequenceNumber;
		public long PresentationTimestamp;
		public uint TimebaseNumerator;
		public uint TimebaseDenominator;
		public NativeVideoFormat NormalizedFormat;
		public NativeBufferLease Video;
		public ulong AudioOpaqueHandle;
		public uint AudioSampleCount;
		public uint AudioChannelCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeOutputFrame
	{
		public NativeIdentity PortId;
		public ulong SequenceNumber;
		public long PresentationTimestamp;
		public uint TimebaseNumerator;
		public uint TimebaseDenominator;
		public NativeVideoFormat Format;
		public ulong OpaqueSurfaceHandle;
		public NativeIdentity SurfaceLeaseId;
		public ulong AudioOpaqueHandle;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePortStatus
	{
		public NativeIdentity PortId;
		public uint SignalState;
		public long ObservedAtUnixMilliseconds;
		public int VendorStatusCode;
	}

	private static class NativeMethods
	{
		private const string Library = "rtaime_media_io";

		[DllImport(Library, EntryPoint = "rtaime_media_io_create_provider", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult CreateProvider(uint abiVersionMajor, uint abiVersionMinor, out IntPtr provider);

		[DllImport(Library, EntryPoint = "rtaime_media_io_destroy_provider", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void DestroyProvider(IntPtr provider);

		[DllImport(Library, EntryPoint = "rtaime_media_io_get_port_count", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult GetPortCount(IntPtr provider, out uint count);

		[DllImport(Library, EntryPoint = "rtaime_media_io_get_port", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult GetPort(IntPtr provider, uint index, out NativePortInfo port);

		[DllImport(Library, EntryPoint = "rtaime_media_io_open_session", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult OpenSession(IntPtr provider, in NativeSessionConfig config, out IntPtr session);

		[DllImport(Library, EntryPoint = "rtaime_media_io_close_session", CallingConvention = CallingConvention.Cdecl)]
		internal static extern void CloseSession(IntPtr session);

		[DllImport(Library, EntryPoint = "rtaime_media_io_get_status", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult GetStatus(IntPtr session, out NativePortStatus status);

		[DllImport(Library, EntryPoint = "rtaime_media_io_input_try_acquire", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult InputTryAcquire(IntPtr session, out NativeInputFrame frame);

		[DllImport(Library, EntryPoint = "rtaime_media_io_input_release", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult InputRelease(IntPtr session, in NativeIdentity leaseId);

		[DllImport(Library, EntryPoint = "rtaime_media_io_output_try_submit", CallingConvention = CallingConvention.Cdecl)]
		internal static extern NativeResult OutputTrySubmit(IntPtr session, in NativeOutputFrame frame);
	}
}

/// <summary>
/// AP-33 pinned-host fallback. The numeric handle is process-local and is only dereferenced inside the RuntimeHost
/// media hot path; it is never serialized through Control/management IPC.
/// </summary>
public static class PinnedHostMediaIoMemory
{
	public const string VideoHandleKind = "rtaime.media-io.pinned-host";
	public const string AudioHandleKind = "rtaime.media-io.pinned-audio";

	public static RgbaFrameBuffer CopyRgba(MediaIoInputFrameLease lease)
	{
		ArgumentNullException.ThrowIfNull(lease);
		var frame = lease.Descriptor.Video;
		if (!string.Equals(frame.Surface.Handle?.Kind, VideoHandleKind, StringComparison.Ordinal))
			throw new InvalidOperationException("Media I/O frame is not backed by a pinned-host handle.");
		if (!ulong.TryParse(frame.Surface.Handle.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var address) || address == 0)
			throw new InvalidOperationException("Media I/O pinned-host address is invalid.");

		var pixels = new byte[RgbaFrameBuffer.RequiredByteLength(frame.Surface.Format)];
		Marshal.Copy(new IntPtr(unchecked((long)address)), pixels, 0, pixels.Length);
		return new RgbaFrameBuffer(frame.Surface.Format, pixels);
	}
}
