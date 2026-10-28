// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaQcEbuXmlTests
{
	private static readonly DateTimeOffset Stamp = new(2026, 10, 28, 16, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Official_EBU_QC_2026_04_sample_conforms_to_pinned_schema()
	{
		var root = LocateRoot();
		var sample = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "ebu-qc",
			"2026-04", "qc-report-generic-sample.xml"));
		MediaQcEbuXml.ValidateSchema(sample);
		Assert.Equal("v3", MediaQcEbuXml.CatalogueApiVersion);
	}

	[Fact]
	public void Local_QC_report_round_trips_exact_2026_04_schema_and_does_not_promote_unmeasured_checks()
	{
		var expected = Report();
		var xml = MediaQcEbuXml.Export(expected);
		MediaQcEbuXml.ValidateSchema(xml);
		var restored = MediaQcEbuXml.Import(xml);
		Assert.Equal(expected.ReportId, restored.ReportId);
		Assert.Equal(expected.CreatedUtc, restored.CreatedUtc);
		Assert.Equal(expected.SourceModifiedUtc, restored.SourceModifiedUtc);
		Assert.Equal(expected.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
			(string?)XDocument.Parse(xml).Root?.Element(XName.Get("LastModifiedDateTime", MediaQcEbuXml.NamespaceUri)));
		Assert.Equal(expected.AssetId, restored.AssetId);
		Assert.Equal(expected.FingerprintSha256, restored.FingerprintSha256);
		Assert.Equal(expected.CatalogueRevision, restored.CatalogueRevision);
		Assert.Equal(expected.Results.Select(x => x.State), restored.Results.Select(x => x.State));
		Assert.False(restored.FullyEvaluated);
		Assert.False(restored.Passed);
		Assert.Equal(2, restored.Results.Count(x => x.State == MediaQcCheckState.NotExecuted));
		var doc = XDocument.Parse(xml);
		XNamespace qc = MediaQcEbuXml.NamespaceUri;
		XNamespace rqc = MediaQcEbuXml.ExtensionNamespaceUri;
		Assert.Null(doc.Root!.Element(qc + "CheckResult"));
		Assert.Equal(5, doc.Descendants(qc + "ItemResult").Count());
		Assert.Equal(7, doc.Descendants(qc + "CheckResultDefinition").Count());
		Assert.Equal(7, doc.Descendants(rqc + "Outcome").Count());
		Assert.Equal(2, doc.Descendants(rqc + "Outcome")
			.Count(x => (string?)x.Attribute("state") == "NOT_EXECUTED"));
		Assert.DoesNotContain("CheckResult</", string.Join("",
			doc.Descendants(qc + "Output").Select(x => (string?)x.Element(qc + "Name"))));
	}

	[Fact]
	public void Timing_extension_uses_rational_edit_units_and_matches_result_locator()
	{
		var report = Report();
		var results = report.Results.ToArray();
		results[0] = results[0] with {
			Range = new MediaQcFrameRange(100, 225, 30000, 1001), TrackId = "video-1"
		};
		report = report with { Results = results };
		var xml = MediaQcEbuXml.Export(report);
		Assert.Contains(MediaQcEbuXml.TimingNamespaceUri, xml);
		Assert.Contains("30000/1001", xml);
		var parsed = MediaQcEbuXml.Import(xml);
		Assert.Equal(results[0].Range, parsed.Results[0].Range);
		Assert.Equal("video-1", parsed.Results[0].TrackId);
	}

	[Theory]
	[InlineData("namespace")]
	[InlineData("release")]
	[InlineData("result")]
	[InlineData("definition")]
	[InlineData("content")]
	[InlineData("tool")]
	[InlineData("doctype")]
	public void Invalid_or_foreign_QC_payloads_are_rejected(string mutation)
	{
		var xml = MediaQcEbuXml.Export(Report());
		xml = mutation switch {
			"namespace" => xml.Replace("tag:qc.ebu.ch,2026-04", "tag:qc.ebu.ch,2026-01", StringComparison.Ordinal),
			"release" => xml.Replace("modelRelease=\"2026-04\"", "modelRelease=\"2026-01\"", StringComparison.Ordinal),
			"result" => xml.Replace("state=\"PASS\"", "state=\"FAILED\"", StringComparison.Ordinal),
			"definition" => xml.Replace("CheckResultDefinition", "LegacyCheckResult", StringComparison.Ordinal),
			"content" => xml.Replace("urn:sha256:" + new string('a', 64),
				"urn:sha256:" + new string('b', 64), StringComparison.Ordinal),
			"tool" => xml.Replace("<Version>1.0.0</Version>", "<Version>9.9.9</Version>", StringComparison.Ordinal),
			"doctype" => "<!DOCTYPE Report [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + xml,
			_ => throw new ArgumentOutOfRangeException(nameof(mutation))
		};
		Assert.ThrowsAny<Exception>(() => MediaQcEbuXml.Import(xml));
	}

	[Fact]
	public void No_competing_CheckResult_outputs_are_created_for_internal_checks()
	{
		var xml = MediaQcEbuXml.Export(Report());
		var doc = XDocument.Parse(xml);
		XNamespace q = MediaQcEbuXml.NamespaceUri;
		Assert.DoesNotContain(doc.Descendants(q + "Output"),
			output => (string?)output.Element(q + "Name") == "CheckResult");
		Assert.All(doc.Descendants(q + "ItemDefinition"), definition =>
			Assert.NotNull(definition.Element(q + "CheckResultDefinition")));
	}

	private static MediaQcReport Report()
	{
		var profile = MediaQcProfile.OfflineFileV1;
		var results = profile.Checks.Select((definition, index) => new MediaQcCheckResult(
			definition, index >= 5 ? MediaQcCheckState.NotExecuted : MediaQcCheckState.Pass,
			index >= 5 ? "Not executed; capability unavailable." : "Verified container evidence.",
			0, index >= 5 ? null : 1.0, "offline-file:sha256:" + new string('a', 64), "1.0.0")).ToArray();
		return new MediaQcReport(Guid.Parse("fcdf3a20-1f44-45f1-af62-72b0035e4450"),
			MediaQcReport.CurrentModelRelease, profile,
			"fdcc239f-f58c-4485-b779-d283ec42c309", 4,
			new string('A', 64), 1024,
			Stamp.AddDays(-1), Stamp, "1.0.0", results);
	}

	private static string LocateRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rtaime.slnx")))
			dir = dir.Parent;
		return dir?.FullName ?? throw new InvalidOperationException("Solution root unavailable.");
	}
}
