// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.AppHost;
using rtaime.Operator;

namespace rtaime.Tests.Operator;

public sealed class StartupLifecycleViewModelTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "rtaime-startup-tests", Guid.NewGuid().ToString("N"));
	private string EvidencePath => Path.Combine(_root, "apphost-lifecycle.json");
	private string DiagnosticPath => Path.Combine(_root, "controlhost-process.log");

	public StartupLifecycleViewModelTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void Required_degraded_stage_keeps_startup_visible_and_reports_degraded_state()
	{
		WriteEvidence(CreateStages(productionReadinessStatus: "DEGRADED"), ApplicationLifecycleStages.ProductionReadiness);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new SynchronizationContext());

		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.True(viewModel.HasStartupDegradation);
		Assert.Equal("DEGRADED", viewModel.StartupVisualState);
		Assert.Equal("STARTUP DEGRADED", viewModel.StartupPhaseLabel);
		Assert.Contains("Wait for authoritative AppHost/ControlHost recovery", viewModel.NextStep, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Failure_remains_latched_until_failed_stage_explicitly_recovers()
	{
		WriteEvidence(
			CreateStages(runtimeStatus: "FAILED", runtimeFailure: "Runtime qualification failed."),
			ApplicationLifecycleStages.RuntimeHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new SynchronizationContext());

		Assert.True(viewModel.HasStartupFailure);
		Assert.Equal("RuntimeHost", viewModel.ActiveStageName);
		Assert.Contains("Runtime qualification failed.", viewModel.Summary, StringComparison.Ordinal);

		WriteEvidence(CreateStages(runtimeStatus: "STARTING"), ApplicationLifecycleStages.RuntimeHost);
		await WaitUntilAsync(() =>
			viewModel.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.RuntimeHost).Status == "STARTING");

		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Contains("Original failure", viewModel.Summary, StringComparison.Ordinal);

		WriteEvidence(CreateStages(), activeStageId: null);
		await WaitUntilAsync(() => viewModel.HasCompletedInitialStartup);

		Assert.False(viewModel.HasStartupFailure);
		Assert.Equal("READY", viewModel.StartupVisualState);
	}

	[Fact]
	public void Complete_initial_qualification_latches_startup_complete()
	{
		WriteEvidence(CreateStages(), activeStageId: null);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new SynchronizationContext());

		Assert.True(viewModel.HasCompletedInitialStartup);
		Assert.False(viewModel.HasStartupFailure);
		Assert.Equal("PRODUCTION RUNTIME READY", viewModel.StartupPhaseLabel);
	}

	[Fact]
	public async Task Later_runtime_failure_does_not_reopen_initial_startup_failure()
	{
		WriteEvidence(CreateStages(), activeStageId: null);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new SynchronizationContext());
		Assert.True(viewModel.HasCompletedInitialStartup);

		WriteEvidence(
			CreateStages(runtimeStatus: "FAILED", runtimeFailure: "Runtime lost after startup."),
			ApplicationLifecycleStages.RuntimeHost);
		await WaitUntilAsync(() =>
			viewModel.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.RuntimeHost).Status == "FAILED");

		Assert.True(viewModel.HasCompletedInitialStartup);
		Assert.False(viewModel.HasStartupFailure);
		Assert.Equal("READY", viewModel.StartupVisualState);
	}

	[Fact]
	public void Optional_ai_failure_does_not_block_initial_production_qualification()
	{
		WriteEvidence(
			CreateStages(aiStatus: "FAILED", aiFailure: "Optional AI capability unavailable."),
			ApplicationLifecycleStages.AIHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new SynchronizationContext());

		Assert.True(viewModel.HasCompletedInitialStartup);
		Assert.False(viewModel.HasStartupFailure);
		Assert.Equal(
			"FAILED",
			viewModel.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.AIHost).Status);
	}

	[Fact]
	public void Published_diagnostic_path_is_available_to_operator_projection()
	{
		File.WriteAllText(DiagnosticPath, "control diagnostics");
		WriteEvidence(
			CreateStages(runtimeStatus: "FAILED", runtimeFailure: "Runtime qualification failed."),
			ApplicationLifecycleStages.RuntimeHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new SynchronizationContext());

		Assert.Equal(DiagnosticPath, viewModel.DiagnosticPath);
		Assert.True(viewModel.HasDiagnosticPath);
		Assert.True(viewModel.CanOpenDiagnostics);
		Assert.Equal("OPEN DIAGNOSTIC LOG", viewModel.DiagnosticActionLabel);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private void WriteEvidence(IReadOnlyList<TestStage> stages, string? activeStageId)
	{
		var payload = JsonSerializer.Serialize(
			new
			{
				copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
				schemaVersion = "1.0",
				observedAtUtc = DateTimeOffset.UtcNow,
				diagnosticPath = DiagnosticPath,
				activeStageId,
				stages = stages.Select(stage => new
				{
					id = stage.Id,
					displayName = stage.DisplayName,
					requirement = stage.Requirement,
					status = stage.Status,
					startedAtUtc = stage.Status == "PENDING" ? null : DateTimeOffset.UtcNow.AddSeconds(-1),
					completedAtUtc = stage.Status is "READY" or "FAILED" ? DateTimeOffset.UtcNow : null,
					statusText = stage.StatusText,
					failureReason = stage.FailureReason,
					canRetry = false
				})
			},
			new JsonSerializerOptions { WriteIndented = true });

		var temporaryPath = EvidencePath + ".tmp";
		File.WriteAllText(temporaryPath, payload + Environment.NewLine);
		File.Move(temporaryPath, EvidencePath, overwrite: true);
	}

	private static IReadOnlyList<TestStage> CreateStages(
		string runtimeStatus = "READY",
		string? runtimeFailure = null,
		string aiStatus = "READY",
		string? aiFailure = null,
		string productionReadinessStatus = "READY")
	{
		return
		[
			new(ApplicationLifecycleStages.ApplicationBootstrap, "Application Bootstrap", "Critical", "READY", "Bootstrap ready.", null),
			new(ApplicationLifecycleStages.Configuration, "Configuration", "Critical", "READY", "Configuration ready.", null),
			new(ApplicationLifecycleStages.OperatorInterface, "Operator Interface", "Critical", "READY", "Operator ready.", null),
			new(ApplicationLifecycleStages.ControlHost, "ControlHost", "RequiredForProduction", "READY", "ControlHost ready.", null),
			new(
				ApplicationLifecycleStages.RuntimeHost,
				"RuntimeHost",
				"RequiredForProduction",
				runtimeStatus,
				runtimeStatus == "STARTING" ? "Runtime recovery starting." : "Runtime state published.",
				runtimeFailure),
			new(
				ApplicationLifecycleStages.AIHost,
				"AIHost",
				"Optional",
				aiStatus,
				aiStatus == "FAILED" ? "Optional AI unavailable." : "AI state published.",
				aiFailure),
			new(
				ApplicationLifecycleStages.ProductionReadiness,
				"Production Readiness",
				"RequiredForProduction",
				productionReadinessStatus,
				productionReadinessStatus == "DEGRADED" ? "Production readiness is degraded." : "Production ready.",
				null)
		];
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
		while (!condition() && DateTimeOffset.UtcNow < deadline)
			await Task.Delay(20);

		Assert.True(condition());
	}

	private sealed record TestStage(
		string Id,
		string DisplayName,
		string Requirement,
		string Status,
		string StatusText,
		string? FailureReason);
}
