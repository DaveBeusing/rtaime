// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

/// <summary>
/// Local, ephemeral caption import/preview owner attached to the existing Media Timeline.
/// No ControlHost commands, no Program text rasterization, no project/catalogue persistence.
/// </summary>
public sealed class TimedTextReviewViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly TimedTextCueStore _store = new();
	private MediaTimelineViewModel? _timeline;
	private string? _context;
	private ulong _contextEpoch;
	private string _status = "NO CAPTIONS · Import EBU-TT-D or Live XML for local review.";
	private string _text = "";
	private string _detail = "TEXT-ONLY PREVIEW · no Runtime/Program overlay; native ANC output UNVERIFIED.";
	private string _fontEvidence = "FONT/GLYPH QUALIFICATION UNAVAILABLE";
	private string _language = "—";
	private bool _disposed;
	private IReadOnlyList<TimedTextIssue> _fontIssues = Array.Empty<TimedTextIssue>();

	public event PropertyChangedEventHandler? PropertyChanged;
	public ulong ContextEpoch => _contextEpoch;
	public string? SourceContext => _context;
	public string Status => _status;
	public string PreviewText => _text;
	public string Detail => _detail;
	public string FontEvidence => _fontEvidence;
	public string Language => _language;
	public bool IsLocalPreviewEnabled
	{
		get => _store.Enabled;
		set
		{
			if (_store.Enabled == value) return;
			_store.Enabled = value;
			Changed(nameof(IsLocalPreviewEnabled));
			Refresh();
		}
	}
	public bool ProgramOverlayAdmitted => false;
	public TimedTextDocument? ImportedDocument => _store.Current;

	public void Attach(MediaTimelineViewModel? timeline)
	{
		if (_disposed || ReferenceEquals(_timeline, timeline)) return;
		if (_timeline is not null) _timeline.PropertyChanged -= OnTimelineChanged;
		_timeline = timeline;
		if (_timeline is not null) _timeline.PropertyChanged += OnTimelineChanged;
		ChangeContext(timeline?.TimedTextSourceContext);
		Refresh();
	}

	public void Detach()
	{
		if (_timeline is not null) _timeline.PropertyChanged -= OnTimelineChanged;
		_timeline = null;
		ChangeContext(null);
		Refresh();
	}

	/// <summary>Only accept a completed read/parse against the unchanged confirmed source generation.</summary>
	public bool Import(ReadOnlySpan<byte> xml, TimedTextProfile profile, ulong expectedContextEpoch)
	{
		if (_disposed) return false;
		if (_context is null || _contextEpoch != expectedContextEpoch ||
			_timeline is not { IsLoaded: true, RequiresSynchronization: false } ||
			_timeline.TimedTextConfirmedFrameRate is null)
		{
			_status = "UNVERIFIED · Timeline context changed while reading the caption document.";
			Changed(nameof(Status));
			return false;
		}

		try
		{
			var parsed = TimedTextInterchangeCodec.Parse(xml, profile);
			if (_contextEpoch != expectedContextEpoch) return false;
			_store.Import(_context, parsed);
			_fontIssues = ValidateFontsAndGlyphs(parsed);
			_fontEvidence = _fontIssues.Count == 0
				? "FONT/GLYPHS · installed typefaces and text glyph maps checked locally; Program/monitor calibration UNVERIFIED"
				: string.Join(" · ", _fontIssues.Take(3).Select(issue => issue.Detail));
			_language = parsed.Cues.Select(cue => cue.Language)
				.Distinct(StringComparer.OrdinalIgnoreCase).Aggregate(parsed.Language,
					(text, next) => text.Contains(next, StringComparison.OrdinalIgnoreCase) ? text : text + ", " + next);
			Changed(nameof(ImportedDocument));
			Changed(nameof(FontEvidence));
			Changed(nameof(Language));
			Refresh();
			return true;
		}
		catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException
			or DecoderFallbackException or FormatException or OverflowException)
		{
			_store.Clear();
			_fontIssues = Array.Empty<TimedTextIssue>();
			_text = "";
			_status = "REJECTED · Timed Text XML import failed profile/security/layout validation.";
			_detail = exception.Message.Length <= 200 ? exception.Message : exception.Message[..200];
			_fontEvidence = "FONT/GLYPHS UNVERIFIED";
			_language = "—";
			NotifyAll();
			return false;
		}
	}

	public void ImportFailure(string reason, ulong expectedContextEpoch)
	{
		if (_contextEpoch != expectedContextEpoch) return;
		_store.Clear();
		_status = "UNAVAILABLE · Timed Text import not completed.";
		_detail = reason.Length > 160 ? reason[..160] : reason;
		_text = "";
		_fontEvidence = "FONT/GLYPHS UNVERIFIED";
		_language = "—";
		NotifyAll();
	}

	private void OnTimelineChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed || _timeline is null) return;
		if (e.PropertyName is nameof(MediaTimelineViewModel.TimedTextSourceContext)
			or nameof(MediaTimelineViewModel.IsLoaded))
		{
			var current = _timeline.IsLoaded ? _timeline.TimedTextSourceContext : null;
			if (!string.Equals(_context, current, StringComparison.Ordinal))
				ChangeContext(current);
		}
		if (e.PropertyName is nameof(MediaTimelineViewModel.TimedTextSourceContext)
			or nameof(MediaTimelineViewModel.TimedTextConfirmedFrameRate)
			or nameof(MediaTimelineViewModel.ConfirmedFrame)
			or nameof(MediaTimelineViewModel.IsLoaded)
			or nameof(MediaTimelineViewModel.RequiresSynchronization)
			or nameof(MediaTimelineViewModel.HasPendingSeek))
			Refresh();
	}

	private void ChangeContext(string? context)
	{
		_store.Clear();
		_context = context;
		_contextEpoch++;
		_fontIssues = Array.Empty<TimedTextIssue>();
		_fontEvidence = "FONT/GLYPHS UNVERIFIED";
		_language = "—";
		NotifyAll();
	}

	private void Refresh()
	{
		if (_timeline is null || _context is null)
		{
			_status = "NO CONFIRMED SOURCE · Timed Text local preview unavailable.";
			_text = "";
			_detail = "Load a local media source before importing EBU-TT XML; Program remains unaffected.";
		}
		else
		{
			var preview = _store.Preview(
				_timeline.TimedTextSourceContext, _timeline.ConfirmedFrame,
				_timeline.TimedTextConfirmedFrameRate, _timeline.IsLoaded,
				_timeline.RequiresSynchronization || _timeline.HasPendingSeek);
			_text = preview.Text;
			_status = preview.State switch
			{
				TimedTextPreviewState.Active => $"LOCAL CAPTION PREVIEW · {preview.ActiveCues.Count} CUE(S) · frame {preview.ConfirmedFrame}",
				TimedTextPreviewState.Ready => $"LOCAL CAPTIONS READY · no active cue at frame {preview.ConfirmedFrame}",
				TimedTextPreviewState.Unverified => "CAPTION PREVIEW UNVERIFIED · source/clock/transport not confirmed",
				_ => "NO CAPTIONS · Import EBU-TT-D or Live XML for local review."
			};
			_detail = preview.Detail + (_store.Current is null ? "" :
				$" · {_store.Current.Profile} · {_store.Current.Cues.Count} cues · " +
				$"{_store.Current.InspectLayout().Count} region/timing warning(s)");
		}
		NotifyAll();
	}

	private static IReadOnlyList<TimedTextIssue> ValidateFontsAndGlyphs(TimedTextDocument document)
	{
		var findings = new List<TimedTextIssue>();
		foreach (var style in document.Styles)
		{
			var family = Fonts.SystemFontFamilies.FirstOrDefault(candidate =>
				string.Equals(candidate.Source, style.FontFamily, StringComparison.OrdinalIgnoreCase));
			if (family is null)
			{
				findings.Add(new("caption.font_missing", TimedTextIssueSeverity.Warning,
					$"Font '{style.FontFamily}' is not installed; no implicit font substitution."));
				continue;
			}
			if (!new Typeface(family, System.Windows.FontStyles.Normal,
					System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal)
				.TryGetGlyphTypeface(out var glyphFace) || glyphFace is null)
			{
				findings.Add(new("caption.glyph_unverified", TimedTextIssueSeverity.Warning,
					$"Font '{style.FontFamily}' glyph map UNVERIFIED."));
				continue;
			}
			var missingGlyph = false;
			foreach (var cue in document.Cues.Where(cue => cue.StyleId == style.Id))
			{
				foreach (var rune in cue.Text.EnumerateRunes())
				{
					if (glyphFace.CharacterToGlyphMap.TryGetValue(rune.Value, out var glyph) && glyph != 0)
						continue;
					missingGlyph = true;
					break;
				}
				if (missingGlyph) break;
			}
			if (missingGlyph)
				findings.Add(new("caption.glyph_missing", TimedTextIssueSeverity.Warning,
					$"Missing glyph in font '{style.FontFamily}'; output cannot be qualified."));
		}
		return findings.AsReadOnly();
	}

	private void NotifyAll()
	{
		Changed(nameof(Status));
		Changed(nameof(PreviewText));
		Changed(nameof(Detail));
		Changed(nameof(FontEvidence));
		Changed(nameof(Language));
		Changed(nameof(ImportedDocument));
	}

	private void Changed([CallerMemberName] string? property = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

	public void Dispose()
	{
		if (_disposed) return;
		Detach();
		_disposed = true;
	}
}
