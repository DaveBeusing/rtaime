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

	[Fact]
	public void Rundown_draft_commands_are_disabled_during_execution_and_recovery()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "RundownViewModel.cs"));

		Assert.Contains("private bool CanEditDraft()", source, StringComparison.Ordinal);
		Assert.Contains("State is not (\"EXECUTING\" or \"RECOVERYREQUIRED\")", source, StringComparison.Ordinal);
		Assert.Contains("() => CanEditDraft() && SelectedItem is not null && Items.Count > 1", source, StringComparison.Ordinal);
		Assert.Contains("if (!CanEditDraft() || SelectedItem is null)", source, StringComparison.Ordinal);
		Assert.Contains("if (!CanEditDraft())", source, StringComparison.Ordinal);
	}

    [Fact]
    public void Live_rundown_and_show_cue_surfaces_distinguish_confirmed_targets_from_local_selection()
    {
        var root = FindRepositoryRoot();
        var folder = Path.Combine(root, "src", "Hosts", "rtaime.Operator");
        var rundownPath = Path.Combine(folder, "RundownControl.xaml");
        var showPath = Path.Combine(folder, "ShowControlCueStackControl.xaml");
        var rundown = XDocument.Load(rundownPath);
        var show = XDocument.Load(showPath);
        var rundownText = File.ReadAllText(rundownPath);
        var showText = File.ReadAllText(showPath);
        var handlers = File.ReadAllText(Path.Combine(folder, "RundownControl.xaml.cs"));

        foreach (var binding in new[] { "NowItemName", "NowItemStatus",
            "NextItemName", "NextItemStatus", "SelectedItemName" })
            Assert.Contains(rundown.Descendants(), node =>
                node.Attributes().Any(attribute =>
                    attribute.Value.Contains("Binding " + binding, StringComparison.Ordinal)));
        Assert.Contains(rundown.Descendants(), node =>
            node.Name.LocalName == "RtaimeButton" &&
            node.Attribute("Click")?.Value == "ToggleAuthoringOptions_Click");
        Assert.Contains(rundown.Descendants(), node =>
            node.Name.LocalName == "RtaimeContextMenu" &&
            node.Descendants().Any(item =>
                item.Name.LocalName == "RtaimeMenuItem" &&
                (item.Attribute("Command")?.Value.Contains("AddGraphicsCommand", StringComparison.Ordinal) ?? false)));
        Assert.Contains("Binding AdvancedAuthoringVisibility", rundownText, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+G", rundownText, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+H", rundownText, StringComparison.Ordinal);
        Assert.Contains("e.IsRepeat", handlers, StringComparison.Ordinal);
        Assert.Contains("model.GoCommand", handlers, StringComparison.Ordinal);
        Assert.Contains("model.HoldCommand", handlers, StringComparison.Ordinal);

        foreach (var binding in new[] { "CueRunStatus", "LocalCueSelection", "NextCueStatus" })
            Assert.Contains(show.Descendants(), node =>
                node.Attributes().Any(attribute =>
                    attribute.Value.Contains("Binding " + binding, StringComparison.Ordinal)));
        Assert.Contains("SHOW GO · F9", showText, StringComparison.Ordinal);
        Assert.Contains("RUNDOWN GO", rundownText, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding GoCommand}\"",
            string.Join("\n", show.Descendants().Where(element =>
                element.Name.LocalName == "RtaimeMenuItem")), StringComparison.Ordinal);
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
