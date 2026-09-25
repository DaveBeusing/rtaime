// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

public sealed class MediaAssetCatalogService
{
	public const int MaxImportBatchSize = 128;

	private readonly IControlRuntimeTransportSeam _runtime;
	private readonly MediaAssetCatalogPersistenceStore _persistence;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private PersistedMediaAssetCatalog? _persisted;

	public MediaAssetCatalogService(
		IControlRuntimeTransportSeam runtime,
		MediaAssetCatalogPersistenceStore persistence)
	{
		_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
		_persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
	}

	public async ValueTask<MediaAssetCatalogSnapshot> GetSnapshotAsync(
		bool refreshAvailability = true,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			if (refreshAvailability)
				await RefreshAvailabilityCoreAsync(cancellationToken).ConfigureAwait(false);
			return _persisted!.Snapshot;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaAssetCatalogMutationResult> ImportAsync(
		IReadOnlyList<string> sourceLocations,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(sourceLocations);
		if (sourceLocations.Count == 0)
			throw new ArgumentException("At least one media asset source location is required.", nameof(sourceLocations));
		if (sourceLocations.Count > MaxImportBatchSize)
			throw new ArgumentOutOfRangeException(
				nameof(sourceLocations),
				$"Media asset import is bounded to {MaxImportBatchSize} files per request.");

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			var assets = _persisted!.Snapshot.Assets.ToList();
			var results = new List<MediaAssetMutationItem>(sourceLocations.Count);
			var changed = false;

			foreach (var requestedLocation in sourceLocations)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var normalized = TryNormalizePath(requestedLocation, out var pathFailure);
				if (normalized is null)
				{
					results.Add(Rejected(requestedLocation ?? string.Empty, pathFailure!.Value));
					continue;
				}

				var file = TryOpenFile(normalized, out var fileFailure);
				if (file is null)
				{
					results.Add(Rejected(normalized, fileFailure!.Value));
					continue;
				}

				await using (file.ConfigureAwait(false))
				{
					var fingerprint = await ComputeFingerprintAsync(file, cancellationToken).ConfigureAwait(false);
					var existingByPath = assets.FirstOrDefault(asset =>
						string.Equals(asset.SourceLocation, normalized, StringComparison.OrdinalIgnoreCase));
					if (existingByPath is not null)
					{
						if (string.Equals(existingByPath.FingerprintSha256, fingerprint, StringComparison.Ordinal))
						{
							results.Add(new MediaAssetMutationItem(
								normalized,
								existingByPath.AssetId,
								MediaAssetMutationDisposition.Duplicate,
								null));
						}
						else
						{
							results.Add(Rejected(
								normalized,
								new Failure(
									"media.catalog.source_conflict",
									"The selected source path is already assigned to a catalogued asset whose content fingerprint differs.")));
						}
						continue;
					}

					var duplicate = assets.FirstOrDefault(asset =>
						string.Equals(asset.FingerprintSha256, fingerprint, StringComparison.Ordinal));
					if (duplicate is not null)
					{
						results.Add(new MediaAssetMutationItem(
							normalized,
							duplicate.AssetId,
							MediaAssetMutationDisposition.Duplicate,
							null));
						continue;
					}

					var assetId = MediaAssetId.New();
					var probe = await ProbeAsync(normalized, assetId, cancellationToken).ConfigureAwait(false);
					if (!probe.Succeeded || probe.Probe is null)
					{
						results.Add(Rejected(
							normalized,
							probe.Failure ?? new Failure("media.catalog.probe_failed", "Media asset probing failed.")));
						continue;
					}

					var fileInfo = new FileInfo(normalized);
					var now = new UtcTimestamp(DateTimeOffset.UtcNow);
					assets.Add(new MediaAssetDescriptor(
						assetId,
						MediaAssetOriginKind.LocalFile,
						normalized,
						fileInfo.Name,
						probe.Probe.Container,
						probe.Probe.VideoCodec,
						probe.Probe.AudioCodec,
						probe.Probe.VideoFormat,
						probe.Probe.AudioFormat,
						probe.Probe.Duration,
						fileInfo.Length,
						fingerprint,
						now,
						now,
						MediaAssetAvailability.Online));
					results.Add(new MediaAssetMutationItem(
						normalized,
						assetId,
						MediaAssetMutationDisposition.Imported,
						null));
					changed = true;
				}
			}

			if (changed)
				await SaveAsync(assets, cancellationToken).ConfigureAwait(false);

			return new MediaAssetCatalogMutationResult(_persisted!.Snapshot, results);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaAssetCatalogMutationResult> RelinkAsync(
		MediaAssetId assetId,
		string sourceLocation,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			var assets = _persisted!.Snapshot.Assets.ToList();
			var index = assets.FindIndex(asset => asset.AssetId == assetId);
			if (index < 0)
			{
				return RejectedResult(
					sourceLocation,
					assetId,
					new Failure("media.catalog.asset_not_found", "The requested media asset is not present in the catalogue."));
			}

			var normalized = TryNormalizePath(sourceLocation, out var pathFailure);
			if (normalized is null)
				return RejectedResult(sourceLocation, assetId, pathFailure!.Value);

			var conflict = assets.FirstOrDefault(asset =>
				asset.AssetId != assetId &&
				string.Equals(asset.SourceLocation, normalized, StringComparison.OrdinalIgnoreCase));
			if (conflict is not null)
			{
				return RejectedResult(
					normalized,
					assetId,
					new Failure("media.catalog.relink_conflict", "The relink target is already assigned to another media asset."));
			}

			var file = TryOpenFile(normalized, out var fileFailure);
			if (file is null)
				return RejectedResult(normalized, assetId, fileFailure!.Value);

			string fingerprint;
			await using (file.ConfigureAwait(false))
				fingerprint = await ComputeFingerprintAsync(file, cancellationToken).ConfigureAwait(false);

			var current = assets[index];
			var fingerprintConflict = assets.FirstOrDefault(asset =>
				asset.AssetId != assetId &&
				string.Equals(asset.FingerprintSha256, fingerprint, StringComparison.Ordinal));
			if (fingerprintConflict is not null)
			{
				return RejectedResult(
					normalized,
					assetId,
					new Failure("media.catalog.relink_conflict", "The relink target content is already catalogued as another media asset."));
			}
			if (!string.Equals(current.FingerprintSha256, fingerprint, StringComparison.Ordinal))
			{
				return RejectedResult(
					normalized,
					assetId,
					new Failure("media.catalog.relink_content_mismatch", "The relink target does not match the original media asset fingerprint."));
			}

			var probe = await ProbeAsync(normalized, assetId, cancellationToken).ConfigureAwait(false);
			if (!probe.Succeeded || probe.Probe is null)
			{
				return RejectedResult(
					normalized,
					assetId,
					probe.Failure ?? new Failure("media.catalog.probe_failed", "Media asset probing failed."));
			}

			var fileInfo = new FileInfo(normalized);
			assets[index] = new MediaAssetDescriptor(
				current.AssetId,
				current.Origin,
				normalized,
				fileInfo.Name,
				probe.Probe.Container,
				probe.Probe.VideoCodec,
				probe.Probe.AudioCodec,
				probe.Probe.VideoFormat,
				probe.Probe.AudioFormat,
				probe.Probe.Duration,
				fileInfo.Length,
				fingerprint,
				current.ImportedAt,
				new UtcTimestamp(DateTimeOffset.UtcNow),
				MediaAssetAvailability.Online);
			await SaveAsync(assets, cancellationToken).ConfigureAwait(false);
			return new MediaAssetCatalogMutationResult(
				_persisted!.Snapshot,
				new[]
				{
					new MediaAssetMutationItem(
						normalized,
						assetId,
						MediaAssetMutationDisposition.Relinked,
						null)
				});
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaAssetCatalogMutationResult> RemoveAsync(
		MediaAssetId assetId,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			var assets = _persisted!.Snapshot.Assets.ToList();
			var existing = assets.FirstOrDefault(asset => asset.AssetId == assetId);
			if (existing is null)
			{
				return RejectedResult(
					string.Empty,
					assetId,
					new Failure("media.catalog.asset_not_found", "The requested media asset is not present in the catalogue."));
			}

			assets.Remove(existing);
			await SaveAsync(assets, cancellationToken).ConfigureAwait(false);
			return new MediaAssetCatalogMutationResult(
				_persisted!.Snapshot,
				new[]
				{
					new MediaAssetMutationItem(
						existing.SourceLocation,
						assetId,
						MediaAssetMutationDisposition.Removed,
						null)
				});
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaAssetCatalogSnapshot> RefreshAvailabilityAsync(
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			await RefreshAvailabilityCoreAsync(cancellationToken).ConfigureAwait(false);
			return _persisted!.Snapshot;
		}
		finally
		{
			_gate.Release();
		}
	}

	private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
	{
		if (_persisted is null)
			_persisted = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask RefreshAvailabilityCoreAsync(CancellationToken cancellationToken)
	{
		var assets = _persisted!.Snapshot.Assets.ToArray();
		var changed = false;
		for (var index = 0; index < assets.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var availability = CheckAvailability(assets[index].SourceLocation);
			if (availability == assets[index].Availability)
				continue;

			var current = assets[index];
			assets[index] = new MediaAssetDescriptor(
				current.AssetId,
				current.Origin,
				current.SourceLocation,
				current.DisplayName,
				current.Container,
				current.VideoCodec,
				current.AudioCodec,
				current.VideoFormat,
				current.AudioFormat,
				current.Duration,
				current.LengthBytes,
				current.FingerprintSha256,
				current.ImportedAt,
				new UtcTimestamp(DateTimeOffset.UtcNow),
				availability);
			changed = true;
		}

		if (changed)
			await SaveAsync(assets, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask SaveAsync(
		IReadOnlyList<MediaAssetDescriptor> assets,
		CancellationToken cancellationToken)
	{
		var snapshot = new MediaAssetCatalogSnapshot(
			MediaContractVersion.Current,
			checked(_persisted!.Snapshot.Revision + 1),
			assets);
		var write = await _persistence
			.SaveAsync(snapshot, _persisted.StorageVersion, cancellationToken)
			.ConfigureAwait(false);
		if (!write.Written || write.Persisted is null)
		{
			throw new InvalidOperationException(
				write.Failure?.Message ?? "Media asset catalogue persistence failed.");
		}
		_persisted = write.Persisted;
	}

	private async ValueTask<MediaAssetProbeResult> ProbeAsync(
		string path,
		MediaAssetId assetId,
		CancellationToken cancellationToken)
	{
		try
		{
			return await _runtime
				.ProbeMediaAssetAsync(path, assetId, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (
			exception is IOException or
			InvalidOperationException or
			NotSupportedException or
			ArgumentException or
			InvalidDataException)
		{
			return MediaAssetProbeResult.Rejected("media.catalog.probe_failed", exception.Message);
		}
	}

	private static string? TryNormalizePath(string? path, out Failure? failure)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			failure = new Failure("media.catalog.path_required", "Media asset source location is required.");
			return null;
		}

		try
		{
			var normalized = Path.GetFullPath(path.Trim());
			failure = null;
			return normalized;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			failure = new Failure("media.catalog.path_invalid", exception.Message);
			return null;
		}
	}

	private static FileStream? TryOpenFile(string path, out Failure? failure)
	{
		try
		{
			var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete,
				bufferSize: 128 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			if (stream.Length <= 0)
			{
				stream.Dispose();
				failure = new Failure("media.catalog.file_empty", "Media asset source file is empty.");
				return null;
			}
			failure = null;
			return stream;
		}
		catch (FileNotFoundException)
		{
			failure = new Failure("media.catalog.file_missing", "Media asset source file does not exist.");
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			failure = new Failure("media.catalog.file_missing", "Media asset source directory does not exist.");
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			failure = new Failure("media.catalog.file_unreadable", exception.Message);
			return null;
		}
	}

	private static async ValueTask<string> ComputeFingerprintAsync(
		Stream stream,
		CancellationToken cancellationToken)
	{
		using var sha = SHA256.Create();
		var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash);
	}

	private static MediaAssetAvailability CheckAvailability(string path)
	{
		try
		{
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete,
				bufferSize: 1,
				FileOptions.None);
			return stream.Length >= 0
				? MediaAssetAvailability.Online
				: MediaAssetAvailability.Offline;
		}
		catch (FileNotFoundException)
		{
			return MediaAssetAvailability.Missing;
		}
		catch (DirectoryNotFoundException)
		{
			return MediaAssetAvailability.Missing;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return MediaAssetAvailability.Offline;
		}
	}

	private MediaAssetCatalogMutationResult RejectedResult(
		string sourceLocation,
		MediaAssetId assetId,
		Failure failure) =>
		new(
			_persisted!.Snapshot,
			new[]
			{
				new MediaAssetMutationItem(
					sourceLocation ?? string.Empty,
					assetId,
					MediaAssetMutationDisposition.Rejected,
					failure)
			});

	private static MediaAssetMutationItem Rejected(string sourceLocation, Failure failure) =>
		new(
			sourceLocation,
			null,
			MediaAssetMutationDisposition.Rejected,
			failure);
}
