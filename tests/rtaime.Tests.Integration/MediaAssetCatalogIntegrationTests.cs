// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Integration;

public sealed class MediaAssetCatalogIntegrationTests
{
	[Fact]
	public async Task Catalogue_survives_restart_preserves_identity_and_supports_missing_relink_and_remove()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-media-catalog-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var database = Path.Combine(root, "management.db");
			var original = Path.Combine(root, "original.mp4");
			var moved = Path.Combine(root, "moved.mp4");
			await File.WriteAllBytesAsync(original, Enumerable.Range(0, 2048).Select(index => (byte)(index % 251)).ToArray());
			File.Copy(original, moved);

			MediaAssetId assetId;
			await using (var management = new SqliteManagementStore(database))
			{
				var service = CreateService(management);
				var imported = await service.ImportAsync([original]);

				Assert.True(imported.Succeeded);
				Assert.Single(imported.Snapshot.Assets);
				Assert.Equal(MediaAssetMutationDisposition.Imported, imported.Items.Single().Disposition);
				assetId = imported.Snapshot.Assets.Single().AssetId;

				var duplicate = await service.ImportAsync([moved]);
				Assert.True(duplicate.Succeeded);
				Assert.Single(duplicate.Snapshot.Assets);
				Assert.Equal(MediaAssetMutationDisposition.Duplicate, duplicate.Items.Single().Disposition);
				Assert.Equal(assetId, duplicate.Items.Single().AssetId);
			}

			File.Delete(original);

			await using (var management = new SqliteManagementStore(database))
			{
				var restarted = CreateService(management);
				var restored = await restarted.GetSnapshotAsync();

				var missing = Assert.Single(restored.Assets);
				Assert.Equal(assetId, missing.AssetId);
				Assert.Equal(MediaAssetAvailability.Missing, missing.Availability);

				var relinked = await restarted.RelinkAsync(assetId, moved);
				Assert.True(relinked.Succeeded);
				var online = Assert.Single(relinked.Snapshot.Assets);
				Assert.Equal(assetId, online.AssetId);
				Assert.Equal(Path.GetFullPath(moved), online.SourceLocation);
				Assert.Equal(MediaAssetAvailability.Online, online.Availability);

				var removed = await restarted.RemoveAsync(assetId);
				Assert.True(removed.Succeeded);
				Assert.Empty(removed.Snapshot.Assets);
				Assert.True(File.Exists(moved));
			}

			await using (var management = new SqliteManagementStore(database))
			{
				var restarted = CreateService(management);
				Assert.Empty((await restarted.GetSnapshotAsync()).Assets);
			}
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Catalogue_rejects_unsupported_media_and_conflicting_relink_without_corrupting_state()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-media-catalog-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var database = Path.Combine(root, "management.db");
			var first = Path.Combine(root, "first.mp4");
			var second = Path.Combine(root, "second.mp4");
			var unsupported = Path.Combine(root, "unsupported.bad");
			await File.WriteAllBytesAsync(first, [1, 2, 3, 4]);
			await File.WriteAllBytesAsync(second, [5, 6, 7, 8]);
			await File.WriteAllBytesAsync(unsupported, [9, 10, 11, 12]);

			await using var management = new SqliteManagementStore(database);
			var service = CreateService(management);
			var imported = await service.ImportAsync([first, second]);
			Assert.True(imported.Succeeded);
			Assert.Equal(2, imported.Snapshot.Assets.Count);

			var firstId = imported.Snapshot.Assets.Single(asset =>
				string.Equals(asset.SourceLocation, Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase)).AssetId;
			var conflict = await service.RelinkAsync(firstId, second);
			Assert.False(conflict.Succeeded);
			Assert.Equal(MediaAssetMutationDisposition.Rejected, conflict.Items.Single().Disposition);
			Assert.Equal("media.catalog.relink_conflict", conflict.Items.Single().Failure?.Code);
			Assert.Equal(2, conflict.Snapshot.Assets.Count);

			var rejected = await service.ImportAsync([unsupported]);
			Assert.False(rejected.Succeeded);
			Assert.Equal("media.catalog.unsupported", rejected.Items.Single().Failure?.Code);
			Assert.Equal(2, rejected.Snapshot.Assets.Count);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Catalogue_import_batch_is_bounded_and_cancelled_import_leaves_no_partial_record()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-media-catalog-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var database = Path.Combine(root, "management.db");
			var media = Path.Combine(root, "cancelled.mp4");
			await File.WriteAllBytesAsync(media, [1, 2, 3, 4, 5]);

