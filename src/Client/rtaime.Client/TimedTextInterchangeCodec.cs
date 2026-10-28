// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>
/// Fail-closed, UTF-8-only EBU-TT-D v1.0.1 / EBU-TT Live v1.0 text subset.
/// No unbounded XML, schema download, external URI, CSS, image, embedded font, scripting,
/// resolved clock, arbitrary TTML animation or automatic caption Program admission.
/// </summary>
public static class TimedTextInterchangeCodec
{
	public const string DistributionProfile = "urn:ebu:tt:distribution:2018-04";
	public const string LiveProfile = "urn:ebu:tt:live:2017-05";
	private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
	private static readonly XNamespace Ttp = "http://www.w3.org/ns/ttml#parameter";
	private static readonly XNamespace Tts = "http://www.w3.org/ns/ttml#styling";
	private static readonly XNamespace Ttm = "http://www.w3.org/ns/ttml#metadata";
	private static readonly XNamespace EbuParams = "urn:ebu:tt:parameters";
	private static readonly XNamespace EbuMetadata = "urn:ebu:tt:metadata";
	private static readonly Regex Clock = new(
		@"\A(?<h>\d{2}):(?<m>[0-5]\d):(?<s>[0-5]\d)(?:\.(?<f>\d{1,7}))?(?::(?<frame>\d{2}))?\z",
		RegexOptions.CultureInvariant);
	private static readonly Regex Offset = new(
		@"\A(?<number>\d+(?:\.\d{1,7})?)(?<unit>ms|s|f)\z", RegexOptions.CultureInvariant);
	private static readonly Regex Percent = new(
		@"\A(?<x>\d{1,3}(?:\.\d{1,2})?)%\s+(?<y>\d{1,3}(?:\.\d{1,2})?)%\z",
		RegexOptions.CultureInvariant);

