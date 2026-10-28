// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>
/// Bounded, byte-preserving ADM metadata reader. The parsed subset can be
/// inspected but cannot be given to a decoder, mixer, encoder or Program route.
/// Unknown XML data is retained in the original bytes rather than dropped.
/// </summary>
public static class AdmAudioXml
{
	public const int MaximumElements = 2048;
	public const int MaximumAttributes = 4096;
	public const int MaximumDepth = 24;
	public const int MaximumObjects = 64;
	public const int MaximumProgrammes = 8;
	private const string EbuCoreNamespace = "urn:ebu:metadata-schema:ebuCore_2014";
	private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		MaxDepth = 12
	};

	public static AdmAudioInspection ImportXml(ReadOnlySpan<byte> source, MediaAssetDescriptor asset,
		string? r123Allocation = null, IReadOnlyList<AdmTrackAssignment>? physicalTracks = null)
	{
		ArgumentNullException.ThrowIfNull(asset);
		if (source.Length is < 16 or > AdmAudioInterchange.MaximumXmlBytes)
			throw new InvalidDataException("ADM source XML exceeds the admitted size.");
		var xml = source.ToArray();
		var document = new AdmAudioInterchange(AdmAudioInterchange.CurrentFormat,
			asset.AssetId.ToString(), asset.FingerprintSha256,
			AdmAudioInterchange.SupportedAdmVersion, AdmAudioInterchange.ProductionGuidance,
			Convert.ToBase64String(xml), r123Allocation,
			physicalTracks?.ToArray() ?? Array.Empty<AdmTrackAssignment>(),
			DateTimeOffset.UtcNow);
		document.Validate(asset.AssetId.ToString(), asset.FingerprintSha256);
		return Parse(document);
	}

	public static AdmAudioInspection ImportSidecarJson(string json, MediaAssetDescriptor asset)
	{
		ArgumentNullException.ThrowIfNull(asset);
		if (string.IsNullOrWhiteSpace(json) ||
			StrictUtf8.GetByteCount(json) > AdmAudioInterchange.MaximumJsonBytes)
			throw new InvalidDataException("ADM sidecar JSON is absent or too large.");
		var bundle = JsonSerializer.Deserialize<AdmAudioInterchange>(json, JsonOptions)
			?? throw new InvalidDataException("ADM sidecar JSON has no document.");
		bundle.Validate(asset.AssetId.ToString(), asset.FingerprintSha256);
		return Parse(bundle);
	}

	public static string ExportSidecarJson(AdmAudioInspection observation)
	{
		ArgumentNullException.ThrowIfNull(observation);
		var existing = Parse(observation.Interchange);
		if (!SameTracks(existing.Tracks, observation.Tracks))
			throw new InvalidDataException("ADM projection differs from the preserved source XML.");
		var json = JsonSerializer.Serialize(observation.Interchange, JsonOptions);
		if (StrictUtf8.GetByteCount(json) > AdmAudioInterchange.MaximumJsonBytes)
			throw new InvalidDataException("Serialized ADM sidecar exceeds its byte limit.");
		return json;
	}

	public static byte[] ExportXml(AdmAudioInspection inspection)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		var existing = Parse(inspection.Interchange);
		if (!SameTracks(existing.Tracks, inspection.Tracks))
			throw new InvalidDataException("ADM projection differs from its source.");
		return inspection.Interchange.OriginalXmlBytes();
	}

	public static AdmAudioInspection Parse(AdmAudioInterchange bundle)
	{
		ArgumentNullException.ThrowIfNull(bundle);
		bundle.Validate();
		var xml = bundle.OriginalXmlBytes();
		var parsed = ParseXml(xml);
		var afe = parsed.Root!.Name.LocalName == "audioFormatExtended"
			? parsed.Root
			: parsed.Root.Descendants().SingleOrDefault(x => x.Name.LocalName == "audioFormatExtended")
				?? throw new InvalidDataException("EBUCore document contains no single ADM audioFormatExtended.");
		if (afe.Name.NamespaceName is not ("" or EbuCoreNamespace) ||
			afe.Attribute("version")?.Value != AdmAudioInterchange.SupportedAdmVersion)
			throw new InvalidDataException("ADM XML namespace or BS.2076 version is unsupported.");
		var groups = new Dictionary<string, Dictionary<string, XElement>>(StringComparer.Ordinal);
		foreach (var (name, prefix, attribute, maximum) in new[]
		{
			("audioProgramme", "APR", "audioProgrammeID", MaximumProgrammes),
			("audioContent", "ACO", "audioContentID", 64),
			("audioObject", "AO", "audioObjectID", MaximumObjects),
			("audioPackFormat", "AP", "audioPackFormatID", 64),
			("audioChannelFormat", "AC", "audioChannelFormatID", 128),
			("audioStreamFormat", "AS", "audioStreamFormatID", 128),
			("audioTrackFormat", "AT", "audioTrackFormatID", 128),
			("audioTrackUID", "ATU", "UID", AdmAudioInterchange.MaximumTracks)
		})
		{
			var elements = afe.Elements().Where(e => e.Name.LocalName == name).ToArray();
			if (elements.Length > maximum)
				throw new InvalidDataException("ADM " + name + " count exceeds the bounded profile.");
			var index = new Dictionary<string, XElement>(StringComparer.Ordinal);
			foreach (var element in elements)
			{
				var id = (string?)element.Attribute(attribute);
				if (!AdmAudioInterchange.ValidAdmId(id, prefix) || !index.TryAdd(id!, element))
					throw new InvalidDataException("ADM element identifier missing, invalid or duplicated: " + name + ".");
			}
			groups.Add(name, index);
		}
		if (groups["audioProgramme"].Count == 0 ||
			groups["audioContent"].Count == 0 ||
			groups["audioObject"].Count == 0 ||
			groups["audioTrackUID"].Count == 0)
			throw new InvalidDataException("ADM interchange requires explicit programme, content, object and track UID nodes.");
		var refs = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["audioContentIDRef"] = "audioContent",
			["audioObjectIDRef"] = "audioObject",
			["audioComplementaryObjectIDRef"] = "audioObject",
			["audioPackFormatIDRef"] = "audioPackFormat",
			["audioTrackUIDRef"] = "audioTrackUID",
			["audioTrackFormatIDRef"] = "audioTrackFormat",
			["audioStreamFormatIDRef"] = "audioStreamFormat",
			["audioChannelFormatIDRef"] = "audioChannelFormat"
		};
		foreach (var element in afe.Descendants())
		{
			if (!refs.TryGetValue(element.Name.LocalName, out var target)) continue;
			var id = element.Value.Trim();
			if (!groups[target].ContainsKey(id))
				throw new InvalidDataException("ADM reference is unresolved: " + element.Name.LocalName + ".");
		}
		EnsureAcyclic(groups["audioObject"], "audioObjectIDRef", "audioComplementaryObjectIDRef");
		EnsureAcyclic(groups["audioPackFormat"], "audioPackFormatIDRef");

		var assigned = bundle.Assignments.ToDictionary(x => x.AudioTrackUid, StringComparer.Ordinal);
		if (assigned.Count > 0 &&
			(assigned.Count != groups["audioTrackUID"].Count ||
				assigned.Keys.Any(id => !groups["audioTrackUID"].ContainsKey(id))))
			throw new InvalidDataException("R 123 mapping must associate each and every ADM track UID.");
		var tracks = new List<AdmTrackMetadataRow>();
		foreach (var (uid, track) in groups["audioTrackUID"])
		{
			var owners = groups["audioObject"]
				.Where(entry => DirectRefs(entry.Value, "audioTrackUIDRef").Contains(uid, StringComparer.Ordinal))
				.ToArray();
			if (owners.Length == 0)
				throw new InvalidDataException("ADM audioTrackUID is not referenced by any audioObject.");
			var content = groups["audioContent"].Values
				.Where(value => DirectRefs(value, "audioObjectIDRef")
					.Any(objectId => owners.Any(owner => owner.Key == objectId))).ToArray();
			var languages = content
				.Select(element => (string?)element.Attribute("audioContentLanguage"))
				.Where(x => !string.IsNullOrWhiteSpace(x))
				.Distinct(StringComparer.Ordinal).ToArray();
			foreach (var language in languages)
				if (!AdmAudioInterchange.ValidLanguage(language))
					throw new InvalidDataException("ADM content language tag is unsupported.");
			var languageLabel = languages.Length == 1 ? languages[0] : null;
			assigned.TryGetValue(uid, out var explicitTrack);
			if (explicitTrack?.Language is not null &&
				languageLabel is not null &&
				!string.Equals(explicitTrack.Language, languageLabel, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Explicit R 123 track language contradicts ADM audioContentLanguage.");
			var objectNode = owners[0].Value;
			var start = (string?)objectNode.Attribute("start");
			var duration = (string?)objectNode.Attribute("duration");
			var end = ValidateAndCalculateTimes(start, duration);
			var pack = DirectRefs(objectNode, "audioPackFormatIDRef").FirstOrDefault() ??
				DirectRefs(track, "audioPackFormatIDRef").FirstOrDefault();
			var trackFormat = DirectRefs(track, "audioTrackFormatIDRef").FirstOrDefault();
			var stream = trackFormat is null ? null
				: DirectRefs(groups["audioTrackFormat"][trackFormat], "audioStreamFormatIDRef").FirstOrDefault();
			var channel = stream is null ? null
				: DirectRefs(groups["audioStreamFormat"][stream], "audioChannelFormatIDRef").FirstOrDefault();
			var channelNode = channel is null ? null : groups["audioChannelFormat"][channel];
			var channelName = (string?)channelNode?.Attribute("audioChannelFormatName");
			var role = ResolveRole(channelName);
			var sourceKind = (string?)channelNode?.Attribute("typeDefinition") ??
				(pack is null ? null : (string?)groups["audioPackFormat"][pack].Attribute("typeDefinition")) ??
				"UNVERIFIED";
			tracks.Add(new AdmTrackMetadataRow(
				uid, explicitTrack?.PhysicalTrackNumber, role,
				explicitTrack?.Language ?? languageLabel,
				owners.Length == 1 ? owners[0].Key : string.Join(",", owners.Select(x => x.Key).OrderBy(x => x)),
				pack, start, end, duration, sourceKind));
		}
		tracks.Sort((a, b) => a.PhysicalTrackNumber.HasValue && b.PhysicalTrackNumber.HasValue
			? a.PhysicalTrackNumber.Value.CompareTo(b.PhysicalTrackNumber.Value)
			: StringComparer.Ordinal.Compare(a.AudioTrackUid, b.AudioTrackUid));
		var inferred = tracks.Count == 2 && tracks.Select(x => x.ChannelRole).OrderBy(x => x)
			.SequenceEqual(new[] { "L", "R" }) ? "2.0 · METADATA ONLY" :
			tracks.Count == 6 && tracks.Select(x => x.ChannelRole).OrderBy(x => x)
				.SequenceEqual(new[] { "C", "L", "LFE", "Ls", "R", "Rs" })
				? "5.1 · METADATA ONLY" : "TRACK / OBJECT METADATA ONLY";
		var layout = bundle.R123Allocation is null ? inferred :
			"R 123 " + bundle.R123Allocation + " DECLARED · SOURCE CHANNEL ASSIGNMENT UNVERIFIED";
		return new AdmAudioInspection(bundle, tracks.AsReadOnly(),
			groups["audioProgramme"].Count, groups["audioObject"].Count, layout);
	}

	private static XDocument ParseXml(byte[] xml)
	{
		// Parse bytes rather than decoded/re-encoded strings so XML declaration,
		// BOM, whitespace and vendor extensions survive byte-for-byte export.
		using var stream = new MemoryStream(xml, writable: false);
		using var reader = XmlReader.Create(stream, new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null, MaxCharactersFromEntities = 0,
			MaxCharactersInDocument = AdmAudioInterchange.MaximumXmlBytes,
			CheckCharacters = true, IgnoreComments = false
		});
		var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
		var root = doc.Root ?? throw new InvalidDataException("ADM XML root is absent.");
		if (root.Name.LocalName is not ("audioFormatExtended" or "ebuCoreMain") ||
			root.Name.NamespaceName is not ("" or EbuCoreNamespace))
			throw new InvalidDataException("Only standalone ADM or EBUCore-wrapped ADM XML is accepted.");
		var nodes = root.DescendantsAndSelf().Take(MaximumElements + 1).ToArray();
		if (nodes.Length > MaximumElements || nodes.Sum(e => e.Attributes().Count()) > MaximumAttributes ||
			nodes.Any(e => e.Ancestors().Count() > MaximumDepth))
			throw new InvalidDataException("ADM XML node, attribute or depth budget exceeded.");
		return doc;
	}

	private static string[] DirectRefs(XElement element, string localName) =>
		element.Elements().Where(x => x.Name.LocalName == localName)
			.Select(x => x.Value.Trim()).ToArray();

	private static void EnsureAcyclic(Dictionary<string, XElement> graph, params string[] referenceNames)
	{
		var visited = new HashSet<string>(StringComparer.Ordinal);
		var active = new HashSet<string>(StringComparer.Ordinal);
		void Visit(string id)
		{
			if (active.Contains(id))
				throw new InvalidDataException("Cyclic ADM object or pack references are not allowed.");
			if (!visited.Add(id)) return;
			active.Add(id);
			foreach (var name in referenceNames)
				foreach (var child in DirectRefs(graph[id], name))
					Visit(child);
			active.Remove(id);
		}
		foreach (var id in graph.Keys) Visit(id);
	}

	private static string? ResolveRole(string? channelName)
	{
		if (string.IsNullOrWhiteSpace(channelName)) return null;
		return channelName.ToLowerInvariant() switch
		{
			"pcm_frontleft" or "frontleft" => "L",
			"pcm_frontright" or "frontright" => "R",
			"pcm_frontcentre" or "frontcentre" or "pcm_frontcenter" => "C",
			"pcm_lowfrequencyeffects" or "lowfrequencyeffects" => "LFE",
			"pcm_sidetleft" or "pcm_sideleft" or "pcm_leftsurround" => "Ls",
			"pcm_sideright" or "pcm_rightsurround" => "Rs",
			_ => null
		};
	}

	private static string? ValidateAndCalculateTimes(string? start, string? duration)
	{
		if (start is null && duration is null) return null;
		if (start is null || duration is null ||
			!TimeSpan.TryParseExact(start, @"hh\:mm\:ss\.fffff",
				CultureInfo.InvariantCulture, out var begin) ||
			!TimeSpan.TryParseExact(duration, @"hh\:mm\:ss\.fffff",
				CultureInfo.InvariantCulture, out var length) ||
			length <= TimeSpan.Zero || begin > TimeSpan.MaxValue - length)
			throw new InvalidDataException("Supported ADM object timing requires 5-digit rational-looking decimal seconds, nonnegative start and positive duration.");
		return (begin + length).ToString(@"hh\:mm\:ss\.fffff", CultureInfo.InvariantCulture);
	}

	private static bool SameTracks(IReadOnlyList<AdmTrackMetadataRow> a, IReadOnlyList<AdmTrackMetadataRow> b) =>
		a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);
}
