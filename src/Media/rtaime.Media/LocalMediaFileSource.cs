// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Media;

public static class LocalMediaCapabilityKinds
{
	public const string Decode = "media.file.decode";
	public const string MediaRoute = "media.route";
}

public enum LocalMediaOpenStatus
{
	Ready = 1,
	Rejected = 2
}

public sealed record LocalMediaOpenResult(
	LocalMediaOpenStatus Status,
	LocalMediaFileSource? Source,
	Failure? Failure)
{
	public bool Succeeded => Status == LocalMediaOpenStatus.Ready && Source is not null;

	public static LocalMediaOpenResult Ready(LocalMediaFileSource source) =>
		new(LocalMediaOpenStatus.Ready, source ?? throw new ArgumentNullException(nameof(source)), null);

	public static LocalMediaOpenResult Rejected(string code, string message) =>
		new(LocalMediaOpenStatus.Rejected, null, new Failure(code, message));
}

public enum LocalMediaFrameReadStatus
{
	Frame = 1,
	Ended = 2,
	Failed = 3
}

public sealed record LocalMediaFrameReadResult(
	LocalMediaFrameReadStatus Status,
	LocalMediaDecodedFrame? Frame,
	Failure? Failure)
{
	public bool Succeeded => Status == LocalMediaFrameReadStatus.Frame && Frame is not null;

	public static LocalMediaFrameReadResult Decoded(LocalMediaDecodedFrame frame) =>
		new(LocalMediaFrameReadStatus.Frame, frame ?? throw new ArgumentNullException(nameof(frame)), null);

	public static LocalMediaFrameReadResult Ended() =>
		new(LocalMediaFrameReadStatus.Ended, null, null);

	public static LocalMediaFrameReadResult Failed(string code, string message) =>
		new(LocalMediaFrameReadStatus.Failed, null, new Failure(code, message));
}

public sealed record LocalMediaSeekResult(long TargetFrame, Failure? Failure)
{
	public bool Succeeded => Failure is null;

	public static LocalMediaSeekResult Positioned(long targetFrame) => new(targetFrame, null);

	public static LocalMediaSeekResult Rejected(long targetFrame, string code, string message) =>
		new(targetFrame, new Failure(code, message));
}

public sealed class LocalMediaFileSource : IDisposable
{
	private readonly ILocalMediaDecoder _decoder;
	private bool _disposed;
    private LocalMediaPlaybackMeasurements _measurements;
    private readonly long _openedAt = Stopwatch.GetTimestamp();
    private long? _previousVideoTimestamp;
    private double? _expectedAudioSeconds;

	internal LocalMediaFileSource(string path, LocalMediaProbe probe, ILocalMediaDecoder decoder, double openMilliseconds = 0)
	{
		Path = path ?? throw new ArgumentNullException(nameof(path));
		Probe = probe ?? throw new ArgumentNullException(nameof(probe));
		_decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _measurements = new LocalMediaPlaybackMeasurements { OpenMilliseconds = openMilliseconds };
	}

	public LocalMediaPlaybackMeasurements Measurements => _measurements with {
        NativeVideoRead = _decoder.VideoReadMeasurements, NativeAudioRead = _decoder.AudioReadMeasurements };

	public string Path { get; }
	public LocalMediaProbe Probe { get; }
	public MediaAssetId AssetId => Probe.AssetId;
	public MediaSourceId SourceId => Probe.SourceId;

