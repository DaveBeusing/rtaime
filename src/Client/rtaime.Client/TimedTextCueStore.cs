// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>Deterministic half-open [begin,end) frame schedule on an exact Program/media rational clock.</summary>
public static class TimedTextFrameClock
{
	public static long FirstFrameAtOrAfter(long ticks, FrameRate rate)
	{
		if (ticks < 0 || ticks > TimeSpan.TicksPerDay ||
			rate.Numerator <= 0 || rate.Denominator <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticks));
		var frames = (decimal)ticks * rate.Numerator /
			(TimeSpan.TicksPerSecond * (decimal)rate.Denominator);
		// TTML rational frame codes are serialized as 100 ns ticks. A genuine frame
		// boundary can round upward by half a tick at 60000/1001; do not shift that
		// boundary one whole frame while still rejecting a full tick beyond it.
		var halfTickInFrames = (decimal)rate.Numerator /
			(2m * TimeSpan.TicksPerSecond * rate.Denominator);
		return checked((long)decimal.Ceiling(frames - halfTickInFrames));
	}

	public static bool IsActive(TimedTextCue cue, long confirmedFrame, FrameRate rate) =>
		confirmedFrame >= 0 &&
		confirmedFrame >= FirstFrameAtOrAfter(cue.BeginTicks, rate) &&
		confirmedFrame < FirstFrameAtOrAfter(cue.EndTicks, rate);
}

public enum TimedTextPreviewState
{
	Unavailable = 0,
	Ready = 1,
	Active = 2,
	Unverified = 3,
	Discontinuity = 4
}

public sealed record TimedTextFramePreview(
	TimedTextPreviewState State,
	long ConfirmedFrame,
	string Language,
	IReadOnlyList<TimedTextCue> ActiveCues,
	IReadOnlyList<TimedTextIssue> Issues,
	string Detail)
{
	public bool IsProgramOutput => false;
	public string Text => string.Join("\n", ActiveCues.Select(cue => cue.Text));

	public static TimedTextFramePreview Empty(TimedTextPreviewState state, long frame, string detail) =>
		new(state, frame, "", Array.Empty<TimedTextCue>(), Array.Empty<TimedTextIssue>(), detail);
}

/// <summary>
/// Local, source-bound import and inspection state, distinct from media cue points,
/// durable Media Library tags, editorial CG and authoritative Program state.
/// </summary>
public sealed class TimedTextCueStore
{
	private TimedTextDocument? _document;
	private string? _sourceIdentity;
	private string? _selectedLanguage;
	private bool _enabled = true;
	private ulong _revision;
	private IReadOnlyList<TimedTextIssue> _layoutIssues = Array.Empty<TimedTextIssue>();

	public ulong Revision => _revision;
	public bool Enabled { get => _enabled; set { _enabled = value; _revision++; } }
	public TimedTextDocument? Current => _document;
	public string? SourceIdentity => _sourceIdentity;
	public string? SelectedLanguage => _selectedLanguage;

	public void Clear()
	{
		_document = null;
		_sourceIdentity = null;
		_selectedLanguage = null;
		_layoutIssues = Array.Empty<TimedTextIssue>();
		_revision++;
	}

	public void Import(string mediaSourceIdentity, TimedTextDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(mediaSourceIdentity);
		ArgumentNullException.ThrowIfNull(document);
		if (mediaSourceIdentity.Length > 256)
			throw new ArgumentOutOfRangeException(nameof(mediaSourceIdentity));
		var layoutIssues = document.InspectLayout(); // Precompute once, not every timeline frame.
		_document = document;
		_sourceIdentity = mediaSourceIdentity;
		_selectedLanguage = document.Cues.Any(cue => string.Equals(cue.Language, document.Language, StringComparison.OrdinalIgnoreCase))
			? document.Language
			: document.Cues.FirstOrDefault()?.Language ?? document.Language;
		_layoutIssues = layoutIssues;
		_revision++;
	}

