// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;

namespace rtaime.ControlHost;

public sealed record PersistedMediaAssetCatalog(
	MediaAssetCatalogSnapshot Snapshot,
	ulong StorageVersion);

public sealed record MediaAssetCatalogPersistenceWriteResult(
	bool Written,
	PersistedMediaAssetCatalog? Persisted,
	Failure? Failure);

public sealed class MediaAssetCatalogPersistenceStore
{
	private const string Area = "media.asset.catalog";
	private const string Key = "catalog";
	private const string DocumentFormat = "rtaime.media-asset-catalog.v1";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly SqliteManagementStore _managementStore;

	public MediaAssetCatalogPersistenceStore(SqliteManagementStore managementStore)
	{
		_managementStore = managementStore ?? throw new ArgumentNullException(nameof(managementStore));
	}

	public async ValueTask<PersistedMediaAssetCatalog> LoadAsync(CancellationToken cancellationToken = default)
	{
		var document = await _managementStore
			.GetDocumentAsync(Area, Key, cancellationToken)
			.ConfigureAwait(false);
		if (document is null)
			return new PersistedMediaAssetCatalog(MediaAssetCatalogSnapshot.Empty, 0);

		var dto = JsonSerializer.Deserialize<MediaAssetCatalogDocument>(document.Json, JsonOptions)
			?? throw new InvalidDataException("Persisted media asset catalogue document is empty.");
		if (!string.Equals(dto.Format, DocumentFormat, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported media asset catalogue document format '{dto.Format}'.");
		if (dto.Assets is null)
			throw new InvalidDataException("Persisted media asset catalogue assets are required.");

		var assets = dto.Assets
			.Select(FromDocument)
			.OrderBy(asset => asset.AssetId.ToString(), StringComparer.Ordinal)
			.ToArray();
		var snapshot = new MediaAssetCatalogSnapshot(
			MediaContractVersion.Current,
			dto.Revision,
			assets);
		return new PersistedMediaAssetCatalog(snapshot, document.Version);
	}

	public async ValueTask<MediaAssetCatalogPersistenceWriteResult> SaveAsync(
		MediaAssetCatalogSnapshot snapshot,
		ulong? expectedStorageVersion = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var dto = new MediaAssetCatalogDocument(
			DocumentFormat,
			snapshot.Revision,
			snapshot.Assets
				.OrderBy(asset => asset.AssetId.ToString(), StringComparer.Ordinal)
				.Select(ToDocument)
				.ToArray());
		var json = JsonSerializer.Serialize(dto, JsonOptions);
		var write = await _managementStore
			.PutDocumentAsync(
				Area,
				Key,
				json,
				expectedStorageVersion,
				cancellationToken)
			.ConfigureAwait(false);
		if (!write.Written || write.Document is null)
			return new MediaAssetCatalogPersistenceWriteResult(false, null, write.Failure);

		return new MediaAssetCatalogPersistenceWriteResult(
			true,
			new PersistedMediaAssetCatalog(snapshot, write.Document.Version),
			null);
	}

	private static MediaAssetDocument ToDocument(MediaAssetDescriptor asset) => new(
		asset.AssetId.ToString(),
		(int)asset.Origin,
		asset.SourceLocation,
		asset.DisplayName,
		(int)asset.Container,
		(int)asset.VideoCodec,
		(int)asset.AudioCodec,
		asset.VideoFormat.Width,
		asset.VideoFormat.Height,
		asset.VideoFormat.FrameRate.ToString(),
		(int)asset.VideoFormat.PixelFormat,
		(int)asset.VideoFormat.ScanMode,
		asset.AudioFormat.SampleRate,
		(int)asset.AudioFormat.ChannelLayout,
		(int)asset.AudioFormat.SampleFormat,
		asset.AudioFormat.ChannelCount,
		asset.Duration.Ticks,
		asset.LengthBytes,
		asset.FingerprintSha256,
		asset.ImportedAt.ToString(),
		asset.UpdatedAt.ToString(),
		(int)asset.Availability);

	private static MediaAssetDescriptor FromDocument(MediaAssetDocument asset)
	{
		if (!Enum.IsDefined(typeof(MediaAssetOriginKind), asset.Origin))
			throw new InvalidDataException("Persisted media asset origin is invalid.");
		if (!Enum.IsDefined(typeof(MediaContainerFormat), asset.Container))
			throw new InvalidDataException("Persisted media container format is invalid.");
		if (!Enum.IsDefined(typeof(MediaVideoCodec), asset.VideoCodec))
			throw new InvalidDataException("Persisted media video codec is invalid.");
		if (!Enum.IsDefined(typeof(MediaAudioCodec), asset.AudioCodec))
			throw new InvalidDataException("Persisted media audio codec is invalid.");
		if (!Enum.IsDefined(typeof(PixelFormat), asset.PixelFormat))
			throw new InvalidDataException("Persisted media pixel format is invalid.");
		if (!Enum.IsDefined(typeof(ScanMode), asset.ScanMode))
			throw new InvalidDataException("Persisted media scan mode is invalid.");
		if (!Enum.IsDefined(typeof(AudioChannelLayout), asset.AudioChannelLayout))
			throw new InvalidDataException("Persisted media audio channel layout is invalid.");
		if (!Enum.IsDefined(typeof(AudioSampleFormat), asset.AudioSampleFormat))
			throw new InvalidDataException("Persisted media audio sample format is invalid.");
		if (!Enum.IsDefined(typeof(MediaAssetAvailability), asset.Availability))
			throw new InvalidDataException("Persisted media asset availability is invalid.");

		return new MediaAssetDescriptor(
			new MediaAssetId(Identity.Parse(asset.AssetId)),
			(MediaAssetOriginKind)asset.Origin,
			asset.SourceLocation,
			asset.DisplayName,
			(MediaContainerFormat)asset.Container,
			(MediaVideoCodec)asset.VideoCodec,
			(MediaAudioCodec)asset.AudioCodec,
			new VideoFormat(
				asset.Width,
				asset.Height,
				FrameRate.Parse(asset.FrameRate),
				(PixelFormat)asset.PixelFormat,
				(ScanMode)asset.ScanMode),
			new AudioFormat(
				asset.AudioSampleRate,
				(AudioChannelLayout)asset.AudioChannelLayout,
				(AudioSampleFormat)asset.AudioSampleFormat,
				asset.AudioChannelCount),
			TimeSpan.FromTicks(asset.DurationTicks),
			asset.LengthBytes,
			asset.FingerprintSha256,
			UtcTimestamp.Parse(asset.ImportedAt),
			UtcTimestamp.Parse(asset.UpdatedAt),
			(MediaAssetAvailability)asset.Availability);
	}

	private sealed record MediaAssetCatalogDocument(
		string Format,
		ulong Revision,
		MediaAssetDocument[] Assets);

	private sealed record MediaAssetDocument(
		string AssetId,
		int Origin,
		string SourceLocation,
		string DisplayName,
		int Container,
		int VideoCodec,
		int AudioCodec,
		uint Width,
		uint Height,
		string FrameRate,
		int PixelFormat,
		int ScanMode,
		uint AudioSampleRate,
		int AudioChannelLayout,
		int AudioSampleFormat,
		uint AudioChannelCount,
		long DurationTicks,
		long LengthBytes,
		string FingerprintSha256,
		string ImportedAt,
		string UpdatedAt,
		int Availability);
}
