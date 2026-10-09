// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Ndi;

public static class NdiRuntimeDiscovery
{
	public const string RuntimeLibraryFileName = "Processing.NDI.Lib.x64.dll";

	public static string? ResolveLibraryPath(string? configuredPath = null)
	{
		if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
			return null;

		foreach (var candidate in EnumerateCandidates(configuredPath))
		{
			try
			{
				var fullPath = Path.GetFullPath(candidate);
				if (File.Exists(fullPath))
					return fullPath;
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
			{
			}
		}

		return null;
	}

	private static IEnumerable<string> EnumerateCandidates(string? configuredPath)
	{
		if (!string.IsNullOrWhiteSpace(configuredPath))
			yield return configuredPath;

		var explicitLibrary = Environment.GetEnvironmentVariable("RTAIME_NDI_LIBRARY_PATH");
		if (!string.IsNullOrWhiteSpace(explicitLibrary))
			yield return explicitLibrary;

		foreach (var directoryVariable in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
		{
			var directory = Environment.GetEnvironmentVariable(directoryVariable);
			if (!string.IsNullOrWhiteSpace(directory))
				yield return Path.Combine(directory, RuntimeLibraryFileName);
		}
	}
}

public sealed class NativeNdiSender : INdiSender
{
	private readonly NetworkOutputConfiguration _configuration;
	private readonly NdiNativeApi _api;
	private IntPtr _sender;
	private bool _disposed;

	public NativeNdiSender(NetworkOutputConfiguration configuration, string runtimeLibraryPath)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		if (_configuration.Protocol != NetworkOutputProtocolFamily.Ndi || _configuration.NdiSettings is null)
			throw new ArgumentException("Native NDI sender requires typed NDI network-output settings.", nameof(configuration));
		if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
			throw new PlatformNotSupportedException("NDI output currently requires Windows x64.");

		_api = NdiNativeApi.Load(runtimeLibraryPath);
		if (!_api.Initialize())
			throw new InvalidOperationException("NDI runtime initialization failed.");

		try
		{
			_sender = _api.CreateSender(_configuration.NdiSettings.SourceName);
			if (_sender == IntPtr.Zero)
				throw new InvalidOperationException("NDI sender creation failed.");
		}
		catch
		{
			_api.Dispose();
			throw;
		}
	}

	public bool IsReady => !_disposed && _sender != IntPtr.Zero;

	public ValueTask<NdiSendResult> SendAsync(NetworkOutputProgramSample sample, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(sample);
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (_sender == IntPtr.Zero)
			throw new InvalidOperationException("NDI sender is not ready.");

		ValidateSample(sample);

		var videoBuffer = sample.Video.Memory.ToArray();
		var interleavedAudio = MemoryMarshal.Cast<byte, float>(sample.Audio.Span);
		var sampleCount = checked((int)sample.AudioTiming.SampleCount);
		var channels = checked((int)_configuration.AudioFormat.ChannelCount);
		var planarAudio = new float[checked(sampleCount * channels)];
		InterleavedToPlanar(interleavedAudio, planarAudio, channels, sampleCount);

		var videoHandle = GCHandle.Alloc(videoBuffer, GCHandleType.Pinned);
		var audioHandle = GCHandle.Alloc(planarAudio, GCHandleType.Pinned);
		try
		{
			var format = _configuration.VideoFormat;
			var timecodeVideo = ToHundredNanoseconds(
				sample.VideoTiming.PresentationTimestamp,
				sample.VideoTiming.Timebase);
			var timecodeAudio = ToHundredNanoseconds(
				sample.AudioTiming.PresentationTimestamp,
				sample.AudioTiming.Timebase);

			var video = new NdiVideoFrameV2
			{
				XRes = checked((int)format.Width),
				YRes = checked((int)format.Height),
				FourCc = NdiFourCc.Rgba,
				FrameRateNumerator = checked((int)format.FrameRate.Numerator),
				FrameRateDenominator = checked((int)format.FrameRate.Denominator),
				PictureAspectRatio = format.Width / (float)format.Height,
				FrameFormatType = NdiFrameFormat.Progressive,
				Timecode = timecodeVideo,
				Data = videoHandle.AddrOfPinnedObject(),
				LineStrideInBytes = checked((int)format.Width * 4),
				Metadata = IntPtr.Zero,
				Timestamp = 0
			};

			var audio = new NdiAudioFrameV3
			{
				SampleRate = checked((int)_configuration.AudioFormat.SampleRate),
				NoChannels = channels,
				NoSamples = sampleCount,
				Timecode = timecodeAudio,
				FourCc = NdiFourCc.Fltp,
				Data = audioHandle.AddrOfPinnedObject(),
				ChannelStrideInBytes = checked(sampleCount * sizeof(float)),
				Metadata = IntPtr.Zero,
				Timestamp = 0
			};

			_api.SendVideo(_sender, ref video);
			cancellationToken.ThrowIfCancellationRequested();
			_api.SendAudio(_sender, ref audio);

			return ValueTask.FromResult(new NdiSendResult(
				MediaFramesSubmitted: 2,
				MediaBytesSubmitted: checked((ulong)(videoBuffer.LongLength + (long)planarAudio.Length * sizeof(float)))));
		}
		finally
		{
			audioHandle.Free();
			videoHandle.Free();
		}
	}

	private void ValidateSample(NetworkOutputProgramSample sample)
	{
		var format = _configuration.VideoFormat;
		var expectedVideoBytes = checked((int)((long)format.Width * format.Height * 4));
		if (sample.Video.Memory.Length != expectedVideoBytes)
			throw new InvalidDataException($"NDI video payload length {sample.Video.Memory.Length} does not match expected Runtime RGBA8 payload length {expectedVideoBytes}.");

		var expectedAudioBytes = checked((int)((long)sample.AudioTiming.SampleCount * _configuration.AudioFormat.ChannelCount * sizeof(float)));
		if (sample.Audio.Length != expectedAudioBytes)
			throw new InvalidDataException($"NDI audio payload length {sample.Audio.Length} does not match expected Runtime Float32 payload length {expectedAudioBytes}.");
		if (sample.VideoTiming.PresentationTimestamp < 0 || sample.AudioTiming.PresentationTimestamp < 0)
			throw new InvalidDataException("NDI output timestamps must be non-negative.");
	}

	internal static long ToHundredNanoseconds(long timestamp, Timebase timebase)
	{
		if (timestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timestamp));
		var numerator = checked((Int128)timestamp * timebase.Numerator * 10_000_000);
		return DivideRound(numerator, timebase.Denominator);
	}