	public LocalMediaFrameReadResult ReadNext(ulong sequenceNumber)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Exception? lastFailure = null;
		for (var attempt = 0; attempt < 2; attempt++)
		{
			try
			{
                var started = Stopwatch.GetTimestamp();
                bool available;
                LocalMediaDecodedFrame? frame;
                try { available = _decoder.TryReadNext(sequenceNumber, out frame); }
                finally { _measurements = _measurements with { Decode = _measurements.Decode.Observe(Stopwatch.GetElapsedTime(started).TotalMilliseconds) }; }
                if (!available) return LocalMediaFrameReadResult.Ended();
                var media = frame!;
                var audioSamples = media.Audio?.Timing.SampleCount ?? 0;
                var videoSeconds = media.Video.Timing.PresentationTimestamp * media.Video.Timing.Timebase.SecondsPerTick;
                var audioSeconds = media.Audio is { } audio
                    ? audio.Timing.PresentationTimestamp * audio.Timing.Timebase.SecondsPerTick : (double?)null;
                _measurements = _measurements with {
                    FirstFrameMilliseconds = _measurements.FirstFrameMilliseconds ??
                        _measurements.OpenMilliseconds + Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds,
                    DecodedFrames = _measurements.DecodedFrames + 1,
                    FirstVideoTimestamp = _measurements.FirstVideoTimestamp ?? media.Video.Timing.PresentationTimestamp,
                    LastVideoTimestamp = media.Video.Timing.PresentationTimestamp,
                    VideoSecondsPerTick = media.Video.Timing.Timebase.SecondsPerTick,
                    VideoForwardGapCount = _measurements.VideoForwardGapCount +
                        (_previousVideoTimestamp is { } prior && videoSeconds -
                            prior * media.Video.Timing.Timebase.SecondsPerTick >
                            1.5 * Probe.VideoFormat.FrameRate.Denominator / (double)Probe.VideoFormat.FrameRate.Numerator ? 1UL : 0),
                    MaximumVideoIntervalMilliseconds = Math.Max(_measurements.MaximumVideoIntervalMilliseconds,
                        _previousVideoTimestamp is { } last ?
                            Math.Max(0, videoSeconds - last * media.Video.Timing.Timebase.SecondsPerTick) * 1000 : 0),
                    DecodedAudioSamples = _measurements.DecodedAudioSamples + audioSamples,
                    MissingAudioFrames = _measurements.MissingAudioFrames + (Probe.AudioCodec != MediaAudioCodec.None && audioSeconds is null ? 1UL : 0),
                    VideoTimestampRegressions = _measurements.VideoTimestampRegressions +
                        (_previousVideoTimestamp is { } previous && media.Video.Timing.PresentationTimestamp <= previous ? 1UL : 0),
                    AudioTimestampDiscontinuities = _measurements.AudioTimestampDiscontinuities +
                        (audioSeconds is { } observed && _expectedAudioSeconds is { } expected && Math.Abs(observed - expected) > 0.002 ? 1UL : 0),
                    MaximumAvTimestampDistanceMilliseconds = Math.Max(_measurements.MaximumAvTimestampDistanceMilliseconds,
                        audioSeconds is { } seconds ? Math.Abs(videoSeconds - seconds) * 1000 : 0)
                };
                _previousVideoTimestamp = media.Video.Timing.PresentationTimestamp;
                if (audioSeconds is { } at) _expectedAudioSeconds = at + audioSamples / (double)Probe.AudioFormat.SampleRate;
                return LocalMediaFrameReadResult.Decoded(media);
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or ExternalException)
			{
				lastFailure = exception;
                _measurements = _measurements with { ReadRetries = _measurements.ReadRetries + (attempt == 0 ? 1UL : 0) };
			}
		}

        _measurements = _measurements with { ReadFailures = _measurements.ReadFailures + 1 };
		return LocalMediaFrameReadResult.Failed(
			"media.file.decode_failed",
			$"Local media decoding failed after a bounded retry: {lastFailure?.Message ?? "unknown decoder failure"}");
	}

	public LocalMediaSeekResult SeekToFrame(long frameNumber)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var started = Stopwatch.GetTimestamp();
        try
        {
            var result = _decoder.SeekToFrame(frameNumber);
            if (result.Succeeded) { _previousVideoTimestamp = null; _expectedAudioSeconds = null; }
            return result;
        }
        finally { _measurements = _measurements with { Seek = _measurements.Seek.Observe(Stopwatch.GetElapsedTime(started).TotalMilliseconds) }; }
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_decoder.Dispose();
	}
}

public sealed class LocalMediaFileProvider
{
	private const int RouteResourceCount = 2;
	private const int DecodeResourceCount = 4;
	private static readonly VideoFormat[] SupportedVideoFormats =
	{
		VideoFormat.Hd1080p50Rgba8,
		VideoFormat.Hd1080p59_94Rgba8
	};
	private readonly VideoFormat _outputFormat;

	public LocalMediaFileProvider()
		: this(VideoFormat.Hd1080p50Rgba8)
	{
	}

