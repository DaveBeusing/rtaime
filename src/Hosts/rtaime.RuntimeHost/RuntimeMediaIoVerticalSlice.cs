// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

/// <summary>
/// Non-authoritative RuntimeHost bridge between physical Media I/O and the existing V1 execution/compositor.
/// It changes only current media working content; production routing and transition authority remain unchanged.
/// </summary>
internal sealed class RuntimeMediaIoVerticalSlice : IDisposable
{
	private readonly V1RuntimeHostService _runtime;
	private readonly MediaIoVerticalSlice _mediaIo;
	private ulong? _sourceASequence;
	private ulong? _sourceBSequence;
	private bool _disposed;

	public RuntimeMediaIoVerticalSlice(
		V1RuntimeHostService runtime,
		IMediaIoProviderAdapter adapter,
		MediaSourceId sourceAId,
		MediaSourceId sourceBId,
		bool requireExternalReference)
	{
		_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
		_mediaIo = new MediaIoVerticalSlice(
			adapter ?? throw new ArgumentNullException(nameof(adapter)),
			sourceAId,
			sourceBId,
			runtime.Format,
			requireExternalReference);
	}

	public MediaIoVerticalSliceStatistics Statistics => _mediaIo.Statistics;
	public MediaIoPortStatus InputAStatus => _mediaIo.InputAStatus;
	public MediaIoPortStatus InputBStatus => _mediaIo.InputBStatus;
	public MediaIoPortStatus ProgramOutputStatus => _mediaIo.ProgramOutputStatus;

	public void PumpInputs()
	{
		ThrowIfDisposed();
		_mediaIo.PumpInputs();
		ApplyLatest(_mediaIo.LatestA, ref _sourceASequence);
		ApplyLatest(_mediaIo.LatestB, ref _sourceBSequence);
	}

	public void SubmitProgram(V1ProgramBoundaryResult boundary)
	{
		ArgumentNullException.ThrowIfNull(boundary);
		ThrowIfDisposed();

		float[]? audioSamples = null;
		AudioBufferTiming? audioTiming = null;
		if (boundary.ProgramAudioPayload.Length > 0)
		{
			audioSamples = MemoryMarshal.Cast<byte, float>(boundary.ProgramAudioPayload.AsSpan()).ToArray();
			audioTiming = boundary.ProgramAudioBuffer.Timing;
		}
		var result = _mediaIo.TrySubmitProgram(
			boundary.CommittedProgramSourceId,
			boundary.ProgramFrame.Timing,
			boundary.ProgramPixels,
			audioSamples,
			audioTiming,
			AudioGain.Unity,
			muted: false);

		if (!result.Accepted && !string.Equals(result.Failure?.Code, "media.io.output.backpressure", StringComparison.Ordinal))
			throw new InvalidOperationException(result.Failure?.Message ?? "Physical Program output rejected the frame.");
	}

	public void Dispose()
	{
		if (_disposed) return;
		_mediaIo.Dispose();
		_disposed = true;
	}

	private void ApplyLatest(MediaIoCapturedInput? capture, ref ulong? appliedSequence)
	{
		if (capture is null || appliedSequence == capture.CaptureSequence)
			return;

		var gpuInput = new RgbaFrameBuffer(capture.Video.Format, capture.Video.Pixels.Span);
		_runtime.SetExternalInputContent(
			capture.RuntimeSourceId,
			gpuInput,
			MapSignal(capture.PortStatus.SignalState));
		if (capture.AudioSamples is { Length: >= 2 } audioSamples)
		{
			_runtime.SetExternalAudioInput(
				capture.RuntimeSourceId,
				audioSamples,
				available: true);
		}
		else
		{
			_runtime.SetExternalAudioMeter(capture.RuntimeSourceId, 0, 0, available: false);
		}
		appliedSequence = capture.CaptureSequence;
	}

	private static V1InputSignalState MapSignal(MediaIoSignalState state) => state switch
	{
		MediaIoSignalState.Locked => V1InputSignalState.Valid,
		MediaIoSignalState.Unstable => V1InputSignalState.Unstable,
		MediaIoSignalState.Lost or MediaIoSignalState.Faulted => V1InputSignalState.Lost,
		_ => V1InputSignalState.Recovering
	};

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
