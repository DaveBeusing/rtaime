// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Media;

public sealed class MediaIoRgbaFrame
{
	private readonly byte[] _pixels;

	public MediaIoRgbaFrame(VideoFormat format, ReadOnlySpan<byte> pixels)
	{
		if (format.PixelFormat != PixelFormat.Rgba8)
			throw new ArgumentException("Media I/O RGBA frames require the Rgba8 pixel format.", nameof(format));
		var expectedLength = RequiredByteLength(format);
		if (pixels.Length != expectedLength)
			throw new ArgumentException($"Media I/O RGBA frame requires exactly '{expectedLength}' bytes.", nameof(pixels));
		Format = format;
		_pixels = pixels.ToArray();
	}

	public VideoFormat Format { get; }
	public ReadOnlyMemory<byte> Pixels => _pixels;

	public static int RequiredByteLength(VideoFormat format) =>
		checked((int)(format.Width * format.Height * 4u));
}

public sealed record MediaIoCapturedInput(
	MediaSourceId RuntimeSourceId,
	MediaIoPortId PortId,
	ulong CaptureSequence,
	MediaIoRgbaFrame Video,
	float[]? AudioSamples,
	AudioBufferTiming? AudioTiming,
	MediaIoPortStatus PortStatus);

public readonly record struct MediaIoVerticalSliceStatistics(
	ulong CapturedA,
	ulong CapturedB,
	ulong CaptureWouldBlock,
	ulong CaptureFailures,
	ulong OutputAccepted,
	ulong OutputBackpressure,
	ulong OutputRejected);

/// <summary>
/// AP-33 orchestration over the vendor-neutral Media I/O seam. It owns exactly two input sessions and one Program
/// output session. There are no unbounded media queues: each capture lease is copied into the current runtime
/// working frame and released before another acquire; output is submitted synchronously and backpressure is surfaced.
/// </summary>
public sealed class MediaIoVerticalSlice : IDisposable
{
	private readonly object _gate = new();
	private readonly IMediaIoProviderAdapter _adapter;
	private readonly IMediaIoInputSession _inputA;
	private readonly IMediaIoInputSession _inputB;
	private readonly IMediaIoOutputSession _output;
	private readonly MediaSourceId _sourceAId;
	private readonly MediaSourceId _sourceBId;
	private readonly VideoFormat _format;
	private MediaIoCapturedInput? _latestA;
	private MediaIoCapturedInput? _latestB;
	private ulong _capturedA;
	private ulong _capturedB;
	private ulong _captureWouldBlock;
	private ulong _captureFailures;
	private ulong _outputAccepted;
	private ulong _outputBackpressure;
	private ulong _outputRejected;
	private bool _disposed;

	public MediaIoVerticalSlice(
		IMediaIoProviderAdapter adapter,
		MediaSourceId sourceAId,
		MediaSourceId sourceBId,
		VideoFormat format,
		bool requireExternalReference = false)
	{
		_adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
		_sourceAId = sourceAId;
		_sourceBId = sourceBId;
		_format = format;

		var inputs = adapter.Descriptor.Ports
			.Where(port => port.Direction == MediaIoDirection.Input)
			.OrderBy(port => port.PortId.ToString(), StringComparer.Ordinal)
			.Take(2)
			.ToArray();
		var outputs = adapter.Descriptor.Ports
			.Where(port => port.Direction == MediaIoDirection.Output)
			.OrderBy(port => port.PortId.ToString(), StringComparer.Ordinal)
			.Take(1)
			.ToArray();
		if (inputs.Length != 2 || outputs.Length != 1)
			throw new InvalidOperationException("AP-33 Media I/O requires exactly two usable input ports and at least one Program output port.");

		var providerId = adapter.Descriptor.Provider.ProviderId;
		_inputA = adapter.OpenInput(CreateRequest(providerId, inputs[0], MediaIoDirection.Input, format, requireExternalReference: false));
		try
		{
			_inputB = adapter.OpenInput(CreateRequest(providerId, inputs[1], MediaIoDirection.Input, format, requireExternalReference: false));
			try
			{
				_output = adapter.OpenOutput(CreateRequest(providerId, outputs[0], MediaIoDirection.Output, format, requireExternalReference));
			}
			catch
			{
				_inputB.Dispose();
				throw;
			}
		}
		catch
		{
			_inputA.Dispose();
			throw;
		}
	}

	public MediaIoPortDescriptor InputAPort => _inputA.Port;
	public MediaIoPortDescriptor InputBPort => _inputB.Port;
	public MediaIoPortDescriptor ProgramOutputPort => _output.Port;
	public MediaIoPortStatus InputAStatus => _inputA.Status;
	public MediaIoPortStatus InputBStatus => _inputB.Status;
	public MediaIoPortStatus ProgramOutputStatus => _output.Status;

	public MediaIoCapturedInput? LatestA
	{
		get { lock (_gate) return _latestA; }
	}

	public MediaIoCapturedInput? LatestB
	{
		get { lock (_gate) return _latestB; }
	}

	public MediaIoVerticalSliceStatistics Statistics
	{
		get
		{
			lock (_gate)
			{
				return new MediaIoVerticalSliceStatistics(
					_capturedA,
					_capturedB,
					_captureWouldBlock,
					_captureFailures,
					_outputAccepted,
					_outputBackpressure,
					_outputRejected);
			}
		}
	}

