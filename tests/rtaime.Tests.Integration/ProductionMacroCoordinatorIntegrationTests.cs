// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Persistence;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class ProductionMacroCoordinatorIntegrationTests
{
	[Fact]
	public async Task Macro_executes_governed_actions_in_deterministic_order()
	{
		await using var fixture = await Fixture.CreateAsync();
		var executed = new List<ShowControlActionKind>();
		await using var coordinator = fixture.CreateCoordinator((action, _) =>
		{
			executed.Add(action.Kind);
			return ValueTask.FromResult<Failure?>(null);
		});
		var macro = fixture.Macro(
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.SetPreview, sourceId: fixture.SourceB.ToString()),
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut));

		await coordinator.SaveAsync(macro, 0);
		var completed = await coordinator.ExecuteAsync(macro.MacroId);

		Assert.Equal([ShowControlActionKind.SetPreview, ShowControlActionKind.Cut], executed);
		Assert.Equal(ProductionMacroExecutionState.Completed, completed.Execution.State);
		Assert.Equal(macro.Actions[^1].ActionId, completed.Execution.LastCompletedActionId);
	}

	[Fact]
	public async Task Macro_failure_stops_before_later_actions()
	{
		await using var fixture = await Fixture.CreateAsync();
		var executed = new List<ShowControlActionKind>();
		await using var coordinator = fixture.CreateCoordinator((action, _) =>
		{
			executed.Add(action.Kind);
			return ValueTask.FromResult<Failure?>(
				action.Kind == ShowControlActionKind.Cut
					? new Failure("production_macro.test.cut_failed", "CUT failed.")
					: null);
		});
		var macro = fixture.Macro(
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.SetPreview, sourceId: fixture.SourceB.ToString()),
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut),
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.SetPreview, sourceId: fixture.SourceA.ToString()));

		await coordinator.SaveAsync(macro, 0);
		var failed = await coordinator.ExecuteAsync(macro.MacroId);

		Assert.Equal([ShowControlActionKind.SetPreview, ShowControlActionKind.Cut], executed);
		Assert.Equal(ProductionMacroExecutionState.Failed, failed.Execution.State);
		Assert.Equal("production_macro.test.cut_failed", failed.Execution.Failure?.Code);
	}

	[Fact]
	public async Task Macro_wait_uses_runtime_frame_target_before_continuing()
	{
		await using var fixture = await Fixture.CreateAsync();
		ulong frame = 10;
		var executed = new List<ShowControlActionKind>();
		await using var coordinator = fixture.CreateCoordinator(
			(action, _) =>
			{
				executed.Add(action.Kind);
				return ValueTask.FromResult<Failure?>(null);
			},
			_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-macro", frame, TimeSpan.FromMilliseconds(5))));
		var macro = fixture.Macro(
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 5),
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut));

		await coordinator.SaveAsync(macro, 0);
		var waiting = await coordinator.ExecuteAsync(macro.MacroId);
		Assert.Equal(ProductionMacroExecutionState.Waiting, waiting.Execution.State);
		Assert.Equal(15UL, waiting.Execution.WaitTargetFrameSequence);
		Assert.Empty(executed);

		frame = 15;
		var completed = await WaitUntilAsync(coordinator, snapshot => snapshot.Execution.State == ProductionMacroExecutionState.Completed);

		Assert.Equal([ShowControlActionKind.Cut], executed);
		Assert.Equal(15UL, frame);
		Assert.Equal(macro.Actions[^1].ActionId, completed.Execution.LastCompletedActionId);
	}

	[Fact]
	public async Task Cancelled_wait_cannot_fire_later_actions()
	{
		await using var fixture = await Fixture.CreateAsync();
		ulong frame = 20;
		var executed = new List<ShowControlActionKind>();
		await using var coordinator = fixture.CreateCoordinator(
			(action, _) =>
			{
				executed.Add(action.Kind);
				return ValueTask.FromResult<Failure?>(null);
			},
			_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-macro", frame, TimeSpan.FromMilliseconds(5))));
		var macro = fixture.Macro(
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 25),
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut));

		await coordinator.SaveAsync(macro, 0);
		await coordinator.ExecuteAsync(macro.MacroId);
		var cancelled = await coordinator.CancelAsync();
		frame = 100;
		await Task.Delay(50);
		var stable = await coordinator.GetSnapshotAsync();

		Assert.Equal(ProductionMacroExecutionState.Cancelled, cancelled.Execution.State);
		Assert.Equal(ProductionMacroExecutionState.Cancelled, stable.Execution.State);
		Assert.Empty(executed);
	}

	[Fact]
	public async Task Restart_during_wait_requires_explicit_recovery_without_replay()
	{
		await using var fixture = await Fixture.CreateAsync();
		var macro = fixture.Macro(
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 100),
			new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut));

		await using (var first = fixture.CreateCoordinator(
			(_, _) => ValueTask.FromResult<Failure?>(null),
			_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-a", 10, TimeSpan.FromMilliseconds(10)))))
		{
			await first.SaveAsync(macro, 0);
			var waiting = await first.ExecuteAsync(macro.MacroId);
			Assert.Equal(ProductionMacroExecutionState.Waiting, waiting.Execution.State);
		}

		await using var restored = fixture.CreateCoordinator(
			(_, _) => ValueTask.FromResult<Failure?>(null),
			_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-b", 10, TimeSpan.FromMilliseconds(10))));
		var snapshot = await restored.GetSnapshotAsync();

		Assert.Equal(ProductionMacroExecutionState.RecoveryRequired, snapshot.Execution.State);
		Assert.True(snapshot.Execution.RequiresAcknowledgement);
		Assert.Equal("show_control.recovery.uncertain_action", snapshot.Execution.Failure?.Code);
	}

	private static async Task<ProductionMacroWorkspaceSnapshot> WaitUntilAsync(
		ProductionMacroCoordinator coordinator,
		Func<ProductionMacroWorkspaceSnapshot, bool> predicate)
	{
		var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var snapshot = await coordinator.GetSnapshotAsync();
			if (predicate(snapshot))
				return snapshot;
			await Task.Delay(10);
		}
		throw new TimeoutException("Production Macro state did not reach the expected condition.");
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root;
		private readonly BoundedProductionJournal _journal;
		private readonly SqliteManagementStore _management;
		private readonly ShowProjectPersistenceStore _showProjectStore;

		private Fixture(
			string root,
			ProductionSourceId sourceA,
			ProductionSourceId sourceB,
			ControlHostService control,
			BoundedProductionJournal journal,
			SqliteManagementStore management,
			ShowProjectPersistenceStore showProjectStore)
		{
			_root = root;
			SourceA = sourceA;
			SourceB = sourceB;
			Control = control;
			_journal = journal;
			_management = management;
			_showProjectStore = showProjectStore;
		}

		public ProductionSourceId SourceA { get; }
		public ProductionSourceId SourceB { get; }
		public ControlHostService Control { get; }

		public static async Task<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-production-macro-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var sourceA = ProductionSourceId.New();
			var sourceB = ProductionSourceId.New();
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				ProductionId.New(),
				"Production Macro Integration",
				[
					new ProductionSourceSpecification(sourceA, "Camera A"),
					new ProductionSourceSpecification(sourceB, "Camera B")
				],
				new ProductionRoutingState(sourceA, sourceA));
			var journal = new BoundedProductionJournal(256);
			var control = new ControlHostService(specification, [], journal);
			var initialized = ControlDomainEngine.Initialize(specification);
			Assert.True(initialized.Succeeded);
			control.RestoreAuthoritativeState(initialized.State!.Authoritative);
			var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			var showProjectStore = new ShowProjectPersistenceStore(management);
			await showProjectStore.LoadOrCreateAsync(specification);
			return new Fixture(root, sourceA, sourceB, control, journal, management, showProjectStore);
		}

		public ProductionMacroDefinition Macro(params ProductionMacroAction[] actions) =>
			new(
				ProductionMacroContractVersion.Current,
				ProductionMacroId.New(),
				"Integration Macro",
				actions);

		public ProductionMacroCoordinator CreateCoordinator(
			ShowControlActionExecutor executor,
			ShowControlFrameObserver? observer = null) =>
			new(
				() => Control,
				_showProjectStore,
				executor,
				observer ?? (_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-a", 1, TimeSpan.FromMilliseconds(20)))),
				null,
				() => { });

		public async ValueTask DisposeAsync()
		{
			await _management.DisposeAsync();
			await _journal.DisposeAsync();
			try
			{
				if (Directory.Exists(_root))
					Directory.Delete(_root, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}
}