	internal static void InterleavedToPlanar(
		ReadOnlySpan<float> interleaved,
		Span<float> planar,
		int channels,
		int samplesPerChannel)
	{
		if (channels <= 0 || samplesPerChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(channels));
		if (interleaved.Length != checked(channels * samplesPerChannel) || planar.Length != interleaved.Length)
			throw new ArgumentException("NDI audio buffer dimensions do not match.");

		for (var channel = 0; channel < channels; channel++)
		{
			var destinationOffset = channel * samplesPerChannel;
			for (var sample = 0; sample < samplesPerChannel; sample++)
				planar[destinationOffset + sample] = interleaved[(sample * channels) + channel];
		}
	}

	private static long DivideRound(Int128 numerator, long denominator)
	{
		if (denominator <= 0)
			throw new ArgumentOutOfRangeException(nameof(denominator));
		return checked((long)((numerator + (denominator / 2)) / denominator));
	}

	public ValueTask DisposeAsync()
	{
		if (_disposed)
			return ValueTask.CompletedTask;
		_disposed = true;

		var sender = Interlocked.Exchange(ref _sender, IntPtr.Zero);
		if (sender != IntPtr.Zero)
			_api.DestroySender(sender);
		_api.Dispose();
		return ValueTask.CompletedTask;
	}
}

internal static class NdiFourCc
{
	public static readonly int Rgba = Make('R', 'G', 'B', 'A');
	public static readonly int Fltp = Make('F', 'L', 'T', 'P');

	private static int Make(char a, char b, char c, char d) =>
		a | (b << 8) | (c << 16) | (d << 24);
}

