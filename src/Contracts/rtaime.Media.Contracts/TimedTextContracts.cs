// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using rtaime.Core;

namespace rtaime.Media.Contracts;

/// <summary>Constrained XML interchange subset; this is not general TTML or EBU schema conformance.</summary>
public enum TimedTextProfile
{
	EbuTtDistribution101 = 1,
	EbuTtLive10 = 2
}

public enum TimedTextTimeBasis
{
	Media = 1,
	Clock = 2
}

public enum TimedTextIssueSeverity
{
	Warning = 1,
	Error = 2
}

public sealed record TimedTextIssue(string Code, TimedTextIssueSeverity Severity, string Detail, string? CueId = null);

/// <summary>Source-raster-relative normalized percent rectangle, not a production output transform.</summary>
public sealed record TimedTextRegion(string Id, decimal X, decimal Y, decimal Width, decimal Height)
{
	public void Validate()
	{
		if (!TimedTextDocument.ValidId(Id) ||
			X is < 0 or > 100 || Y is < 0 or > 100 || Width is <= 0 or > 100 ||
			Height is <= 0 or > 100 || X + Width > 100 || Y + Height > 100)
			throw new InvalidDataException("Caption region must be a bounded percentage rectangle.");
	}

	/// <summary>5% product review margin; not a normatively guaranteed caption-safe area.</summary>
	public bool WithinReviewSafeArea => X >= 5 && Y >= 5 && X + Width <= 95 && Y + Height <= 95;
}

public sealed record TimedTextStyle(
	string Id,
	string FontFamily,
	int FontSizePixels,
	string TextColor,
	string BackgroundColor,
	string Align)
{
	public void Validate()
	{
		if (!TimedTextDocument.ValidId(Id) || FontFamily.Length is < 1 or > 64 ||
			FontFamily.Any(char.IsControl) || FontSizePixels is < 8 or > 96 ||
			!ValidColor(TextColor) || !ValidColor(BackgroundColor) ||
			Align is not ("left" or "center" or "right"))
			throw new InvalidDataException("Caption style is outside the supported bounded text rendering subset.");
	}

	public static bool ValidColor(string value) =>
		(value.Length is 7 or 9) && value[0] == '#' &&
		value.AsSpan(1).ToArray().All(Uri.IsHexDigit);
}

public sealed record TimedTextCue(
	string Id,
	string Language,
	string Role,
	string RegionId,
	string StyleId,
	string Text,
	long BeginTicks,
	long EndTicks)
{
	public void Validate()
	{
		if (!TimedTextDocument.ValidId(Id) ||
			!Regex.IsMatch(Language, @"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z", RegexOptions.CultureInvariant) ||
			Role.Length is > 64 or < 1 || Role.Any(char.IsControl) ||
			!TimedTextDocument.ValidId(RegionId) || !TimedTextDocument.ValidId(StyleId) ||
			string.IsNullOrWhiteSpace(Text) || Text.Length > 1024 ||
			Text.Count(character => character == '\n') >= 8 ||
			Text.Any(character => char.IsControl(character) && character != '\n') ||
			BeginTicks < 0 || EndTicks <= BeginTicks || EndTicks > TimeSpan.TicksPerDay)
			throw new InvalidDataException("Timed Text cue identity, language, text or timing is invalid.");
	}
}

