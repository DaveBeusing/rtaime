// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ProductionMacroOperatorTests
{
	[Fact]
	public void Macro_surface_uses_only_custom_interaction_controls()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProductionMacroControl.xaml");
		var document = XDocument.Load(path);

		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeComboBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeListBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeTextBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeCheckBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeButton");

		Assert.DoesNotContain(document.Descendants(), element =>
			element.Name.LocalName is "Button" or "ToggleButton" or "CheckBox" or "RadioButton" or
				"TextBox" or "ComboBox" or "ListBox" or "ListView" or "DataGrid" or "Slider");
	}

	[Fact]
	public void Macro_editor_exposes_closed_governed_union_without_script_surface()
	{
		var root = FindRepositoryRoot();
		var viewModel = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProductionMacroViewModel.cs"));
		var surface = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProductionMacroControl.xaml"));

		foreach (var action in new[]
		{
			"ShowControlActionKind.ActivateScene",
			"ShowControlActionKind.SetPreview",
			"ShowControlActionKind.Cut",
			"ShowControlActionKind.Dissolve",
			"ShowControlActionKind.JumpMediaCue",
			"ShowControlActionKind.MediaOpen",
			"ShowControlActionKind.MediaPlay",
			"ShowControlActionKind.MediaPause",
			"ShowControlActionKind.MediaStop",
			"ShowControlActionKind.SetLayerVisibility",
			"ShowControlActionKind.StartRecording",
			"ShowControlActionKind.StopRecording",
			"ShowControlActionKind.WaitFrames",
			"ShowControlActionKind.SetAudioRouting",
			"ShowControlActionKind.RouteOutputRole",
			"ShowControlActionKind.SetAudioInputState"
		})
		{
			Assert.Contains(action, viewModel, StringComparison.Ordinal);
		}

		Assert.DoesNotContain("CodeEditor", surface, StringComparison.Ordinal);
		Assert.DoesNotContain("ScriptText", surface, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Process.Start", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeHost", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("Provider.", viewModel, StringComparison.Ordinal);
	}

	[Fact]
	public void Scenes_and_cues_workspace_hosts_macro_surface()
	{
		var root = FindRepositoryRoot();
		var live = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "LiveSceneCueControl.xaml"));
		var window = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml.cs"));

		Assert.Contains("Header=\"MACROS\"", live, StringComparison.Ordinal);
		Assert.Contains("ProductionMacroControl", live, StringComparison.Ordinal);
		Assert.Contains("ProductionMacros = new ProductionMacroViewModel(client)", window, StringComparison.Ordinal);
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
