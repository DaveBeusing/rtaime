// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Reflection;
using System.Text.Json;
using rtaime.AppHost;
using rtaime.Operator;
using Xunit;

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
	public void ControlHost_failure_names_the_affected_stage_and_blocks_initial_completion()
	{
		WriteEvidence(
			CreateStages(controlStatus: "FAILED", controlFailure: "ControlHost failed to start."),
			ApplicationLifecycleStages.ControlHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Equal("ControlHost", viewModel.ActiveStageName);
		Assert.Contains("ControlHost failed to start.", viewModel.Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void Required_ai_failure_blocks_initial_completion()
	{
		WriteEvidence(
			CreateStages(
				aiStatus: "FAILED",
				aiFailure: "Required AIHost failed.",
				aiRequirement: "RequiredForProduction"),
			ApplicationLifecycleStages.AIHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Equal("AIHost", viewModel.ActiveStageName);
		Assert.Contains("Required AIHost failed.", viewModel.Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void Production_readiness_timeout_is_presented_as_startup_failure()
	{
		WriteEvidence(
			CreateStages(
				productionReadinessStatus: "FAILED",
				productionReadinessFailure: "Production readiness timed out."),
			ApplicationLifecycleStages.ProductionReadiness);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Equal("Production Readiness", viewModel.ActiveStageName);
		Assert.Contains("Production readiness timed out.", viewModel.Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void Required_degraded_stage_keeps_startup_visible_and_reports_degraded_state()
	{
		WriteEvidence(CreateStages(productionReadinessStatus: "DEGRADED"), ApplicationLifecycleStages.ProductionReadiness);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.True(viewModel.HasStartupDegradation);
		Assert.Equal("DEGRADED", viewModel.StartupVisualState);
		Assert.Equal("STARTUP DEGRADED", viewModel.StartupPhaseLabel);
		Assert.Contains("Wait for authoritative AppHost/ControlHost recovery", viewModel.NextStep, StringComparison.Ordinal);
	}

	[Fact]
	public void Failure_remains_latched_until_failed_stage_explicitly_recovers()
	{
		WriteEvidence(
			CreateStages(runtimeStatus: "FAILED", runtimeFailure: "Runtime qualification failed."),
			ApplicationLifecycleStages.RuntimeHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.True(viewModel.HasStartupFailure);
		Assert.Equal("RuntimeHost", viewModel.ActiveStageName);
		Assert.Contains("Runtime qualification failed.", viewModel.Summary, StringComparison.Ordinal);

		WriteEvidence(CreateStages(runtimeStatus: "STARTING"), ApplicationLifecycleStages.RuntimeHost);
		Refresh(viewModel);
		Assert.Equal(
			"STARTING",
			viewModel.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.RuntimeHost).Status);

		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Contains("Original failure", viewModel.Summary, StringComparison.Ordinal);

		WriteEvidence(CreateStages(), activeStageId: null);
		Refresh(viewModel);
		Assert.True(viewModel.HasCompletedInitialStartup);

		Assert.False(viewModel.HasStartupFailure);
		Assert.Equal("READY", viewModel.StartupVisualState);
	}

	[Fact]
	public void First_startup_failure_stays_latched_when_another_stage_fails_during_recovery()
	{
		WriteEvidence(
			CreateStages(runtimeStatus: "FAILED", runtimeFailure: "Runtime qualification failed."),
			ApplicationLifecycleStages.RuntimeHost);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		WriteEvidence(
			CreateStages(
				runtimeStatus: "STARTING",
				productionReadinessStatus: "FAILED",
				productionReadinessFailure: "Production readiness failed."),
			ApplicationLifecycleStages.ProductionReadiness);
		Refresh(viewModel);
		Assert.Equal(
			"FAILED",
			viewModel.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.ProductionReadiness).Status);

		Assert.True(viewModel.HasStartupFailure);
		Assert.Equal("RuntimeHost", viewModel.ActiveStageName);
		Assert.Contains("Runtime qualification failed.", viewModel.Summary, StringComparison.Ordinal);
		Assert.DoesNotContain("Production readiness failed.", viewModel.Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void Complete_initial_qualification_latches_startup_complete()
	{
		WriteEvidence(CreateStages(), activeStageId: null);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.True(viewModel.HasCompletedInitialStartup);
		Assert.False(viewModel.HasStartupFailure);
		Assert.Equal("PRODUCTION RUNTIME READY", viewModel.StartupPhaseLabel);
	}

	[Fact]
	public void Later_runtime_failure_does_not_reopen_initial_startup_failure()
	{
		WriteEvidence(CreateStages(), activeStageId: null);
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());
		Assert.True(viewModel.HasCompletedInitialStartup);

		WriteEvidence(
			CreateStages(runtimeStatus: "FAILED", runtimeFailure: "Runtime lost after startup."),
			ApplicationLifecycleStages.RuntimeHost);
		Refresh(viewModel);
		Assert.Equal(
			"FAILED",
			viewModel.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.RuntimeHost).Status);

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
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

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
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());

		Assert.Equal(DiagnosticPath, viewModel.DiagnosticPath);
		Assert.True(viewModel.HasDiagnosticPath);
		Assert.True(viewModel.CanOpenDiagnostics);
		Assert.Equal("OPEN DIAGNOSTIC LOG", viewModel.DiagnosticActionLabel);
	}

	[Fact]
	public void Aggregate_timeout_shows_blocking_phase_and_existing_diagnostic_actions_without_retry()
	{
		WriteEvidence(CreateStages(controlStatus: "DEGRADED", runtimeStatus: "PENDING",
			productionReadinessStatus: "FAILED", productionReadinessFailure: "Startup deadline expired."),
			ApplicationLifecycleStages.ProductionReadiness);
		using var original = JsonDocument.Parse(File.ReadAllText(EvidencePath));
		var fields = original.RootElement.EnumerateObject().ToDictionary(property => property.Name,
			property => property.Value.Clone());
		var failure = JsonSerializer.SerializeToElement(new
		{
			aggregateDeadlineExpired = true,
			hosts = new[] { new { host = "ControlHost", lastProgressPhase = "restore-audio", phaseOutcome = "failed" } },
			unmetCriteria = new[] { new { host = "ControlHost", code = "control.binding.qualified" } }
		});
		fields["readinessFailure"] = failure;
		File.WriteAllText(EvidencePath, JsonSerializer.Serialize(fields));
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());
		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Contains("ControlHost: control.binding.qualified", viewModel.Summary);
		Assert.Contains("restore-audio / failed", viewModel.Summary);
		Assert.Contains(EvidencePath, viewModel.NextStep);
		Assert.Contains("restart through the supported application lifecycle", viewModel.NextStep);
		Assert.All(viewModel.Stages, stage => Assert.False(stage.CanRetry));
		Assert.Equal("PENDING", viewModel.Stages.Single(stage => stage.Id == "runtime-host").Status);
	}

	[Fact]
	public void Evidence_failure_with_ready_hosts_shows_actual_cause_instead_of_empty_blocking_criteria()
	{
		var reason = "Atomic startup evidence publication failed for '" + EvidencePath + "'.";
		WriteEvidence(CreateStages(productionReadinessStatus: "FAILED", productionReadinessFailure: reason),
			ApplicationLifecycleStages.ProductionReadiness);
		using var original = JsonDocument.Parse(File.ReadAllText(EvidencePath));
		var fields = original.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
		fields["readinessFailure"] = JsonSerializer.SerializeToElement(new
		{
			reasonCode = "startup.evidence.publish.failed",
			originalFailure = reason,
			aggregateDeadlineExpired = false,
			hosts = new[] { new { host = "ControlHost", lastProgressPhase = "qualified-ready", phaseOutcome = "completed" } },
			unmetCriteria = Array.Empty<object>()
		});
		File.WriteAllText(EvidencePath, JsonSerializer.Serialize(fields));
		using var viewModel = new StartupLifecycleViewModel(EvidencePath, new PassiveSynchronizationContext());
		Assert.True(viewModel.HasStartupFailure);
		Assert.False(viewModel.HasCompletedInitialStartup);
		Assert.Contains(reason, viewModel.Summary);
		Assert.DoesNotContain("Blocking criteria:", viewModel.Summary);
		Assert.Contains("qualified-ready / completed", viewModel.Summary);
		Assert.Equal("READY", viewModel.Stages.Single(stage => stage.Id == "control-host").Status);
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
					startedAtUtc = stage.Status == "PENDING" ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddSeconds(-1),
					completedAtUtc = stage.Status is "READY" or "FAILED" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
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
		string controlStatus = "READY",
		string? controlFailure = null,
		string runtimeStatus = "READY",
		string? runtimeFailure = null,
		string aiStatus = "READY",
		string? aiFailure = null,
		string aiRequirement = "Optional",
		string productionReadinessStatus = "READY",
		string? productionReadinessFailure = null)
	{
		return
		[
			new(ApplicationLifecycleStages.ApplicationBootstrap, "Application Bootstrap", "Critical", "READY", "Bootstrap ready.", null),
			new(ApplicationLifecycleStages.Configuration, "Configuration", "Critical", "READY", "Configuration ready.", null),
			new(ApplicationLifecycleStages.OperatorInterface, "Operator Interface", "Critical", "READY", "Operator ready.", null),
			new(
				ApplicationLifecycleStages.ControlHost,
				"ControlHost",
				"RequiredForProduction",
				controlStatus,
				controlStatus == "FAILED" ? "ControlHost startup failed." : "ControlHost ready.",
				controlFailure),
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
				aiRequirement,
				aiStatus,
				aiStatus == "FAILED" ? "Optional AI unavailable." : "AI state published.",
				aiFailure),
			new(
				ApplicationLifecycleStages.ProductionReadiness,
				"Production Readiness",
				"RequiredForProduction",
				productionReadinessStatus,
				productionReadinessStatus == "DEGRADED" ? "Production readiness is degraded." : "Production ready.",
				productionReadinessFailure)
		];
	}

	private static void Refresh(StartupLifecycleViewModel viewModel)
	{
		var method = typeof(StartupLifecycleViewModel).GetMethod(
			"TryRefresh",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method.Invoke(viewModel, null);
	}

	private sealed class PassiveSynchronizationContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state)
		{
			// File watcher delivery is deliberately suppressed so tests control evidence refresh deterministically.
		}
	}

	private sealed record TestStage(
		string Id,
		string DisplayName,
		string Requirement,
		string Status,
		string StatusText,
		string? FailureReason);
}
