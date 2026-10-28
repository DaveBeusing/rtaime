// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media.Contracts;

// Internal QC outcomes are distinct from EBU QC ExecutionStatus (complete/error).
// An absent or unsupported measurement must never be represented as CheckResult=true.
public enum MediaQcCheckState { NotExecuted, Unsupported, Failed, Pass }
public enum MediaQcJobState { Queued, Running, Completed, Cancelled, Failed, Stale }

public sealed record MediaQcCheckDefinition(
	string Id, string Name, string Version, string Description,
	string CheckResultDefinition)
{
	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 ||
			!Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
			!System.Text.RegularExpressions.Regex.IsMatch(Version, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)$") ||
			string.IsNullOrWhiteSpace(Name) || Name.Length > 128 ||
			string.IsNullOrWhiteSpace(Description) || Description.Length > 512 ||
			string.IsNullOrWhiteSpace(CheckResultDefinition) || CheckResultDefinition.Length > 512)
			throw new ArgumentException("Invalid bounded QC check definition.");
	}
}

public sealed record MediaQcFrameRange(long Start, long End, uint EditRateNumerator, uint EditRateDenominator)
{
	public void Validate()
	{
		if (Start < 0 || End <= Start || EditRateNumerator == 0 || EditRateDenominator == 0)
			throw new ArgumentException("QC edit-unit range and rational edit rate must be positive and ordered.");
	}
}

public sealed record MediaQcCheckResult(
	MediaQcCheckDefinition Definition,
	MediaQcCheckState State,
	string Detail,
	int Severity,
	double? Confidence,
	string EvidenceSource,
	string ToolVersion,
	MediaQcFrameRange? Range = null,
	string? TrackId = null)
{
	public void Validate()
	{
		Definition.Validate();
		if (!Enum.IsDefined(State) || string.IsNullOrWhiteSpace(Detail) || Detail.Length > 2048 ||
			Severity is < 0 or > 10 ||
			Confidence is double confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1) ||
			string.IsNullOrWhiteSpace(EvidenceSource) || EvidenceSource.Length > 256 ||
			string.IsNullOrWhiteSpace(ToolVersion) || ToolVersion.Length > 64 ||
			TrackId is { Length: > 128 })
			throw new ArgumentException("Invalid QC result fields.");
		if (State is MediaQcCheckState.NotExecuted or MediaQcCheckState.Unsupported &&
			(Confidence.HasValue || Severity != 0))
			throw new ArgumentException("Unexecuted/unsupported checks cannot claim a severity or confidence.");
		Range?.Validate();
	}
}

public sealed record MediaQcProfile(string ProfileId, string Name, string Version,
	IReadOnlyList<MediaQcCheckDefinition> Checks)
{
	public static MediaQcProfile OfflineFileV1 { get; } = new(
		"b65f72d6-a8f0-4bbd-9d31-9e8abed70a05",
		"rtaime offline media structural QC",
		"1.0",
		[
			new("rtaime-container", "Container structure", "1.0",
				"Observe top-level ISO BMFF box identity and legal boundaries.",
				"True when recognised container boxes satisfy the bounded structural scan."),
			new("rtaime-truncation", "Container truncation", "1.0",
				"Inspect top-level box lengths and the declared file boundary.",
				"True when all inspected top-level boxes fit the complete hashed source."),
			new("rtaime-streams", "Track presence", "1.0",
				"Identify actual video/audio handlers in the container tracks.",
				"True when the expected catalogued video and optional audio handlers exist."),
			new("rtaime-timestamp", "Decode timestamp deltas", "1.0",
				"Check parsed stts decode-time deltas for strictly positive duration.",
				"True for every parsed time-to-sample entry; does not establish presentation-timestamp monotonicity."),
			new("rtaime-metadata", "Container metadata availability", "1.0",
				"Inspect the existence of a movie header and track sample tables.",
				"True when a movie header and sample tables are structurally present."),
			new("rtaime-loudness", "Programme loudness", "1.0",
				"Future offline file loudness adapter; Runtime programme values are not file QC.",
				"Requires independent offline R 128 measurement."),
			new("rtaime-range", "Video signal range", "1.0",
				"Future source-bound R 103/colour-range adapter.",
				"Requires independently qualified full source analysis.")
		]);

	public void Validate()
	{
		if (!Guid.TryParse(ProfileId, out _) || string.IsNullOrWhiteSpace(Name) ||
			Name.Length > 128 || Version != "1.0" ||
			Checks is null || Checks.Count is < 1 or > 32)
			throw new ArgumentException("Invalid or unsupported QC profile.");
		foreach (var check in Checks) check.Validate();
		if (Checks.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Checks.Count)
			throw new ArgumentException("Duplicate QC check identifiers.");
	}
}

public sealed record MediaQcReport(
	Guid ReportId, string ModelRelease, MediaQcProfile Profile, string AssetId,
	ulong CatalogueRevision, string FingerprintSha256, long SourceLengthBytes,
	DateTimeOffset SourceModifiedUtc, DateTimeOffset CreatedUtc,
	string ToolVersion, IReadOnlyList<MediaQcCheckResult> Results)
{
	public const string CurrentModelRelease = "2026-04";
	public const int MaxResults = 32;
	public bool FullyEvaluated => Results.All(r => r.State is MediaQcCheckState.Pass or MediaQcCheckState.Failed);
	public bool Passed => FullyEvaluated && Results.All(r => r.State == MediaQcCheckState.Pass);
	public void Validate()
	{
		Profile.Validate();
		if (ReportId == Guid.Empty || ModelRelease != CurrentModelRelease ||
			!Guid.TryParse(AssetId, out _) ||
			FingerprintSha256.Length != 64 || FingerprintSha256.Any(x => !Uri.IsHexDigit(x)) ||
			SourceLengthBytes <= 0 || CreatedUtc.Offset != TimeSpan.Zero ||
			SourceModifiedUtc.Offset != TimeSpan.Zero ||
			string.IsNullOrWhiteSpace(ToolVersion) || ToolVersion.Length > 64 ||
			Results is null || Results.Count != Profile.Checks.Count || Results.Count > MaxResults)
			throw new ArgumentException("Invalid QC report identity, version, or source evidence.");
		for (var index = 0; index < Results.Count; index++)
		{
			Results[index].Validate();
			if (Results[index].Definition != Profile.Checks[index])
				throw new ArgumentException("QC result does not match the versioned profile definition.");
		}
	}
}

public sealed record MediaQcJobSnapshot(
	Guid JobId, string AssetId, MediaQcJobState State, string Detail,
	MediaQcReport? Report, DateTimeOffset UpdatedUtc);
