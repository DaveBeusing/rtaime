// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class RundownAutomationUiTests
{
	[Fact]
	public void Rundown_surface_uses_custom_controls_for_bounded_automation_authoring()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "RundownControl.xaml");
		var document = XDocument.Load(path);

		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeComboBox" &&
			string.Equals(element.Attribute("ItemsSource")?.Value, "{Binding FollowActions}", StringComparison.Ordinal));
		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeComboBox" &&
			string.Equals(element.Attribute("ItemsSource")?.Value, "{Binding RepeatModes}", StringComparison.Ordinal));
		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeTextBox" &&
			(element.Attribute("Text")?.Value?.Contains("FollowDelayFrames", StringComparison.Ordinal) ?? false));
		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeStatusBadge" &&
			(element.Attribute("Content")?.Value?.Contains("AutomationState", StringComparison.Ordinal) ?? false));

		Assert.DoesNotContain(document.Descendants(), element =>
			element.Name.LocalName is "Button" or "ToggleButton" or "CheckBox" or "RadioButton" or
				"TextBox" or "ComboBox" or "ListBox" or "ListView" or "DataGrid" or "Slider");
	}

	[Fact]
	public void Rundown_surface_projects_confirmed_pending_timing_and_repeat_state()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "RundownControl.xaml"));
		var viewModel = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "RundownViewModel.cs"));

		Assert.Contains("Binding PendingNext", source, StringComparison.Ordinal);
		Assert.Contains("Binding FollowCountdown", source, StringComparison.Ordinal);
		Assert.Contains("Binding RepeatStatus", source, StringComparison.Ordinal);
		Assert.Contains("execution.PendingNextItemId", viewModel, StringComparison.Ordinal);
		Assert.Contains("execution.FollowTargetFrameSequence", viewModel, StringComparison.Ordinal);
		Assert.Contains("execution.RemainingFollowFrames", viewModel, StringComparison.Ordinal);
		Assert.Contains("execution.RemainingItemRepeats", viewModel, StringComparison.Ordinal);
		Assert.Contains("execution.RemainingRundownRepeats", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("DispatcherTimer", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("System.Timers", viewModel, StringComparison.Ordinal);
	}

	[Fact]
	public void Rundown_authoring_exposes_the_closed_follow_and_repeat_unions()
	{
		var root = FindRepositoryRoot();
		var viewModel = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "RundownViewModel.cs"));

		foreach (var follow in new[]
		{
			"RundownFollowActionKind.Manual",
			"RundownFollowActionKind.PrepareNext",
			"RundownFollowActionKind.AutoGoNext",
			"RundownFollowActionKind.AutoGoNextAfterFrames",
			"RundownFollowActionKind.AutoOnMediaEnd",
			"RundownFollowActionKind.Hold"
		})
		{
			Assert.Contains(follow, viewModel, StringComparison.Ordinal);
		}

		Assert.Contains("RundownRepeatMode.RepeatItem", viewModel, StringComparison.Ordinal);
		Assert.Contains("RundownRepeatMode.RepeatRundown", viewModel, StringComparison.Ordinal);
		Assert.Contains("AutoOnMediaEnd is valid only for media rundown items.", viewModel, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
	}
}