	public LocalMediaFileProvider(VideoFormat outputFormat)
	{
		if (!SupportedVideoFormats.Contains(outputFormat))
			throw new ArgumentException("Local media output format must be 1080p50 or 1080p59.94 RGBA8.", nameof(outputFormat));
		_outputFormat = outputFormat;
		var providerId = new ProviderId(LocalMediaIdentity.Create("provider", "local-media-file"));
		var availability = OperatingSystem.IsWindows()
			? new ProviderAvailability(ProviderAvailabilityState.Available)
			: new ProviderAvailability(
				ProviderAvailabilityState.Unavailable,
				new Failure("media.file.platform_unsupported", "The V1 local media provider requires Windows Media Foundation."));

		var capabilities = new[]
		{
			new ProviderCapabilityDescriptor(
				new CapabilityId(LocalMediaIdentity.Create("capability", "local-media-file", LocalMediaCapabilityKinds.MediaRoute)),
				LocalMediaCapabilityKinds.MediaRoute,
				SupportedVideoFormats),
			new ProviderCapabilityDescriptor(
				new CapabilityId(LocalMediaIdentity.Create("capability", "local-media-file", LocalMediaCapabilityKinds.Decode)),
				LocalMediaCapabilityKinds.Decode,
				SupportedVideoFormats)
		};

		var resources = Enumerable.Range(0, RouteResourceCount)
			.Select(index => new ProviderResourceDescriptor(
				new ProviderResourceId(LocalMediaIdentity.Create("resource", "local-media-file", LocalMediaCapabilityKinds.MediaRoute, index.ToString())),
				providerId,
				LocalMediaCapabilityKinds.MediaRoute,
				1,
				true))
			.Concat(Enumerable.Range(0, DecodeResourceCount)
				.Select(index => new ProviderResourceDescriptor(
					new ProviderResourceId(LocalMediaIdentity.Create("resource", "local-media-file", LocalMediaCapabilityKinds.Decode, index.ToString())),
					providerId,
					LocalMediaCapabilityKinds.Decode,
					1,
					true)))
			.ToArray();

		Descriptor = new ProviderDescriptor(
			ProviderContractVersion.Current,
			providerId,
			"Windows Media Foundation Local File",
			availability,
			capabilities,
			resources);
	}

	public ProviderDescriptor Descriptor { get; }
	public IReadOnlyList<VideoFormat> VideoFormats => new ReadOnlyCollection<VideoFormat>(SupportedVideoFormats);

	public LocalMediaOpenResult TryOpen(
		string path,
		MediaSourceId sourceId,
		MediaAssetId? assetId = null)
	{
		if (string.IsNullOrWhiteSpace(path))
			return LocalMediaOpenResult.Rejected("media.file.path_required", "A local media file path is required.");

		string fullPath;
		try
		{
			fullPath = System.IO.Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return LocalMediaOpenResult.Rejected("media.file.path_invalid", $"The local media file path is invalid: {exception.Message}");
		}

		if (!File.Exists(fullPath))
			return LocalMediaOpenResult.Rejected("media.file.not_found", "The local media file does not exist.");
		if (!string.Equals(System.IO.Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
			return LocalMediaOpenResult.Rejected("media.file.container_unsupported", "V1 local media supports MP4 containers only.");
		if (!OperatingSystem.IsWindows())
			return LocalMediaOpenResult.Rejected("media.file.platform_unsupported", "The V1 local media provider requires Windows Media Foundation.");

		try
		{
			using (new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
			}
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			return LocalMediaOpenResult.Rejected("media.file.unreadable", $"The local media file is not readable: {exception.Message}");
		}

		var resolvedAssetId = assetId ?? new MediaAssetId(LocalMediaIdentity.Create("asset", fullPath));
		var openStarted = Stopwatch.GetTimestamp();
		var open = WindowsMediaFoundationLocalMediaDecoder.TryOpen(fullPath, resolvedAssetId, sourceId, _outputFormat);
		if (!open.Succeeded)
			return LocalMediaOpenResult.Rejected(open.Failure!.Value.Code, open.Failure.Value.Message);

		var decoder = open.Decoder!;
		return LocalMediaOpenResult.Ready(new LocalMediaFileSource(fullPath, decoder.Probe, decoder, Stopwatch.GetElapsedTime(openStarted).TotalMilliseconds));
	}
}

internal interface ILocalMediaDecoder : IDisposable
{
	LocalMediaProbe Probe { get; }
    LocalMediaReadMeasurements VideoReadMeasurements => default;
    LocalMediaReadMeasurements AudioReadMeasurements => default;
	bool TryReadNext(ulong sequenceNumber, out LocalMediaDecodedFrame? frame);
	LocalMediaSeekResult SeekToFrame(long frameNumber);
}

internal readonly record struct LocalMediaDecoderOpenResult(
	ILocalMediaDecoder? Decoder,
	Failure? Failure)
{
	public bool Succeeded => Decoder is not null && Failure is null;

	public static LocalMediaDecoderOpenResult Ready(ILocalMediaDecoder decoder) => new(decoder, null);
	public static LocalMediaDecoderOpenResult Rejected(string code, string message) => new(null, new Failure(code, message));
}

internal static class LocalMediaIdentity
{
	public static Identity Create(string scope, params string[] parts)
	{
		var canonical = string.Join('\u001f', new[] { scope }.Concat(parts));
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return new Identity(new Guid(hash.AsSpan(0, 16)));
	}
}
