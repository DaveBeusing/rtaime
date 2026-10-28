// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.RegularExpressions;

namespace rtaime.Media.Contracts;

// This metadata contract is not part of the Runtime/AudioProduction execution
// graph. It neither provisions a decoder nor asserts immersive capabilities.
public sealed record AdmTrackAssignment(
	int PhysicalTrackNumber,
	string AudioTrackUid,
	string R123Role,
	string? Language = null)
{
	public void Validate()
	{
		if (PhysicalTrackNumber is < 1 or > 16 ||
			!AdmAudioInterchange.ValidAdmId(AudioTrackUid, "ATU") ||
			string.IsNullOrWhiteSpace(R123Role) || R123Role.Length > 32 ||
			R123Role.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
			Language is not null && !AdmAudioInterchange.ValidLanguage(Language))
			throw new InvalidDataException("Invalid explicit ADM physical-track assignment.");
	}
}

/// <summary>
/// Source-bound, byte-exact ADM XML sidecar. SourceXmlBase64 retains every
/// source byte including whitespace, namespaces, BOM and unknown XML fields.
/// Unlike compact EBUCore catalogue fields, ADM XML is never flattened into
/// 2-KiB mapped metadata or inserted into a Runtime management snapshot.
/// </summary>
public sealed record AdmAudioInterchange(
	string Format,
	string AssetId,
	string FingerprintSha256,
	string AdmVersion,
	string Profile,
	string SourceXmlBase64,
	string? R123Allocation,
	AdmTrackAssignment[] Assignments,
	DateTimeOffset ImportedAtUtc)
{
	public const string CurrentFormat = "rtaime.media-adm.tech3393-2025.v1";
	public const string SupportedAdmVersion = "ITU-R_BS.2076-3";
	public const string ProductionGuidance = "EBU-Tech-3393-2025-10-07";
	public const int MaximumXmlBytes = 192 * 1024;
	public const int MaximumJsonBytes = 320 * 1024;
	public const int MaximumTracks = 16;

	public void Validate(string? expectedAssetId = null, string? expectedFingerprint = null)
	{
		if (Format != CurrentFormat ||
			!Guid.TryParseExact(AssetId, "D", out var id) || id == Guid.Empty ||
			FingerprintSha256 is null || FingerprintSha256.Length != 64 ||
			FingerprintSha256.Any(c => !Uri.IsHexDigit(c)) ||
			AdmVersion != SupportedAdmVersion || Profile != ProductionGuidance ||
			SourceXmlBase64 is null || SourceXmlBase64.Length is < 4 or > MaximumXmlBytes * 4 / 3 + 12 ||
			ImportedAtUtc.Offset != TimeSpan.Zero ||
			expectedAssetId is not null && !string.Equals(AssetId, expectedAssetId, StringComparison.OrdinalIgnoreCase) ||
			expectedFingerprint is not null && !string.Equals(
				FingerprintSha256, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("ADM interchange format, version, source identity or size is unsupported.");
		if (Assignments is null || Assignments.Length > MaximumTracks)
			throw new InvalidDataException("ADM track-assignment count is out of bounds.");
		foreach (var item in Assignments)
		{
			if (item is null) throw new InvalidDataException("ADM track assignment is missing.");
			item.Validate();
		}
		if (Assignments.Select(a => a.PhysicalTrackNumber).Distinct().Count() != Assignments.Length ||
			Assignments.Select(a => a.AudioTrackUid).Distinct(StringComparer.Ordinal).Count() != Assignments.Length)
			throw new InvalidDataException("Duplicated physical track or ADM track UID.");
		if (!R123TrackAllocation.IsRecognized(R123Allocation))
			throw new InvalidDataException("Unsupported EBU R 123 allocation profile.");
		R123TrackAllocation.Validate(R123Allocation, Assignments);
		// Format check also rejects invalid Base64 without materializing an
		// unbounded document. Strict XML parse is required at the boundary.
		var sourceBytes = Convert.FromBase64String(SourceXmlBase64);
		if (sourceBytes.Length is < 16 or > MaximumXmlBytes)
			throw new InvalidDataException("ADM XML byte budget exceeded.");
	}

	public byte[] OriginalXmlBytes()
	{
		Validate();
		return Convert.FromBase64String(SourceXmlBase64);
	}

	public static bool ValidAdmId(string? value, string prefix) =>
		value is not null && value.Length <= 32 &&
		Regex.IsMatch(value, @"\A" + Regex.Escape(prefix) +
			@"_[0-9A-Fa-f]{4,8}(?:_[0-9A-Fa-f]{2})?\z", RegexOptions.CultureInvariant);

	public static bool ValidLanguage(string? language) =>
		!string.IsNullOrWhiteSpace(language) && language.Length <= 32 &&
		Regex.IsMatch(language, @"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z",
			RegexOptions.CultureInvariant);
}

public static class R123TrackAllocation
{
	// The named reference arrangements are derived from EBU R 123 (2009)
	// Table 1. Each requires explicit physical-track evidence; a 6-channel
	// file alone must never be claimed as the eight-track R 123 '8a'/'8b'.
	private static readonly IReadOnlyDictionary<string, string[]> Allocations =
		new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			["4b"] = ["S_L", "S_R", "IT_OR_EFX_L", "IT_OR_EFX_R"],
			["8a"] = ["S_L", "S_R", "L", "R", "C", "LFE", "Ls", "Rs"],
			["8b"] = ["L", "R", "C", "LFE", "Ls", "Rs", "S_L", "S_R"]
		};

	public static bool IsRecognized(string? code) =>
		code is null || Allocations.ContainsKey(code);

	public static void Validate(string? code, IReadOnlyList<AdmTrackAssignment> assignments)
	{
		if (code is null)
		{
			if (assignments.Count != 0)
				throw new InvalidDataException("A physical track assignment requires an explicit R 123 profile.");
			return;
		}
		if (!Allocations.TryGetValue(code, out var roles) || assignments.Count != roles.Length)
			throw new InvalidDataException("EBU R 123 allocation track count does not match the selected profile.");
		for (var i = 0; i < roles.Length; i++)
		{
			var item = assignments.SingleOrDefault(a => a.PhysicalTrackNumber == i + 1);
			if (item is null || !string.Equals(item.R123Role, roles[i], StringComparison.Ordinal))
				throw new InvalidDataException("Explicit physical-track role differs from EBU R 123 allocation " + code + ".");
		}
	}

	public static IReadOnlyList<string> ExpectedRoles(string code) =>
		Allocations.TryGetValue(code, out var roles)
			? Array.AsReadOnly((string[])roles.Clone())
			: throw new NotSupportedException("The R 123 profile is outside the admitted subset.");
}

public sealed record AdmTrackMetadataRow(
	string AudioTrackUid,
	int? PhysicalTrackNumber,
	string? ChannelRole,
	string? Language,
	string? AudioObjectId,
	string? AudioPackFormatId,
	string? Start,
	string? Duration,
	string SourceKind)
{
	public string DisplayTrack => PhysicalTrackNumber is int number
		? "Track " + number : "TRACK UNMAPPED · " + AudioTrackUid;
	public string DisplayStatus => "METADATA ONLY · DECODER UNVERIFIED · IMMERSIVE RENDER UNSUPPORTED";
}

/// <summary>
/// Parsed/retained metadata is not a media capability. A detected object or
/// 5.1 layout cannot become a playable, renderable or publishable signal.
/// </summary>
public sealed record AdmAudioInspection(
	AdmAudioInterchange Interchange,
	IReadOnlyList<AdmTrackMetadataRow> Tracks,
	int ProgrammeCount,
	int AudioObjectCount,
	string LayoutLabel,
	string InterchangeStatus = "PARSED · TECH 3393 PROFILE CONFORMANCE UNVERIFIED",
	string DecodeStatus = "UNVERIFIED",
	string RenderStatus = "UNSUPPORTED · IMMERSIVE RENDERING NOT IMPLEMENTED",
	string PublishStatus = "UNSUPPORTED · IMMERSIVE OUTPUT NOT IMPLEMENTED");
