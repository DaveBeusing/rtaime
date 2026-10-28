// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class TimedTextContractTests
{
	private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Fact]
	public void Caption_cue_identity_and_authority_are_separate_from_media_markers_and_catalogue_metadata()
	{
		var document = Document(new[]
		{
			new TimedTextCue("cueOne", "de-DE", "subtitle", "bottom", "base",
				"One", TimeSpan.TicksPerSecond, 2 * TimeSpan.TicksPerSecond)
		});
		Assert.Equal(TimedTextProfile.EbuTtDistribution101, document.Profile);
		Assert.Single(document.Cues);
		Assert.Equal("cueOne", document.Cues[0].Id);
		Assert.Equal("de-DE", document.Cues[0].Language);
		Assert.Equal(Hash, document.SourceSha256);
		Assert.NotEqual(typeof(MediaCuePoint), typeof(TimedTextCue));
	}

	[Fact]
	public void Safe_area_and_same_language_region_collisions_are_explicit_warnings()
	{
		var document = Document(new[]
		{
			new TimedTextCue("a", "en-GB", "subtitle", "bottom", "base", "First", 0, 20_000_000),
			new TimedTextCue("b", "en-GB", "subtitle", "bottom", "base", "Second", 10_000_000, 30_000_000)
		}, new TimedTextRegion("bottom", 0, 90, 100, 10));
		Assert.Contains(document.InspectLayout(), issue => issue.Code == "caption.safe_area_review");
		Assert.Contains(document.InspectLayout(), issue => issue.Code == "caption.region_overlap");
	}

	[Fact]
	public void Overlong_cue_and_invalid_region_references_are_rejected()
	{
		Assert.Throws<InvalidDataException>(() => Document(new[]
		{
			new TimedTextCue("bad", "en", "subtitle", "unknown", "base", "Text", 0, 10_000_000)
		}));
		Assert.Throws<InvalidDataException>(() => Document(new[]
		{
			new TimedTextCue("bad", "en", "subtitle", "bottom", "base", new string('a', 1025), 0, 10_000_000)
		}));
		Assert.Throws<InvalidDataException>(() => Document(new[]
		{
			new TimedTextCue("duplicate", "en", "subtitle", "bottom", "base", "One", 0, 10_000_000),
			new TimedTextCue("duplicate", "en", "subtitle", "bottom", "base", "Two", 10_000_000, 20_000_000)
		}));
	}

	[Fact]
	public void Live_requires_bounded_sequence_and_distribution_rejects_clock_origin()
	{
		Assert.Throws<InvalidDataException>(() => new TimedTextDocument(
			TimedTextProfile.EbuTtLive10, TimedTextTimeBasis.Media, "en", Hash,
			[new TimedTextRegion("bottom", 5, 75, 90, 20)],
			[new TimedTextStyle("base", "Segoe UI", 32, "#FFFFFFFF", "#00000000", "center")],
			[], "programme", 0));
		Assert.Throws<InvalidDataException>(() => new TimedTextDocument(
			TimedTextProfile.EbuTtDistribution101, TimedTextTimeBasis.Clock, "en", Hash,
			[new TimedTextRegion("bottom", 5, 75, 90, 20)],
			[new TimedTextStyle("base", "Segoe UI", 32, "#FFFFFFFF", "#00000000", "center")],
			[]));
	}

	private static TimedTextDocument Document(
		TimedTextCue[] cues, TimedTextRegion? region = null) => new(
		TimedTextProfile.EbuTtDistribution101, TimedTextTimeBasis.Media,
		"en-GB", Hash,
		[region ?? new TimedTextRegion("bottom", 5, 75, 90, 20)],
		[new TimedTextStyle("base", "Segoe UI", 32, "#FFFFFFFF", "#00000000", "center")],
		cues);
}