	public static TimedTextDocument Parse(ReadOnlySpan<byte> xml, TimedTextProfile profile)
	{
		if (xml.IsEmpty || xml.Length > TimedTextDocument.MaxXmlBytes ||
			xml.IndexOf((byte)0) >= 0 || !Enum.IsDefined(profile))
			throw new InvalidDataException("Timed Text XML must be bounded nonempty UTF-8.");
		// Reject UTF-16 and malformed UTF-8 before the XML parser, including mislabeled encodings.
		var decoded = new UTF8Encoding(false, true).GetString(xml);
		if (decoded.StartsWith('\uFEFF')) decoded = decoded[1..];
		if (Regex.IsMatch(decoded, @"<\?xml[^>]*encoding\s*=\s*['""](?!utf-8['""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
			throw new InvalidDataException("Only UTF-8 Timed Text interchange is admitted.");
		var settings = new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersInDocument = TimedTextDocument.MaxXmlBytes,
			MaxCharactersFromEntities = 0,
			CheckCharacters = true,
			IgnoreComments = false
		};
		XDocument document;
		using (var reader = XmlReader.Create(new StringReader(decoded), settings))
			document = XDocument.Load(reader, LoadOptions.None);
		if (document.Nodes().Any(node => node is XDocumentType or XProcessingInstruction))
			throw new InvalidDataException("DTD, processing instructions and external declarations are forbidden.");
		var root = document.Root ?? throw new InvalidDataException("Missing TTML document root.");
		EnsureBoundedDocumentTree(root);
		ExpectName(root, "tt");
		AllowAttributes(root, XNamespace.Xml + "lang", Ttp + "timeBase",
			Ttp + "frameRate", Ttp + "frameRateMultiplier", Ttp + "clockMode",
			EbuParams + "sequenceIdentifier", EbuParams + "sequenceNumber");
		var basis = Attribute(root, Ttp + "timeBase", required: true) switch
		{
			"media" => TimedTextTimeBasis.Media,
			"clock" when profile == TimedTextProfile.EbuTtLive10 => TimedTextTimeBasis.Clock,
			_ => throw new InvalidDataException("Unsupported EBU-TT timeBase; smpte and unqualified clock are not admitted.")
		};
		var clockMode = Attribute(root, Ttp + "clockMode");
		if (basis == TimedTextTimeBasis.Clock ? clockMode is not ("local" or "utc") : clockMode is not null)
			throw new InvalidDataException("Clock mode must be explicit only for Live clock timeBase.");
		var language = Attribute(root, XNamespace.Xml + "lang", required: true)!;
		var sourceRate = ResolveFrameRate(root);
		string? sequenceId = null;
		ulong? sequenceNumber = null;
		if (profile == TimedTextProfile.EbuTtLive10)
		{
			sequenceId = Attribute(root, EbuParams + "sequenceIdentifier", required: true);
			var value = Attribute(root, EbuParams + "sequenceNumber", required: true);
			if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
				throw new InvalidDataException("Live sequenceNumber must be a positive integer.");
			sequenceNumber = number;
		}
		else if (root.Attribute(EbuParams + "sequenceIdentifier") is not null ||
			root.Attribute(EbuParams + "sequenceNumber") is not null)
		{
			throw new InvalidDataException("Distribution profile cannot claim Live sequence semantics.");
		}
		var head = SingleChild(root, "head", required: true)!;
		var body = SingleChild(root, "body", required: true)!;
		if (root.Elements().Any(element => element != head && element != body))
			throw new InvalidDataException("Unsupported TTML root content.");
		AllowAttributes(head);
		var metadata = SingleChild(head, "metadata");
		var styling = SingleChild(head, "styling", required: true)!;
		var layout = SingleChild(head, "layout", required: true)!;
		if (head.Elements().Any(element => element != metadata && element != styling && element != layout))
			throw new InvalidDataException("Unsupported TTML head feature.");
		if (metadata is not null)
		{
			AllowAttributes(metadata);
			var claims = metadata.Descendants(EbuMetadata + "conformsToStandard").ToArray();
			if (metadata.Descendants().Any(element => element.Name != EbuMetadata + "conformsToStandard"))
				throw new InvalidDataException("Unknown metadata could conceal an unsupported TTML feature.");
			if (claims.Any(x => x.HasAttributes || x.HasElements || x.Value.Length > 256))
				throw new InvalidDataException("Invalid Timed Text profile metadata.");
			if (profile == TimedTextProfile.EbuTtDistribution101 &&
				!claims.Any(x => x.Value == DistributionProfile))
				throw new InvalidDataException("EBU-TT-D v1.0.1 requires its exact profile identifier in this subset.");
			if (claims.Any(x => x.Value.StartsWith("urn:ebu:tt:", StringComparison.Ordinal) &&
				x.Value != (profile == TimedTextProfile.EbuTtDistribution101 ? DistributionProfile : LiveProfile)))
				throw new InvalidDataException("Document contains an incompatible EBU Timed Text profile claim.");
		}
		else if (profile == TimedTextProfile.EbuTtDistribution101)
			throw new InvalidDataException("EBU-TT-D profile identifier is required for this supported subset.");
		AllowAttributes(styling);
		AllowAttributes(layout);
		var styles = styling.Elements().Select(ParseStyle).ToArray();
		var regions = layout.Elements().Select(ParseRegion).ToArray();
		if (styles.Length is < 1 or > TimedTextDocument.MaxStyles ||
			regions.Length is < 1 or > TimedTextDocument.MaxRegions)
			throw new InvalidDataException("Timed Text requires explicit bounded styles and regions.");

		var cues = new List<TimedTextCue>();
		ParseTimedContainer(body, 0, TimeSpan.TicksPerDay, language,
			null, null, null, sourceRate, cues);
		var digest = Convert.ToHexString(SHA256.HashData(xml)).ToLowerInvariant();
		return new TimedTextDocument(profile, basis, language, digest,
			regions, styles, cues, sequenceId, sequenceNumber, sourceRate);
	}

	public static byte[] Serialize(TimedTextDocument value)
	{
		ArgumentNullException.ThrowIfNull(value);
		var root = new XElement(Tt + "tt",
			new XAttribute(XNamespace.Xmlns + "ttp", Ttp),
			new XAttribute(XNamespace.Xmlns + "tts", Tts),
			new XAttribute(XNamespace.Xmlns + "ttm", Ttm),
			new XAttribute(XNamespace.Xmlns + "ebuttp", EbuParams),
			new XAttribute(XNamespace.Xmlns + "ebuttm", EbuMetadata),
			new XAttribute(XNamespace.Xml + "lang", value.Language),
			new XAttribute(Ttp + "timeBase", value.TimeBasis == TimedTextTimeBasis.Media ? "media" : "clock"));
		if (value.TimeBasis == TimedTextTimeBasis.Clock)
			root.Add(new XAttribute(Ttp + "clockMode", "local"));
		if (value.DocumentFrameRate is { } rate)
		{
			if (rate == FrameRate.Fps59_94)
			{
				root.Add(new XAttribute(Ttp + "frameRate", 60));
				root.Add(new XAttribute(Ttp + "frameRateMultiplier", "1000 1001"));
			}
			else if (rate.Denominator == 1 && rate.Numerator is 25 or 50)
				root.Add(new XAttribute(Ttp + "frameRate", rate.Numerator));
			else
				throw new InvalidDataException("Only 25, 50 and exact 60000/1001 document frame rates are supported.");
		}
		if (value.Profile == TimedTextProfile.EbuTtLive10)
		{
			root.Add(new XAttribute(EbuParams + "sequenceIdentifier", value.SequenceIdentifier!));
			root.Add(new XAttribute(EbuParams + "sequenceNumber", value.SequenceNumber!.Value));
		}
		var head = new XElement(Tt + "head");
		if (value.Profile == TimedTextProfile.EbuTtDistribution101)
			head.Add(new XElement(Tt + "metadata",
				new XElement(EbuMetadata + "conformsToStandard", DistributionProfile)));
		var styling = new XElement(Tt + "styling");
		foreach (var style in value.Styles)
		{
			style.Validate();
			styling.Add(new XElement(Tt + "style",
				new XAttribute(XNamespace.Xml + "id", style.Id),
				new XAttribute(Tts + "fontFamily", style.FontFamily),
				new XAttribute(Tts + "fontSize", style.FontSizePixels + "px"),
				new XAttribute(Tts + "color", style.TextColor),
				new XAttribute(Tts + "backgroundColor", style.BackgroundColor),
				new XAttribute(Tts + "textAlign", style.Align)));
		}
		var layout = new XElement(Tt + "layout");
		foreach (var region in value.Regions)
		{
			region.Validate();
			layout.Add(new XElement(Tt + "region",
				new XAttribute(XNamespace.Xml + "id", region.Id),
				new XAttribute(Tts + "origin", FormattableString.Invariant($"{region.X}% {region.Y}%")),
				new XAttribute(Tts + "extent", FormattableString.Invariant($"{region.Width}% {region.Height}%"))));
		}
		head.Add(styling, layout);
		var div = new XElement(Tt + "div");
		foreach (var cue in value.Cues)
		{
			cue.Validate();
			var p = new XElement(Tt + "p",
				new XAttribute(XNamespace.Xml + "id", cue.Id),
				new XAttribute(XNamespace.Xml + "lang", cue.Language),
				new XAttribute(Ttm + "role", cue.Role),
				new XAttribute("region", cue.RegionId),
				new XAttribute("style", cue.StyleId),
				new XAttribute("begin", FormatTicks(cue.BeginTicks)),
				new XAttribute("end", FormatTicks(cue.EndTicks)));
			var lines = cue.Text.Split('\n');
			for (var i = 0; i < lines.Length; i++)
			{
				if (i != 0) p.Add(new XElement(Tt + "br"));
				p.Add(new XText(lines[i]));
			}
			div.Add(p);
		}
		root.Add(head, new XElement(Tt + "body", div));
		var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
		using var output = new MemoryStream();
		using (var writer = XmlWriter.Create(output, new XmlWriterSettings
		{
			Encoding = new UTF8Encoding(false),
			OmitXmlDeclaration = false,
			Indent = false,
			CloseOutput = false
		}))
			document.Save(writer);
		if (output.Length > TimedTextDocument.MaxXmlBytes)
			throw new InvalidDataException("Timed Text XML serialization exceeded the fixed document bound.");
		return output.ToArray();
	}

	private static string FormatTicks(long ticks) =>
		TimeSpan.FromTicks(ticks).ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);

