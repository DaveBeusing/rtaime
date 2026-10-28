// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class AdmAudioInterchangeTests
{
	private static readonly string Hash = new('A', 64);
	private static readonly DateTimeOffset Clock =
		new(2026, 10, 28, 16, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Stereo_adm_roundtrip_is_byte_identical_even_with_unknown_vendor_xml()
	{
		var asset = Asset();
		var bytes = Fixture("stereo.xml");
		var inspection = AdmAudioXml.ImportXml(bytes, asset);
		Assert.Equal(2, inspection.Tracks.Count);
		Assert.Equal("2.0 · METADATA ONLY", inspection.LayoutLabel);
		Assert.Equal(new[] { "L", "R" }, inspection.Tracks.Select(x => x.ChannelRole));
		Assert.All(inspection.Tracks, track => {
			Assert.Equal("en", track.Language);
			Assert.Equal("00:00:00.00000", track.Start);
			Assert.Equal("00:00:10.00000", track.End);
			Assert.Equal("DirectSpeakers", track.SourceKind);
			Assert.Null(track.PhysicalTrackNumber);
			Assert.Contains("IMMERSIVE RENDER UNSUPPORTED", track.DisplayStatus);
		});
		Assert.Equal(bytes, AdmAudioXml.ExportXml(inspection));
		var sidecar = AdmAudioXml.ExportSidecarJson(inspection);
		var loaded = AdmAudioXml.ImportSidecarJson(sidecar, asset);
		Assert.Equal(bytes, AdmAudioXml.ExportXml(loaded));
		Assert.Contains("urn:rtaime:example-vendor", Encoding.UTF8.GetString(AdmAudioXml.ExportXml(loaded)));
		Assert.Equal("UNVERIFIED", loaded.DecodeStatus);
		Assert.StartsWith("UNSUPPORTED", loaded.RenderStatus);
		Assert.StartsWith("UNSUPPORTED", loaded.PublishStatus);
	}

	[Fact]
	public void EBUCore_wrapped_ADM_XML_preserves_its_namespace_and_all_embedded_source_bytes()
	{
		const string ebucore = "urn:ebu:metadata-schema:ebuCore_2014";
		var xml = Encoding.UTF8.GetString(Fixture("stereo.xml"));
		xml = xml.Replace(
			"<audioFormatExtended version=",
			"<ebuCoreMain xmlns=\"" + ebucore + "\"><coreMetadata><format>" +
			"<audioFormatExtended version=", StringComparison.Ordinal);
		xml = xml.Replace("</audioFormatExtended>",
			"</audioFormatExtended></format></coreMetadata></ebuCoreMain>", StringComparison.Ordinal);
		var bytes = Encoding.UTF8.GetBytes(xml);
		var observation = AdmAudioXml.ImportXml(bytes, Asset());
		Assert.Equal(2, observation.Tracks.Count);
		Assert.Equal(bytes, AdmAudioXml.ExportXml(observation));
		Assert.Contains(ebucore, Encoding.UTF8.GetString(AdmAudioXml.ExportXml(observation)));
	}

		[Fact]
	public void Unknown_vendor_elements_using_ADM_names_are_preserved_but_not_interpreted()
	{
		var original = Encoding.UTF8.GetString(Fixture("stereo.xml"));
		var modified = original.Replace("</audioFormatExtended>",
			"<vendor:audioObject audioObjectID=\"AO_0001\">" +
			"<vendor:audioTrackUIDRef>ATU_99999999</vendor:audioTrackUIDRef>" +
			"</vendor:audioObject>" +
			"<vendor:audioFormatExtended version=\"bogus\"/>" +
			"</audioFormatExtended>", StringComparison.Ordinal);
		var bytes = Encoding.UTF8.GetBytes(modified);
		var result = AdmAudioXml.ImportXml(bytes, Asset());
		Assert.Equal(2, result.Tracks.Count);
		Assert.Equal(bytes, AdmAudioXml.ExportXml(result));
	}

		[Fact]
	public void Stereo_bom_and_original_whitespace_survive_an_independent_json_exchange()
	{
		var source = Fixture("stereo.xml");
		var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(source).ToArray();
		var result = AdmAudioXml.ImportXml(bytes, Asset());
		Assert.Equal(bytes, AdmAudioXml.ExportXml(
			AdmAudioXml.ImportSidecarJson(AdmAudioXml.ExportSidecarJson(result), Asset())));
	}

	[Fact]
	public void Six_channel_surround_is_metadata_only_and_not_an_R123_eight_track_claim()
	{
		var result = AdmAudioXml.ImportXml(Fixture("surround-5-1.xml"), Asset());
		Assert.Equal(6, result.Tracks.Count);
		Assert.Equal("5.1 · METADATA ONLY", result.LayoutLabel);
		Assert.Null(result.Interchange.R123Allocation);
		Assert.All(result.Tracks, x => Assert.Null(x.PhysicalTrackNumber));
		Assert.All(result.Tracks, x => Assert.Contains("UNSUPPORTED", x.DisplayStatus));
		Assert.Equal("UNVERIFIED", result.DecodeStatus);
	}

	[Fact]
	public void R123_8a_explicit_full_track_allocation_can_be_preserved_without_exposing_immersive_output()
	{
		var asset = Asset();
		var result = AdmAudioXml.ImportXml(Fixture("r123-8a.xml"), asset);
		var roles = R123TrackAllocation.ExpectedRoles("8a");
		var mapped = result.Interchange with {
			R123Allocation = "8a",
			Assignments = roles.Select((role, i) =>
				new AdmTrackAssignment(i + 1, "ATU_" + (i + 1).ToString("X8"), role, "en")).ToArray()
		};
		var projected = AdmAudioXml.Parse(mapped);
		Assert.StartsWith("R 123 8a DECLARED", projected.LayoutLabel);
		Assert.Equal(8, projected.Tracks.Count);
		Assert.Equal(Enumerable.Range(1, 8), projected.Tracks.Select(x => x.PhysicalTrackNumber!.Value));
		Assert.Equal(roles, projected.Tracks.Select(row => row.R123AssignedRole));
		Assert.Equal(roles, projected.Tracks.Select(row =>
			mapped.Assignments.Single(x => x.AudioTrackUid == row.AudioTrackUid).R123Role));
		var serialized = AdmAudioXml.ExportSidecarJson(projected);
		var reimported = AdmAudioXml.ImportSidecarJson(serialized, asset);
		Assert.Equal(projected.Tracks, reimported.Tracks);
		Assert.Equal(Fixture("r123-8a.xml"), AdmAudioXml.ExportXml(reimported));
		Assert.Contains("UNSUPPORTED", reimported.RenderStatus);
	}

	[Fact]
	public void Object_audio_retains_positions_and_extensions_without_claiming_a_renderer()
	{
		var bytes = Fixture("audio-object.xml");
		var result = AdmAudioXml.ImportXml(bytes, Asset());
		Assert.Single(result.Tracks);
		Assert.Equal("Objects", result.Tracks[0].SourceKind);
		Assert.Null(result.Tracks[0].ChannelRole);
		Assert.Equal("TRACK / OBJECT METADATA ONLY", result.LayoutLabel);
		Assert.Equal(bytes, AdmAudioXml.ExportXml(result));
		Assert.Contains("attribute=\"still-preserved\"",
			Encoding.UTF8.GetString(AdmAudioXml.ExportXml(result)), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("missing-ref")]
	[InlineData("duplicate-id")]
	[InlineData("invalid-uid")]
	[InlineData("wrong-version")]
	[InlineData("missing-track")]
	[InlineData("cycle")]
	[InlineData("bad-time")]
	[InlineData("orphan-content")]
	[InlineData("missing-content-object")]
	public void Broken_references_profiles_and_times_cannot_enter_the_interchange(string mutation)
	{
		var xml = Encoding.UTF8.GetString(Fixture("stereo.xml"));
		xml = mutation switch {
			"missing-ref" => xml.Replace("<audioObjectIDRef>AO_0001</audioObjectIDRef>",
				"<audioObjectIDRef>AO_9999</audioObjectIDRef>", StringComparison.Ordinal),
			"duplicate-id" => xml.Replace("</audioFormatExtended>",
				"<audioTrackUID UID=\"ATU_00000001\"/></audioFormatExtended>", StringComparison.Ordinal),
			"invalid-uid" => xml.Replace("ATU_00000001", "NOT_AN_ADM_UID", StringComparison.Ordinal),
			"wrong-version" => xml.Replace("ITU-R_BS.2076-3", "ITU-R_BS.2076-1", StringComparison.Ordinal),
			"missing-track" => xml.Replace("<audioTrackUIDRef>ATU_00000002</audioTrackUIDRef>", "", StringComparison.Ordinal),
			"cycle" => xml.Replace("</audioObject>",
				"<audioObjectIDRef>AO_0001</audioObjectIDRef></audioObject>", StringComparison.Ordinal),
			"bad-time" => xml.Replace("duration=\"00:00:10.00000\"", "duration=\"-10\"", StringComparison.Ordinal),
			"orphan-content" => xml.Replace("</audioFormatExtended>",
				"<audioContent audioContentID=\"ACO_9999\" audioContentName=\"Unused\"/></audioFormatExtended>",
				StringComparison.Ordinal),
			"missing-content-object" => xml.Replace("<audioObjectIDRef>AO_0001</audioObjectIDRef>",
				"", StringComparison.Ordinal),
			_ => throw new ArgumentOutOfRangeException(nameof(mutation))
		};
		Assert.ThrowsAny<Exception>(() => AdmAudioXml.ImportXml(Encoding.UTF8.GetBytes(xml), Asset()));
	}

	[Theory]
	[InlineData("4b", 4)]
	[InlineData("8a", 8)]
	[InlineData("8b", 8)]
	public void R123_legacy_exchange_layouts_have_explicit_unambiguous_physical_role_order(
		string code, int count)
	{
		var roles = R123TrackAllocation.ExpectedRoles(code);
		Assert.Equal(count, roles.Count);
		var tracks = roles.Select((role, index) =>
			new AdmTrackAssignment(index + 1, "ATU_" + (index + 1).ToString("X8"), role)).ToArray();
		R123TrackAllocation.Validate(code, tracks);
		Assert.Throws<InvalidDataException>(() => R123TrackAllocation.Validate(
			code, tracks.Select((track, index) =>
				index == 0 ? track with { R123Role = "UNKNOWN" } : track).ToArray()));
		Assert.Throws<InvalidDataException>(() => R123TrackAllocation.Validate(null, tracks));
	}

		[Fact]
	public void Unknown_R123_arrangement_and_misordered_channels_are_rejected()
	{
		var result = AdmAudioXml.ImportXml(Fixture("r123-8a.xml"), Asset());
		var assignments = R123TrackAllocation.ExpectedRoles("8a").Select((role, i) =>
			new AdmTrackAssignment(i + 1, "ATU_" + (i + 1).ToString("X8"), role)).ToArray();
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.Parse(result.Interchange with { R123Allocation = "12a", Assignments = assignments }));
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.Parse(result.Interchange with {
				R123Allocation = "8a",
				Assignments = assignments.Where(x => x.PhysicalTrackNumber != 8).ToArray()
			}));
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.Parse(result.Interchange with {
				R123Allocation = "8a",
				Assignments = assignments.Select(x => x.PhysicalTrackNumber == 3
					? x with { R123Role = "LFE" } : x).ToArray()
			}));
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.Parse(result.Interchange with {
				R123Allocation = "8a",
				Assignments = assignments.Select(x => x.PhysicalTrackNumber == 8
					? x with { AudioTrackUid = "ATU_99999999" } : x).ToArray()
			}));
	}

	[Fact]
	public void Unknown_JSON_fields_wrong_asset_and_fingerprint_are_rejected()
	{
		var asset = Asset();
		var observation = AdmAudioXml.ImportXml(Fixture("stereo.xml"), asset);
		var json = AdmAudioXml.ExportSidecarJson(observation);
		Assert.ThrowsAny<Exception>(() => AdmAudioXml.ImportSidecarJson(json.Replace(
			"\"format\":", "\"unreviewed\":true,\"format\":", StringComparison.Ordinal), asset));
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.ImportSidecarJson(json, Asset(MediaAssetId.New())));
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.ImportSidecarJson(json, Asset(fingerprint: new string('B', 64))));
		Assert.Throws<InvalidDataException>(() =>
			AdmAudioXml.ImportSidecarJson(json.Replace("tech3393-2025.v1", "tech3392.v1",
				StringComparison.Ordinal), asset));
	}

	[Fact]
	public void DTD_XXE_excessive_depth_and_size_are_rejected_without_network_resolution()
	{
		var clean = Encoding.UTF8.GetString(Fixture("stereo.xml"));
		Assert.ThrowsAny<Exception>(() => AdmAudioXml.ImportXml(Encoding.UTF8.GetBytes(
			"<!DOCTYPE audioFormatExtended [<!ENTITY ext SYSTEM \"file:///etc/passwd\">]>" +
			clean), Asset()));
		Assert.ThrowsAny<Exception>(() => AdmAudioXml.ImportXml(Encoding.UTF8.GetBytes(
			clean.Replace("</audioFormatExtended>",
				new string(' ', 1) + string.Concat(Enumerable.Repeat("<nested>", 32)) +
				"payload" + string.Concat(Enumerable.Repeat("</nested>", 32)) +
				"</audioFormatExtended>", StringComparison.Ordinal)), Asset()));
		Assert.Throws<InvalidDataException>(() => AdmAudioXml.ImportXml(
			new byte[AdmAudioInterchange.MaximumXmlBytes + 1], Asset()));
	}

	private static MediaAssetDescriptor Asset(MediaAssetId? id = null, string? fingerprint = null)
	{
		var timestamp = new UtcTimestamp(Clock);
		return new MediaAssetDescriptor(
			id ?? new MediaAssetId(Identity.Parse("bd000000-0000-0000-0000-000000000001")),
			MediaAssetOriginKind.LocalFile, @"C:\asset\studio.mp4", "Studio",
			MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(10), 1024, fingerprint ?? Hash,
			timestamp, timestamp, MediaAssetAvailability.Online);
	}

	private static byte[] Fixture(string name)
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
			root = root.Parent;
		if (root is null) throw new InvalidOperationException("Solution root not available.");
		return File.ReadAllBytes(Path.Combine(root.FullName, "tests", "fixtures", "adm", "2025", name));
	}
}
