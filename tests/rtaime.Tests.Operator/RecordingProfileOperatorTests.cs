// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class RecordingProfileOperatorTests
{
	[Fact]
	public void Recording_surface_selects_only_confirmed_profiles_with_custom_controls()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml");
		var document = XDocument.Load(path);
		var source = File.ReadAllText(path);

		Assert.Contains(document.Descendants(), element =>
			element.Name.LocalName == "RtaimeComboBox" &&
			element.Attributes().Any(attribute =>
				attribute.Value.Contains("RecordingProfiles", StringComparison.Ordinal)));
		Assert.Contains("SelectedRecordingProfile", source, StringComparison.Ordinal);
		Assert.Contains("RecordingProfileDetail", source, StringComparison.Ordinal);
		Assert.DoesNotContain("<ComboBox", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Recording_profile_selection_is_driven_by_confirmed_snapshot_data()
	{
		var root = FindRepositoryRoot();
		var viewModel = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "OperatorViewModel.cs"));

		Assert.Contains("recording.Profiles", viewModel, StringComparison.Ordinal);
		Assert.Contains("recording.DefaultProfileId", viewModel, StringComparison.Ordinal);
		Assert.Contains("recording.ActiveProfileId", viewModel, StringComparison.Ordinal);
		Assert.Contains("SelectedRecordingProfile?.ProfileId", viewModel, StringComparison.Ordinal);
		Assert.Contains("profile.Available", viewModel, StringComparison.Ordinal);
	}

	[Fact]
	public void Operator_does_not_hard_code_recording_format_names_or_future_codecs()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml");
		var document = XDocument.Load(path);
		var advertisedText = string.Join(
			" ",
			document
				.Descendants()
				.SelectMany(element => element.Attributes())
				.Where(attribute => attribute.Name.LocalName is "Text" or "Content" or "Header" or "ToolTip")
				.Select(attribute => attribute.Value));

		foreach (var unsupported in new[] { "MOV", "MXF", "ProRes", "DNxHR", "AVC-Intra" })
			Assert.DoesNotContain(unsupported, advertisedText, StringComparison.OrdinalIgnoreCase);
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
