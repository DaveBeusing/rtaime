// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Persistence;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class ShowControlCoordinatorIntegrationTests
{
	[Fact]
	public async Task Go_executes_actions_in_order_and_rearms_between_cues()
	{
		await using var fixture = await Fixture.CreateAsync();
		var executed = new List<ShowControlActionKind>();
		await using var coordinator = fixture.CreateCoordinator(
			(action, _) =>
			{
				executed.Add(action.Kind);
				return ValueTask.FromResult<Failure?>(null);
			});

		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			ShowControlCueListId.New(),
			"Main show",
			[
				new ShowControlCue(
					ShowControlCueId.New(),
					"Take camera B",
					[
						new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: fixture.SourceB.ToString()),
						new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.Cut)
					]),
				new ShowControlCue(
					ShowControlCueId.New(),
					"Stop record",
					[new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.StopRecording)])
			]);

		await coordinator.SaveCueListAsync(cueList);
		await coordinator.SelectCueListAsync(cueList.CueListId);
		var armed = await coordinator.ArmAsync();
		Assert.Equal(ShowControlExecutionState.Armed, armed.Execution.State);

		var afterFirstGo = await coordinator.GoAsync();

		Assert.Equal(
			[ShowControlActionKind.SetPreview, ShowControlActionKind.Cut],
			executed);
		Assert.Equal(ShowControlExecutionState.Armed, afterFirstGo.Execution.State);
		Assert.Equal(1, afterFirstGo.Execution.CueIndex);

		var completed = await coordinator.GoAsync();

		Assert.Equal(
			[ShowControlActionKind.SetPreview, ShowControlActionKind.Cut, ShowControlActionKind.StopRecording],
			executed);
		Assert.Equal(ShowControlExecutionState.Completed, completed.Execution.State);
	}

	[Fact]
	public async Task Production_action_failure_stops_sequence_without_advancing()
	{
		await using var fixture = await Fixture.CreateAsync();
		var executed = new List<ShowControlActionKind>();
		await using var coordinator = fixture.CreateCoordinator(
			(action, _) =>
			{
				executed.Add(action.Kind);
				return ValueTask.FromResult<Failure?>(
					action.Kind == ShowControlActionKind.Cut
						? new Failure("show_control.test.cut_failed", "CUT failed.")
						: null);
			});

		var cue = new ShowControlCue(
			ShowControlCueId.New(),
			"Failing cue",
			[
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: fixture.SourceB.ToString()),
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.Cut),
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.MediaStop, mediaAssetId: Identity.New().ToString())
			]);
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			ShowControlCueListId.New(),
			"Failure show",
			[cue]);

		await coordinator.SaveCueListAsync(cueList);
		await coordinator.SelectCueListAsync(cueList.CueListId);
		await coordinator.ArmAsync();
		var failed = await coordinator.GoAsync();

		Assert.Equal([ShowControlActionKind.SetPreview, ShowControlActionKind.Cut], executed);
		Assert.Equal(ShowControlExecutionState.Failed, failed.Execution.State);
		Assert.Equal("show_control.test.cut_failed", failed.Execution.Failure?.Code);
		Assert.Equal(0, failed.Execution.CueIndex);
		Assert.Equal(1, failed.Execution.ActionIndex);
	}

	[Fact]
	public async Task Restart_during_frame_wait_restores_recovery_required()
	{
		await using var fixture = await Fixture.CreateAsync();
		var cue = new ShowControlCue(
			ShowControlCueId.New(),
			"Bounded wait",
			[new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 100)]);
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			ShowControlCueListId.New(),
			"Recovery show",
			[cue]);

		await using (var first = fixture.CreateCoordinator(
			(_, _) => ValueTask.FromResult<Failure?>(null),
			_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-a", 10, TimeSpan.FromMilliseconds(20)))))
		{
			await first.SaveCueListAsync(cueList);
			await first.SelectCueListAsync(cueList.CueListId);
			await first.ArmAsync();
			var waiting = await first.GoAsync();
			Assert.Equal(ShowControlExecutionState.Waiting, waiting.Execution.State);
		}

		await using var restored = fixture.CreateCoordinator(
			(_, _) => ValueTask.FromResult<Failure?>(null),
			_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-a", 10, TimeSpan.FromMilliseconds(20))));
		var snapshot = await restored.GetSnapshotAsync();

		Assert.Equal(ShowControlExecutionState.RecoveryRequired, snapshot.Execution.State);
		Assert.True(snapshot.Execution.RequiresAcknowledgement);
		Assert.Equal("show_control.recovery.uncertain_action", snapshot.Execution.Failure?.Code);
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root;
		private readonly BoundedProductionJournal _journal;
		private readonly SqliteManagementStore _management;
		private readonly ShowControlPersistenceStore _showControlStore;

		private Fixture(
			string root,
			ProductionSourceId sourceB,
			ControlHostService control,
			BoundedProductionJournal journal,
			SqliteManagementStore management)
		{
			_root = root;
			SourceB = sourceB;
			Control = control;
			_journal = journal;
			_management = management;
			_showControlStore = new ShowControlPersistenceStore(management);
		}

		public ProductionSourceId SourceB { get; }
		public ControlHostService Control { get; }

		public static async Task<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-show-control-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var sourceA = ProductionSourceId.New();
			var sourceB = ProductionSourceId.New();
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				ProductionId.New(),
				"Show Control Integration",
				[
					new ProductionSourceSpecification(sourceA, "Camera A"),
					new ProductionSourceSpecification(sourceB, "Camera B")
				],
				new ProductionRoutingState(sourceA, sourceA));
			var journal = new BoundedProductionJournal(128);
			var control = new ControlHostService(specification, [], journal);
			var initialized = ControlDomainEngine.Initialize(specification);
			Assert.True(initialized.Succeeded);
			control.RestoreAuthoritativeState(initialized.State!.Authoritative);
			var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			return new Fixture(root, sourceB, control, journal, management);
		}

		public ShowControlCoordinator CreateCoordinator(
			ShowControlActionExecutor executor,
			ShowControlFrameObserver? observer = null) =>
			new(
				() => Control,
				_showControlStore,
				executor,
				observer ?? (_ => ValueTask.FromResult(new ShowControlFrameObservation("runtime-a", 1, TimeSpan.FromMilliseconds(20)))),
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
