// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Core;

namespace rtaime.Media.Contracts;

public enum MediaAssetOriginKind
{
	LocalFile = 1
}

public enum MediaAssetAvailability
{
	Online = 1,
	Offline = 2,
	Missing = 3
}

public enum MediaAssetMutationDisposition
{
	Imported = 1,
	Duplicate = 2,
	Relinked = 3,
	Removed = 4,
	Rejected = 5
}

public sealed record MediaAssetDescriptor
{
	public MediaAssetDescriptor(
		MediaAssetId assetId,
		MediaAssetOriginKind origin,
		string sourceLocation,
		string displayName,
		MediaContainerFormat container,
		MediaVideoCodec videoCodec,
		MediaAudioCodec audioCodec,
		VideoFormat videoFormat,
		AudioFormat audioFormat,
		TimeSpan duration,
		long lengthBytes,
		string fingerprintSha256,
		UtcTimestamp importedAt,
		UtcTimestamp updatedAt,
		MediaAssetAvailability availability)
	{
		if (!Enum.IsDefined(typeof(MediaAssetOriginKind), origin))
			throw new ArgumentOutOfRangeException(nameof(origin));
		if (string.IsNullOrWhiteSpace(sourceLocation))
			throw new ArgumentException("Media asset source location is required.", nameof(sourceLocation));
		if (string.IsNullOrWhiteSpace(displayName))
			throw new ArgumentException("Media asset display name is required.", nameof(displayName));
		if (!Enum.IsDefined(typeof(MediaContainerFormat), container))
			throw new ArgumentOutOfRangeException(nameof(container));
		if (!Enum.IsDefined(typeof(MediaVideoCodec), videoCodec))
			throw new ArgumentOutOfRangeException(nameof(videoCodec));
		if (!Enum.IsDefined(typeof(MediaAudioCodec), audioCodec))
			throw new ArgumentOutOfRangeException(nameof(audioCodec));
		if (duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));
		if (lengthBytes <= 0)
			throw new ArgumentOutOfRangeException(nameof(lengthBytes));
		if (fingerprintSha256.Length != 64 || fingerprintSha256.Any(character => !Uri.IsHexDigit(character)))
			throw new ArgumentException("Media asset fingerprint must be a SHA-256 hex digest.", nameof(fingerprintSha256));
		if (updatedAt.Value < importedAt.Value)
			throw new ArgumentException("Media asset update timestamp cannot precede import timestamp.", nameof(updatedAt));
		if (!Enum.IsDefined(typeof(MediaAssetAvailability), availability))
			throw new ArgumentOutOfRangeException(nameof(availability));

		AssetId = assetId;
		Origin = origin;
		SourceLocation = sourceLocation.Trim();
		DisplayName = displayName.Trim();
		Container = container;
		VideoCodec = videoCodec;
		AudioCodec = audioCodec;
		VideoFormat = videoFormat;
		AudioFormat = audioFormat;
		Duration = duration;
		LengthBytes = lengthBytes;
		FingerprintSha256 = fingerprintSha256.ToUpperInvariant();
		ImportedAt = importedAt;
		UpdatedAt = updatedAt;
		Availability = availability;
	}

	public MediaAssetId AssetId { get; }
	public MediaAssetOriginKind Origin { get; }
	public string SourceLocation { get; }
	public string DisplayName { get; }
	public MediaContainerFormat Container { get; }
	public MediaVideoCodec VideoCodec { get; }
	public MediaAudioCodec AudioCodec { get; }
	public VideoFormat VideoFormat { get; }
	public AudioFormat AudioFormat { get; }
	public TimeSpan Duration { get; }
	public long LengthBytes { get; }
	public string FingerprintSha256 { get; }
	public UtcTimestamp ImportedAt { get; }
	public UtcTimestamp UpdatedAt { get; }
	public MediaAssetAvailability Availability { get; }
}

public sealed class MediaAssetCatalogSnapshot
{
	private readonly ReadOnlyCollection<MediaAssetDescriptor> _assets;

	public MediaAssetCatalogSnapshot(
		CompatibilityVersion version,
		ulong revision,
		IReadOnlyList<MediaAssetDescriptor> assets)
	{
		MediaContractVersion.EnsureSupported(version);
		ArgumentNullException.ThrowIfNull(assets);
		if (assets.Any(asset => asset is null))
			throw new ArgumentException("Media asset catalogue cannot contain null assets.", nameof(assets));
		if (assets.Select(asset => asset.AssetId).Distinct().Count() != assets.Count)
			throw new ArgumentException("Media asset catalogue contains duplicate asset identities.", nameof(assets));

		Version = version;
		Revision = revision;
		_assets = Array.AsReadOnly(assets.ToArray());
	}

	public CompatibilityVersion Version { get; }
	public ulong Revision { get; }
	public IReadOnlyList<MediaAssetDescriptor> Assets => _assets;

	public static MediaAssetCatalogSnapshot Empty { get; } =
		new(MediaContractVersion.Current, 0, Array.Empty<MediaAssetDescriptor>());
}

public sealed record MediaAssetMutationItem(
	string SourceLocation,
	MediaAssetId? AssetId,
	MediaAssetMutationDisposition Disposition,
	Failure? Failure)
{
	public bool Succeeded =>
		Disposition is MediaAssetMutationDisposition.Imported or
			MediaAssetMutationDisposition.Duplicate or
			MediaAssetMutationDisposition.Relinked or
			MediaAssetMutationDisposition.Removed;
}

public sealed class MediaAssetCatalogMutationResult
{
	private readonly ReadOnlyCollection<MediaAssetMutationItem> _items;

	public MediaAssetCatalogMutationResult(
		MediaAssetCatalogSnapshot snapshot,
		IReadOnlyList<MediaAssetMutationItem> items)
	{
		Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
		ArgumentNullException.ThrowIfNull(items);
		if (items.Any(item => item is null))
			throw new ArgumentException("Media asset mutation result cannot contain null items.", nameof(items));
		_items = Array.AsReadOnly(items.ToArray());
	}

	public MediaAssetCatalogSnapshot Snapshot { get; }
	public IReadOnlyList<MediaAssetMutationItem> Items => _items;
	public bool Succeeded => _items.Count > 0 && _items.All(item => item.Succeeded);
}

public sealed record MediaAssetProbeResult
{
	private MediaAssetProbeResult(LocalMediaProbe? probe, Failure? failure)
	{
		if ((probe is null) == (failure is null))
			throw new ArgumentException("Media asset probe requires exactly one of probe or failure.");

		Probe = probe;
		Failure = failure;
	}

	public LocalMediaProbe? Probe { get; }
	public Failure? Failure { get; }
	public bool Succeeded => Probe is not null;

	public static MediaAssetProbeResult Ready(LocalMediaProbe probe) =>
		new(probe ?? throw new ArgumentNullException(nameof(probe)), null);

	public static MediaAssetProbeResult Rejected(string code, string message) =>
		new(null, new Failure(code, message));
}
