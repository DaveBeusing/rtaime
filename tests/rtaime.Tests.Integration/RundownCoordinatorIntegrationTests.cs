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
		Assert.Equal(0, fixture.ExecutedActions.Count);
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
					return ValueTask.FromResult<Failure?>(null);
				},
				_ => ValueTask.FromResult(new ShowControlFrameObservation(
					"runtime-rundown",
					10,
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