	/// <summary>
	/// Tries each input once. The provider lease is always released before this method returns.
	/// </summary>
	public bool PumpInputs()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			var captured = false;
			captured |= TryCapture(_inputA, _sourceAId, isSourceA: true);
			captured |= TryCapture(_inputB, _sourceBId, isSourceA: false);
			return captured;
		}
	}

	/// <summary>
	/// Submits one Program frame. The pinned handles exist only for the duration of the synchronous provider call.
	/// A WOULD_BLOCK result is surfaced as output backpressure and never queued in managed memory.
	/// </summary>
	public MediaIoOutputSubmitResult TrySubmitProgram(
		MediaSourceId programSourceId,
		FrameTiming timing,
		byte[] rgbaPixels,
		float[]? audioSamples = null,
		AudioBufferTiming? audioTiming = null,
		AudioGain? gain = null,
		bool muted = false)
	{
		ArgumentNullException.ThrowIfNull(rgbaPixels);
		if (rgbaPixels.Length != MediaIoRgbaFrame.RequiredByteLength(_format))
			throw new ArgumentException("Program RGBA payload does not match the configured Media I/O format.", nameof(rgbaPixels));
		if ((audioSamples is null) != (audioTiming is null))
			throw new ArgumentException("Program audio samples and timing must either both be supplied or both be omitted.");

		lock (_gate)
		{
			ThrowIfDisposed();
			var adjustedAudio = audioSamples is null ? null : ApplyAudioState(audioSamples, gain ?? AudioGain.Unity, muted);
			var videoPin = GCHandle.Alloc(rgbaPixels, GCHandleType.Pinned);
			GCHandle audioPin = default;
			try
			{
				var surfaceId = SurfaceId.New();
				var videoAddress = checked((ulong)videoPin.AddrOfPinnedObject().ToInt64());
				var surface = new SurfaceDescriptor(
					surfaceId,
					_format,
					SurfaceStorageDomain.Host,
					SurfaceOwnership.ProducerOwned,
					new SurfaceLifetimeDescriptor(new Generation(timing.SequenceNumber), null),
					new OpaqueSurfaceHandle(PinnedHostMediaIoMemory.VideoHandleKind, videoAddress.ToString(CultureInfo.InvariantCulture)));
				var video = new FrameDescriptor(MediaContractVersion.Current, programSourceId, surface, timing);

				AudioBufferDescriptor? audio = null;
				if (adjustedAudio is not null && audioTiming is { } bufferTiming)
				{
					var expectedValues = checked((int)(bufferTiming.SampleCount * AudioFormat.Stereo48kFloat32.ChannelCount));
					if (adjustedAudio.Length != expectedValues)
						throw new ArgumentException("Program audio payload does not match its sample count.", nameof(audioSamples));
					audioPin = GCHandle.Alloc(adjustedAudio, GCHandleType.Pinned);
					var audioAddress = checked((ulong)audioPin.AddrOfPinnedObject().ToInt64());
					var timingDomain = Identity.New();
					audio = new AudioBufferDescriptor(
						MediaContractVersion.Current,
						AudioStreamId.New(),
						AudioFormat.Stereo48kFloat32,
						timingDomain,
						bufferTiming,
						new OpaqueAudioHandle(PinnedHostMediaIoMemory.AudioHandleKind, audioAddress.ToString(CultureInfo.InvariantCulture)));
				}

				var result = _output.TrySubmit(new MediaIoOutputFrameDescriptor(
					MediaIoContractVersion.Current,
					_output.Port.PortId,
					video,
					audio));
				if (result.Accepted)
					_outputAccepted++;
				else if (string.Equals(result.Failure?.Code, "media.io.output.backpressure", StringComparison.Ordinal))
					_outputBackpressure++;
				else
					_outputRejected++;
				return result;
			}
			finally
			{
				if (audioPin.IsAllocated) audioPin.Free();
				videoPin.Free();
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_output.Dispose();
			_inputB.Dispose();
			_inputA.Dispose();
			_adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
			_disposed = true;
		}
	}

	private bool TryCapture(IMediaIoInputSession session, MediaSourceId runtimeSourceId, bool isSourceA)
	{
		try
		{
			if (!session.TryAcquire(out var lease) || lease is null)
			{
				_captureWouldBlock++;
				return false;
			}

			using (lease)
			{
				var video = PinnedHostMediaIoMemory.CopyRgba(lease);
				float[]? audio = null;
				AudioBufferTiming? audioTiming = null;
				if (lease.Descriptor.EmbeddedAudio is { } embeddedAudio)
				{
					audio = PinnedHostMediaIoMemory.CopyStereoFloat32(embeddedAudio);
					audioTiming = embeddedAudio.Timing;
				}

				var capture = new MediaIoCapturedInput(
					runtimeSourceId,
					session.Port.PortId,
					lease.Descriptor.Video.Timing.SequenceNumber,
					video,
					audio,
					audioTiming,
					session.Status);
				if (isSourceA)
				{
					_latestA = capture;
					_capturedA++;
				}
				else
				{
					_latestB = capture;
					_capturedB++;
				}
			}
			return true;
		}
		catch
		{
			_captureFailures++;
			throw;
		}
	}

	private static MediaIoSessionRequest CreateRequest(
		ProviderId providerId,
		MediaIoPortDescriptor port,
		MediaIoDirection direction,
		VideoFormat format,
		bool requireExternalReference) =>
		new(
			MediaIoContractVersion.Current,
			providerId,
			port.PortId,
			direction,
			format,
			MediaIoTransferMode.PinnedHostLease,
			AudioFormat.Stereo48kFloat32,
			requireExternalReference);

	private static float[] ApplyAudioState(float[] samples, AudioGain gain, bool muted)
	{
		var output = new float[samples.Length];
		if (muted || gain.Linear == 0)
			return output;
		for (var index = 0; index < samples.Length; index++)
			output[index] = Math.Clamp((float)(samples[index] * gain.Linear), -1f, 1f);
		return output;
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
