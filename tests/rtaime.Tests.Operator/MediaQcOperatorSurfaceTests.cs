// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaQcOperatorSurfaceTests
{
	[Fact]
	public void Media_library_exposes_start_cancel_results_and_source_bound_XML_actions()
	{
		var xml = XDocument.Load(Path.Combine(Root(), "src", "Hosts",
			"rtaime.Operator", "OperatorMediaLibraryView.xaml"));
		var buttons = xml.Descendants()
			.Where(x => x.Name.LocalName == "RtaimeButton").ToArray();
		foreach (var command in new[] { "StartQcCommand", "CancelQcCommand" })
			Assert.Contains(buttons, b => b.Attribute("Command")?.Value?.Contains(
				"MediaPool." + command, StringComparison.Ordinal) == true);
		Assert.Contains(buttons, b => b.Attribute("Click")?.Value == "OnMediaQcImportClick");
		Assert.Contains(buttons, b => b.Attribute("Click")?.Value == "OnMediaQcExportClick");
		Assert.Contains(xml.Descendants(), x =>
			x.Attribute("AutomationProperties.Name")?.Value ==
			"Per-check QC outcomes including NOT EXECUTED and UNSUPPORTED");
		Assert.Contains(xml.Descendants(), x =>
			x.Attribute("AutomationProperties.Name")?.Value ==
			"Offline QC job status and evidence freshness");
	}

	[Fact]
	public void Offline_QC_cannot_advance_Program_or_mutate_the_catalogue()
	{
		var source = File.ReadAllText(Path.Combine(Root(), "src", "Hosts",
			"rtaime.Operator", "MediaPoolOfflineQc.cs"));
		Assert.Contains("new MediaQcScheduler()", source, StringComparison.Ordinal);
		Assert.Contains("_qcWorker.Begin(", source, StringComparison.Ordinal);
		Assert.Contains("_qcWorker.Cancel(", source, StringComparison.Ordinal);
		Assert.Contains("ReconcileQc(", source, StringComparison.Ordinal);
		Assert.DoesNotContain("SetPreview", source, StringComparison.Ordinal);
		Assert.DoesNotContain("TakeProgram", source, StringComparison.Ordinal);
		Assert.DoesNotContain("UpsertMediaAsset", source, StringComparison.Ordinal);
		Assert.DoesNotContain("SetAudioProduction", source, StringComparison.Ordinal);
	}

	private static string Root()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rtaime.slnx")))
			dir = dir.Parent;
		return dir?.FullName ?? throw new InvalidOperationException("Repository root not available.");
	}
}
