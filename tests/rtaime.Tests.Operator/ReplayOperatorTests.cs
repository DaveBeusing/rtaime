// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ReplayOperatorTests
{
	[Fact]
	public void Replay_surface_uses_custom_interaction_controls()
	{
		var root = FindRepositoryRoot();
		var document = XDocument.Load(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ReplayControl.xaml"));

		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeButton");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeTextBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeMetricBar");
		Assert.DoesNotContain(document.Descendants(), element =>
			element.Name.LocalName is "Button" or "ToggleButton" or "CheckBox" or "RadioButton" or
				"TextBox" or "ComboBox" or "ListBox" or "ListView" or "DataGrid" or "Slider");
	}

	[Fact]
	public void Replay_operator_reuses_media_deck_and_preview_authority()
	{
		var root = FindRepositoryRoot();
		var viewModel = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ReplayViewModel.cs"));

		Assert.Contains("OpenCatalogAssetAsync", viewModel, StringComparison.Ordinal);
		Assert.Contains("SelectPreviewAsync", viewModel, StringComparison.Ordinal);
		Assert.Contains("MarkReplayInAsync", viewModel, StringComparison.Ordinal);
		Assert.Contains("CreateReplayClipAsync", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeHost", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("Provider.", viewModel, StringComparison.Ordinal);
	}

	[Fact]
	public void Replay_surface_exposes_bounded_operator_workflow()
	{
		var root = FindRepositoryRoot();
		var surface = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ReplayControl.xaml"));

		foreach (var label in new[] { "MARK IN", "MARK OUT", "CREATE CLIP", "OPEN IN MEDIA DECK", "SEND TO PREVIEW" })
			Assert.Contains(label, surface, StringComparison.Ordinal);
		Assert.Contains("StoragePercent", surface, StringComparison.Ordinal);
		Assert.Contains("BackpressureStatus", surface, StringComparison.Ordinal);
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
