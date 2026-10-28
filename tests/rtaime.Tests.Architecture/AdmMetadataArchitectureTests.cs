// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class AdmMetadataArchitectureTests
{
	[Fact]
	public void ADM_metadata_never_enables_audio_dsp_immersive_routing_or_new_authority()
	{
		var root = RepositorySnapshot.Load().Root;
		var contract = Read(root, "src/Contracts/rtaime.Media.Contracts/AdmAudioTrackMetadata.cs");
		var parser = Read(root, "src/Client/rtaime.Client/AdmAudioXml.cs");
		var store = Read(root, "src/Client/rtaime.Client/AdmAudioSidecarStore.cs");
		var inspector = Read(root, "src/Hosts/rtaime.Operator/MediaPoolAdmMetadata.cs");
		var audio = Read(root, "src/Media/rtaime.Media/AudioProduction.cs");
		var catalogue = Read(root, "src/Hosts/rtaime.ControlHost/MediaAssetCatalogPersistenceStore.cs");
		Assert.Contains("SourceXmlBase64", contract, StringComparison.Ordinal);
		Assert.Contains("IMMERSIVE RENDER UNSUPPORTED", contract, StringComparison.Ordinal);
		Assert.Contains("MaxCharactersInDocument", parser, StringComparison.Ordinal);
		Assert.Contains("DtdProcessing.Prohibit", parser, StringComparison.Ordinal);
		Assert.Contains("XmlResolver = null", parser, StringComparison.Ordinal);
		Assert.Contains("File.Move(temporary, target, overwrite: true)", store, StringComparison.Ordinal);
		Assert.Contains("SelectedItem", inspector, StringComparison.Ordinal);
		Assert.DoesNotContain("AdmAudioInterchange", audio, StringComparison.Ordinal);
		Assert.DoesNotContain("AdmAudioInterchange", catalogue, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeProgramExecutionService", inspector, StringComparison.Ordinal);
		Assert.DoesNotContain("SetProgram", inspector, StringComparison.Ordinal);
		Assert.DoesNotContain("DispatcherTimer", parser, StringComparison.Ordinal);
		Assert.DoesNotContain("new AudioProductionEngine", parser, StringComparison.Ordinal);
	}

	[Fact]
	public void Metadata_import_is_bounded_and_wont_rewrite_legacy_EBUCore_catalogue()
	{
		var root = RepositorySnapshot.Load().Root;
		var model = Read(root, "src/Contracts/rtaime.Media.Contracts/AdmAudioTrackMetadata.cs");
		Assert.Contains("MaximumXmlBytes = 192 * 1024", model, StringComparison.Ordinal);
		Assert.Contains("MaximumTracks = 16", model, StringComparison.Ordinal);
		Assert.Contains("R123TrackAllocation.Validate", model, StringComparison.Ordinal);
		var ebucore = Read(root, "src/Contracts/rtaime.Media.Contracts/MediaInterchangeMetadata.cs");
		Assert.Contains("rtaime.media-interchange.ebucore-1.10.v1", ebucore, StringComparison.Ordinal);
		Assert.Contains("MaximumJsonBytes = 2048", ebucore, StringComparison.Ordinal);
	}

	private static string Read(string root, string file) =>
		File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
}
