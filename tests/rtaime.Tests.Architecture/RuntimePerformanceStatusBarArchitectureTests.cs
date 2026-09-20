// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class RuntimePerformanceStatusBarArchitectureTests
{
	[Fact]
	public void Status_bar_releases_operator_subscription_on_dispose()
	{
		var repo = RepositorySnapshot.Load();
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/RuntimePerformanceStatusBarViewModel.cs");
		var window = Read(repo, "src/Hosts/rtaime.Operator/MainWindow.xaml.cs");

		Assert.Contains("_operator.PropertyChanged += OperatorPropertyChanged;", viewModel, StringComparison.Ordinal);
		Assert.Contains("_operator.PropertyChanged -= OperatorPropertyChanged;", viewModel, StringComparison.Ordinal);
		Assert.Contains("RuntimePerformanceStatus = new RuntimePerformanceStatusBarViewModel", window, StringComparison.Ordinal);
		Assert.Contains("RuntimePerformanceStatus.Dispose();", window, StringComparison.Ordinal);
	}

	[Fact]
	public void Status_bar_uses_completed_runtime_observations_without_an_independent_poller()
	{
		var repo = RepositorySnapshot.Load();
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/RuntimePerformanceStatusBarViewModel.cs");

		Assert.Contains("nameof(OperatorViewModel.HealthObserved)", viewModel, StringComparison.Ordinal);
		Assert.Contains("nameof(OperatorViewModel.IsConnected)", viewModel, StringComparison.Ordinal);
		Assert.Contains("nameof(OperatorViewModel.IsStale)", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("PeriodicTimer", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("DispatcherTimer", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Delay", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Run", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("PerformanceCounter", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("ManagementObjectSearcher", viewModel, StringComparison.Ordinal);
	}

	[Fact]
	public void Program_cadence_observation_remains_constant_space_and_scheduler_driven()
	{
		var repo = RepositorySnapshot.Load();
		var counter = Read(repo, "src/Hosts/rtaime.RuntimeHost/RuntimeFrameDropCounter.cs");
		var process = Read(repo, "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs");

		Assert.Contains("OutputFramesPerSecond", counter, StringComparison.Ordinal);
		Assert.Contains("OutputRateSmoothingFactor", counter, StringComparison.Ordinal);
		Assert.DoesNotContain("List<", counter, StringComparison.Ordinal);
		Assert.DoesNotContain("Queue<", counter, StringComparison.Ordinal);
		Assert.DoesNotContain("Dictionary<", counter, StringComparison.Ordinal);
		Assert.DoesNotContain("Stopwatch", counter, StringComparison.Ordinal);
		Assert.DoesNotContain("PeriodicTimer", counter, StringComparison.Ordinal);
		Assert.Contains("_frameDropCounter.OutputFramesPerSecond", process, StringComparison.Ordinal);
	}

	[Fact]
	public void Production_shell_keeps_metric_slots_permanent_and_out_of_titlebar()
	{
		var repo = RepositorySnapshot.Load();
		var window = Read(repo, "src/Hosts/rtaime.Operator/MainWindow.xaml");
		var statusBar = Read(repo, "src/Hosts/rtaime.Operator/RuntimePerformanceStatusBarControl.xaml");
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/RuntimePerformanceStatusBarViewModel.cs");

		Assert.Contains("<local:RuntimePerformanceStatusBarControl", window, StringComparison.Ordinal);
		Assert.Contains("Grid.Row=\"2\"", window, StringComparison.Ordinal);
		Assert.Contains("ItemsSource=\"{Binding Slots}\"", statusBar, StringComparison.Ordinal);
		foreach (var label in new[] { "CPU", "GPU", "RAM", "VRAM", "FRAME", "FPS", "DROPPED" })
			Assert.Contains($"\"{label}\"", viewModel, StringComparison.Ordinal);
	}

	private static string Read(RepositorySnapshot repo, string relativePath) =>
		File.ReadAllText(Path.Combine(repo.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
