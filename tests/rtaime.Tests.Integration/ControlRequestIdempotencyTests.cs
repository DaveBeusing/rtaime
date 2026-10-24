// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.IO.Pipes;
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

public sealed class ControlRequestIdempotencyTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	[InlineData(true, false)]
	public async Task Concurrent_Go_reserves_one_cue_and_rejects_conflicting_in_flight_payloads(bool ownerPipe, bool duplicatePipe)
	{
		await using var fixture = await Fixture.CreateAsync();
		var barrier = fixture.Runtime.PauseNextRoute();
		var requestId = Identity.New().ToString();
		var correlationId = Identity.New().ToString();
		var first = fixture.SendAsync(ownerPipe, "control.show_control.go", requestId, correlationId, "{}");
		await barrier.Entered.Task.WaitAsync(Deadline);
		var replays = fixture.Server.RequestCacheSnapshot.Replays;
		var duplicateCorrelation = Identity.New().ToString();
		var duplicate = fixture.SendAsync(duplicatePipe, "control.show_control.go", requestId, duplicateCorrelation, "{}");
		await WaitUntilAsync(() => fixture.Server.RequestCacheSnapshot.Replays == replays + 1);
		Assert.False(duplicate.IsCompleted);

		var conflict = await fixture.SendAsync(duplicatePipe, "control.show_control.go", requestId,
			Identity.New().ToString(), "{\"conflicting\":true}").WaitAsync(Deadline);
		Assert.Equal("ipc.request_id_conflict", conflict.ErrorCode);
		Assert.Equal(1, fixture.Runtime.RouteAttempts);
		barrier.Release.TrySetResult(true);

		var original = await first.WaitAsync(Deadline);
		var replay = await duplicate.WaitAsync(Deadline);
		Assert.Equal("control.show_control.snapshot.response", original.ResponseType);
		Assert.Equal(correlationId, original.CorrelationId);
		Assert.Equal(original.Sequence, replay.Sequence);
		Assert.Equal(original.StateVersion, replay.StateVersion);
		Assert.Equal(original.PayloadJson, replay.PayloadJson);
		Assert.Equal(duplicateCorrelation, replay.CorrelationId);
		Assert.Equal(1, fixture.Runtime.RouteCalls);
		var workspace = await fixture.Commands.GetShowControlSnapshotAsync();
		Assert.Equal(ShowControlExecutionState.Armed, workspace.Execution.State);
		Assert.Equal(1, workspace.Execution.CueIndex);
		var completedCorrelation = Identity.New().ToString();
		var completedReplay = await fixture.SendAsync(duplicatePipe, "control.show_control.go", requestId, completedCorrelation, "{}");
		Assert.Equal(original.Sequence, completedReplay.Sequence);
		Assert.Equal(original.StateVersion, completedReplay.StateVersion);
		Assert.Equal(original.PayloadJson, completedReplay.PayloadJson);
		Assert.Equal(completedCorrelation, completedReplay.CorrelationId);
		Assert.Equal(1, fixture.Runtime.RouteCalls);
	}

	[Fact]
	public async Task Cancelled_duplicate_wait_does_not_cancel_or_release_the_original_reservation()
	{
		await using var fixture = await Fixture.CreateAsync();
		var barrier = fixture.Runtime.PauseNextRoute();
		var requestId = Identity.New().ToString();
		var owner = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		await barrier.Entered.Task.WaitAsync(Deadline);
		using var cancelled = new CancellationTokenSource();
		var waiter = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}", cancelled.Token);
		cancelled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
		Assert.Equal(1, fixture.Server.RequestCacheSnapshot.InFlight);
		Assert.False(owner.IsCompleted);
		var survivingWaiter = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		Assert.False(survivingWaiter.IsCompleted);
		barrier.Release.TrySetResult(true);
		var original = await owner.WaitAsync(Deadline);
		Assert.Equal(original.PayloadJson, (await survivingWaiter.WaitAsync(Deadline)).PayloadJson);
		Assert.Equal(1, fixture.Runtime.RouteAttempts);
		Assert.Equal(1, fixture.Runtime.RouteCalls);
		Assert.Equal(0, fixture.Server.RequestCacheSnapshot.InFlight);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Failed_or_cancelled_owner_retains_unknown_outcome_without_repeating_a_committed_action(bool cancel)
	{
		await using var fixture = await Fixture.CreateAsync();
		var barrier = fixture.Runtime.PauseNextRoute(applyBeforeWait: true, failAfterWait: !cancel);
		var requestId = Identity.New().ToString();
		var payload = JsonSerializer.Serialize(new { mode = 2, breakawaySourceId = fixture.SourceB.ToString(), expectedRoutingRevision = 0UL }, JsonOptions);
		using var cancellation = new CancellationTokenSource();
		var owner = fixture.SendAsync(false, "control.audio.routing.set", requestId, Identity.New().ToString(), payload, cancellation.Token);
		await barrier.Entered.Task.WaitAsync(Deadline);
		Assert.Equal(1, fixture.Runtime.RouteCalls);
		var duplicate = fixture.SendAsync(false, "control.audio.routing.set", requestId, Identity.New().ToString(), payload);
		if (cancel) cancellation.Cancel();
		else barrier.Release.TrySetResult(true);
		if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.WaitAsync(Deadline));
		else await Assert.ThrowsAsync<TimeoutException>(() => owner.WaitAsync(Deadline));

		var unknown = await duplicate.WaitAsync(Deadline);
		Assert.Equal("ipc.request_outcome_unknown", unknown.ErrorCode);
		var laterCorrelation = Identity.New().ToString();
		var later = await fixture.SendAsync(true, "control.audio.routing.set", requestId, laterCorrelation, payload);
		Assert.Equal(unknown.Sequence, later.Sequence);
		Assert.Equal(unknown.PayloadJson, later.PayloadJson);
		Assert.Equal(laterCorrelation, later.CorrelationId);
		Assert.Equal(1, fixture.Runtime.RouteAttempts);
		Assert.Equal(1, fixture.Runtime.RouteCalls);
		Assert.Equal(0, fixture.Server.RequestCacheSnapshot.InFlight);
	}

	[Fact]
	public async Task Known_rejection_is_replayed_as_the_same_result()
	{
		await using var fixture = await Fixture.CreateAsync();
		var requestId = Identity.New().ToString();
		var first = await fixture.SendAsync(false, "control.audio.routing.set", requestId, Identity.New().ToString(), "{\"mode\":99}");
		var secondCorrelation = Identity.New().ToString();
		var second = await fixture.SendAsync(true, "control.audio.routing.set", requestId, secondCorrelation, "{\"mode\":99}");
		Assert.Equal("control.audio.routing.mode.invalid", first.ErrorCode);
		Assert.Equal(first.Sequence, second.Sequence);
		Assert.Equal(first.PayloadJson, second.PayloadJson);
		Assert.Equal(secondCorrelation, second.CorrelationId);
		Assert.Equal(0, fixture.Runtime.RouteAttempts);
	}

	[Fact]
	public async Task Completed_request_churn_cannot_evict_a_live_cue_reservation()
	{
		await using var fixture = await Fixture.CreateAsync();
		var barrier = fixture.Runtime.PauseNextRoute();
		var requestId = Identity.New().ToString();
		var owner = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		await barrier.Entered.Task.WaitAsync(Deadline);
		for (var index = 0; index < 300; index++)
		{
			var ping = await fixture.SendAsync(false, "control.ping", Identity.New().ToString(), Identity.New().ToString(), "{}");
			Assert.Equal("control.ping.response", ping.ResponseType);
			Assert.InRange(fixture.Server.RequestCacheSnapshot.Entries, 1, 256);
		}
		var replay = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		Assert.Equal(1, fixture.Server.RequestCacheSnapshot.InFlight);
		Assert.False(replay.IsCompleted);
		barrier.Release.TrySetResult(true);
		Assert.Equal((await owner.WaitAsync(Deadline)).PayloadJson, (await replay.WaitAsync(Deadline)).PayloadJson);
		Assert.Equal(1, fixture.Runtime.RouteCalls);
	}

	[Fact]
	public async Task Fully_occupied_cache_rejects_new_work_but_keeps_existing_duplicates_and_recovers_capacity()
	{
		await using var fixture = await Fixture.CreateAsync();
		var barrier = fixture.Runtime.PauseNextRoute();
		var requestId = Identity.New().ToString();
		var owner = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		await barrier.Entered.Task.WaitAsync(Deadline);
		var queued = Enumerable.Range(0, 255).Select(_ => fixture.SendAsync(false, "control.show_control.snapshot.get",
			Identity.New().ToString(), Identity.New().ToString(), "{}")).ToArray();
		Assert.Equal(256, fixture.Server.RequestCacheSnapshot.Entries);
		Assert.Equal(256, fixture.Server.RequestCacheSnapshot.InFlight);
		var rejectedId = Identity.New().ToString();
		var busy = await fixture.SendAsync(false, "control.ping", rejectedId, Identity.New().ToString(), "{}").WaitAsync(Deadline);
		Assert.Equal("ipc.request_capacity_exceeded", busy.ErrorCode);
		Assert.Equal(256, fixture.Server.RequestCacheSnapshot.Entries);
		Assert.Equal(1, fixture.Server.RequestCacheSnapshot.CapacityRejections);
		var replay = fixture.SendAsync(false, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		Assert.False(replay.IsCompleted);
		barrier.Release.TrySetResult(true);
		Assert.Equal((await owner.WaitAsync(Deadline)).PayloadJson, (await replay.WaitAsync(Deadline)).PayloadJson);
		var snapshots = await Task.WhenAll(queued).WaitAsync(Deadline);
		Assert.All(snapshots, snapshot => Assert.Equal("control.show_control.snapshot.response", snapshot.ResponseType));
		Assert.Equal(1, fixture.Runtime.RouteCalls);
		Assert.Equal(0, fixture.Server.RequestCacheSnapshot.InFlight);
		Assert.Equal("control.ping.response", (await fixture.SendAsync(false, "control.ping", rejectedId, Identity.New().ToString(), "{}")).ResponseType);
		Assert.Equal(256, fixture.Server.RequestCacheSnapshot.Entries);
	}

	[Fact]
	public async Task Pipe_shutdown_releases_owner_and_duplicate_without_dispatching_a_late_cue()
	{
		await using var fixture = await Fixture.CreateAsync();
		var barrier = fixture.Runtime.PauseNextRoute();
		var requestId = Identity.New().ToString();
		var owner = fixture.SendAsync(true, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		await barrier.Entered.Task.WaitAsync(Deadline);
		var replays = fixture.Server.RequestCacheSnapshot.Replays;
		var duplicate = fixture.SendAsync(true, "control.show_control.go", requestId, Identity.New().ToString(), "{}");
		await WaitUntilAsync(() => fixture.Server.RequestCacheSnapshot.Replays == replays + 1);
		await fixture.StopAsync();
		Assert.NotNull(await Record.ExceptionAsync(() => owner.WaitAsync(Deadline)));
		Assert.NotNull(await Record.ExceptionAsync(() => duplicate.WaitAsync(Deadline)));
		Assert.Equal(0, fixture.Runtime.RouteCalls);
		Assert.Equal(0, fixture.Server.RequestCacheSnapshot.InFlight);
		Assert.Equal(0, fixture.Server.Sessions.ActiveSessions);
		Assert.False(fixture.Server.Sessions.DrainTimedOut);
	}

	private static async Task WaitUntilAsync(Func<bool> predicate)
	{
		using var deadline = new CancellationTokenSource(Deadline);
		while (!predicate()) await Task.Delay(5, deadline.Token);
	}

	private sealed record Reply(string ResponseType, string CorrelationId, ulong StateVersion, ulong Sequence, string PayloadJson, string? ErrorCode);

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root;
		private readonly SqliteManagementStore _management;
		private readonly BoundedProductionJournal _journal;
		private readonly CancellationTokenSource _requestsStop = new();
		private Task? _stop;

		private Fixture(string root, SqliteManagementStore management, BoundedProductionJournal journal,
			ControlHostIpcServer server, BarrierRuntimeTransport runtime, ProductionSourceId sourceB)
		{
			_root = root;
			_management = management;
			_journal = journal;
			Server = server;
			Runtime = runtime;
			SourceB = sourceB;
			Commands = new NamedPipeOperatorControlTransport(server.Endpoint, Deadline, TimeSpan.FromSeconds(15));
		}

		public ControlHostIpcServer Server { get; }
		public BarrierRuntimeTransport Runtime { get; }
		public ProductionSourceId SourceB { get; }
		public NamedPipeOperatorControlTransport Commands { get; }

		public static async Task<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-request-id-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			var sourceA = ProductionSourceId.New();
			var sourceB = ProductionSourceId.New();
			var specification = new ProductionSpecification(ControlContractVersion.Current, ProductionId.New(),
				"Request identity", [new(sourceA, "Camera A"), new(sourceB, "Camera B")],
				new ProductionRoutingState(sourceA, sourceA));
			var journal = new BoundedProductionJournal(128);
			var control = new ControlHostService(specification, [], journal);
			var initialized = ControlDomainEngine.Initialize(specification);
			Assert.True(initialized.Succeeded);
			control.RestoreAuthoritativeState(initialized.State!.Authoritative);
			var runtime = new BarrierRuntimeTransport(new MediaSourceId(sourceA.Value));
			var server = new ControlHostIpcServer("rtaime.test.request-id." + Guid.NewGuid().ToString("N"),
				() => control, runtime, showControlPersistence: new ShowControlPersistenceStore(management));
			var fixture = new Fixture(root, management, journal, server, runtime, sourceB);
			try
			{
				await server.StartAsync();
				await fixture.Commands.GetSnapshotAsync();
				var list = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), "Two cues",
					[new(ShowControlCueId.New(), "Route B", [new(ShowControlActionId.New(), ShowControlActionKind.SetAudioRouting,
						sourceId: sourceB.ToString(), audioRoutingMode: 2)]),
					 new(ShowControlCueId.New(), "Route A", [new(ShowControlActionId.New(), ShowControlActionKind.SetAudioRouting,
						sourceId: sourceA.ToString(), audioRoutingMode: 2)])]);
				await fixture.Commands.SaveShowControlCueListAsync(list);
				await fixture.Commands.SelectShowControlCueListAsync(list.CueListId);
				Assert.Equal(ShowControlExecutionState.Armed, (await fixture.Commands.ArmShowControlAsync()).Execution.State);
				return fixture;
			}
			catch { await fixture.DisposeAsync(); throw; }
		}

		public async Task<Reply> SendAsync(bool pipe, string operation, string requestId, string correlationId,
			string payloadJson, CancellationToken cancellationToken = default)
		{
			using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestsStop.Token);
			cancellationToken = requestStop.Token;
			if (!pipe)
			{
				var result = await Server.DispatchExternalAsync(operation, requestId, correlationId, "request-test-client", payloadJson, cancellationToken);
				return new(result.ResponseType, result.CorrelationId, result.StateVersion, result.Sequence, result.PayloadJson, result.ErrorCode);
			}

			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(TimeSpan.FromSeconds(15));
			await using var connection = new NamedPipeClientStream(".", Server.Endpoint, PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await connection.ConnectAsync(deadline.Token);
			await WriteAsync(connection, "client.hello", Identity.New().ToString(), correlationId,
				JsonSerializer.SerializeToElement(new { protocolVersion = "1.0", role = "OperatorClient",
					hostInstanceId = "request-test-client", contractVersions = new Dictionary<string, string>
					{ ["control"] = ControlContractVersion.Current.ToString() } }, JsonOptions), deadline.Token);
			using (var hello = await ReadAsync(connection, deadline.Token))
				Assert.Equal("server.hello", hello.RootElement.GetProperty("messageType").GetString());
			using var payload = JsonDocument.Parse(payloadJson);
			await WriteAsync(connection, operation, requestId, correlationId, payload.RootElement, deadline.Token);
			using var response = await ReadAsync(connection, deadline.Token);
			var envelope = response.RootElement;
			var type = envelope.GetProperty("messageType").GetString()!;
			return new(type, envelope.GetProperty("correlationId").GetString()!, envelope.GetProperty("stateVersion").GetUInt64(),
				envelope.GetProperty("sequence").GetUInt64(), envelope.GetProperty("payload").GetRawText(),
				type == "error" ? envelope.GetProperty("payload").GetProperty("code").GetString() : null);
		}

		private static async Task WriteAsync(Stream stream, string messageType, string requestId, string correlationId,
			JsonElement payload, CancellationToken cancellationToken)
		{
			var bytes = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = "1.0", messageType, requestId, correlationId,
				hostInstanceId = "request-test-client", sentAtUtc = DateTimeOffset.UtcNow, stateVersion = 0UL, sequence = 0UL, payload }, JsonOptions);
			var prefix = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
			await stream.WriteAsync(prefix, cancellationToken);
			await stream.WriteAsync(bytes, cancellationToken);
			await stream.FlushAsync(cancellationToken);
		}

		private static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken cancellationToken)
		{
			var prefix = new byte[4];
			await stream.ReadExactlyAsync(prefix, cancellationToken);
			var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
			Assert.InRange(length, 1u, 1024u * 1024u);
			var bytes = new byte[checked((int)length)];
			await stream.ReadExactlyAsync(bytes, cancellationToken);
			return JsonDocument.Parse(bytes);
		}

		public Task StopAsync() => (_stop ??= StopCoreAsync()).WaitAsync(Deadline);

		private async Task StopCoreAsync()
		{
			_requestsStop.Cancel();
			await Server.DisposeAsync();
		}

		public async ValueTask DisposeAsync()
		{
			try { await StopAsync(); }
			finally
			{
				Runtime.ReleaseBarrier();
				await _management.DisposeAsync();
				await _journal.DisposeAsync();
				_requestsStop.Dispose();
				Directory.Delete(_root, recursive: true);
			}
		}
	}

	private sealed class RouteBarrier(bool applyBeforeWait, bool failAfterWait)
	{
		public bool ApplyBeforeWait { get; } = applyBeforeWait;
		public bool FailAfterWait { get; } = failAfterWait;
		public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed class BarrierRuntimeTransport : IControlRuntimeTransportSeam
	{
		private readonly MediaSourceId _source;
		private RuntimeAudioProgramSnapshot _audioProgram;
		private RouteBarrier? _nextBarrier;
		private RouteBarrier? _barrier;
		private int _routeAttempts;
		private int _routeCalls;

		public BarrierRuntimeTransport(MediaSourceId source)
		{
			_source = source;
			_audioProgram = new(source, AudioStreamId.New(), 1, false, 0, 0, 0, false, "HEALTHY", ActiveAudioSourceId: source);
		}

		public bool IsConnected => true;
		public string HostInstanceId => "runtime-request-identity";
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => [];
		public int RouteAttempts => Volatile.Read(ref _routeAttempts);
		public int RouteCalls => Volatile.Read(ref _routeCalls);
		public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
		public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ProviderDescriptors);

		public RouteBarrier PauseNextRoute(bool applyBeforeWait = false, bool failAfterWait = false)
		{
			_barrier = new RouteBarrier(applyBeforeWait, failAfterWait);
			if (Interlocked.CompareExchange(ref _nextBarrier, _barrier, null) is not null)
				throw new InvalidOperationException("The previous route barrier has not been entered.");
			return _barrier;
		}

		public void ReleaseBarrier() => _barrier?.Release.TrySetResult(true);

		public ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(new RuntimeRemoteSnapshot(HostInstanceId,
				new RuntimeExecutionState(RuntimeContractVersion.Current, null, Revision.Initial, RuntimeExecutionStatus.Idle, null),
				null, null, 1, 0, 0, VideoFormat.Hd1080p50Rgba8,
				new Dictionary<MediaSourceId, string> { [_source] = "VALID" },
				new(false, null, 0, 0, false, 0, 0, 1), new Dictionary<MediaSourceId, RuntimeAudioInputSnapshot>(),
				Volatile.Read(ref _audioProgram), 1));
		}

		public async ValueTask<RuntimeAudioProgramSnapshot> SetAudioRoutingAsync(int mode, MediaSourceId? breakawaySourceId,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _routeAttempts);
			var barrier = Interlocked.Exchange(ref _nextBarrier, null);
			if (barrier?.ApplyBeforeWait == true) ApplyRoute(mode, breakawaySourceId);
			if (barrier is not null)
			{
				barrier.Entered.TrySetResult(true);
				await barrier.Release.Task.WaitAsync(cancellationToken);
				if (barrier.FailAfterWait) throw new TimeoutException("Runtime reply was lost after applying the route.");
			}
			cancellationToken.ThrowIfCancellationRequested();
			return barrier?.ApplyBeforeWait == true ? Volatile.Read(ref _audioProgram) : ApplyRoute(mode, breakawaySourceId);
		}

		private RuntimeAudioProgramSnapshot ApplyRoute(int mode, MediaSourceId? source)
		{
			Interlocked.Increment(ref _routeCalls);
			var current = Volatile.Read(ref _audioProgram);
			var updated = current with { RoutingMode = mode, RoutingRevision = current.RoutingRevision + 1,
				ActiveAudioSourceId = source ?? _source };
			Volatile.Write(ref _audioProgram, updated);
			return updated;
		}

		public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(PreparedExecutionContract preparedExecution,
			MediaSinkId programSinkId, RuntimeProgramTransitionIntent? transition, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<RuntimeRemoteApplyResult>(new InvalidOperationException("The fixture exercises governed audio routing."));
	}
}