	private static FrameRate? ResolveFrameRate(XElement root)
	{
		var rate = Attribute(root, Ttp + "frameRate");
		var multiplier = Attribute(root, Ttp + "frameRateMultiplier");
		if (rate is null)
		{
			if (multiplier is not null) throw new InvalidDataException("frameRateMultiplier requires explicit frameRate.");
			return null;
		}
		if (!long.TryParse(rate, NumberStyles.None, CultureInfo.InvariantCulture, out var nominal) ||
			nominal is < 1 or > 240_000)
			throw new InvalidDataException("Invalid TTML frameRate.");
		long numerator = nominal, denominator = 1;
		if (multiplier is not null)
		{
			var parts = multiplier.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2 ||
				!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
				!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ||
				x is < 1 or > 10_000 || y is < 1 or > 10_000)
				throw new InvalidDataException("Invalid TTML frameRateMultiplier.");
			numerator = checked(numerator * x);
			denominator = y;
		}
		var effective = new FrameRate(numerator, denominator);
		if (!(effective.Denominator == 1 && effective.Numerator is 25 or 50) &&
			effective != FrameRate.Fps59_94)
			throw new InvalidDataException("This Timed Text subset only admits exact 25, 50 and 60000/1001 document timebases.");
		return effective;
	}

	private static TimedTextStyle ParseStyle(XElement element)
	{
		ExpectName(element, "style");
		AllowAttributes(element, XNamespace.Xml + "id", Tts + "fontFamily", Tts + "fontSize",
			Tts + "color", Tts + "backgroundColor", Tts + "textAlign");
		if (element.HasElements || !string.IsNullOrWhiteSpace(element.Value))
			throw new InvalidDataException("Complex style inheritance and embedded resources are not supported.");
		var size = Attribute(element, Tts + "fontSize", required: true)!;
		if (!size.EndsWith("px", StringComparison.Ordinal) ||
			!int.TryParse(size[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var pixels))
			throw new InvalidDataException("Only explicit single-pixel caption fontSize is supported.");
		var style = new TimedTextStyle(
			Attribute(element, XNamespace.Xml + "id", required: true)!,
			Attribute(element, Tts + "fontFamily", required: true)!,
			pixels,
			Attribute(element, Tts + "color", required: true)!,
			Attribute(element, Tts + "backgroundColor", required: true)!,
			Attribute(element, Tts + "textAlign", required: true)!);
		style.Validate();
		return style;
	}

	private static TimedTextRegion ParseRegion(XElement element)
	{
		ExpectName(element, "region");
		AllowAttributes(element, XNamespace.Xml + "id", Tts + "origin", Tts + "extent");
		if (element.HasElements || !string.IsNullOrWhiteSpace(element.Value))
			throw new InvalidDataException("Timed/animated regions are not supported.");
		var origin = Percent.Match(Attribute(element, Tts + "origin", required: true)!);
		var extent = Percent.Match(Attribute(element, Tts + "extent", required: true)!);
		if (!origin.Success || !extent.Success)
			throw new InvalidDataException("Caption region origin and extent must use explicit percent pairs.");
		var region = new TimedTextRegion(Attribute(element, XNamespace.Xml + "id", required: true)!,
			Decimal(origin.Groups["x"].Value), Decimal(origin.Groups["y"].Value),
			Decimal(extent.Groups["x"].Value), Decimal(extent.Groups["y"].Value));
		region.Validate();
		return region;
	}

	private static void ParseTimedContainer(
		XElement element, long parentBegin, long parentEnd, string parentLanguage,
		string? parentRegion, string? parentStyle, string? parentRole,
		FrameRate? rate, List<TimedTextCue> cues)
	{
		if (element.Name != Tt + "body" && element.Name != Tt + "div" && element.Name != Tt + "p")
			throw new InvalidDataException("Unsupported Timed Text body element.");
		AllowAttributes(element, "begin", "end", "dur", "region", "style",
			XNamespace.Xml + "lang", XNamespace.Xml + "id", Ttm + "role", "timeContainer");
		if (Attribute(element, "timeContainer") is { } container && container != "par")
			throw new InvalidDataException("Sequential time containers are not supported.");
		var start = checked(parentBegin + ParseOptionalTime(Attribute(element, "begin"), rate));
		var endText = Attribute(element, "end");
		var durText = Attribute(element, "dur");
		if (endText is not null && durText is not null)
			throw new InvalidDataException("Ambiguous TTML end and duration cannot be combined.");
		var end = endText is not null ? checked(parentBegin + ParseTime(endText, rate))
			: durText is not null ? checked(start + ParseTime(durText, rate))
			: parentEnd;
		if (start < parentBegin || end > parentEnd || start >= end || end > TimeSpan.TicksPerDay)
			throw new InvalidDataException("Nested Timed Text interval is outside the parent timing window.");
		var language = Attribute(element, XNamespace.Xml + "lang") ?? parentLanguage;
		var region = Attribute(element, "region") ?? parentRegion;
		var style = Attribute(element, "style") ?? parentStyle;
		var role = Attribute(element, Ttm + "role") ?? parentRole ?? "subtitle";
		if (element.Name == Tt + "p")
		{
			if (end == TimeSpan.TicksPerDay || region is null || style is null)
				throw new InvalidDataException("Caption paragraph needs explicit bounded end and region/style.");
			var cue = new TimedTextCue(
				Attribute(element, XNamespace.Xml + "id", required: true)!,
				language, role, region, style, ParseText(element, style), start, end);
			cue.Validate();
			if (cues.Count == TimedTextDocument.MaxCues)
				throw new InvalidDataException("Caption cue count exceeds the fixed budget.");
			cues.Add(cue);
		}
		else
		{
			foreach (var child in element.Elements())
				ParseTimedContainer(child, start, end, language, region, style, role, rate, cues);
			if (element.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
				throw new InvalidDataException("Text outside caption paragraphs is not supported.");
		}
	}

	private static string ParseText(XElement paragraph, string inheritedStyle)
	{
		var result = new StringBuilder();
		void Visit(XContainer node)
		{
			foreach (var item in node.Nodes())
			{
				switch (item)
				{
					case XText text:
						result.Append(text.Value);
						break;
					case XElement el when el.Name == Tt + "br":
						AllowAttributes(el);
						if (el.HasElements || !string.IsNullOrEmpty(el.Value))
							throw new InvalidDataException("Line break cannot carry content.");
						result.Append('\n');
						break;
					case XElement el when el.Name == Tt + "span":
						AllowAttributes(el, "style");
						var style = Attribute(el, "style");
						if (style is not null && style != inheritedStyle)
							throw new InvalidDataException("Mixed inline styling requires a qualified multi-run renderer.");
						Visit(el);
						break;
					default:
						throw new InvalidDataException("Unsupported Timed Text inline element or entity.");
				}
			}
		}
		Visit(paragraph);
		var lines = result.ToString().Split('\n');
		return string.Join("\n", lines.Select(text =>
			Regex.Replace(text, @"\s+", " ").Trim()));
	}

	private static long ParseOptionalTime(string? value, FrameRate? rate) =>
		value is null ? 0 : ParseTime(value, rate);

	private static long ParseTime(string value, FrameRate? rate)
	{
		var match = Clock.Match(value);
		if (match.Success)
		{
			var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
			if (hours > 23) throw new InvalidDataException("TTML hour exceeds the bounded day.");
			decimal seconds = hours * 3600 +
				int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60 +
				int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
			if (match.Groups["f"].Success)
				seconds += Decimal("0." + match.Groups["f"].Value);
			if (match.Groups["frame"].Success)
			{
				if (match.Groups["f"].Success || rate is null)
					throw new InvalidDataException("Frame timecode needs an explicit document rate and no subsecond fraction.");
				var frames = int.Parse(match.Groups["frame"].Value, CultureInfo.InvariantCulture);
				if (frames >= (decimal)rate.Value.Numerator / rate.Value.Denominator)
					throw new InvalidDataException("Timecode frame field exceeds the exact frame rate.");
				seconds += (decimal)frames * rate.Value.Denominator / rate.Value.Numerator;
			}
			return checked((long)decimal.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
		}
		match = Offset.Match(value);
		if (!match.Success) throw new InvalidDataException("Unsupported TTML time expression.");
		var n = Decimal(match.Groups["number"].Value);
		var secondsOffset = match.Groups["unit"].Value switch
		{
			"ms" => n / 1000,
			"s" => n,
			"f" when rate is not null => n * rate.Value.Denominator / rate.Value.Numerator,
			_ => throw new InvalidDataException("Frame offsets require an explicit frame rate.")
		};
		if (secondsOffset < 0 || secondsOffset > 86_400)
			throw new InvalidDataException("Caption time exceeds the bounded day.");
		return checked((long)decimal.Round(secondsOffset * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
	}

	private static decimal Decimal(string value) =>
		decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

	private static void EnsureBoundedDocumentTree(XElement root)
	{
		const int maxElements = 2048;
		const int maxDepth = 12;
		var pending = new Stack<(XElement Element, int Depth)>();
		pending.Push((root, 1));
		var count = 0;
		while (pending.TryPop(out var item))
		{
			if (++count > maxElements || item.Depth > maxDepth)
				throw new InvalidDataException("Timed Text XML element count or nesting depth exceeds the supported bound.");
			foreach (var child in item.Element.Elements())
				pending.Push((child, item.Depth + 1));
		}
	}

	private static void ExpectName(XElement element, string local)
	{
		if (element.Name != Tt + local)
			throw new InvalidDataException($"Expected TTML {local} element within the supported profile.");
	}

	private static XElement? SingleChild(XElement parent, string local, bool required = false)
	{
		var matches = parent.Elements(Tt + local).Take(2).ToArray();
		if (matches.Length > 1 || (required && matches.Length != 1))
			throw new InvalidDataException($"Expected exactly one TTML {local} element.");
		return matches.FirstOrDefault();
	}

	private static string? Attribute(XElement element, XName name, bool required = false)
	{
		var result = element.Attribute(name)?.Value;
		if ((required && string.IsNullOrWhiteSpace(result)) || result is { Length: > 256 })
			throw new InvalidDataException($"Missing or oversized TTML {name.LocalName} attribute.");
		return result;
	}

	private static void AllowAttributes(XElement element, params XName[] allowed)
	{
		foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
			if (!allowed.Contains(attribute.Name))
				throw new InvalidDataException($"Unsupported TTML attribute {attribute.Name.LocalName}; rendering must not silently ignore it.");
	}
}
