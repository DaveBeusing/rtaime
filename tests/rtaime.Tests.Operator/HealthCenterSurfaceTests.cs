// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class HealthCenterSurfaceTests
{
	[Fact]
	public void Incident_actions_are_named_keyboard_controls_inside_scrollable_evidence()
	{
		DirectoryInfo? root = new(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
			root = root.Parent;
		Assert.NotNull(root);
		var document = XDocument.Load(Path.Combine(root.FullName, "src", "Hosts", "rtaime.Operator", "HealthCenterControl.xaml"));
		var evidence = Assert.Single(document.Descendants().Where(element =>
			element.Name.LocalName == "RtaimeScrollViewer" &&
			element.Attribute("AutomationProperties.Name")?.Value == "Incident evidence and recovery"));
		Assert.Equal("Auto", evidence.Attribute("VerticalScrollBarVisibility")?.Value);
		foreach (var command in new[] { "RecoverCommand", "CopyIncidentSummaryCommand", "Startup.ExportSupportBundleCommand" })
		{
			var action = Assert.Single(evidence.Descendants().Where(element =>
				element.Name.LocalName == "RtaimeButton" &&
				(element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) ?? false)));
			Assert.False(string.IsNullOrWhiteSpace(action.Attribute("AutomationProperties.Name")?.Value));
			Assert.NotEqual("False", action.Attribute("Focusable")?.Value);
		}
		foreach (var field in new[] { "ProgramImpact", "EvidenceSource", "FirstObserved", "LastObserved", "Detail", "RecoveryStatus" })
			Assert.Contains(evidence.Descendants(), element =>
				element.Attribute("Text")?.Value == "{Binding SelectedSubsystem." + field + "}");
		Assert.Contains(evidence.Descendants(), element =>
			element.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite");
	}
}