public sealed class TimedTextDocument
{
	public const int MaxXmlBytes = 262_144;
	public const int MaxCues = 256;
	public const int MaxRegions = 16;
	public const int MaxStyles = 16;
	private static readonly Regex Identifier = new(@"\A[A-Za-z_][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant);

	public TimedTextDocument(
		TimedTextProfile profile,
		TimedTextTimeBasis timeBasis,
		string language,
		string sourceSha256,
		IEnumerable<TimedTextRegion> regions,
		IEnumerable<TimedTextStyle> styles,
		IEnumerable<TimedTextCue> cues,
		string? sequenceIdentifier = null,
		ulong? sequenceNumber = null,
		FrameRate? documentFrameRate = null)
	{
		if (!Enum.IsDefined(profile) || !Enum.IsDefined(timeBasis) ||
			!Regex.IsMatch(language ?? "", @"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z", RegexOptions.CultureInvariant) ||
			sourceSha256 is null || sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit))
			throw new InvalidDataException("Timed Text profile, language or content digest is invalid.");
		if (profile == TimedTextProfile.EbuTtDistribution101 && (sequenceIdentifier is not null || sequenceNumber is not null || timeBasis != TimedTextTimeBasis.Media))
			throw new InvalidDataException("EBU-TT-D subset requires media timing and no Live sequence metadata.");
		if (profile == TimedTextProfile.EbuTtLive10 &&
			(string.IsNullOrWhiteSpace(sequenceIdentifier) || sequenceIdentifier.Length > 128 ||
			 sequenceNumber is null or 0))
			throw new InvalidDataException("EBU-TT Live requires a bounded sequence identifier and positive sequence number.");
		if (documentFrameRate is { } rate && (rate.Numerator <= 0 || rate.Denominator <= 0))
			throw new InvalidDataException("Document frame rate must be an exact positive rational.");

		var r = (regions ?? throw new ArgumentNullException(nameof(regions))).ToArray();
		var s = (styles ?? throw new ArgumentNullException(nameof(styles))).ToArray();
		var c = (cues ?? throw new ArgumentNullException(nameof(cues))).ToArray();
		if (r.Length is < 1 or > MaxRegions || s.Length is < 1 or > MaxStyles || c.Length > MaxCues ||
			r.Any(x => x is null) || s.Any(x => x is null) || c.Any(x => x is null))
			throw new InvalidDataException("Caption regions/styles/cue count exceeds the bounded profile.");
		foreach (var value in r) value.Validate();
		foreach (var value in s) value.Validate();
		foreach (var value in c) value.Validate();
		if (r.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != r.Length ||
			s.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != s.Length ||
			c.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != c.Length)
			throw new InvalidDataException("Timed Text IDs must be unique within each resource class.");
		var regionIds = r.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
		var styleIds = s.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
		if (c.Any(x => !regionIds.Contains(x.RegionId) || !styleIds.Contains(x.StyleId)))
			throw new InvalidDataException("Cue references a missing region or style.");

		Profile = profile;
		TimeBasis = timeBasis;
		Language = language;
		SourceSha256 = sourceSha256.ToLowerInvariant();
		SequenceIdentifier = sequenceIdentifier;
		SequenceNumber = sequenceNumber;
		DocumentFrameRate = documentFrameRate;
		Regions = new ReadOnlyCollection<TimedTextRegion>(r);
		Styles = new ReadOnlyCollection<TimedTextStyle>(s);
		Cues = new ReadOnlyCollection<TimedTextCue>(c.OrderBy(x => x.BeginTicks).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray());
	}

	public TimedTextProfile Profile { get; }
	public TimedTextTimeBasis TimeBasis { get; }
	public string Language { get; }
	public string SourceSha256 { get; }
	public string? SequenceIdentifier { get; }
	public ulong? SequenceNumber { get; }
	public FrameRate? DocumentFrameRate { get; }
	public IReadOnlyList<TimedTextRegion> Regions { get; }
	public IReadOnlyList<TimedTextStyle> Styles { get; }
	public IReadOnlyList<TimedTextCue> Cues { get; }

	public IReadOnlyList<TimedTextIssue> InspectLayout()
	{
		var findings = new List<TimedTextIssue>();
		foreach (var region in Regions.Where(x => !x.WithinReviewSafeArea))
			findings.Add(new TimedTextIssue("caption.safe_area_review", TimedTextIssueSeverity.Warning,
				$"Region '{region.Id}' extends beyond the conservative 5% review area."));
		foreach (var cue in Cues)
		{
			if (Cues.Any(other => other.Id != cue.Id &&
				other.RegionId == cue.RegionId && other.Language == cue.Language &&
				other.BeginTicks < cue.EndTicks && cue.BeginTicks < other.EndTicks))
				findings.Add(new TimedTextIssue("caption.region_overlap", TimedTextIssueSeverity.Warning,
					"Overlapping same-language cues compete for the same region.", cue.Id));
		}
		return findings.AsReadOnly();
	}

	public static bool ValidId(string? value) => value is not null && Identifier.IsMatch(value);
}
