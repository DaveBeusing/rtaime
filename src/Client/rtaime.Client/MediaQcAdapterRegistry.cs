// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>
/// Optional, trusted in-process offline QC adapter. An adapter may read the
/// independently hashed local file under the QC worker's cancellation/budget.
/// It must never open a production decoder, change a media source or write to
/// the authoritative media catalogue.
/// </summary>
public interface IMediaQcCheckAdapter
{
	MediaQcCheckDefinition Definition { get; }
	ValueTask<MediaQcCheckResult> InspectAsync(
		MediaQcCheckContext context, CancellationToken cancellationToken);
}

public sealed record MediaQcCheckContext(
	MediaAssetDescriptor Asset, ulong CatalogueRevision, string FingerprintSha256,
	long SourceLengthBytes, DateTimeOffset SourceModifiedUtc)
{
	public string FilePath => Asset.SourceLocation;
}

/// <summary>
/// Version and identity gate for future independently implemented R 128 and
/// source-range checks. The structural checks remain owned by the built-in
/// read-only BMFF inspector; no unregistered catalogue checks are assumed.
/// </summary>
public static class MediaQcAdapterRegistry
{
	public const int MaximumAdapters = 2;
	public static IReadOnlyList<IMediaQcCheckAdapter> Admit(
		IReadOnlyList<IMediaQcCheckAdapter>? adapters)
	{
		if (adapters is null || adapters.Count == 0)
			return Array.Empty<IMediaQcCheckAdapter>();
		if (adapters.Count > MaximumAdapters)
			throw new ArgumentException("Offline QC adapter capacity exceeded.", nameof(adapters));
		var approved = MediaQcProfile.OfflineFileV1.Checks
			.Where(check => check.Id is "rtaime-loudness" or "rtaime-range")
			.ToDictionary(check => check.Id, StringComparer.Ordinal);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var adapter in adapters)
		{
			if (adapter is null || adapter.Definition is null ||
				!approved.TryGetValue(adapter.Definition.Id, out var definition) ||
				adapter.Definition != definition || !seen.Add(definition.Id))
				throw new ArgumentException("Unknown, duplicate or version-mismatched QC adapter.", nameof(adapters));
		}
		return Array.AsReadOnly(adapters.ToArray());
	}

	public static void ValidateResult(IMediaQcCheckAdapter adapter, MediaQcCheckResult result)
	{
		ArgumentNullException.ThrowIfNull(adapter);
		ArgumentNullException.ThrowIfNull(result);
		result.Validate();
		if (result.Definition != adapter.Definition ||
			!result.EvidenceSource.StartsWith("offline-file:", StringComparison.Ordinal))
			throw new InvalidDataException("QC adapter result is not bound to the admitted offline check.");
	}
}
