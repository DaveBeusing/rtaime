// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class MediaMarkerPersistenceIntegrationTests
{
	[Fact]
	public async Task Marker_snapshot_round_trips_and_rejects_stale_writes()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-marker-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			await using var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			var store = new MediaMarkerPersistenceStore(management);
			var assetId = new MediaAssetId(Id(100));
			var snapshot = new MediaMarkerSnapshot(
				MediaContractVersion.Current,
				assetId,
				50,
				5,
				45,
				new[]
				{
					new MediaCuePoint(new MediaCuePointId(Id(2)), "Second", 30),
					new MediaCuePoint(new MediaCuePointId(Id(1)), "First", 10)
				});

			var firstWrite = await store.SaveAsync(snapshot, expectedStorageVersion: 0);
			Assert.True(firstWrite.Written, firstWrite.Failure?.Message);
			Assert.Equal(1UL, firstWrite.Persisted?.StorageVersion);

			var loaded = await store.LoadAsync(assetId, 50);
			Assert.Equal(1UL, loaded.StorageVersion);
			Assert.Equal(5, loaded.Snapshot.InPointFrame);
			Assert.Equal(45, loaded.Snapshot.OutPointFrame);
			Assert.Equal(new[] { "First", "Second" }, loaded.Snapshot.CuePoints.Select(cue => cue.Name));

			var staleWrite = await store.SaveAsync(
				new MediaMarkerSnapshot(
					MediaContractVersion.Current,
					assetId,
					50,
					6,
					44,
					loaded.Snapshot.CuePoints),
				expectedStorageVersion: 0);
			Assert.False(staleWrite.Written);
			Assert.Equal("persistence.version_conflict", staleWrite.Failure?.Code);

			await Assert.ThrowsAsync<InvalidDataException>(
				() => store.LoadAsync(assetId, 60).AsTask());
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Missing_marker_document_returns_empty_asset_snapshot()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-marker-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			await using var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			var store = new MediaMarkerPersistenceStore(management);
			var assetId = new MediaAssetId(Id(200));

			var loaded = await store.LoadAsync(assetId, 100);

			Assert.Equal(0UL, loaded.StorageVersion);
			Assert.Equal(assetId, loaded.Snapshot.AssetId);
			Assert.Equal(100, loaded.Snapshot.TotalFrames);
			Assert.Null(loaded.Snapshot.InPointFrame);
			Assert.Null(loaded.Snapshot.OutPointFrame);
			Assert.Empty(loaded.Snapshot.CuePoints);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static Identity Id(int value) =>
		Identity.Parse($"44000000-0000-0000-0000-{value:000000000000}");
}
