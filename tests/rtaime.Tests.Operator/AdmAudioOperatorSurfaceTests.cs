// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Xml.Linq;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Operator;

public sealed class AdmAudioOperatorSurfaceTests
{
	[Fact]
	public void Media_library_exposes_manual_ADM_import_and_lossless_export_without_audio_controls()
	{
		var doc = XDocument.Load(Path.Combine(Root(), "src", "Hosts",
			"rtaime.Operator", "OperatorMediaLibraryView.xaml"));
		var buttons = doc.Descendants().Where(x => x.Name.LocalName == "RtaimeButton").ToArray();
		Assert.Contains(buttons, x => (string?)x.Attribute("Click") == "OnMediaAdmImportClick");
		Assert.Contains(buttons, x => (string?)x.Attribute("Click") == "OnMediaAdmExportXmlClick");
		Assert.Contains(buttons, x => (string?)x.Attribute("Click") == "OnMediaAdmExportJsonClick");
		Assert.Contains(doc.Descendants(), x =>
			(string?)x.Attribute("AutomationProperties.Name") ==
				"ADM physical tracks channel roles languages objects and timing");
		Assert.Contains(doc.Descendants(), x =>
			(string?)x.Attribute("AutomationProperties.Name") ==
				"ADM parsing and immersive output capability state");
		Assert.Contains(doc.Descendants(), x =>
			x.Attribute("ItemsSource")?.Value.Contains("MediaPool.AdmTrackRows", StringComparison.Ordinal) == true);
		Assert.Contains(doc.Descendants(), x =>
			x.Attribute("Text")?.Value.Contains("Binding R123AssignedRole", StringComparison.Ordinal) == true);
	}

	[Fact]
	public void Existing_Inspector_metadata_tab_reuses_readonly_ADM_rows()
	{
		var root = Root();
		var vm = File.ReadAllText(Path.Combine(root, "src", "Hosts",
			"rtaime.Operator", "MediaPoolInspectorViewModel.cs"));
		var adm = File.ReadAllText(Path.Combine(root, "src", "Hosts",
			"rtaime.Operator", "MediaPoolAdmMetadata.cs"));
		Assert.Contains("AddAdmInspectorRows();", vm, StringComparison.Ordinal);
		Assert.Contains("ReconcileAdm(snapshot);", vm, StringComparison.Ordinal);
		Assert.Contains("OnAdmSelectionChanged();", vm, StringComparison.Ordinal);
		Assert.Contains("DisposeAdm();", vm, StringComparison.Ordinal);
		Assert.Contains("ADM IMPORTED · LOSSLESS", adm, StringComparison.Ordinal);
		Assert.DoesNotContain("SetAudioInputStateAsync", adm, StringComparison.Ordinal);
		Assert.DoesNotContain("ApplyAudioProduction", adm, StringComparison.Ordinal);
		Assert.DoesNotContain("SelectPreviewAsync", adm, StringComparison.Ordinal);
		Assert.DoesNotContain("CutProgramAsync", adm, StringComparison.Ordinal);
		Assert.DoesNotContain("UpsertInterchangeAsync", adm, StringComparison.Ordinal);
	}

	private static string Root()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rtaime.slnx")))
			dir = dir.Parent;
		return dir?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
	}
}
