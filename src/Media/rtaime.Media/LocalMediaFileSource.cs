// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
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

	internal LocalMediaFileSource(string path, LocalMediaProbe probe, ILocalMediaDecoder decoder)
	{
		Path = path ?? throw new ArgumentNullException(nameof(path));
		Probe = probe ?? throw new ArgumentNullException(nameof(probe));
		_decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
	}

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
				return _decoder.TryReadNext(sequenceNumber, out var frame)
					? LocalMediaFrameReadResult.Decoded(frame!)
					: LocalMediaFrameReadResult.Ended();
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or ExternalException)
			{
				lastFailure = exception;
			}
		}

		return LocalMediaFrameReadResult.Failed(
			"media.file.decode_failed",
			$"Local media decoding failed after a bounded retry: {lastFailure?.Message ?? "unknown decoder failure"}");
	}

	public LocalMediaSeekResult SeekToFrame(long frameNumber)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _decoder.SeekToFrame(frameNumber);
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
		var open = WindowsMediaFoundationLocalMediaDecoder.TryOpen(fullPath, resolvedAssetId, sourceId, _outputFormat);
		if (!open.Succeeded)
			return LocalMediaOpenResult.Rejected(open.Failure!.Value.Code, open.Failure.Value.Message);

		var decoder = open.Decoder!;
		return LocalMediaOpenResult.Ready(new LocalMediaFileSource(fullPath, decoder.Probe, decoder));
	}
}

internal interface ILocalMediaDecoder : IDisposable
{
	LocalMediaProbe Probe { get; }
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
