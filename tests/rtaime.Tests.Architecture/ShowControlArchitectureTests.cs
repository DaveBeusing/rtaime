// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class ShowControlArchitectureTests
{
	[Fact]
	public void Show_control_coordinator_does_not_bypass_control_or_provider_boundaries()
	{
		var repo = RepositorySnapshot.Load();
		var coordinator = Read(repo, "src/Hosts/rtaime.ControlHost/ShowControlCoordinator.cs");
		var ipc = Read(repo, "src/Hosts/rtaime.ControlHost/ControlHostIpcServer.cs");

		Assert.Contains("ShowControlActionExecutor", coordinator, StringComparison.Ordinal);
		Assert.Contains("_actionExecutor", coordinator, StringComparison.Ordinal);
		Assert.DoesNotContain("V1RuntimeHostService", coordinator, StringComparison.Ordinal);
		Assert.DoesNotContain("ApplyExecution(", coordinator, StringComparison.Ordinal);
		Assert.DoesNotContain("Provider.", coordinator, StringComparison.Ordinal);

		Assert.Contains("ExecuteShowControlMutationAsync", ipc, StringComparison.Ordinal);
		Assert.Contains("_mediaDeck.ApplyTransportAsync", ipc, StringComparison.Ordinal);
		Assert.Contains("SetCompositingLayerStateAsync", ipc, StringComparison.Ordinal);
		Assert.Contains("StartRecordingAsync", ipc, StringComparison.Ordinal);
		Assert.Contains("StopRecordingAsync", ipc, StringComparison.Ordinal);
	}

	[Fact]
	public void Operator_show_control_keeps_selection_separate_from_go()
	{
		var repo = RepositorySnapshot.Load();
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/ShowControlViewModel.cs");
		var surface = Read(repo, "src/Hosts/rtaime.Operator/ShowControlCueStackControl.xaml");

		Assert.Contains("public ShowControlCueEditorItem? SelectedCue", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("GoAsync();", ExtractSelectionSetter(viewModel), StringComparison.Ordinal);
		Assert.Contains("SelectedItem="{Binding SelectedCue, Mode=TwoWay}"", surface, StringComparison.Ordinal);
		Assert.Contains("Command="{Binding GoCommand}"", surface, StringComparison.Ordinal);
	}

	[Fact]
	public void Authoritative_wait_progression_is_not_owned_by_operator_timers()
	{
		var repo = RepositorySnapshot.Load();
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/ShowControlViewModel.cs");
		var surfaceCode = Read(repo, "src/Hosts/rtaime.Operator/ShowControlCueStackControl.xaml.cs");

		Assert.DoesNotContain("DispatcherTimer", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("PeriodicTimer", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Delay", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("DispatcherTimer", surfaceCode, StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Delay", surfaceCode, StringComparison.Ordinal);
	}

	private static string ExtractSelectionSetter(string source)
	{
		var start = source.IndexOf("public ShowControlCueEditorItem? SelectedCue", StringComparison.Ordinal);
		Assert.True(start >= 0);
		var end = source.IndexOf("public ShowControlActionEditorItem? SelectedAction", start, StringComparison.Ordinal);
		Assert.True(end > start);
		return source[start..end];
	}

	private static string Read(RepositorySnapshot repo, string relativePath) =>
		File.ReadAllText(Path.Combine(repo.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
