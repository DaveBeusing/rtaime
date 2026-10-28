// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Security.Cryptography;
using rtaime.Media.Contracts;

namespace rtaime.Client;

// Read-only, independent file inspection. Never use a Program/Preview decoder,
// RuntimeHost media callback, production reservation or mutable catalogue store.
public static class MediaQcFileInspector
{
	public const long DefaultMaximumFileBytes = 1024L * 1024 * 1024;
	public const string CheckImplementationVersion = "1.0.0";
	private const int HashBufferBytes = 64 * 1024;
	private const long ThrottleBytes = 1024 * 1024;
	private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(125);

	public static async Task<MediaQcReport> InspectAsync(
		MediaAssetDescriptor asset, ulong catalogueRevision,
		long maximumFileBytes = DefaultMaximumFileBytes,
		CancellationToken cancellationToken = default,
		IReadOnlyList<IMediaQcCheckAdapter>? adapters = null)
	{
		ArgumentNullException.ThrowIfNull(asset);
		var admittedAdapters = MediaQcAdapterRegistry.Admit(adapters);
		if (maximumFileBytes is < 1 or > 32L * 1024 * 1024 * 1024)
			throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
		if (asset.Origin != MediaAssetOriginKind.LocalFile ||
			asset.Availability != MediaAssetAvailability.Online ||
			asset.SourceLocation.StartsWith(@"\\", StringComparison.Ordinal))
			throw new NotSupportedException("QC requires an online, local catalogue file; no network locations.");
		var path = Path.GetFullPath(asset.SourceLocation);
		cancellationToken.ThrowIfCancellationRequested();
		var before = new FileInfo(path);
		if (!before.Exists)
			throw new FileNotFoundException("Media QC source is unavailable.");
		if (before.Length == 0 || before.Length != asset.LengthBytes)
			throw new MediaQcSourceChangedException("Media length changed since the catalogue observation.");
		if (before.Length > maximumFileBytes)
			throw new NotSupportedException("Media QC file exceeds the admitted byte/I/O quota.");
		var beforeLength = before.Length;
		var beforeStamp = before.LastWriteTimeUtc;
		using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
			FileShare.Read | FileShare.Delete, HashBufferBytes,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		var hash = await ComputeThrottledHashAsync(source, cancellationToken).ConfigureAwait(false);
		if (!string.Equals(hash, asset.FingerprintSha256, StringComparison.OrdinalIgnoreCase))
			throw new MediaQcSourceChangedException("Content SHA-256 differs from the immutable catalogue fingerprint.");
		cancellationToken.ThrowIfCancellationRequested();
		source.Position = 0;
		var scanned = BmffStructure.Inspect(source, cancellationToken);
		before.Refresh();
		if (!before.Exists || before.Length != beforeLength || before.LastWriteTimeUtc != beforeStamp)
			throw new MediaQcSourceChangedException("Media changed while QC inspected it.");
		cancellationToken.ThrowIfCancellationRequested();
		var checks = MediaQcProfile.OfflineFileV1.Checks;
		MediaQcCheckResult Result(int index, MediaQcCheckState state, string detail,
			int severity = 0, double? confidence = null) =>
			new(checks[index], state, detail,
				state is MediaQcCheckState.NotExecuted or MediaQcCheckState.Unsupported ? 0 : severity,
				state is MediaQcCheckState.NotExecuted or MediaQcCheckState.Unsupported ? null : confidence,
				"offline-file:sha256:" + hash.ToLowerInvariant(),
				CheckImplementationVersion);
		var structurallyKnown = scanned.HasMoov || scanned.HasFtyp;
		var valid = !scanned.Truncated && !scanned.LimitExceeded;
		var results = new MediaQcCheckResult[] {
			Result(0, !structurallyKnown || scanned.LimitExceeded ? MediaQcCheckState.Unsupported :
				valid && scanned.HasMoov && scanned.HasMdat
					? MediaQcCheckState.Pass : MediaQcCheckState.Failed,
				!structurallyKnown ? "Not a recognised, supported ISO BMFF/QuickTime box structure." :
				scanned.HasMoov && scanned.HasMdat && valid
					? "Recognised movie and media-data box structures fit the verified source." :
					"Container headers, boundaries or required boxes are invalid.",
				structurallyKnown && (!valid || !scanned.HasMoov || !scanned.HasMdat) ? 7 : 0,
				structurallyKnown ? 1 : null),
			Result(1, scanned.LimitExceeded || !structurallyKnown ? MediaQcCheckState.Unsupported :
				scanned.Truncated ? MediaQcCheckState.Failed : MediaQcCheckState.Pass,
				scanned.LimitExceeded || !structurallyKnown ? "No supported container structure or structural scan budget exhausted; truncation not fully evaluated." :
				scanned.Truncated ? "An ISO BMFF box length or nested boundary exceeds the hashed file." :
					"All inspected box boundaries fit the complete SHA-256 verified file.",
				scanned.Truncated ? 8 : 0,
				scanned.LimitExceeded ? null : 1),
			Result(2, !scanned.HasMoov || scanned.LimitExceeded
				? MediaQcCheckState.Unsupported :
				scanned.VideoTracks < 1 ||
					(asset.AudioCodec != MediaAudioCodec.None && scanned.AudioTracks < 1)
					? MediaQcCheckState.Failed : MediaQcCheckState.Pass,
				$"Observed handlers: video={scanned.VideoTracks}, audio={scanned.AudioTracks}; expected audio={(asset.AudioCodec != MediaAudioCodec.None ? "YES" : "OPTIONAL")}.",
				scanned.HasMoov && (scanned.VideoTracks < 1 ||
					(asset.AudioCodec != MediaAudioCodec.None && scanned.AudioTracks < 1)) ? 7 : 0,
				scanned.HasMoov && !scanned.LimitExceeded ? 1 : null),
			Result(3, scanned.LimitExceeded || scanned.VideoTimeToSampleTables == 0 ||
				scanned.VideoTimeToSampleTables < scanned.VideoTracks
				? MediaQcCheckState.Unsupported :
				scanned.InvalidTimeToSample ? MediaQcCheckState.Failed : MediaQcCheckState.Pass,
				scanned.VideoTimeToSampleTables == 0
					? "No video time-to-sample table; decode timestamp monotonicity not established." :
					$"Validated {scanned.VideoTimeToSampleTables} video stts tables (positive decode deltas); presentation timestamps, CTTS and edit lists remain unverified.",
				scanned.InvalidTimeToSample ? 5 : 0,
				scanned.LimitExceeded || scanned.VideoTimeToSampleTables == 0 ? null : 1),
			Result(4, !scanned.HasMoov || scanned.LimitExceeded
				? MediaQcCheckState.Unsupported :
				scanned.HasMovieHeader && scanned.VideoSampleTables >= scanned.VideoTracks && scanned.VideoTracks > 0
					? MediaQcCheckState.Pass : MediaQcCheckState.Failed,
				$"Movie header={(scanned.HasMovieHeader ? "FOUND" : "MISSING")}; video sample tables={scanned.VideoSampleTables}; video tracks={scanned.VideoTracks}.",
				scanned.HasMoov && (!scanned.HasMovieHeader || scanned.VideoSampleTables < scanned.VideoTracks) ? 4 : 0,
				scanned.HasMoov && !scanned.LimitExceeded ? 1 : null),
			Result(5, MediaQcCheckState.NotExecuted,
				"Independent file R 128 analysis is not installed; live Program LUFS cannot qualify a media asset."),
			Result(6, MediaQcCheckState.NotExecuted,
				"Source-bound R 103 range analysis is not installed; no synthetic pass is permitted.")
		};
		if (admittedAdapters.Count != 0)
		{
			var context = new MediaQcCheckContext(asset, catalogueRevision,
				hash, beforeLength, new DateTimeOffset(beforeStamp, TimeSpan.Zero));
			foreach (var adapter in admittedAdapters)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var index = Array.FindIndex(checks.ToArray(), definition => definition.Id == adapter.Definition.Id);
				if (index < 0)
					throw new InvalidDataException("An unknown QC adapter was admitted.");
				try
				{
					var result = await adapter.InspectAsync(context, cancellationToken).ConfigureAwait(false);
					MediaQcAdapterRegistry.ValidateResult(adapter, result);
					if (!string.Equals(result.EvidenceSource,
						"offline-file:sha256:" + hash.ToLowerInvariant(), StringComparison.Ordinal))
						throw new InvalidDataException("QC adapter used a different content fingerprint.");
					results[index] = result;
				}
				catch (NotSupportedException)
				{
					results[index] = Result(index, MediaQcCheckState.Unsupported,
						"Independent offline measurement is unavailable for this source.");
				}
			}
			// An adapter can perform long sequential reads. Rehash the path
			// after it completes so an in-place edit or replacement never
			// inherits the original source's recorded QC result.
			using var verifiedSource = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.Read | FileShare.Delete, HashBufferBytes,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			if (verifiedSource.Length != beforeLength ||
				!string.Equals(await ComputeThrottledHashAsync(verifiedSource, cancellationToken)
					.ConfigureAwait(false), hash, StringComparison.Ordinal))
				throw new MediaQcSourceChangedException("Media content changed during the optional QC checks.");
			before.Refresh();
			if (!before.Exists || before.Length != beforeLength || before.LastWriteTimeUtc != beforeStamp)
				throw new MediaQcSourceChangedException("Media source changed during the optional QC checks.");
		}
		cancellationToken.ThrowIfCancellationRequested();
		var report = new MediaQcReport(Guid.NewGuid(), MediaQcReport.CurrentModelRelease,
			MediaQcProfile.OfflineFileV1, asset.AssetId.ToString(), catalogueRevision,
			hash, beforeLength, new DateTimeOffset(beforeStamp, TimeSpan.Zero),
			DateTimeOffset.UtcNow, CheckImplementationVersion, results);
		report.Validate();
		return report;
	}

	private static async Task<string> ComputeThrottledHashAsync(Stream source, CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var buffer = new byte[HashBufferBytes];
		long sinceThrottle = 0;
		int read;
		while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
		{
			hash.AppendData(buffer.AsSpan(0, read));
			sinceThrottle += read;
			if (sinceThrottle >= ThrottleBytes)
			{
				sinceThrottle = 0;
				await Task.Delay(ThrottleDelay, cancellationToken).ConfigureAwait(false);
			}
		}
		cancellationToken.ThrowIfCancellationRequested();
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private sealed class BmffStructure
	{
		private const int MaxBoxes = 20000;
		private const int MaxDepth = 8;
		private int _boxes;
		public bool HasFtyp { get; private set; }
		public bool HasMoov { get; private set; }
		public bool HasMdat { get; private set; }
		public bool HasMovieHeader { get; private set; }
		public bool Truncated { get; private set; }
		public bool LimitExceeded { get; private set; }
		public bool InvalidTimeToSample { get; private set; }
		public int VideoTracks { get; private set; }
		public int AudioTracks { get; private set; }
		public int SampleTables { get; private set; }
		public int VideoSampleTables { get; private set; }
		public int TimeToSampleTables { get; private set; }
		public int VideoTimeToSampleTables { get; private set; }
		private sealed class Track
		{
			public string Handler = "";
			public int TimeToSampleTables;
			public int SampleTables;
		}

		public static BmffStructure Inspect(Stream stream, CancellationToken cancellationToken)
		{
			var result = new BmffStructure();
			result.Scan(stream, 0, stream.Length, 0, null, cancellationToken);
			return result;
		}

		private void Scan(Stream file, long start, long end, int depth,
			Track? track, CancellationToken cancellationToken)
		{
			if (depth > MaxDepth) { LimitExceeded = true; return; }
			long position = start;
			Span<byte> header = stackalloc byte[16];
			while (position < end && !Truncated && !LimitExceeded)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (++_boxes > MaxBoxes) { LimitExceeded = true; return; }
				if (end - position < 8) { Truncated = true; return; }
				file.Position = position;
				try { file.ReadExactly(header[..8]); }
				catch (EndOfStreamException) { Truncated = true; return; }
				ulong declared = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
				var headerBytes = 8;
				if (declared == 1)
				{
					if (end - position < 16) { Truncated = true; return; }
					try { file.ReadExactly(header[8..16]); }
					catch (EndOfStreamException) { Truncated = true; return; }
					declared = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
					headerBytes = 16;
				}
				if (declared == 0) declared = (ulong)(end - position);
				if (declared < (ulong)headerBytes || declared > (ulong)(end - position))
				{ Truncated = true; return; }
				var boxEnd = position + (long)declared;
				var contentStart = position + headerBytes;
				var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
				if (depth == 0)
				{
					if (type == "ftyp") HasFtyp = true;
					if (type == "moov") HasMoov = true;
					if (type == "mdat") HasMdat = true;
				}
				if (type == "mvhd" && depth > 0) HasMovieHeader = true;
				if (type == "stbl" && track is not null)
				{
					SampleTables++;
					track.SampleTables++;
				}
				if (type == "hdlr" && track is not null)
				{
					if (boxEnd - contentStart < 12) { Truncated = true; return; }
					file.Position = contentStart;
					Span<byte> handler = stackalloc byte[12];
					try { file.ReadExactly(handler); }
					catch (EndOfStreamException) { Truncated = true; return; }
					track.Handler = System.Text.Encoding.ASCII.GetString(handler[8..12]);
				}
				if (type == "stts" && track is not null)
				{
					ReadTimeToSample(file, contentStart, boxEnd, cancellationToken);
					if (!Truncated && !LimitExceeded) track.TimeToSampleTables++;
				}
				if (type == "trak")
				{
					var nextTrack = new Track();
					Scan(file, contentStart, boxEnd, depth + 1, nextTrack, cancellationToken);
					if (nextTrack.Handler == "vide")
					{
						VideoTracks++;
						VideoTimeToSampleTables += nextTrack.TimeToSampleTables;
						VideoSampleTables += nextTrack.SampleTables;
					}
					else if (nextTrack.Handler == "soun") AudioTracks++;
				}
				else if (type is "moov" or "mdia" or "minf" or "stbl")
					Scan(file, contentStart, boxEnd, depth + 1, track, cancellationToken);
				position = boxEnd;
			}
		}

		private void ReadTimeToSample(Stream file, long start, long end, CancellationToken cancellationToken)
		{
			if (end - start < 8) { Truncated = true; return; }
			Span<byte> header = stackalloc byte[8];
			file.Position = start;
			try { file.ReadExactly(header); }
			catch (EndOfStreamException) { Truncated = true; return; }
			var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
			if (count > 65536) { LimitExceeded = true; return; }
			if (count == 0) InvalidTimeToSample = true;
			if ((long)count * 8 > end - start - 8) { Truncated = true; return; }
			for (uint i = 0; i < count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try { file.ReadExactly(header); }
				catch (EndOfStreamException) { Truncated = true; return; }
				var samples = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
				var delta = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
				if (samples == 0 || delta == 0) InvalidTimeToSample = true;
			}
			TimeToSampleTables++;
		}
	}
}

public sealed class MediaQcSourceChangedException(string message) : IOException(message);