internal static class NdiFrameFormat
{
	public const int Progressive = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NdiVideoFrameV2
{
	public int XRes;
	public int YRes;
	public int FourCc;
	public int FrameRateNumerator;
	public int FrameRateDenominator;
	public float PictureAspectRatio;
	public int FrameFormatType;
	public long Timecode;
	public IntPtr Data;
	public int LineStrideInBytes;
	public IntPtr Metadata;
	public long Timestamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NdiAudioFrameV3
{
	public int SampleRate;
	public int NoChannels;
	public int NoSamples;
	public long Timecode;
	public int FourCc;
	public IntPtr Data;
	public int ChannelStrideInBytes;
	public IntPtr Metadata;
	public long Timestamp;
}

internal sealed class NdiNativeApi : IDisposable
{
	private readonly IntPtr _library;
	private readonly NdiInitialize _initialize;
	private readonly NdiDestroy _destroy;
	private readonly NdiSendCreate _sendCreate;
	private readonly NdiSendDestroy _sendDestroy;
	private readonly NdiSendVideoV2 _sendVideo;
	private readonly NdiSendAudioV3 _sendAudio;
	private bool _initialized;
	private bool _disposed;

	private NdiNativeApi(IntPtr library)
	{
		_library = library;
		_initialize = Load<NdiInitialize>("NDIlib_initialize");
		_destroy = Load<NdiDestroy>("NDIlib_destroy");
		_sendCreate = Load<NdiSendCreate>("NDIlib_send_create");
		_sendDestroy = Load<NdiSendDestroy>("NDIlib_send_destroy");
		_sendVideo = Load<NdiSendVideoV2>("NDIlib_send_send_video_v2");
		_sendAudio = Load<NdiSendAudioV3>("NDIlib_send_send_audio_v3");
	}

	public static NdiNativeApi Load(string libraryPath)
	{
		if (string.IsNullOrWhiteSpace(libraryPath))
			throw new ArgumentException("NDI runtime library path is required.", nameof(libraryPath));
		if (!NativeLibrary.TryLoad(Path.GetFullPath(libraryPath), out var library))
			throw new DllNotFoundException("Configured NDI runtime library could not be loaded.");

		try
		{
			return new NdiNativeApi(library);
		}
		catch
		{
			NativeLibrary.Free(library);
			throw;
		}
	}

	public bool Initialize()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_initialized)
			return true;
		_initialized = _initialize();
		return _initialized;
	}

	public IntPtr CreateSender(string sourceName)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_initialized)
			throw new InvalidOperationException("NDI runtime is not initialized.");

		var name = Marshal.StringToCoTaskMemUTF8(sourceName);
		try
		{
			var create = new NdiSendCreateDescriptor
			{
				NdiName = name,
				Groups = IntPtr.Zero,
				ClockVideo = false,
				ClockAudio = false
			};
			return _sendCreate(ref create);
		}
		finally
		{
			Marshal.FreeCoTaskMem(name);
		}
	}

	public void DestroySender(IntPtr sender)
	{
		if (sender != IntPtr.Zero)
			_sendDestroy(sender);
	}

	public void SendVideo(IntPtr sender, ref NdiVideoFrameV2 frame) => _sendVideo(sender, ref frame);
	public void SendAudio(IntPtr sender, ref NdiAudioFrameV3 frame) => _sendAudio(sender, ref frame);

	private T Load<T>(string export) where T : Delegate
	{
		if (!NativeLibrary.TryGetExport(_library, export, out var address))
			throw new EntryPointNotFoundException($"Required NDI runtime export '{export}' is unavailable.");
		return Marshal.GetDelegateForFunctionPointer<T>(address);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (_initialized)
		{
			_destroy();
			_initialized = false;
		}
		NativeLibrary.Free(_library);
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	[return: MarshalAs(UnmanagedType.I1)]
	private delegate bool NdiInitialize();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void NdiDestroy();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate IntPtr NdiSendCreate(ref NdiSendCreateDescriptor createSettings);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void NdiSendDestroy(IntPtr sender);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void NdiSendVideoV2(IntPtr sender, ref NdiVideoFrameV2 videoFrame);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void NdiSendAudioV3(IntPtr sender, ref NdiAudioFrameV3 audioFrame);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NdiSendCreateDescriptor
{
	public IntPtr NdiName;
	public IntPtr Groups;
	[MarshalAs(UnmanagedType.I1)]
	public bool ClockVideo;
	[MarshalAs(UnmanagedType.I1)]
	public bool ClockAudio;
}
