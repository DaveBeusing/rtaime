// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using rtaime.Client;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class ShowControlSnapshotConcurrencyTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Operator_snapshot_releases_mutation_ownership_for_Go_and_frame_wait_continuation(bool frameWait)
	{
		if (!OperatingSystem.IsWindows()) return;
		await using var fixture = await Fixture.CreateAsync(frameWait);
		SnapshotBarrier? frameObservation = null;
		Task<ShowControlWorkspaceSnapshot>? go = null;
		if (frameWait)
		{
			Assert.Equal(ShowControlExecutionState.Waiting, (await fixture.Commands.GoShowControlAsync()).Execution.State);
			frameObservation = fixture.Runtime.PauseNextSnapshot();
			await frameObservation.Entered.Task.WaitAsync(Deadline);
		}

		var operatorObservation = fixture.Runtime.PauseNextSnapshot();
		var poll = fixture.CreateClient().GetSnapshotAsync().AsTask();
		await operatorObservation.Entered.Task.WaitAsync(Deadline);
		var actionObservation = fixture.Runtime.PauseNextSnapshot();
		if (frameWait)
		{
			fixture.Runtime.NextFrame = 2;
			frameObservation!.Release.TrySetResult(true);
		}
		else
		{
			go = fixture.Commands.GoShowControlAsync().AsTask();
		}
		await actionObservation.Entered.Task.WaitAsync(Deadline);

		// The Operator owns mutation while the cue owns Show Control. Let polling reach
		// the busy coordinator before the action proceeds into the production mutation path.
		operatorObservation.Release.TrySetResult(true);
		await fixture.AssertMutationGateAvailableAsync();
		Assert.False(poll.IsCompleted);
		actionObservation.Release.TrySetResult(true);

		if (go is not null)
			Assert.Equal(ShowControlExecutionState.Completed, (await go.WaitAsync(Deadline)).Execution.State);
		var snapshot = await poll.WaitAsync(Deadline);
		Assert.Equal(ShowControlExecutionState.Completed, snapshot.ShowControl.Execution.State);
		Assert.Equal(1, fixture.Runtime.AudioRoutingCalls);
		Assert.Equal(1UL, snapshot.AudioProgram.RoutingRevision);
		Assert.Equal(fixture.SourceB.ToString(), snapshot.AudioProgram.ActiveAudioSourceId);
		Assert.Equal(fixture.InitialProduction, snapshot.Production);
		Assert.Equal(snapshot.Production, (await fixture.CreateClient().GetSnapshotAsync()).Production);
		await fixture.StopAsync();
		Assert.Equal(0, fixture.Server.Sessions.ActiveSessions);
		Assert.False(fixture.Server.Sessions.DrainTimedOut);
	}

	[Fact]
	public async Task Shutdown_cancels_snapshot_contention_and_in_flight_cue_without_a_late_action()
	{
		if (!OperatingSystem.IsWindows()) return;
		await using var fixture = await Fixture.CreateAsync(frameWait: false);
		var operatorObservation = fixture.Runtime.PauseNextSnapshot();
		var poll = fixture.CreateClient().GetSnapshotAsync().AsTask();
		await operatorObservation.Entered.Task.WaitAsync(Deadline);
		var actionObservation = fixture.Runtime.PauseNextSnapshot();
		var go = fixture.Commands.GoShowControlAsync().AsTask();
		await actionObservation.Entered.Task.WaitAsync(Deadline);
		operatorObservation.Release.TrySetResult(true);
		await fixture.AssertMutationGateAvailableAsync();

		// Keep the action paused; shutdown must cancel both the server-side poll wait
		// and the cue's runtime observation, rather than depending on client timeouts.
		await fixture.StopAsync();
		Assert.NotNull(await Record.ExceptionAsync(() => poll.WaitAsync(Deadline)));
		Assert.NotNull(await Record.ExceptionAsync(() => go.WaitAsync(Deadline)));
		Assert.Equal(0, fixture.Runtime.AudioRoutingCalls);
		Assert.Equal(0, fixture.Server.Sessions.ActiveSessions);
		Assert.False(fixture.Server.Sessions.DrainTimedOut);
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root;
		private readonly SqliteManagementStore _management;
		private readonly BoundedProductionJournal _journal;
		private Task? _stop;

		private Fixture(string root, SqliteManagementStore management, BoundedProductionJournal journal,
			ControlHostIpcServer server, BarrierRuntimeTransport runtime, ProductionSourceId sourceB,
			AuthoritativeProductionState initialProduction)
		{
			_root = root;
			_management = management;
			_journal = journal;
			Server = server;
			Runtime = runtime;
			SourceB = sourceB;
			InitialProduction = initialProduction;
			Commands = CreateClient();
		}

		public ControlHostIpcServer Server { get; }
		public BarrierRuntimeTransport Runtime { get; }
		public ProductionSourceId SourceB { get; }
		public AuthoritativeProductionState InitialProduction { get; }
		public NamedPipeOperatorControlTransport Commands { get; }

		public NamedPipeOperatorControlTransport CreateClient() => new(Server.Endpoint, Deadline, TimeSpan.FromSeconds(15));

		public static async Task<Fixture> CreateAsync(bool frameWait)
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-show-poll-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			var sourceA = ProductionSourceId.New();
			var sourceB = ProductionSourceId.New();
			var specification = new ProductionSpecification(ControlContractVersion.Current, ProductionId.New(),
				"Show polling concurrency", [new(sourceA, "Camera A"), new(sourceB, "Camera B")],
				new ProductionRoutingState(sourceA, sourceA));
			var journal = new BoundedProductionJournal(128);
			var control = new ControlHostService(specification, [], journal);
			var initialized = ControlDomainEngine.Initialize(specification);
			Assert.True(initialized.Succeeded);
			control.RestoreAuthoritativeState(initialized.State!.Authoritative);
			var runtime = new BarrierRuntimeTransport(new MediaSourceId(sourceA.Value));
			var server = new ControlHostIpcServer("rtaime.test.show-poll." + Guid.NewGuid().ToString("N"),
				() => control, runtime, showControlPersistence: new ShowControlPersistenceStore(management));
			var fixture = new Fixture(root, management, journal, server, runtime, sourceB, control.State);
			try
			{
				await server.StartAsync();
				await fixture.Commands.GetSnapshotAsync();
				var actions = new List<ShowControlAction>();
				if (frameWait)
					actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 1));
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.SetAudioRouting,
					sourceId: sourceB.ToString(), audioRoutingMode: 2));
				var list = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(),
					"Single cue", [new(ShowControlCueId.New(), "Breakaway B", actions)]);
				await fixture.Commands.SaveShowControlCueListAsync(list);
				await fixture.Commands.SelectShowControlCueListAsync(list.CueListId);
				await fixture.Commands.ArmShowControlAsync();
				return fixture;
			}
			catch
			{
				await fixture.DisposeAsync();
				throw;
			}
		}

		public async Task AssertMutationGateAvailableAsync()
		{
			var field = typeof(ControlHostIpcServer).GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic);
			var gate = Assert.IsType<SemaphoreSlim>(field!.GetValue(Server));
			Assert.True(await gate.WaitAsync(Deadline), "Operator polling retained mutation ownership while waiting for Show Control.");
			gate.Release();
		}

		public Task StopAsync() => (_stop ??= Server.DisposeAsync().AsTask()).WaitAsync(Deadline);

		public async ValueTask DisposeAsync()
		{
			try { await StopAsync(); }
			finally
			{
				Runtime.ReleaseBarriers();
				await _management.DisposeAsync();
				await _journal.DisposeAsync();
				Directory.Delete(_root, recursive: true);
			}
		}
	}

	private sealed class SnapshotBarrier
	{
		public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed class BarrierRuntimeTransport : IControlRuntimeTransportSeam
	{
		private readonly MediaSourceId _source;
		private readonly List<SnapshotBarrier> _barriers = [];
		private SnapshotBarrier? _nextBarrier;
		private RuntimeAudioProgramSnapshot _audioProgram;
		private int _audioRoutingCalls;
		private long _nextFrame = 1;

		public BarrierRuntimeTransport(MediaSourceId source)
		{
			_source = source;
			_audioProgram = new(source, AudioStreamId.New(), 1, false, 0, 0, 0, false, "HEALTHY", ActiveAudioSourceId: source);
		}

		public bool IsConnected => true;
		public string HostInstanceId => "runtime-show-concurrency";
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => [];
		public int AudioRoutingCalls => Volatile.Read(ref _audioRoutingCalls);
		public long NextFrame { set => Interlocked.Exchange(ref _nextFrame, value); }
		public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
		public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(ProviderDescriptors);

		public SnapshotBarrier PauseNextSnapshot()
		{
			var barrier = new SnapshotBarrier();
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
				new Dictionary<MediaSourceId, string> { [_source] = "VALID" },
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
				ActiveAudioSourceId = breakawaySourceId ?? _source };
			Volatile.Write(ref _audioProgram, updated);
			return ValueTask.FromResult(updated);
		}

		public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(PreparedExecutionContract preparedExecution,
			MediaSinkId programSinkId, RuntimeProgramTransitionIntent? transition, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<RuntimeRemoteApplyResult>(new InvalidOperationException("This fixture exercises the governed audio route."));
	}
}
