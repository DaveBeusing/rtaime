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
		var evidence = Assert.Single(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeScrollViewer" &&
			element.Attribute("AutomationProperties.Name")?.Value == "Incident evidence and recovery");
		Assert.Equal("Auto", evidence.Attribute("VerticalScrollBarVisibility")?.Value);
		foreach (var command in new[] { "RecoverCommand", "CopyIncidentSummaryCommand", "Startup.ExportSupportBundleCommand" })
		{
			var action = Assert.Single(evidence.Descendants(), element =>
				element.Name.LocalName == "RtaimeButton" &&
				(element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) ?? false));
			Assert.False(string.IsNullOrWhiteSpace(action.Attribute("AutomationProperties.Name")?.Value));
			Assert.NotEqual("False", action.Attribute("Focusable")?.Value);
		}
		foreach (var field in new[] { "ProgramImpact", "EvidenceSource", "FirstObserved", "LastObserved", "Detail", "RecoveryStatus" })
			Assert.Contains(evidence.Descendants(), element =>
				element.Attribute("Text")?.Value == "{Binding SelectedSubsystem." + field + "}");
		Assert.Contains(evidence.Descendants(), element =>
			element.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite");
	}
	[Fact]
	public void Health_diagnostics_and_advanced_links_reuse_visible_custom_incident_controls()
	{
		DirectoryInfo? root = new(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
			root = root.Parent;
		Assert.NotNull(root);
		var document = XDocument.Load(Path.Combine(root.FullName, "src", "Hosts", "rtaime.Operator", "HealthCenterControl.xaml"));
		var elements = document.Descendants().ToArray();
		Assert.Contains(elements, element => element.Name.LocalName == "RtaimeButton" &&
			element.Attribute("Command")?.Value == "{Binding ToggleTelemetryOverviewCommand}" &&
			!string.IsNullOrWhiteSpace(element.Attribute("AutomationProperties.Name")?.Value));
		Assert.Contains(elements, element => element.Name.LocalName == "RtaimeListBox" &&
			element.Attribute("ItemsSource")?.Value == "{Binding Overview}" &&
			element.Descendants().Any(trigger => trigger.Name.LocalName == "DataTrigger" &&
				trigger.Attribute("Binding")?.Value == "{Binding TelemetryOverviewVisible}" &&
				trigger.Attribute("Value")?.Value == "True"));
		Assert.Contains(elements, element => element.Name.LocalName == "RtaimeListBox" &&
			element.Attribute("ItemsSource")?.Value == "{Binding SelectedSubsystem.Metrics}" &&
			element.Descendants().Any(trigger => trigger.Name.LocalName == "DataTrigger" &&
				trigger.Attribute("Binding")?.Value == "{Binding TechnicalDetailsVisible}" &&
				trigger.Attribute("Value")?.Value == "True"));
		foreach (var destination in new[] { "AI", "SETTINGS" })
			Assert.Contains(elements, element => element.Name.LocalName == "RtaimeButton" &&
				element.Attribute("CommandParameter")?.Value == destination &&
				element.Attribute("Command")?.Value?.Contains("Shell.SelectWorkspaceCommand", StringComparison.Ordinal) == true &&
				!string.IsNullOrWhiteSpace(element.Attribute("AutomationProperties.Name")?.Value));
		foreach (var command in new[] { "OpenShowChecksCommand", "OpenCommandPaletteCommand" })
			Assert.Contains(elements, element => element.Name.LocalName == "RtaimeButton" &&
				element.Attribute("Command")?.Value?.Contains(command, StringComparison.Ordinal) == true);
		Assert.Contains(elements, element =>
			element.Attribute("AutomationProperties.Name")?.Value == "Operator product version and build identity" &&
			element.Attribute("Text")?.Value == "{Binding ProductBuildIdentity, ElementName=RootWindow}");
		// RootWindow is explicitly registered to the real MainWindow inside detached
		// OperatorPanelWindow; all links must retain command ownership while floating.
		foreach (var action in new[] { "OpenShowChecksCommand", "OpenCommandPaletteCommand", "Shell.SelectWorkspaceCommand" })
			Assert.Contains(elements, element => element.Name.LocalName == "RtaimeButton" &&
				element.Attribute("Command")?.Value == "{Binding " + action + ", ElementName=RootWindow}");

	}

}
