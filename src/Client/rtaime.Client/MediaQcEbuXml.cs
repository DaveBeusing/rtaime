// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using rtaime.Media.Contracts;

namespace rtaime.Client;

// Exact pinned EBU QC 2026-04 Report XML; the rtaime extension is optional
// for external consumers but mandatory when importing evidence into this app.
public static class MediaQcEbuXml
{
	public const string NamespaceUri = "tag:qc.ebu.ch,2026-04";
	public const string TimingNamespaceUri = "tag:qc.ebu.ch,2026-04:extensions:timing";
	public const string ExtensionNamespaceUri = "urn:rtaime:offline-qc:2026-04";
	public const string CatalogueApiVersion = "v3";
	public const int MaximumDocumentCharacters = 1024 * 1024;
	private static readonly XNamespace Q = NamespaceUri;
	private static readonly XNamespace T = TimingNamespaceUri;
	private static readonly XNamespace R = ExtensionNamespaceUri;
	private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas);

	public static string Export(MediaQcReport report)
	{
		ArgumentNullException.ThrowIfNull(report);
		report.Validate();
		var utc = report.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
		var identity = new XElement(Q + "ContentId",
			new XElement(Q + "ContentIdentifier",
				new XElement(Q + "ID", "urn:uuid:" + Guid.Parse(report.AssetId).ToString("D"))),
			new XElement(Q + "ContentIdentifier",
				new XElement(Q + "ID", "urn:sha256:" + report.FingerprintSha256.ToLowerInvariant())));
		var items = report.Profile.Checks.Select(definition => new XElement(Q + "Item",
			new XElement(Q + "EBUQCID", definition.Id),
			new XElement(Q + "EBUQCName", definition.Name),
			new XElement(Q + "EBUQCVersion", definition.Version),
			new XElement(Q + "UsedAs", "check"),
			new XElement(Q + "Scopes"),
			new XElement(Q + "Inputs")));
		var definitions = report.Profile.Checks.Select(definition => new XElement(Q + "ItemDefinition",
			new XElement(Q + "EBUQCID", definition.Id),
			new XElement(Q + "EBUQCName", definition.Name),
			new XElement(Q + "EBUQCVersion", definition.Version),
			new XElement(Q + "Status", "draft"),
			new XElement(Q + "UsableAs", "check"),
			new XElement(Q + "Description", definition.Description),
			new XElement(Q + "Definition", definition.Description),
			new XElement(Q + "CheckResultDefinition", definition.CheckResultDefinition),
			new XElement(Q + "Layer", "wrapper"),
			new XElement(Q + "Category", "structure"),
			new XElement(Q + "EssenceType", "all"),
			new XElement(Q + "Inputs"),
			new XElement(Q + "Outputs")));
		var resultItems = report.Results
			.Where(result => result.State is MediaQcCheckState.Pass or MediaQcCheckState.Failed)
			.Select(result => new XElement(Q + "ItemResult",
				new XElement(Q + "EBUQCID", result.Definition.Id),
				new XElement(Q + "EBUQCName", result.Definition.Name),
				new XElement(Q + "EBUQCVersion", result.Definition.Version),
				new XElement(Q + "AnalysisMethodUsed", "measurement"),
				new XElement(Q + "ExecutionStatus", "complete"),
				new XElement(Q + "CheckResult", result.State == MediaQcCheckState.Pass),
				new XElement(Q + "ResultDescription", result.Detail),
				ToolInformation(result.ToolVersion),
				new XElement(Q + "MaxSeverity", result.Severity),
				new XElement(Q + "DetectionMethod", "automatic"),
				new XElement(Q + "CreationDateTime", utc),
				new XElement(Q + "Outputs",
					new XElement(Q + "Output",
						new XElement(Q + "Name", "Observed"),
						result.Range is null ? null : new XElement(Q + "Locator",
							new XElement(Q + "Start", result.Range.Start.ToString(CultureInfo.InvariantCulture)),
							new XElement(Q + "End", result.Range.End.ToString(CultureInfo.InvariantCulture))),
						result.TrackId is null ? null : new XElement(Q + "Track", result.TrackId),
						new XElement(Q + "Value", result.Detail),
						result.Confidence is null ? null : new XElement(Q + "Confidence",
							result.Confidence.Value.ToString("0.################", CultureInfo.InvariantCulture)),
						new XElement(Q + "Severity", result.Severity)))));
		var profile = new XElement(Q + "Profile",
			new XElement(Q + "ID", "urn:uuid:" + Guid.Parse(report.Profile.ProfileId).ToString("D")),
			new XElement(Q + "Name", report.Profile.Name),
			new XElement(identity),
			new XElement(Q + "CheckResultRule", "AND"),
			new XElement(Q + "Description", "Local unregistered check IDs; not an EBU Catalogue v3 certification claim."),
			new XElement(Q + "Scopes"),
			new XElement(Q + "Items", items),
			new XElement(Q + "ItemDefinitions", definitions));
		var ext = new XElement(Q + "ExtensionProperties",
			new XElement(R + "Binding",
				new XAttribute("modelRelease", report.ModelRelease),
				new XAttribute("assetId", report.AssetId),
				new XAttribute("catalogueRevision", report.CatalogueRevision),
				new XAttribute("sha256", report.FingerprintSha256),
				new XAttribute("sourceBytes", report.SourceLengthBytes),
				new XAttribute("sourceModifiedUtc", report.SourceModifiedUtc.ToString("O", CultureInfo.InvariantCulture)),
				new XAttribute("createdUtc", report.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)),
				new XAttribute("toolVersion", report.ToolVersion),
				report.Results.Select(result => new XElement(R + "Outcome",
					new XAttribute("checkId", result.Definition.Id),
					new XAttribute("state", FormatState(result.State)),
					new XAttribute("severity", result.Severity),
					new XAttribute("detail", result.Detail),
					new XAttribute("source", result.EvidenceSource),
					new XAttribute("toolVersion", result.ToolVersion),
					result.Confidence is null ? null : new XAttribute("confidence",
						result.Confidence.Value.ToString("R", CultureInfo.InvariantCulture)),
					result.TrackId is null ? null : new XAttribute("track", result.TrackId),
					result.Range is null ? null : new XAttribute("start", result.Range.Start),
					result.Range is null ? null : new XAttribute("end", result.Range.End),
					result.Range is null ? null : new XAttribute("editRate",
						$"{result.Range.EditRateNumerator}/{result.Range.EditRateDenominator}")))));
		var rates = report.Results.Where(result => result.Range is not null)
			.Select(result => $"{result.Range!.EditRateNumerator}/{result.Range.EditRateDenominator}")
			.Distinct(StringComparer.Ordinal).ToArray();
		if (rates.Length > 1)
			throw new ArgumentException("EBU QC Timing Extension supports one consistent edit rate per report.");
		if (rates.Length == 1)
			ext.Add(new XElement(T + "TimingExtensionMediaPlaybackEditUnits",
				new XElement(T + "EditRate", rates[0])));

		var root = new XElement(Q + "Report",
			new XAttribute(XNamespace.Xmlns + "qc", NamespaceUri),
			new XAttribute(XNamespace.Xmlns + "rqc", ExtensionNamespaceUri),
			new XAttribute(XNamespace.Xmlns + "qcet", TimingNamespaceUri),
			new XElement(Q + "ReportId", "urn:uuid:" + report.ReportId.ToString("D")),
			new XElement(Q + "ExecutionStatus", "complete"),
			report.FullyEvaluated ? new XElement(Q + "CheckResult", report.Passed) : null,
			identity,
			new XElement(Q + "LastModifiedDateTime",
				report.SourceModifiedUtc.ToString("O", CultureInfo.InvariantCulture)),
			ToolInformation(report.ToolVersion),
			profile,
			new XElement(Q + "ItemResults", resultItems),
			ext);
		var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
		if (xml.Length > MaximumDocumentCharacters)
			throw new InvalidDataException("QC report exceeds the bounded XML size.");
		ValidateSchema(xml);
		return xml;
	}

	public static MediaQcReport Import(string xml)
	{
		var root = ParseValidated(xml);
		if (root.Name != Q + "Report")
			throw new InvalidDataException("An EBU QC 2026-04 Report element is required.");
		var binding = root.Element(Q + "ExtensionProperties")?.Element(R + "Binding")
			?? throw new InvalidDataException("Missing source-bound QC result extension.");
		if ((string?)binding.Attribute("modelRelease") != MediaQcReport.CurrentModelRelease)
			throw new InvalidDataException("QC release drift: only 2026-04 is supported.");
		var profile = MediaQcProfile.OfflineFileV1;
		var profileElement = root.Element(Q + "Profile")
			?? throw new InvalidDataException("QC profile is missing.");
		if ((string?)profileElement.Element(Q + "ID") != "urn:uuid:" + profile.ProfileId ||
			(string?)profileElement.Element(Q + "CheckResultRule") != "AND" ||
			(string?)profileElement.Element(Q + "Name") != profile.Name)
			throw new InvalidDataException("Unknown or modified QC profile.");
		if ((string?)root.Element(Q + "ExecutionStatus") != "complete")
			throw new InvalidDataException("Incomplete/error QC reports cannot become verified evidence.");
		var items = profileElement.Element(Q + "Items")?.Elements(Q + "Item").ToArray() ?? [];
		if (items.Length != profile.Checks.Count ||
			items.Where((item, index) =>
				(string?)item.Element(Q + "EBUQCID") != profile.Checks[index].Id ||
				(string?)item.Element(Q + "EBUQCVersion") != profile.Checks[index].Version ||
				(string?)item.Element(Q + "UsedAs") != "check").Any())
			throw new InvalidDataException("QC item IDs, versions or use differ from the admitted profile.");
		var definitions = root.Element(Q + "Profile")?.Element(Q + "ItemDefinitions")?
			.Elements(Q + "ItemDefinition").ToArray() ?? [];
		if (definitions.Length != profile.Checks.Count)
			throw new InvalidDataException("Incomplete QC 2026-04 item definitions.");
		for (var i = 0; i < definitions.Length; i++)
			if ((string?)definitions[i].Element(Q + "EBUQCID") != profile.Checks[i].Id ||
				(string?)definitions[i].Element(Q + "EBUQCVersion") != profile.Checks[i].Version ||
				(string?)definitions[i].Element(Q + "CheckResultDefinition") != profile.Checks[i].CheckResultDefinition)
				throw new InvalidDataException("QC item definition or CheckResultDefinition mismatch.");
		var measured = root.Element(Q + "ItemResults")?.Elements(Q + "ItemResult")
			.ToDictionary(e => (string?)e.Element(Q + "EBUQCID") ??
				throw new InvalidDataException("Missing measured QC ID"), StringComparer.Ordinal) ??
			new Dictionary<string, XElement>(StringComparer.Ordinal);
		var outcomes = binding.Elements(R + "Outcome").ToArray();
		if (outcomes.Length != profile.Checks.Count)
			throw new InvalidDataException("Incomplete QC outcome extension.");
		var results = new List<MediaQcCheckResult>(outcomes.Length);
		for (var index = 0; index < outcomes.Length; index++)
		{
			var outcome = outcomes[index];
			var definition = profile.Checks[index];
			if ((string?)outcome.Attribute("checkId") != definition.Id ||
				!TryParseState((string?)outcome.Attribute("state"), out var state))
				throw new InvalidDataException("Invalid QC result identity/state.");
			var confidence = (string?)outcome.Attribute("confidence");
			var track = (string?)outcome.Attribute("track");
			var editRate = (string?)outcome.Attribute("editRate");
			MediaQcFrameRange? range = null;
			if (editRate is not null)
			{
				var parts = editRate.Split('/');
				if (parts.Length != 2)
					throw new InvalidDataException("Invalid QC timing edit rate.");
				range = new MediaQcFrameRange(
					long.Parse((string?)outcome.Attribute("start") ?? "", CultureInfo.InvariantCulture),
					long.Parse((string?)outcome.Attribute("end") ?? "", CultureInfo.InvariantCulture),
					uint.Parse(parts[0], CultureInfo.InvariantCulture),
					uint.Parse(parts[1], CultureInfo.InvariantCulture));
			}
			else if (outcome.Attribute("start") is not null || outcome.Attribute("end") is not null)
				throw new InvalidDataException("Missing edit rate for a bounded QC region.");
			var result = new MediaQcCheckResult(definition, state,
				(string?)outcome.Attribute("detail") ?? "",
				int.Parse((string?)outcome.Attribute("severity") ?? "-1", CultureInfo.InvariantCulture),
				confidence is null ? null : double.Parse(confidence, CultureInfo.InvariantCulture),
				(string?)outcome.Attribute("source") ?? "",
				(string?)outcome.Attribute("toolVersion") ?? "", range, track);
			result.Validate();
			if (state is MediaQcCheckState.Pass or MediaQcCheckState.Failed)
			{
				if (!measured.Remove(definition.Id, out var e) ||
					(string?)e.Element(Q + "EBUQCVersion") != definition.Version ||
					(bool?)e.Element(Q + "CheckResult") != (state == MediaQcCheckState.Pass) ||
					(string?)e.Element(Q + "ResultDescription") != result.Detail ||
					(int?)e.Element(Q + "MaxSeverity") != result.Severity)
					throw new InvalidDataException("QC CheckResult and evidence extension disagree.");
				var output = e.Element(Q + "Outputs")?.Elements(Q + "Output").SingleOrDefault();
				if (output is null ||
					(string?)output.Element(Q + "Value") != result.Detail ||
					(int?)output.Element(Q + "Severity") != result.Severity ||
					(result.Confidence is double declared
						? (double?)output.Element(Q + "Confidence") != declared
						: output.Element(Q + "Confidence") is not null) ||
					(result.Range is { } region
						? (string?)output.Element(Q + "Locator")?.Element(Q + "Start") != region.Start.ToString(CultureInfo.InvariantCulture) ||
						  (string?)output.Element(Q + "Locator")?.Element(Q + "End") != region.End.ToString(CultureInfo.InvariantCulture)
						: output.Element(Q + "Locator") is not null))
					throw new InvalidDataException("QC output confidence, severity or timing conflicts with the outcome.");
			}
			else if (measured.ContainsKey(definition.Id))
				throw new InvalidDataException("Unexecuted checks cannot have measured CheckResult evidence.");
			results.Add(result);
		}
		if (measured.Count != 0)
			throw new InvalidDataException("Unknown measured QC item.");
		var report = new MediaQcReport(
			ParseUrn((string?)root.Element(Q + "ReportId")),
			MediaQcReport.CurrentModelRelease, profile,
			(string?)binding.Attribute("assetId") ?? "",
			ulong.Parse((string?)binding.Attribute("catalogueRevision") ?? "", CultureInfo.InvariantCulture),
			(string?)binding.Attribute("sha256") ?? "",
			long.Parse((string?)binding.Attribute("sourceBytes") ?? "", CultureInfo.InvariantCulture),
			DateTimeOffset.Parse((string?)binding.Attribute("sourceModifiedUtc") ?? "",
				CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
			DateTimeOffset.Parse((string?)binding.Attribute("createdUtc") ?? "",
				CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
			(string?)binding.Attribute("toolVersion") ?? "", results);
		report.Validate();
		if ((string?)root.Element(Q + "LastModifiedDateTime") !=
			report.SourceModifiedUtc.ToString("O", CultureInfo.InvariantCulture) ||
			!root.Descendants(Q + "ContentIdentifier").Any(x =>
				(string?)x.Element(Q + "ID") == "urn:sha256:" + report.FingerprintSha256.ToLowerInvariant()))
			throw new InvalidDataException("QC content identifiers and source time disagree.");
		var reportCheck = (bool?)root.Element(Q + "CheckResult");
		if (reportCheck.HasValue != report.FullyEvaluated ||
			reportCheck.HasValue && reportCheck.Value != report.Passed)
			throw new InvalidDataException("QC report result is not supported by measured checks.");
		var rates = report.Results.Where(r => r.Range is not null)
			.Select(r => $"{r.Range!.EditRateNumerator}/{r.Range.EditRateDenominator}")
			.Distinct().ToArray();
		var extension = root.Element(Q + "ExtensionProperties")?.Element(T + "TimingExtensionMediaPlaybackEditUnits");
		if (rates.Length > 1 || (rates.Length == 1 ?
			(string?)extension?.Element(T + "EditRate") != rates[0] : extension is not null))
			throw new InvalidDataException("QC timing extension/range mismatch.");
		return report;
	}

	public static void ValidateSchema(string xml) => _ = ParseValidated(xml);

	private static XElement ParseValidated(string xml)
	{
		if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumDocumentCharacters)
			throw new InvalidDataException("Empty/oversized QC XML.");
		var settings = new XmlReaderSettings {
			XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit,
			ValidationType = ValidationType.Schema,
			Schemas = Schemas.Value,
			MaxCharactersInDocument = MaximumDocumentCharacters
		};
		settings.ValidationEventHandler += (_, args) =>
			throw new InvalidDataException("QC XML schema validation failed: " + args.Message);
		using var reader = XmlReader.Create(new StringReader(xml), settings);
		return XDocument.Load(reader, LoadOptions.None).Root ??
			throw new InvalidDataException("QC XML root is missing.");
	}

	private static XmlSchemaSet LoadSchemas()
	{
		var schemas = new XmlSchemaSet { XmlResolver = null };
		var assembly = typeof(MediaQcEbuXml).Assembly;
		foreach (var (resource, ns) in new[] {
			("rtaime.Qc202604.qc.xsd", NamespaceUri),
			("rtaime.Qc202604.timing.xsd", TimingNamespaceUri)
		})
		{
			using var stream = assembly.GetManifestResourceStream(resource)
				?? throw new InvalidOperationException("Pinned EBU QC schema missing: " + resource);
			using var xml = XmlReader.Create(stream, new XmlReaderSettings {
				XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit
			});
			schemas.Add(ns, xml);
		}
		schemas.Compile();
		return schemas;
	}

	private static string FormatState(MediaQcCheckState state) => state switch
	{
		MediaQcCheckState.NotExecuted => "NOT_EXECUTED",
		MediaQcCheckState.Unsupported => "UNSUPPORTED",
		MediaQcCheckState.Failed => "FAILED",
		MediaQcCheckState.Pass => "PASS",
		_ => throw new InvalidDataException("Invalid QC check state.")
	};

	private static bool TryParseState(string? value, out MediaQcCheckState state)
	{
		state = value switch
		{
			"NOT_EXECUTED" => MediaQcCheckState.NotExecuted,
			"UNSUPPORTED" => MediaQcCheckState.Unsupported,
			"FAILED" => MediaQcCheckState.Failed,
			"PASS" => MediaQcCheckState.Pass,
			_ => (MediaQcCheckState)(-1)
		};
		return Enum.IsDefined(state);
	}

	private static XElement ToolInformation(string version) => new(Q + "ToolInformation",
		new XElement(Q + "ToolID", "urn:rtaime:offline-qc"),
		new XElement(Q + "ToolName", "rtaime Offline Media QC"),
		new XElement(Q + "Version", version));

	private static Guid ParseUrn(string? value)
	{
		if (value is null || !value.StartsWith("urn:uuid:", StringComparison.Ordinal) ||
			!Guid.TryParseExact(value["urn:uuid:".Length..], "D", out var id))
			throw new InvalidDataException("Invalid EBU QC UUID URN.");
		return id;
	}
}
