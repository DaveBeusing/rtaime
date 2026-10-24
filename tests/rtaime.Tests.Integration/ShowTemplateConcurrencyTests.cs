// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Text.Json;
using rtaime.Client;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Integration;

public sealed class ShowTemplateConcurrencyTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Theory]
	[InlineData("create", false)]
	[InlineData("preflight", false)]
	[InlineData("instantiate", false)]
	[InlineData("create", true)]
	[InlineData("preflight", true)]
	[InlineData("instantiate", true)]
	public async Task Template_authoring_waits_without_blocking_Go_or_frame_wait_continuation(string operation, bool frameWait)
	{
		await using var fixture = await Fixture.CreateAsync(frameWait);
		var action = await fixture.PauseCueBeforeMutationAsync(frameWait);
		var template = fixture.TemplateAsync(operation);

		// Dispatch reaches its first blocked await before returning this task. The cue
		// owns Show Control, so the waiting template must leave production mutation free.
		await fixture.AssertMutationAvailableAsync();
		Assert.False(template.IsCompleted);
		action.Release.TrySetResult(true);
		var response = await template.WaitAsync(Deadline);
		var workspace = await fixture.Coordinator.GetSnapshotAsync(fixture.Token).AsTask().WaitAsync(Deadline);
		Assert.Equal(ShowControlExecutionState.Completed, workspace.Execution.State);
		Assert.Equal(1, fixture.Runtime.AudioRoutingCalls);
		Assert.Equal(fixture.InitialProduction, fixture.Control.State);
		Assert.Single(workspace.CueLists);
		Assert.Equal(fixture.CueList.CueListId, workspace.SelectedCueListId);

		if (operation == "instantiate")
		{
			Assert.Equal("control.show_template.rejected", response.ErrorCode);
			Assert.Contains("STALE", response.ErrorMessage, StringComparison.Ordinal);
		}
		else
		{
			Assert.Null(response.ErrorCode);
			if (operation == "create")
			{
				var document = ShowTemplateSerializer.Deserialize(ReadTemplate(response));
				Assert.Equal(ShowControlCanonicalSerializer.Serialize(fixture.CueList), document.Content.CueListJson);
			}
			else
			{
				var preview = ReadPreflight(response);
				Assert.True(preview.CanCommit, string.Join("; ", preview.Conflicts));
				var project = await fixture.Projects.LoadAsync(fixture.Control.Specification);
				Assert.Equal(project.StorageVersion, preview.ProjectVersion);
				Assert.Equal(project.ShowControlStorageVersion, preview.WorkspaceVersion);
			}
		}
		await fixture.AssertDurableCatalogsAsync(expectedLists: 1, expectedScenes: 1);
	}

	[Theory]
	[InlineData("create")]
	[InlineData("preflight")]
	[InlineData("instantiate")]
	public async Task Cancelling_template_waiting_for_Show_Control_leaves_the_cue_and_mutation_available(string operation)
	{
		await using var fixture = await Fixture.CreateAsync(frameWait: false);
		var action = await fixture.PauseCueBeforeMutationAsync(frameWait: false);
		using var cancellation = new CancellationTokenSource();
		var template = fixture.TemplateAsync(operation, cancellation.Token);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => template.WaitAsync(Deadline));
		await fixture.AssertMutationAvailableAsync();
		action.Release.TrySetResult(true);
		var snapshot = await fixture.Coordinator.GetSnapshotAsync(fixture.Token).AsTask().WaitAsync(Deadline);
		Assert.Equal(ShowControlExecutionState.Completed, snapshot.Execution.State);
		Assert.Equal(1, fixture.Runtime.AudioRoutingCalls);
		await fixture.AssertDurableCatalogsAsync(expectedLists: 1, expectedScenes: 1);
	}

	[Theory]
	[InlineData("create")]
	[InlineData("preflight")]
	[InlineData("instantiate")]
	public async Task Cancelling_template_waiting_for_mutation_releases_Show_Control_ownership(string operation)
	{
		await using var fixture = await Fixture.CreateAsync(frameWait: false);
		var mutation = new Barrier();
		var blocker = fixture.HoldMutationAsync(mutation);
		await mutation.Entered.Task.WaitAsync(Deadline);
		using var cancellation = new CancellationTokenSource();
		var template = fixture.TemplateAsync(operation, cancellation.Token);
		var action = fixture.Runtime.PauseNextSnapshot();
		var go = fixture.DispatchAsync("show_control.go", new { });
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => template.WaitAsync(Deadline));
		// GO must obtain Show Control even while the unrelated mutation is still paused.
		await action.Entered.Task.WaitAsync(Deadline);
		mutation.Release.TrySetResult(true);
		await blocker.WaitAsync(Deadline);
		action.Release.TrySetResult(true);
		Assert.Null((await go.WaitAsync(Deadline)).ErrorCode);
		Assert.Equal(1, fixture.Runtime.AudioRoutingCalls);
		await fixture.AssertDurableCatalogsAsync(expectedLists: 1, expectedScenes: 1);
	}

	[Fact]
	public async Task Concurrent_imports_commit_one_authored_instance_and_restore_without_replay()
	{
		await using var fixture = await Fixture.CreateAsync(frameWait: false, arm: false);
		var observation = fixture.Runtime.PauseNextSnapshot();
		var first = fixture.TemplateAsync("instantiate");
		await observation.Entered.Task.WaitAsync(Deadline);
		var second = fixture.TemplateAsync("instantiate");
		Assert.False(second.IsCompleted);
		observation.Release.TrySetResult(true);
		Assert.Null((await first.WaitAsync(Deadline)).ErrorCode);
		var rejected = await second.WaitAsync(Deadline);
		Assert.Equal("control.show_template.rejected", rejected.ErrorCode);
		Assert.Contains("STALE", rejected.ErrorMessage, StringComparison.Ordinal);
		var workspace = await fixture.Coordinator.GetSnapshotAsync();
		Assert.Equal(ShowControlExecutionState.Idle, workspace.Execution.State);
		Assert.Equal(fixture.CueList.CueListId, workspace.SelectedCueListId);
		Assert.Equal(fixture.InitialProduction, fixture.Control.State);
		Assert.Equal(0, fixture.Runtime.AudioRoutingCalls);
		await fixture.AssertDurableCatalogsAsync(expectedLists: 2, expectedScenes: 2);
		var project = await fixture.Projects.LoadAsync(fixture.Control.Specification);
		await using var restoredJournal = new BoundedProductionJournal(128);
		var restoredControl = new ControlHostService(project.ApplyTo(fixture.Control.Specification), [], restoredJournal);
		restoredControl.RestoreAuthoritativeState(fixture.InitialProduction);
		await using var restored = new ShowControlCoordinator(() => restoredControl,
			new ShowControlPersistenceStore(fixture.Management, fixture.Projects, restoredControl.Specification),
			(_, _) => throw new InvalidOperationException("Template restore must not execute actions."),
			_ => throw new InvalidOperationException("Template restore must not start a frame wait."), () => { });
		var restoredWorkspace = await restored.GetSnapshotAsync();
		Assert.Equal(workspace.CueLists.Select(list => list.CueListId), restoredWorkspace.CueLists.Select(list => list.CueListId));
		Assert.Equal(workspace.Execution, restoredWorkspace.Execution);
		Assert.Equal(workspace.SelectedCueListId, restoredWorkspace.SelectedCueListId);
	}

	[Fact]
	public async Task Cancelling_import_during_preflight_keeps_durable_and_in_memory_catalogs_unchanged()
	{
		await using var fixture = await Fixture.CreateAsync(frameWait: false, arm: false);
		var before = await fixture.Projects.LoadAsync(fixture.Control.Specification);
		var observation = fixture.Runtime.PauseNextSnapshot();
		using var cancellation = new CancellationTokenSource();
		var import = fixture.TemplateAsync("instantiate", cancellation.Token);
		await observation.Entered.Task.WaitAsync(Deadline);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitAsync(Deadline));
		await fixture.AssertMutationAvailableAsync();
		var after = await fixture.Projects.LoadAsync(fixture.Control.Specification);
		Assert.Equal(before.StorageVersion, after.StorageVersion);
		Assert.Equal(before.ShowControlStorageVersion, after.ShowControlStorageVersion);
		await fixture.AssertDurableCatalogsAsync(expectedLists: 1, expectedScenes: 1);
	}

	[Theory]
	[InlineData("create")]
	[InlineData("preflight")]
	[InlineData("instantiate")]
	public async Task Shutdown_drains_template_contention_without_a_late_action_or_partial_import(string operation)
	{
		if (!OperatingSystem.IsWindows()) return;
		await using var fixture = await Fixture.CreateAsync(frameWait: false);
		await fixture.Server.StartAsync();
		var commands = fixture.CreateClient();
		var action = fixture.Runtime.PauseNextSnapshot();
		var go = commands.GoShowControlAsync().AsTask();
		await action.Entered.Task.WaitAsync(Deadline);
		var templates = fixture.CreateClient();
		Task template = operation switch
		{
			"create" => templates.CreateShowTemplateAsync("Export", fixture.CueList.CueListId.ToString()).AsTask(),
			"preflight" => templates.PreflightShowTemplateAsync(fixture.Request with
				{ ExpectedProjectVersion = null, ExpectedWorkspaceVersion = null }).AsTask(),
			_ => templates.InstantiateShowTemplateAsync(fixture.Request).AsTask()
		};
		using var deadline = new CancellationTokenSource(Deadline);
		while (fixture.Server.Sessions.ActiveSessions < 2)
			await Task.Delay(1, deadline.Token);
		await fixture.StopAsync();
		Assert.NotNull(await Record.ExceptionAsync(() => go.WaitAsync(Deadline)));
		Assert.NotNull(await Record.ExceptionAsync(() => template.WaitAsync(Deadline)));
		Assert.Equal(0, fixture.Runtime.AudioRoutingCalls);
		Assert.Equal(0, fixture.Server.Sessions.ActiveSessions);
		Assert.False(fixture.Server.Sessions.DrainTimedOut);
		var project = await fixture.Projects.LoadAsync(fixture.Control.Specification);
		Assert.Single(project.Scenes);
		var persisted = await fixture.Persistence.LoadAsync(fixture.Control.Specification.ProductionId);
		Assert.Single(persisted.CueLists);
	}

	private static string ReadTemplate(ExternalControlDispatchResult response)
	{
		using var document = JsonDocument.Parse(response.PayloadJson);
		return document.RootElement.GetProperty("templateJson").GetString()!;
	}

	private static ShowTemplatePreflight ReadPreflight(ExternalControlDispatchResult response) =>
		JsonSerializer.Deserialize<ShowTemplatePreflight>(response.PayloadJson, JsonOptions)!;

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root;
		private readonly BoundedProductionJournal _journal;
		private readonly CancellationTokenSource _requests = new();
		private readonly List<Task> _tasks = [];
		private Task? _stop;

		private Fixture(string root, SqliteManagementStore management, ShowProjectPersistenceStore projects,
			ShowControlPersistenceStore persistence, BoundedProductionJournal journal, ControlHostService control,
			ControlHostIpcServer server, BarrierRuntimeTransport runtime, ShowControlCueList cueList)
		{
			_root = root;
			Management = management;
			Projects = projects;
			Persistence = persistence;
			_journal = journal;
			Control = control;
			Server = server;
			Runtime = runtime;
			CueList = cueList;
			InitialProduction = control.State;
			Coordinator = Assert.IsType<ShowControlCoordinator>(typeof(ControlHostIpcServer)
				.GetField("_showControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server));
		}

		public SqliteManagementStore Management { get; }
		public ShowProjectPersistenceStore Projects { get; }
		public ShowControlPersistenceStore Persistence { get; }
		public ControlHostService Control { get; }
		public ControlHostIpcServer Server { get; }
		public ShowControlCoordinator Coordinator { get; }
		public BarrierRuntimeTransport Runtime { get; }
		public ShowControlCueList CueList { get; }
		public AuthoritativeProductionState InitialProduction { get; }
		public ShowTemplateRequest Request { get; private set; } = null!;
		public CancellationToken Token => _requests.Token;

		public static async Task<Fixture> CreateAsync(bool frameWait, bool arm = true)
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-template-concurrency-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			var sourceA = ProductionSourceId.New();
			var sourceB = ProductionSourceId.New();
			var specification = new ProductionSpecification(ControlContractVersion.Current, ProductionId.New(),
				"Template concurrency", [new(sourceA, "Camera A"), new(sourceB, "Camera B")],
				new(sourceA, sourceA), [new(SceneId.New(), "Saved setup", new(sourceA, sourceB))]);
			var journal = new BoundedProductionJournal(128);
			var control = new ControlHostService(specification, [], journal);
			var initialized = ControlDomainEngine.Initialize(specification);
			Assert.True(initialized.Succeeded);
			control.RestoreAuthoritativeState(initialized.State!.Authoritative);
			var projects = new ShowProjectPersistenceStore(management);
			var project = await projects.LoadOrCreateAsync(specification);
			var persistence = new ShowControlPersistenceStore(management, projects, specification);
			var runtime = new BarrierRuntimeTransport(new(sourceA.Value), new(sourceB.Value));
			var server = new ControlHostIpcServer("rtaime.test.template." + Guid.NewGuid().ToString("N"),
				() => control, runtime, showControlPersistence: persistence, showProjectStore: projects, showProject: project);
			var actions = new List<ShowControlAction>();
			if (frameWait)
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 1));
			actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.SetAudioRouting,
				sourceId: sourceB.ToString(), audioRoutingMode: 2));
			var cueList = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(),
				"Original show", [new(ShowControlCueId.New(), "Breakaway B", actions)]);
			var fixture = new Fixture(root, management, projects, persistence, journal, control, server, runtime, cueList);
			try
			{
				Assert.Null((await fixture.DispatchAsync("show_control.cue_list.save", new
					{ CueListJson = ShowControlCanonicalSerializer.Serialize(cueList) })).ErrorCode);
				var exported = await fixture.TemplateAsync("create");
				Assert.Null(exported.ErrorCode);
				var json = ReadTemplate(exported);
				var document = ShowTemplateSerializer.Deserialize(json);
				fixture.Request = new(json, "Imported show", document.Content.Dependencies
					.Select(dependency => new ShowTemplateBinding(dependency.Kind, dependency.ReferenceId, dependency.ReferenceId)).ToArray());
				var previewResponse = await fixture.TemplateAsync("preflight");
				Assert.Null(previewResponse.ErrorCode);
				var preview = ReadPreflight(previewResponse);
				Assert.True(preview.CanCommit, string.Join("; ", preview.Conflicts));
				fixture.Request = fixture.Request with
					{ ExpectedProjectVersion = preview.ProjectVersion, ExpectedWorkspaceVersion = preview.WorkspaceVersion };
				if (arm)
					Assert.Null((await fixture.DispatchAsync("show_control.arm", new { })).ErrorCode);
				return fixture;
			}
			catch
			{
				await fixture.DisposeAsync();
				throw;
			}
		}

		public Task<ExternalControlDispatchResult> TemplateAsync(string operation, CancellationToken cancellationToken = default) =>
			DispatchAsync("show_template." + operation, operation == "create"
				? new { Name = "Export", CueListId = CueList.CueListId.ToString() }
				: operation == "preflight" ? Request with { ExpectedProjectVersion = null, ExpectedWorkspaceVersion = null }
				: Request, cancellationToken);

		public Task<ExternalControlDispatchResult> DispatchAsync(string operation, object payload, CancellationToken cancellationToken = default)
		{
			var task = DispatchCoreAsync(operation, payload, cancellationToken);
			_tasks.Add(task);
			return task;
		}

		private async Task<ExternalControlDispatchResult> DispatchCoreAsync(string operation, object payload, CancellationToken cancellationToken)
		{
			using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
			return await Server.DispatchExternalAsync("control." + operation, Guid.NewGuid().ToString("N"),
				Guid.NewGuid().ToString("N"), "template-concurrency", JsonSerializer.Serialize(payload, JsonOptions), lifetime.Token);
		}

		public async Task<Barrier> PauseCueBeforeMutationAsync(bool frameWait)
		{
			if (frameWait)
			{
				Assert.Null((await DispatchAsync("show_control.go", new { })).ErrorCode);
				Assert.Equal(ShowControlExecutionState.Waiting, (await Coordinator.GetSnapshotAsync()).Execution.State);
				var frames = Runtime.PauseNextSnapshot();
				await frames.Entered.Task.WaitAsync(Deadline);
				var action = Runtime.PauseNextSnapshot();
				Runtime.NextFrame = 2;
				frames.Release.TrySetResult(true);
				await action.Entered.Task.WaitAsync(Deadline);
				return action;
			}
			else
			{
				var action = Runtime.PauseNextSnapshot();
				_ = DispatchAsync("show_control.go", new { });
				await action.Entered.Task.WaitAsync(Deadline);
				return action;
			}
		}

		public Task AssertMutationAvailableAsync() => Server.RunSerializedMutationAsync(
			_ => ValueTask.CompletedTask, Token).AsTask().WaitAsync(Deadline);

		public Task HoldMutationAsync(Barrier barrier)
		{
			var task = Server.RunSerializedMutationAsync(async cancellationToken =>
			{
				barrier.Entered.TrySetResult(true);
				await barrier.Release.Task.WaitAsync(cancellationToken);
			}, Token).AsTask();
			_tasks.Add(task);
			return task;
		}

		public async Task AssertDurableCatalogsAsync(int expectedLists, int expectedScenes)
		{
			var workspace = await Coordinator.GetSnapshotAsync(Token).AsTask().WaitAsync(Deadline);
			var project = await Projects.LoadAsync(Control.Specification);
			var persisted = await Persistence.LoadAsync(Control.Specification.ProductionId);
			Assert.Equal(expectedLists, workspace.CueLists.Count);
			Assert.Equal(expectedLists, persisted.CueLists.Count);
			Assert.Equal(expectedScenes, Control.Specification.Scenes.Count);
			Assert.Equal(expectedScenes, project.Scenes.Count);
			Assert.Equal(workspace.CueLists.Select(list => list.CueListId), persisted.CueLists.Select(list => list.CueListId));
			Assert.Equal(Control.Specification.Scenes.Select(scene => scene.SceneId), project.Scenes.Select(scene => scene.SceneId));
			Assert.Equal(project.ShowControlStorageVersion, persisted.StorageVersion);
			Assert.Equal(workspace.Execution, persisted.Execution);
			Assert.Equal(workspace.SelectedCueListId, persisted.SelectedCueListId);
		}

		public NamedPipeOperatorControlTransport CreateClient() => new(Server.Endpoint, Deadline, Deadline);
		public Task StopAsync() => (_stop ??= Server.DisposeAsync().AsTask()).WaitAsync(Deadline);

		public async ValueTask DisposeAsync()
		{
			_requests.Cancel();
			Runtime.ReleaseBarriers();
			try { await Task.WhenAll(_tasks).WaitAsync(Deadline); }
			catch (Exception) when (_tasks.All(task => task.IsCompleted)) { }
			try { await StopAsync(); }
			finally
			{
				_requests.Dispose();
				await Management.DisposeAsync();
				await _journal.DisposeAsync();
				Directory.Delete(_root, recursive: true);
			}
		}
	}

	private sealed class Barrier
	{
		public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed class BarrierRuntimeTransport(MediaSourceId sourceA, MediaSourceId sourceB) : IControlRuntimeTransportSeam
	{
		private readonly List<Barrier> _barriers = [];
		private Barrier? _nextBarrier;
		private RuntimeAudioProgramSnapshot _audioProgram = new(sourceA, AudioStreamId.New(), 1, false, 0, 0, 0, false,
			"HEALTHY", ActiveAudioSourceId: sourceA);
		private int _audioRoutingCalls;
		private long _nextFrame = 1;

		public bool IsConnected => true;
		public string HostInstanceId => "runtime-template-concurrency";
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => [];
		public int AudioRoutingCalls => Volatile.Read(ref _audioRoutingCalls);
		public long NextFrame { set => Interlocked.Exchange(ref _nextFrame, value); }
		public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
		public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(ProviderDescriptors);

		public Barrier PauseNextSnapshot()
		{
			var barrier = new Barrier();
			if (Interlocked.CompareExchange(ref _nextBarrier, barrier, null) is not null)
				throw new InvalidOperationException("The previous snapshot barrier has not been entered.");
			_barriers.Add(barrier);
			return barrier;
		}

		public void ReleaseBarriers()
		{
			foreach (var barrier in _barriers)
				barrier.Release.TrySetResult(true);
		}

		public async ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			if (Interlocked.Exchange(ref _nextBarrier, null) is { } barrier)
			{
				barrier.Entered.TrySetResult(true);
				await barrier.Release.Task.WaitAsync(cancellationToken);
			}
			cancellationToken.ThrowIfCancellationRequested();
			return new(HostInstanceId,
				new RuntimeExecutionState(RuntimeContractVersion.Current, null, Revision.Initial, RuntimeExecutionStatus.Idle, null),
				null, null, checked((ulong)Interlocked.Read(ref _nextFrame)), 0, 0, VideoFormat.Hd1080p50Rgba8,
				new Dictionary<MediaSourceId, string> { [sourceA] = "VALID", [sourceB] = "VALID" },
				new(false, null, 0, 0, false, 0, 0, 1), new Dictionary<MediaSourceId, RuntimeAudioInputSnapshot>(),
				Volatile.Read(ref _audioProgram), 1);
		}

		public ValueTask<RuntimeAudioProgramSnapshot> SetAudioRoutingAsync(int mode, MediaSourceId? breakawaySourceId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _audioRoutingCalls);
			var current = Volatile.Read(ref _audioProgram);
			var updated = current with { RoutingMode = mode, RoutingRevision = current.RoutingRevision + 1,
				ActiveAudioSourceId = breakawaySourceId ?? sourceA };
			Volatile.Write(ref _audioProgram, updated);
			return ValueTask.FromResult(updated);
		}

		public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(PreparedExecutionContract preparedExecution,
			MediaSinkId programSinkId, RuntimeProgramTransitionIntent? transition, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<RuntimeRemoteApplyResult>(new InvalidOperationException("This fixture exercises the governed audio route."));
	}
}
