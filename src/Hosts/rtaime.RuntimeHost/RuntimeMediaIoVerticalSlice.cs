// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media;
using rtaime.Media.Contracts;

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

		var followedCapture = boundary.CommittedProgramSourceId == _mediaIo.LatestA?.RuntimeSourceId
			? _mediaIo.LatestA
			: boundary.CommittedProgramSourceId == _mediaIo.LatestB?.RuntimeSourceId
				? _mediaIo.LatestB
				: null;
		var audioSamples = followedCapture?.AudioSamples;
		var audioTiming = followedCapture?.AudioTiming;
		var result = _mediaIo.TrySubmitProgram(
			boundary.CommittedProgramSourceId,
			boundary.ProgramFrame.Timing,
			boundary.ProgramPixels,
			audioSamples,
			audioTiming,
			boundary.Audio.Gain,
			boundary.Audio.Muted);

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
		_runtime.SetExternalInputContent(
			capture.RuntimeSourceId,
			capture.Video,
			MapSignal(capture.PortStatus.SignalState));
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
