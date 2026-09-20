// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class HealthCenterArchitectureTests
{
	[Fact]
	public void Health_center_releases_all_event_subscriptions_on_dispose()
	{
		var repo = RepositorySnapshot.Load();
		var provider = Read(repo, "src/Hosts/rtaime.Operator/OperatorHealthSnapshotProvider.cs");
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/HealthCenterViewModel.cs");

		AssertSubscriptionPair(provider, "_operator.PropertyChanged += OperatorPropertyChanged;", "_operator.PropertyChanged -= OperatorPropertyChanged;");
		AssertSubscriptionPair(provider, "_mediaDeck.PropertyChanged += MediaDeckPropertyChanged;", "_mediaDeck.PropertyChanged -= MediaDeckPropertyChanged;");
		AssertSubscriptionPair(provider, "_monitoring.PropertyChanged += MonitoringPropertyChanged;", "_monitoring.PropertyChanged -= MonitoringPropertyChanged;");
		AssertSubscriptionPair(provider, "_output.PropertyChanged += OutputPropertyChanged;", "_output.PropertyChanged -= OutputPropertyChanged;");
		AssertSubscriptionPair(provider, "_compositing.PropertyChanged += CompositingPropertyChanged;", "_compositing.PropertyChanged -= CompositingPropertyChanged;");
		AssertSubscriptionPair(viewModel, "_provider.Changed += ProviderChanged;", "_provider.Changed -= ProviderChanged;");
		AssertSubscriptionPair(viewModel, "_readiness.Changed += ReadinessChanged;", "_readiness.Changed -= ReadinessChanged;");
	}

	[Fact]
	public void Health_center_refresh_sources_remain_bounded_and_event_driven()
	{
		var repo = RepositorySnapshot.Load();
		var provider = Read(repo, "src/Hosts/rtaime.Operator/OperatorHealthSnapshotProvider.cs");
		var compositing = Read(repo, "src/Hosts/rtaime.Operator/CompositingGraphViewModel.cs");

		Assert.Contains("nameof(OperatorViewModel.HealthObserved)", provider, StringComparison.Ordinal);
		Assert.Contains("nameof(OperatorViewModel.GlobalReadinessState)", provider, StringComparison.Ordinal);
		Assert.Contains("nameof(CompositingGraphViewModel.HealthRevision)", provider, StringComparison.Ordinal);
		Assert.Contains("PublishHealthRevision(graph.Nodes);", compositing, StringComparison.Ordinal);
		Assert.DoesNotContain("PeriodicTimer", provider, StringComparison.Ordinal);
		Assert.DoesNotContain("DispatcherTimer", provider, StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Delay", provider, StringComparison.Ordinal);
		Assert.DoesNotContain("ProgramImage", provider, StringComparison.Ordinal);
		Assert.DoesNotContain("PreviewImage", provider, StringComparison.Ordinal);
	}

	[Fact]
	public void Health_contract_does_not_become_a_second_readiness_authority()
	{
		var repo = RepositorySnapshot.Load();
		var contract = Read(repo, "src/Client/rtaime.Client/HealthSnapshots.cs");
		var provider = Read(repo, "src/Hosts/rtaime.Operator/OperatorHealthSnapshotProvider.cs");
		var viewModel = Read(repo, "src/Hosts/rtaime.Operator/HealthCenterViewModel.cs");

		Assert.Contains("public enum SubsystemHealthState", contract, StringComparison.Ordinal);
		Assert.Contains("public interface IHealthSnapshotProvider", contract, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeReadinessState", contract, StringComparison.Ordinal);
		Assert.DoesNotContain("IsProductionReady", provider, StringComparison.Ordinal);
		Assert.Contains("IRuntimeReadinessService", viewModel, StringComparison.Ordinal);
		Assert.Contains("ProductionReadiness => _readiness.Current.IsProductionReady", viewModel, StringComparison.Ordinal);
	}

	private static string Read(RepositorySnapshot repo, string relativePath) =>
		File.ReadAllText(Path.Combine(repo.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

	private static void AssertSubscriptionPair(string source, string subscribe, string unsubscribe)
	{
		Assert.Contains(subscribe, source, StringComparison.Ordinal);
		Assert.Contains(unsubscribe, source, StringComparison.Ordinal);
	}
}
