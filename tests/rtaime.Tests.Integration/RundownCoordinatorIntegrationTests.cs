// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class RundownCoordinatorIntegrationTests
{
	[Fact]
	public async Task Restart_during_in_flight_hold_requires_acknowledgement_without_replay()
	{
		await using var fixture = await Fixture.CreateAsync();
		var item = new RundownHoldItem(
			new RundownItemId(Identity.Parse("83000000-0000-0000-0000-000000000001")),
			"Production hold",
			100);
		var rundown = new RundownDefinition(
			RundownContractVersion.Current,
			new RundownId(Identity.Parse("83000000-0000-0000-0000-000000000002")),
			"Recovery rundown",
			[item]);

		await using (var show = fixture.CreateShowControl())
		await using (var first = fixture.CreateRundown(show))
		{
			var saved = await first.SaveAsync(rundown, expectedStorageVersion: 0);
			Assert.Equal(1UL, saved.StorageVersion);

			var prepared = await first.PrepareAsync(item.ItemId);
			Assert.Equal(RundownExecutionState.Prepared, prepared.Execution.State);

			var executing = await first.GoAsync();
			Assert.Equal(RundownExecutionState.Executing, executing.Execution.State);
			Assert.Equal(item.ItemId, executing.Execution.CurrentItemId);
		}

		await using var restoredShow = fixture.CreateShowControl();
		await using var restored = fixture.CreateRundown(restoredShow);
		var recovery = await restored.GetSnapshotAsync();

		Assert.Equal(RundownExecutionState.RecoveryRequired, recovery.Execution.State);
		Assert.True(recovery.Execution.RequiresAcknowledgement);
		Assert.Equal(item.ItemId, recovery.Execution.CurrentItemId);
		Assert.Equal("show_control.recovery.uncertain_action", recovery.Execution.Failure?.Code);

		var cancelled = await restored.AcknowledgeRecoveryAsync(resume: false);

		Assert.Equal(RundownExecutionState.Held, cancelled.Execution.State);
		Assert.False(cancelled.Execution.RequiresAcknowledgement);
		Assert.Null(cancelled.Execution.PreparedItemId);
	}

	[Fact]
	public async Task Manual_navigation_invalidates_previous_prepared_identity_without_executing_it()
	{
		await using var fixture = await Fixture.CreateAsync();
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA);
		var secondItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene B",
			fixture.SceneB);
		var rundown = new RundownDefinition(
			RundownContractVersion.Current,
			RundownId.New(),
			"Navigation rundown",
			[firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, expectedStorageVersion: 0);

		var firstPrepared = await coordinator.PrepareAsync(firstItem.ItemId);
		var firstRevision = firstPrepared.Execution.Revision;
		Assert.Equal(firstItem.ItemId, firstPrepared.Execution.PreparedItemId);

		var nextPrepared = await coordinator.NextAsync();

		Assert.Equal(RundownExecutionState.Prepared, nextPrepared.Execution.State);
		Assert.Equal(secondItem.ItemId, nextPrepared.Execution.PreparedItemId);
		Assert.Null(nextPrepared.Execution.CurrentItemId);
		Assert.True(nextPrepared.Execution.Revision > firstRevision);
		Assert.Empty(fixture.ExecutedActions);
	}

	[Fact]
	public async Task Prepare_next_arms_the_bounded_next_item_without_program_mutation()
	{
		await using var fixture = await Fixture.CreateAsync();
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.PrepareNext);
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Prepare-next", [firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, 0);
		await coordinator.PrepareAsync(firstItem.ItemId);
		await coordinator.GoAsync();

		var prepared = await WaitUntilAsync(
			coordinator,
			snapshot => snapshot.Execution.State == RundownExecutionState.Prepared &&
				snapshot.Execution.PreparedItemId == secondItem.ItemId);

		Assert.Equal(firstItem.ItemId, prepared.Execution.CurrentItemId);
		Assert.Equal(secondItem.ItemId, prepared.Execution.PreparedItemId);
		Assert.Single(fixture.ExecutedActions);
		Assert.Equal(ShowControlActionKind.ActivateScene, fixture.ExecutedActions[0]);
	}

	[Fact]
	public async Task Auto_go_next_runs_only_after_confirmed_current_completion()
	{
		await using var fixture = await Fixture.CreateAsync();
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNext);
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Auto GO", [firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, 0);
		await coordinator.PrepareAsync(firstItem.ItemId);
		await coordinator.GoAsync();

		var completedFollow = await WaitUntilAsync(
			coordinator,
			snapshot => snapshot.Execution.State == RundownExecutionState.Held &&
				snapshot.Execution.CurrentItemId == secondItem.ItemId);

		Assert.False(completedFollow.Execution.AutoAdvanceArmed);
		Assert.Equal(2, fixture.ExecutedActions.Count);
		Assert.All(fixture.ExecutedActions, action => Assert.Equal(ShowControlActionKind.ActivateScene, action));
	}

	[Fact]
	public async Task Delayed_auto_go_uses_runtime_frame_target_and_exposes_confirmed_countdown()
	{
		await using var fixture = await Fixture.CreateAsync();
		fixture.FrameSequence = 10;
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNextAfterFrames(5));
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Delayed GO", [firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, 0);
		await coordinator.PrepareAsync(firstItem.ItemId);
		await coordinator.GoAsync();

		var waiting = await WaitUntilAsync(
			coordinator,
			snapshot => snapshot.Execution.FollowTargetFrameSequence == 15);
		Assert.Equal("runtime-rundown", waiting.Execution.RuntimeHostInstanceId);
		Assert.Equal(10UL, waiting.Execution.FollowStartFrameSequence);
		Assert.Equal(5U, waiting.Execution.RemainingFollowFrames);
		Assert.Equal(secondItem.ItemId, waiting.Execution.PendingNextItemId);

		fixture.FrameSequence = 14;
		var countdown = await coordinator.GetSnapshotAsync();
		Assert.Equal(1U, countdown.Execution.RemainingFollowFrames);

		fixture.FrameSequence = 15;
		var followed = await WaitUntilAsync(
			coordinator,
			snapshot => snapshot.Execution.State == RundownExecutionState.Held &&
				snapshot.Execution.CurrentItemId == secondItem.ItemId);

		Assert.Null(followed.Execution.FollowTargetFrameSequence);
		Assert.Equal(2, fixture.ExecutedActions.Count);
	}

	[Fact]
	public async Task Hold_invalidates_delayed_follow_and_stale_frame_completion_cannot_fire()
	{
		await using var fixture = await Fixture.CreateAsync();
		fixture.FrameSequence = 20;
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNextAfterFrames(10));
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Manual override", [firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, 0);
		await coordinator.PrepareAsync(firstItem.ItemId);
		await coordinator.GoAsync();
		await WaitUntilAsync(coordinator, snapshot => snapshot.Execution.FollowTargetFrameSequence == 30);

		var held = await coordinator.HoldAsync();
		Assert.Equal(RundownExecutionState.Held, held.Execution.State);
		Assert.False(held.Execution.AutoAdvanceArmed);
		Assert.Null(held.Execution.PendingNextItemId);

		fixture.FrameSequence = 100;
		await Task.Delay(50);
		var stable = await coordinator.GetSnapshotAsync();
		Assert.Equal(RundownExecutionState.Held, stable.Execution.State);
		Assert.Equal(firstItem.ItemId, stable.Execution.CurrentItemId);
		Assert.Single(fixture.ExecutedActions);
	}

	[Fact]
	public async Task Runtime_identity_change_during_delayed_follow_requires_recovery()
	{
		await using var fixture = await Fixture.CreateAsync();
		fixture.FrameSequence = 40;
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNextAfterFrames(10));
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Runtime restart", [firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, 0);
		await coordinator.PrepareAsync(firstItem.ItemId);
		await coordinator.GoAsync();
		await WaitUntilAsync(coordinator, snapshot => snapshot.Execution.FollowTargetFrameSequence == 50);

		fixture.RuntimeHostInstanceId = "runtime-restarted";
		var recovery = await WaitUntilAsync(
			coordinator,
			snapshot => snapshot.Execution.State == RundownExecutionState.RecoveryRequired);

		Assert.True(recovery.Execution.RequiresAcknowledgement);
		Assert.Equal("show_control.wait.runtime_restarted", recovery.Execution.Failure?.Code);
		Assert.Equal(firstItem.ItemId, recovery.Execution.CurrentItemId);
		Assert.Equal(secondItem.ItemId, recovery.Execution.PendingNextItemId);
	}

	[Fact]
	public async Task Repeat_current_and_repeat_rundown_are_finite()
	{
		await using var fixture = await Fixture.CreateAsync();
		var repeatedItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			repeat: new RundownRepeatPolicy(RundownRepeatMode.RepeatItem, 2),
			followAction: RundownFollowAction.AutoGoNext);
		var finalItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var firstRundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Repeat current", [repeatedItem, finalItem]);

		await using (var show = fixture.CreateShowControl())
		await using (var coordinator = fixture.CreateRundown(show))
		{
			await coordinator.SaveAsync(firstRundown, 0);
			await coordinator.PrepareAsync(repeatedItem.ItemId);
			await coordinator.GoAsync();
			await WaitUntilAsync(
				coordinator,
				snapshot => snapshot.Execution.State == RundownExecutionState.Held &&
					snapshot.Execution.CurrentItemId == finalItem.ItemId);
		}
		Assert.Equal(4, fixture.ExecutedActions.Count);

		fixture.ExecutedActions.Clear();
		var cycleA = new RundownSceneItem(
			RundownItemId.New(),
			"Cycle A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNext);
		var cycleB = new RundownSceneItem(
			RundownItemId.New(),
			"Cycle B",
			fixture.SceneB,
			repeat: new RundownRepeatPolicy(RundownRepeatMode.RepeatRundown, 1),
			followAction: RundownFollowAction.AutoGoNext);
		var secondRundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Repeat rundown", [cycleA, cycleB]);

		await using var secondShow = fixture.CreateShowControl();
		await using var secondCoordinator = fixture.CreateRundown(secondShow);
		var current = await secondCoordinator.GetSnapshotAsync();
		await secondCoordinator.SaveAsync(secondRundown, current.StorageVersion);
		await secondCoordinator.PrepareAsync(cycleA.ItemId);
		await secondCoordinator.GoAsync();
		var completed = await WaitUntilAsync(
			secondCoordinator,
			snapshot => snapshot.Execution.State == RundownExecutionState.Completed);

		Assert.Equal(cycleB.ItemId, completed.Execution.CurrentItemId);
		Assert.Equal(4, fixture.ExecutedActions.Count);
	}

	[Fact]
	public async Task Completed_automatic_follow_is_not_replayed_after_restart()
	{
		await using var fixture = await Fixture.CreateAsync();
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNext);
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Restart ambiguity", [firstItem, secondItem]);

		await using (var show = fixture.CreateShowControl())
		await using (var coordinator = fixture.CreateRundown(show))
		{
			await coordinator.SaveAsync(rundown, 0);
			await coordinator.PrepareAsync(firstItem.ItemId);
			var directShowCompletion = await show.GoAsync();
			Assert.Equal(ShowControlExecutionState.Completed, directShowCompletion.Execution.State);
		}

		await using var restoredShow = fixture.CreateShowControl();
		await using var restored = fixture.CreateRundown(restoredShow);
		var recovery = await restored.GetSnapshotAsync();

		Assert.Equal(RundownExecutionState.RecoveryRequired, recovery.Execution.State);
		Assert.Equal("rundown.recovery.pending_follow_ambiguous", recovery.Execution.Failure?.Code);
		Assert.Equal(secondItem.ItemId, recovery.Execution.PendingNextItemId);
		Assert.Single(fixture.ExecutedActions);

		var resumed = await restored.AcknowledgeRecoveryAsync(resume: true);
		Assert.Equal(RundownExecutionState.Prepared, resumed.Execution.State);
		Assert.Equal(secondItem.ItemId, resumed.Execution.PreparedItemId);
		Assert.Single(fixture.ExecutedActions);
	}

	[Fact]
	public async Task Execution_failure_stops_follow_progression()
	{
		await using var fixture = await Fixture.CreateAsync();
		fixture.FailActionExecutionNumber = 1;
		var firstItem = new RundownSceneItem(
			RundownItemId.New(),
			"Scene A",
			fixture.SceneA,
			followAction: RundownFollowAction.AutoGoNext);
		var secondItem = new RundownSceneItem(RundownItemId.New(), "Scene B", fixture.SceneB);
		var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Failure stop", [firstItem, secondItem]);

		await using var show = fixture.CreateShowControl();
		await using var coordinator = fixture.CreateRundown(show);
		await coordinator.SaveAsync(rundown, 0);
		await coordinator.PrepareAsync(firstItem.ItemId);
		var failed = await coordinator.GoAsync();

		Assert.Equal(RundownExecutionState.Failed, failed.Execution.State);
		Assert.False(failed.Execution.AutoAdvanceArmed);
		Assert.Null(failed.Execution.PendingNextItemId);
		Assert.Single(fixture.ExecutedActions);
	}

	private static async Task<RundownWorkspaceSnapshot> WaitUntilAsync(
		RundownCoordinator coordinator,
		Func<RundownWorkspaceSnapshot, bool> predicate)
	{
		var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			var snapshot = await coordinator.GetSnapshotAsync();
			if (predicate(snapshot))
				return snapshot;
			await Task.Delay(10);
		}
		throw new TimeoutException("Rundown state did not reach the expected condition.");
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root;
		private readonly BoundedProductionJournal _journal;
		private readonly SqliteManagementStore _management;
		private readonly ShowControlPersistenceStore _showControlStore;
		private readonly ShowProjectPersistenceStore _showProjectStore;

		private Fixture(
			string root,
			ProductionSceneSpecification sceneA,
			ProductionSceneSpecification sceneB,
			ControlHostService control,
			BoundedProductionJournal journal,
			SqliteManagementStore management)
		{
			_root = root;
			SceneA = sceneA.SceneId;
			SceneB = sceneB.SceneId;
			Control = control;
			_journal = journal;
			_management = management;
			_showControlStore = new ShowControlPersistenceStore(management);
			_showProjectStore = new ShowProjectPersistenceStore(management);
		}

		public SceneId SceneA { get; }
		public SceneId SceneB { get; }
		public ControlHostService Control { get; }
		public List<ShowControlActionKind> ExecutedActions { get; } = [];
		public ulong FrameSequence { get; set; } = 10;
		public string RuntimeHostInstanceId { get; set; } = "runtime-rundown";
		public int? FailActionExecutionNumber { get; set; }
		private int ActionExecutionCount { get; set; }

		public static async Task<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-rundown-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var sourceA = ProductionSourceId.New();
			var sourceB = ProductionSourceId.New();
			var sceneA = new ProductionSceneSpecification(
				SceneId.New(),
				"Scene A",
				new ProductionRoutingState(sourceA, sourceA));
			var sceneB = new ProductionSceneSpecification(
				SceneId.New(),
				"Scene B",
				new ProductionRoutingState(sourceB, sourceB));
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				ProductionId.New(),
				"Rundown Integration",
				[
					new ProductionSourceSpecification(sourceA, "Camera A"),
					new ProductionSourceSpecification(sourceB, "Camera B")
				],
				new ProductionRoutingState(sourceA, sourceA),
				[sceneA, sceneB]);

			var journal = new BoundedProductionJournal(128);
			var control = new ControlHostService(specification, [], journal);
			var initialized = ControlDomainEngine.Initialize(specification);
			Assert.True(initialized.Succeeded);
			control.RestoreAuthoritativeState(initialized.State!.Authoritative);

			var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			var fixture = new Fixture(root, sceneA, sceneB, control, journal, management);
			await fixture._showProjectStore.LoadOrCreateAsync(specification);
			return fixture;
		}

		public ShowControlCoordinator CreateShowControl() =>
			new(
				() => Control,
				_showControlStore,
				(action, _) =>
				{
					ExecutedActions.Add(action.Kind);
					ActionExecutionCount++;
					return ValueTask.FromResult(
						FailActionExecutionNumber == ActionExecutionCount
							? new Failure("test.rundown.action_failed", "Injected rundown action failure.")
							: null);
				},
				_ => ValueTask.FromResult(new ShowControlFrameObservation(
					RuntimeHostInstanceId,
					FrameSequence,
					TimeSpan.FromMilliseconds(20))),
				() => { });

		public RundownCoordinator CreateRundown(ShowControlCoordinator showControl) =>
			new(
				() => Control,
				_showProjectStore,
				showControl,
				mediaCatalog: null,
				mediaDeck: null,
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