	public void SelectLanguage(string language)
	{
		if (_document is null ||
			!_document.Cues.Any(cue => string.Equals(cue.Language, language, StringComparison.OrdinalIgnoreCase)))
			throw new InvalidOperationException("Caption language is not present in the imported cue document.");
		_selectedLanguage = language;
		_revision++;
	}

	public TimedTextFramePreview Preview(
		string? confirmedMediaSourceIdentity,
		long confirmedFrame,
		FrameRate? confirmedFrameRate,
		bool confirmedMedia,
		bool transportUncertain)
	{
		if (_document is null || _sourceIdentity is null || !_enabled)
			return TimedTextFramePreview.Empty(TimedTextPreviewState.Unavailable, confirmedFrame,
				"NO CAPTIONS · local preview is disabled or no Timed Text file is loaded.");
		if (!confirmedMedia || transportUncertain || confirmedFrameRate is null ||
			!string.Equals(confirmedMediaSourceIdentity, _sourceIdentity, StringComparison.Ordinal) ||
			confirmedFrame < 0)
			return TimedTextFramePreview.Empty(TimedTextPreviewState.Unverified, confirmedFrame,
				"UNVERIFIED · media source, playback frame or transport confirmation is stale or changed.");
		if (_document.TimeBasis != TimedTextTimeBasis.Media)
			return TimedTextFramePreview.Empty(TimedTextPreviewState.Unverified, confirmedFrame,
				"UNVERIFIED · Live clock timeBase has no authoritative Runtime-to-Program clock origin.");
		var active = _document.Cues.Where(cue =>
			string.Equals(cue.Language, _selectedLanguage, StringComparison.OrdinalIgnoreCase) &&
			TimedTextFrameClock.IsActive(cue, confirmedFrame, confirmedFrameRate.Value)).ToArray();
		var state = active.Length == 0 ? TimedTextPreviewState.Ready : TimedTextPreviewState.Active;
		return new(state, confirmedFrame, _selectedLanguage ?? _document.Language,
			Array.AsReadOnly(active), _layoutIssues,
			active.Length == 0
				? "PREVIEW ONLY · no caption at confirmed frame; no Program overlay or ANC transport."
				: "PREVIEW ONLY · local text evidence at confirmed media frame; not Runtime composited or Program-approved.");
	}
}

/// <summary>
/// Ordered EBU-TT Live document state. Gaps and duplicates require explicit resync;
/// a late segment cannot resurrect potentially stale or replaced captions.
/// </summary>
public sealed class TimedTextLiveSegmentStore
{
	private readonly List<TimedTextDocument> _segments = [];
	private string? _sequenceIdentifier;
	private ulong? _lastSequence;
	private bool _requiresResync;
	private ulong _generation;

	public bool RequiresResynchronization => _requiresResync;
	public ulong Generation => _generation;
	public IReadOnlyList<TimedTextDocument> Segments => _segments.AsReadOnly();

	public void BeginNewSequence(string identifier)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
		if (identifier.Length > 128) throw new ArgumentOutOfRangeException(nameof(identifier));
		_sequenceIdentifier = identifier;
		_lastSequence = null;
		_requiresResync = false;
		_segments.Clear();
		_generation++;
	}

	public bool Accept(TimedTextDocument segment)
	{
		ArgumentNullException.ThrowIfNull(segment);
		if (segment.Profile != TimedTextProfile.EbuTtLive10 || _sequenceIdentifier is null ||
			_requiresResync || segment.SequenceIdentifier != _sequenceIdentifier)
			return Invalidate();
		if (_lastSequence.HasValue &&
			(_lastSequence.Value == ulong.MaxValue ||
			 segment.SequenceNumber != _lastSequence.Value + 1))
			return Invalidate();
		if (_segments.Count == 16)
			_segments.RemoveAt(0);
		_segments.Add(segment);
		_lastSequence = segment.SequenceNumber;
		_generation++;
		return true;
	}

	public void SignalGap() => Invalidate();

	private bool Invalidate()
	{
		_segments.Clear();
		_requiresResync = true;
		_generation++;
		return false;
	}
}