			await using var management = new SqliteManagementStore(database);
			var service = CreateService(management);
			var oversized = Enumerable.Range(0, MediaAssetCatalogService.MaxImportBatchSize + 1)
				.Select(index => $"{index}.mp4")
				.ToArray();

			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
				() => service.ImportAsync(oversized).AsTask());

			using var cancelled = new CancellationTokenSource();
			cancelled.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => service.ImportAsync([media], cancelled.Token).AsTask());

			Assert.Empty((await service.GetSnapshotAsync(refreshAvailability: false)).Assets);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Catalogue_persistence_rejects_stale_storage_version()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-media-catalog-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			await using var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			var store = new MediaAssetCatalogPersistenceStore(management);
			var asset = Descriptor(
				new MediaAssetId(Id(100)),
				Path.Combine(root, "asset.mp4"),
				"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
			var snapshot = new MediaAssetCatalogSnapshot(MediaContractVersion.Current, 1, [asset]);

			var first = await store.SaveAsync(snapshot, expectedStorageVersion: 0);
			Assert.True(first.Written, first.Failure?.Message);
			Assert.Equal(1UL, first.Persisted?.StorageVersion);

			var stale = await store.SaveAsync(
				new MediaAssetCatalogSnapshot(MediaContractVersion.Current, 2, [asset]),
				expectedStorageVersion: 0);
			Assert.False(stale.Written);
			Assert.Equal("persistence.version_conflict", stale.Failure?.Code);

			var loaded = await store.LoadAsync();
			Assert.Equal(1UL, loaded.Snapshot.Revision);
			Assert.Equal(asset.AssetId, Assert.Single(loaded.Snapshot.Assets).AssetId);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static MediaAssetCatalogService CreateService(SqliteManagementStore management) =>
		new(
			new ProbeRuntimeTransport(),
			new MediaAssetCatalogPersistenceStore(management));

	private static MediaAssetDescriptor Descriptor(
		MediaAssetId assetId,
		string sourceLocation,
		string fingerprint)
	{
		var timestamp = new UtcTimestamp(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
		return new MediaAssetDescriptor(
			assetId,
			MediaAssetOriginKind.LocalFile,
			sourceLocation,
			Path.GetFileName(sourceLocation),
			MediaContainerFormat.Mp4,
			MediaVideoCodec.H264,
			MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(10),
			1024,
			fingerprint,
			timestamp,
			timestamp,
			MediaAssetAvailability.Online);
	}

	private static Identity Id(int value) =>
		Identity.Parse($"55000000-0000-0000-0000-{value:000000000000}");

	private sealed class ProbeRuntimeTransport : IControlRuntimeTransportSeam
	{
		public bool IsConnected => true;
		public string? HostInstanceId => "catalog-test-runtime";
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => Array.Empty<ProviderDescriptor>();

		public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

		public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
			new(Array.Empty<ProviderDescriptor>());

		public ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromException<RuntimeRemoteSnapshot>(new NotSupportedException());

		public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
			PreparedExecutionContract preparedExecution,
			MediaSinkId programSinkId,
			RuntimeProgramTransitionIntent? transition,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<RuntimeRemoteApplyResult>(new NotSupportedException());

		public ValueTask<MediaAssetProbeResult> ProbeMediaAssetAsync(
			string path,
			MediaAssetId assetId,
			CancellationToken cancellationToken = default)
		{
			if (path.EndsWith(".bad", StringComparison.OrdinalIgnoreCase))
			{
				return ValueTask.FromResult(
					MediaAssetProbeResult.Rejected(
						"media.catalog.unsupported",
						"Test media is intentionally unsupported."));
			}

			return ValueTask.FromResult(
				MediaAssetProbeResult.Ready(
					new LocalMediaProbe(
						MediaContractVersion.Current,
						assetId,
						new MediaSourceId(Id(900)),
						Path.GetFileName(path),
						MediaContainerFormat.Mp4,
						MediaVideoCodec.H264,
						MediaAudioCodec.Aac,
						VideoFormat.Hd1080p50Rgba8,
						AudioFormat.Stereo48kFloat32,
						TimeSpan.FromSeconds(10))));
		}

		public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
	}
}
