// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

public sealed record PersistedMediaMarkerSnapshot(
	MediaMarkerSnapshot Snapshot,
	ulong StorageVersion);

public sealed record MediaMarkerPersistenceWriteResult(
	bool Written,
	PersistedMediaMarkerSnapshot? Persisted,
	Failure? Failure);

public sealed class MediaMarkerPersistenceStore
{
	private const string Area = "media.asset.markers";
	private const string DocumentFormat = "rtaime.media-markers.v1";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly SqliteManagementStore _managementStore;

	public MediaMarkerPersistenceStore(SqliteManagementStore managementStore)
	{
		_managementStore = managementStore ?? throw new ArgumentNullException(nameof(managementStore));
	}

	public async ValueTask<PersistedMediaMarkerSnapshot> LoadAsync(
		MediaAssetId assetId,
		long totalFrames,
		CancellationToken cancellationToken = default)
	{
		if (totalFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(totalFrames));

		var document = await _managementStore
			.GetDocumentAsync(Area, assetId.ToString(), cancellationToken)
			.ConfigureAwait(false);
		if (document is null)
		{
			return new PersistedMediaMarkerSnapshot(
				new MediaMarkerSnapshot(
					MediaContractVersion.Current,
					assetId,
					totalFrames),
				0);
		}

		var dto = JsonSerializer.Deserialize<MediaMarkerDocument>(document.Json, JsonOptions)
			?? throw new InvalidDataException("Persisted media marker document is empty.");
		if (!string.Equals(dto.Format, DocumentFormat, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported media marker document format '{dto.Format}'.");
		if (!string.Equals(dto.AssetId, assetId.ToString(), StringComparison.Ordinal))
			throw new InvalidDataException("Persisted media marker asset identity does not match the requested asset.");
		if (dto.TotalFrames != totalFrames)
			throw new InvalidDataException(
				$"Persisted marker frame count '{dto.TotalFrames}' does not match current media frame count '{totalFrames}'.");

		var cues = dto.CuePoints
			.Select(cue => new MediaCuePoint(
				new MediaCuePointId(Identity.Parse(cue.Id)),
				cue.Name,
				cue.PositionFrame))
			.ToArray();
		var snapshot = new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			assetId,
			totalFrames,
			dto.InPointFrame,
			dto.OutPointFrame,
			cues);
		return new PersistedMediaMarkerSnapshot(snapshot, document.Version);
	}

	public async ValueTask<MediaMarkerPersistenceWriteResult> SaveAsync(
		MediaMarkerSnapshot snapshot,
		ulong? expectedStorageVersion = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var dto = new MediaMarkerDocument(
			DocumentFormat,
			snapshot.AssetId.ToString(),
			snapshot.TotalFrames,
			snapshot.InPointFrame,
			snapshot.OutPointFrame,
			snapshot.CuePoints
				.Select(cue => new MediaCuePointDocument(cue.Id.ToString(), cue.Name, cue.PositionFrame))
				.ToArray());
		var json = JsonSerializer.Serialize(dto, JsonOptions);
		var write = await _managementStore
			.PutDocumentAsync(
				Area,
				snapshot.AssetId.ToString(),
				json,
				expectedStorageVersion,
				cancellationToken)
			.ConfigureAwait(false);
		if (!write.Written || write.Document is null)
			return new MediaMarkerPersistenceWriteResult(false, null, write.Failure);

		return new MediaMarkerPersistenceWriteResult(
			true,
			new PersistedMediaMarkerSnapshot(snapshot, write.Document.Version),
			null);
	}

	private sealed record MediaMarkerDocument(
		string Format,
		string AssetId,
		long TotalFrames,
		long? InPointFrame,
		long? OutPointFrame,
		IReadOnlyList<MediaCuePointDocument> CuePoints);

	private sealed record MediaCuePointDocument(
		string Id,
		string Name,
		long PositionFrame);
}
