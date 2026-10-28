// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Text;
using System.Xml;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class TimedTextInterchangeTests
{
	private const string Head = """
	  <head>
	    <metadata><ebuttm:conformsToStandard>urn:ebu:tt:distribution:2018-04</ebuttm:conformsToStandard></metadata>
	    <styling><style xml:id="base" tts:fontFamily="Segoe UI" tts:fontSize="32px" tts:color="#FFFFFFFF" tts:backgroundColor="#00000000" tts:textAlign="center"/></styling>
	    <layout><region xml:id="bottom" tts:origin="5% 75%" tts:extent="90% 20%"/></layout>
	  </head>
	""";
	private const string LiveHead = """
	  <head>
	    <styling><style xml:id="base" tts:fontFamily="Segoe UI" tts:fontSize="32px" tts:color="#FFFFFFFF" tts:backgroundColor="#00000000" tts:textAlign="center"/></styling>
	    <layout><region xml:id="bottom" tts:origin="5% 75%" tts:extent="90% 20%"/></layout>
	  </head>
	""";

	[Fact]
	public void Distribution_v101_import_export_preserves_explicit_language_role_region_style_and_utf8()
	{
		var original = Distribution("<p xml:id=\"p1\" xml:lang=\"de-DE\" ttm:role=\"subtitle\" " +
			"region=\"bottom\" style=\"base\" begin=\"00:00:01.000\" end=\"00:00:02.000\">Grüße 世界<span>!</span><br/>Zweite Zeile</p>");
		var input = Encoding.UTF8.GetBytes(original);
		var doc = TimedTextInterchangeCodec.Parse(input, TimedTextProfile.EbuTtDistribution101);
		Assert.Equal("de-DE", Assert.Single(doc.Cues).Language);
		Assert.Equal("subtitle", doc.Cues[0].Role);
		Assert.Equal("Grüße 世界!\nZweite Zeile", doc.Cues[0].Text);
		Assert.Equal("bottom", doc.Cues[0].RegionId);
		Assert.Equal("base", doc.Cues[0].StyleId);
		Assert.Empty(doc.InspectLayout());
		var exported = TimedTextInterchangeCodec.Serialize(doc);
		var again = TimedTextInterchangeCodec.Parse(exported, TimedTextProfile.EbuTtDistribution101);
		Assert.Equal(doc.Cues[0], Assert.Single(again.Cues));
		Assert.Equal(doc.Styles[0], again.Styles[0]);
		Assert.Equal(doc.Regions[0], again.Regions[0]);
		Assert.NotEmpty(again.SourceSha256);
		Assert.NotEqual(doc.SourceSha256, again.SourceSha256);
	}

	[Theory]
	[InlineData("25", "00:00:01:24", 49L)]
	[InlineData("50", "00:00:01:49", 99L)]
	public void Frame_timecode_import_preserves_integer_frame_boundaries(string rate, string time, long expectedFirst)
	{
		var document = TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(
			Distribution("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"" +
				time + "\" dur=\"1f\">Frame aligned</p>", rate)), TimedTextProfile.EbuTtDistribution101);
		var effective = document.DocumentFrameRate!.Value;
		Assert.Equal(expectedFirst, TimedTextFrameClock.FirstFrameAtOrAfter(document.Cues[0].BeginTicks, effective));
		Assert.True(TimedTextFrameClock.IsActive(document.Cues[0], expectedFirst, effective));
		Assert.False(TimedTextFrameClock.IsActive(document.Cues[0], expectedFirst + 1, effective));
	}

	[Fact]
	public void Exact_60000_over_1001_timing_has_no_rounded_59_94_drift()
	{
		var xml = Distribution(
			"<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"00:00:01.000\" end=\"00:00:02.000\">Frame</p>",
			rate: "60", multiplier: "1000 1001");
		var document = TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(xml), TimedTextProfile.EbuTtDistribution101);
		var fps = document.DocumentFrameRate!.Value;
		Assert.Equal(60000, fps.Numerator);
		Assert.Equal(1001, fps.Denominator);
		Assert.Equal(60L, TimedTextFrameClock.FirstFrameAtOrAfter(document.Cues[0].BeginTicks, fps));
		Assert.Equal(120L, TimedTextFrameClock.FirstFrameAtOrAfter(document.Cues[0].EndTicks, fps));
		Assert.False(TimedTextFrameClock.IsActive(document.Cues[0], 59, fps));
		Assert.True(TimedTextFrameClock.IsActive(document.Cues[0], 60, fps));
		Assert.True(TimedTextFrameClock.IsActive(document.Cues[0], 119, fps));
		Assert.False(TimedTextFrameClock.IsActive(document.Cues[0], 120, fps));
	}

	[Fact]
	public void Source_bound_preview_reacts_to_pause_seek_transport_unknown_and_source_replacement()
	{
		var document = TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(
			Distribution("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">Caption</p>")),
			TimedTextProfile.EbuTtDistribution101);
		var store = new TimedTextCueStore();
		store.Import("asset-a/preview", document);
		var rate = new FrameRate(25, 1);
		var active = store.Preview("asset-a/preview", 25, rate, confirmedMedia: true, transportUncertain: false);
		Assert.Equal(TimedTextPreviewState.Active, active.State);
		Assert.Equal("Caption", active.Text);
		Assert.False(active.IsProgramOutput);
		// A paused confirmed frame does not advance and keeps the same cue.
		Assert.Equal(active.Text, store.Preview("asset-a/preview", 25, rate, true, false).Text);
		Assert.Equal(TimedTextPreviewState.Ready, store.Preview("asset-a/preview", 55, rate, true, false).State);
		Assert.Equal(TimedTextPreviewState.Unverified, store.Preview("asset-b/preview", 25, rate, true, false).State);
		Assert.Equal(TimedTextPreviewState.Unverified, store.Preview("asset-a/preview", 25, rate, true, true).State);
		Assert.Equal(TimedTextPreviewState.Unverified, store.Preview("asset-a/preview", 25, rate, false, false).State);
		Assert.Equal(TimedTextPreviewState.Unverified, store.Preview("asset-a/preview", 25, null, true, false).State);
		store.Enabled = false;
		Assert.Equal(TimedTextPreviewState.Unavailable, store.Preview("asset-a/preview", 25, rate, true, false).State);
	}

	[Fact]
	public void Live_v10_sequence_identity_roundtrips_and_clock_origin_is_explicitly_unverified()
	{
		var xml = Live("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">Live text</p>",
			timeBase: "clock", clockMode: "local");
		var document = TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(xml), TimedTextProfile.EbuTtLive10);
		Assert.Equal(TimedTextTimeBasis.Clock, document.TimeBasis);
		Assert.Equal("feed-1", document.SequenceIdentifier);
		Assert.Equal(3UL, document.SequenceNumber);
		var store = new TimedTextCueStore();
		store.Import("asset-a", document);
		Assert.Equal(TimedTextPreviewState.Unverified,
			store.Preview("asset-a", 25, new FrameRate(25, 1), true, false).State);
		Assert.Contains("clock", store.Preview("asset-a", 25, new FrameRate(25, 1), true, false).Detail);
		var exported = TimedTextInterchangeCodec.Serialize(document);
		var again = TimedTextInterchangeCodec.Parse(exported, TimedTextProfile.EbuTtLive10);
		Assert.Equal(document.SequenceNumber, again.SequenceNumber);
		Assert.Equal(document.Cues[0], Assert.Single(again.Cues));
	}

	[Fact]
	public void Missing_segment_duplicate_gap_and_discontinuity_require_explicit_resynchronization()
	{
		var live = TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(
			Live("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">Live</p>")),
			TimedTextProfile.EbuTtLive10);
		var segments = new TimedTextLiveSegmentStore();
		segments.BeginNewSequence("feed-1");
		Assert.True(segments.Accept(live));
		Assert.False(segments.Accept(live)); // Duplicate sequenceNumber=3
		Assert.True(segments.RequiresResynchronization);
		Assert.Empty(segments.Segments);
		Assert.False(segments.Accept(live)); // Late segment cannot resurrect stale cues.
		segments.BeginNewSequence("feed-1");
		Assert.True(segments.Accept(live));
		segments.SignalGap();
		Assert.True(segments.RequiresResynchronization);
		Assert.Empty(segments.Segments);
	}

	[Theory]
	[InlineData("<!DOCTYPE tt [ <!ENTITY xxe SYSTEM 'file:///etc/passwd'> ]>")]
	[InlineData("<!DOCTYPE tt SYSTEM 'https://example.test/foreign.dtd'>")]
	public void Xxe_and_external_entity_declarations_are_rejected_without_network_or_file_access(string xmlPrefix)
	{
		var xml = xmlPrefix + Distribution("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">Text</p>");
		Assert.ThrowsAny<XmlException>(() => TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(xml),
			TimedTextProfile.EbuTtDistribution101));
	}

	[Theory]
	[InlineData("<span style=\"foreign\">Different renderer</span>")]
	[InlineData("<image src=\"https://example.org/image.png\"/>")]
	[InlineData("<span href=\"https://example.org\">Remote URI</span>")]
	public void Unsupported_inline_style_external_resources_and_images_are_not_silently_dropped(string contents)
	{
		var xml = Distribution("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">" + contents + "</p>");
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(Encoding.UTF8.GetBytes(xml),
			TimedTextProfile.EbuTtDistribution101));
	}

	[Fact]
	public void Distribution_rejects_wrong_profile_missing_style_and_invalid_intervals()
	{
		var valid = Distribution("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">Caption</p>");
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(
			Encoding.UTF8.GetBytes(valid.Replace("2018-04", "2014-01", StringComparison.Ordinal)),
			TimedTextProfile.EbuTtDistribution101));
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(
			Encoding.UTF8.GetBytes(valid.Replace("style=\"base\"", "style=\"absent\"", StringComparison.Ordinal)),
			TimedTextProfile.EbuTtDistribution101));
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(
			Encoding.UTF8.GetBytes(valid.Replace("begin=\"1s\" end=\"2s\"", "begin=\"2s\" end=\"1s\"", StringComparison.Ordinal)),
			TimedTextProfile.EbuTtDistribution101));
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(
			Encoding.UTF8.GetBytes(valid.Replace("ttp:timeBase=\"media\"", "ttp:timeBase=\"smpte\"", StringComparison.Ordinal)),
			TimedTextProfile.EbuTtDistribution101));
	}

	[Fact]
	public void Utf8_budget_invalid_control_and_external_dangerous_uri_fail_closed()
	{
		var sample = Distribution("<p xml:id=\"p1\" region=\"bottom\" style=\"base\" begin=\"1s\" end=\"2s\">Caption</p>");
		Assert.ThrowsAny<Exception>(() => TimedTextInterchangeCodec.Parse(
			[0xC0, 0xAF], TimedTextProfile.EbuTtDistribution101));
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(
			new byte[TimedTextDocument.MaxXmlBytes + 1], TimedTextProfile.EbuTtDistribution101));
		Assert.Throws<InvalidDataException>(() => TimedTextInterchangeCodec.Parse(
			Encoding.UTF8.GetBytes(sample.Replace("<span>", "<span href=\"http://example.org\">",
				StringComparison.Ordinal)), TimedTextProfile.EbuTtDistribution101));
	}

	private static string Distribution(string paragraph, string? rate = null, string? multiplier = null) =>
		"""<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttp="http://www.w3.org/ns/ttml#parameter" xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:ebuttm="urn:ebu:tt:metadata" xml:lang="en-GB" ttp:timeBase="media" """ +
		(rate is null ? "" : "ttp:frameRate=\"" + rate + "\" ") +
		(multiplier is null ? "" : "ttp:frameRateMultiplier=\"" + multiplier + "\" ") +
		">" + Head + "<body><div>" + paragraph + "</div></body></tt>";

	private static string Live(string paragraph, string timeBase = "media", string? clockMode = null) =>
		"""<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttp="http://www.w3.org/ns/ttml#parameter" xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:ebuttp="urn:ebu:tt:parameters" xml:lang="en-GB" ebuttp:sequenceIdentifier="feed-1" ebuttp:sequenceNumber="3" ttp:timeBase=""" +
		"\"" + timeBase + "\" " + (clockMode is null ? "" : "ttp:clockMode=\"" + clockMode + "\" ") +
		">" + LiveHead + "<body><div>" + paragraph + "</div></body></tt>";
}
