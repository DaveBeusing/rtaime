// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.RuntimeHost;

/// <summary>
/// Materializes a pinned encoded replay range into one ordinary MP4 clip by decoding the retained
/// MP4 segments through the existing local-media provider and re-encoding through the existing
/// Media Foundation recording writer. This is non-realtime work and never feeds Program directly.
/// </summary>
public sealed class ReplayClipMaterializer
{
	private readonly RollingReplaySegmentStore _store;
	private readonly string _outputDirectory;
	private readonly VideoFormat _outputFormat;

	public ReplayClipMaterializer(
		RollingReplaySegmentStore store,
		string outputDirectory,
		VideoFormat outputFormat)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		if (string.IsNullOrWhiteSpace(outputDirectory))
			throw new ArgumentException("Replay clip output directory is required.", nameof(outputDirectory));
		_outputDirectory = Path.GetFullPath(outputDirectory);
		_outputFormat = outputFormat;
	}

	public async ValueTask<ReplayClipResult> MaterializeAsync(
		ReplayClipRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return await Task.Run(
			() => MaterializeCoreAsync(request, cancellationToken),
			cancellationToken).Unwrap().ConfigureAwait(false);
	}

	private async Task<ReplayClipResult> MaterializeCoreAsync(
		ReplayClipRequest request,
		CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows())
		{
			return Failed(
				request,
				"replay.clip.platform_unsupported",
				"Replay clip materialization requires Windows Media Foundation.");
		}

		RollingReplaySegmentStore.ReplaySegmentPin? pin = null;
		IReplaySegmentWriter? writer = null;
		string? finalPath = null;
		try
		{
			pin = _store.PinRange(request.Range);
			Directory.CreateDirectory(_outputDirectory);

			writer = new WindowsMediaFoundationMp4RecordingWriter(_outputDirectory);
			var fileName = BuildFileName(request);
			writer.ConfigureTarget(_outputDirectory, fileName);
			var outputId = new RecordingOutputId(request.ClipId.Value);
			var sinkId = new MediaSinkId(Identity.New());
			await writer.OpenAsync(
				new RecordingStartRequest(
					RecordingContractVersion.Current,
					RecordingSessionId.New(),
					new RecordingOutputDescriptor(outputId, sinkId, request.Name)),
				cancellationToken).ConfigureAwait(false);

			var provider = new LocalMediaFileProvider(_outputFormat);
			ulong outputSequence = 0;
			ulong audioSamplePosition = 0;
			var wroteAny = false;

			foreach (var segment in pin.Segments)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sourceId = new MediaSourceId(Identity.New());
				var assetId = new MediaAssetId(segment.SegmentId.Value);
				var open = provider.TryOpen(segment.Path, sourceId, assetId);
				if (!open.Succeeded || open.Source is null)
					throw new InvalidDataException(open.Failure?.Message ?? $"Replay segment '{segment.SegmentId}' could not be decoded.");

				using var source = open.Source;
				if (source.Probe.VideoFormat != _outputFormat)
					throw new InvalidDataException("Replay segment format does not match the active Runtime Program format.");
				if (source.Probe.AudioCodec == MediaAudioCodec.None)
					throw new InvalidDataException("Replay segment has no Program audio.");

				var localIn = request.Range.In > segment.Start ? request.Range.In - segment.Start : TimeSpan.Zero;
				var localOut = request.Range.Out < segment.End ? request.Range.Out - segment.Start : segment.Duration;
				var fps = source.Probe.VideoFormat.FrameRate.Numerator /
					(double)source.Probe.VideoFormat.FrameRate.Denominator;
				var totalFrames = Math.Max(1L, (long)Math.Ceiling(source.Probe.Duration.TotalSeconds * fps));
				var startFrame = Math.Clamp(
					(long)Math.Ceiling((localIn.TotalSeconds * fps) - 1e-9),
					0,
					totalFrames - 1);
				var endExclusive = Math.Clamp(
					(long)Math.Ceiling((localOut.TotalSeconds * fps) - 1e-9),
					startFrame + 1,
					totalFrames);

				var seek = source.SeekToFrame(startFrame);
				if (!seek.Succeeded)
					throw new InvalidDataException(seek.Failure?.Message ?? "Replay segment seek failed.");

				for (var frameIndex = startFrame; frameIndex < endExclusive; frameIndex++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var decoded = source.ReadNext(outputSequence);
					if (!decoded.Succeeded || decoded.Frame is null)
					{
						if (decoded.Status == LocalMediaFrameReadStatus.Ended)
							break;
						throw new InvalidDataException(decoded.Failure?.Message ?? "Replay segment decode failed.");
					}

					var frame = decoded.Frame;
					if (frame.Audio is null || frame.AudioPayload.IsEmpty)
						throw new InvalidDataException("Replay clip requires final mixed Program audio on every materialized boundary.");

					var frameRate = frame.Video.Surface.Format.FrameRate;
					var rebasedVideo = new FrameDescriptor(
						MediaContractVersion.Current,
						frame.Video.SourceId,
						frame.Video.Surface,
						new FrameTiming(
							outputSequence,
							checked((long)outputSequence),
							new Timebase(frameRate.Denominator, frameRate.Numerator)));

					var audio = frame.Audio;
					var rebasedAudio = new AudioBufferDescriptor(
						MediaContractVersion.Current,
						audio.StreamId,
						audio.Format,
						audio.TimingDomainId,
						new AudioBufferTiming(
							audioSamplePosition,
							audio.Timing.SampleCount,
							checked((long)audioSamplePosition),
							new Timebase(1, audio.Format.SampleRate)),
						audio.Handle);

					var sample = new RecordingProgramSample(
						RecordingContractVersion.Current,
						outputId,
						rebasedVideo,
						rebasedAudio);
					var lease = new ReplayBytePayloadLease(frame.RgbaPixels.ToArray());
					var staged = false;
					try
					{
						writer.StagePayload(outputSequence, lease, frame.AudioPayload.ToArray());
						staged = true;
						await writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
					}
					finally
					{
						if (!staged)
							lease.Dispose();
					}

					audioSamplePosition = checked(audioSamplePosition + audio.Timing.SampleCount);
					outputSequence++;
					wroteAny = true;
				}
			}

			if (!wroteAny)
				throw new InvalidDataException("Replay selection did not materialize any complete Program frames.");

			await writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);
			finalPath = writer.FinalPath
				?? throw new InvalidDataException("Replay clip writer finalized without a published path.");

			var probe = new LocalMediaFileProvider(_outputFormat).TryOpen(
				finalPath,
				new MediaSourceId(Identity.New()),
				new MediaAssetId(request.ClipId.Value));
			if (!probe.Succeeded || probe.Source is null)
				throw new InvalidDataException(probe.Failure?.Message ?? "Finalized replay clip failed media probing.");
			probe.Source.Dispose();

			await using var stream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
			return new ReplayClipResult(
				request.ClipId,
				finalPath,
				request.Range,
				null,
				Convert.ToHexString(hash),
				true,
				null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (writer is not null)
				await AbortQuietlyAsync(writer).ConfigureAwait(false);
			if (finalPath is not null)
				TryDelete(finalPath);
			throw;
		}
		catch (Exception exception) when (
			exception is IOException or
			UnauthorizedAccessException or
			InvalidDataException or
			InvalidOperationException or
			ArgumentException or
			ExternalException)
		{
			if (writer is not null)
				await AbortQuietlyAsync(writer).ConfigureAwait(false);
			if (finalPath is not null)
				TryDelete(finalPath);
			return Failed(request, "replay.clip.materialization_failed", exception.Message);
		}
		finally
		{
			pin?.Dispose();
		}
	}

	private static string BuildFileName(ReplayClipRequest request)
	{
		var builder = new StringBuilder(request.Name.Length);
		foreach (var character in request.Name)
		{
			if (char.IsLetterOrDigit(character) || character is '-' or '_')
				builder.Append(character);
			else if (char.IsWhiteSpace(character))
				builder.Append('-');
		}

		var safe = builder.ToString().Trim('-');
		if (safe.Length == 0)
			safe = "replay";
		if (safe.Length > 48)
			safe = safe[..48];
		return $"{safe}-{request.ClipId}.mp4";
	}

	private static ReplayClipResult Failed(ReplayClipRequest request, string code, string message) =>
		new(request.ClipId, string.Empty, request.Range, null, string.Empty, false, new Failure(code, message));

	private static async ValueTask AbortQuietlyAsync(IProgramRecordingWriter writer)
	{
		try { await writer.AbortAsync(CancellationToken.None).ConfigureAwait(false); }
		catch { }
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private sealed class ReplayBytePayloadLease : IProgramRecordingPayloadLease
	{
		private byte[]? _payload;

		public ReplayBytePayloadLease(byte[] payload) =>
			_payload = payload ?? throw new ArgumentNullException(nameof(payload));

		public ReadOnlyMemory<byte> Memory =>
			Volatile.Read(ref _payload) ?? throw new ObjectDisposedException(nameof(ReplayBytePayloadLease));

		public void Dispose() => Interlocked.Exchange(ref _payload, null);
	}
}
