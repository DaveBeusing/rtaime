// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
extern alias ControlHostWire;

using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.IntegrationHost;
using EpochWire = ControlHostWire::Rtaime.ExternalControl.V1;

namespace rtaime.Tests.Integration;

public sealed partial class ExternalControlIntegrationTests
{
	[Fact]
	public async Task Gateway_successful_GO_on_a_replacement_legacy_host_does_not_advance_a_second_cue()
	{
		await using var first = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var replacement = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		var independent = await ArmTwoEpochTestCuesAsync(replacement);
		var bridgeState = new GatewayEpochBridgeState(first.Control.IpcServer!, loseFirstReply: false);
		await using var bridge = await GatewayEpochBridgeFixture.StartAsync(bridgeState);
		await using var grpc = bridge.CreateClient();
		await using var gateway = new IntegrationGateway(EpochGatewayOptions(), new OperatorControlClient(grpc));
		await gateway.StartAsync();
		var observedHost = grpc.HostInstanceId;

		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var trigger = gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "go"), deadline.Token).AsTask();
		try
		{
			// The mapping owns the client gate here; the snapshot pump cannot
			// observe the replacement before this in-flight command receives it.
			await bridgeState.FirstGoBoundary.Task.WaitAsync(deadline.Token);
			Assert.Equal(first.Control.IpcServer!.HostInstanceId, observedHost);
			bridgeState.Dispatcher = replacement.Control.IpcServer!;
			bridgeState.ReleaseFirstGo.TrySetResult();
			await trigger.WaitAsync(deadline.Token);

			var workspace = await independent.GetShowControlSnapshotAsync(deadline.Token);
			Assert.Equal(1, workspace.Execution.CueIndex);
			Assert.Equal(ShowControlExecutionState.Armed, workspace.Execution.State);
			var request = Assert.Single(bridgeState.GoRequests);
			Assert.Equal(observedHost, request.ExpectedHostInstanceId);
			Assert.Equal(1, bridgeState.SuccessfulActions);
			Assert.Equal("DEGRADED", gateway.Health.State);
		}
		finally
		{
			bridgeState.ReleaseFirstGo.TrySetResult();
		}
	}

	[Fact]
	public async Task Gateway_lost_GO_reply_does_not_resend_to_a_legacy_replacement()
	{
		await using var first = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var replacement = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		var firstOperator = await ArmTwoEpochTestCuesAsync(first);
		var replacementOperator = await ArmTwoEpochTestCuesAsync(replacement);
		var bridgeState = new GatewayEpochBridgeState(first.Control.IpcServer!, loseFirstReply: true)
		{
			EnforceEpochFence = false
		};
		await using var bridge = await GatewayEpochBridgeFixture.StartAsync(bridgeState);
		await using var grpc = bridge.CreateClient();
		await using var gateway = new IntegrationGateway(EpochGatewayOptions(), new OperatorControlClient(grpc));
		await gateway.StartAsync();
		var observedHost = grpc.HostInstanceId;

		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var trigger = gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "go"), deadline.Token).AsTask();
		try
		{
			await bridgeState.FirstGoBoundary.Task.WaitAsync(deadline.Token);
			Assert.Equal(1, (await firstOperator.GetShowControlSnapshotAsync(deadline.Token)).Execution.CueIndex);
			bridgeState.Dispatcher = replacement.Control.IpcServer!;
			bridgeState.ReleaseFirstGo.TrySetResult();
			await trigger.WaitAsync(deadline.Token);

			var requests = bridgeState.GoRequests;
			Assert.Equal(observedHost, Assert.Single(requests).ExpectedHostInstanceId);
			Assert.Equal(1, bridgeState.SuccessfulActions);
			Assert.Equal(0, (await replacementOperator.GetShowControlSnapshotAsync(deadline.Token)).Execution.CueIndex);
			Assert.Equal("DEGRADED", gateway.Health.State);
		}
		finally
		{
			bridgeState.ReleaseFirstGo.TrySetResult();
		}
	}

	[Fact]
	public async Task Gateway_resynchronizes_and_executes_once_after_a_confirmed_pre_dispatch_epoch_rejection()
	{
		await using var first = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var replacement = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		var independent = await ArmTwoEpochTestCuesAsync(replacement);
		var bridgeState = new GatewayEpochBridgeState(first.Control.IpcServer!, loseFirstReply: false)
		{
			EnforceEpochFence = true
		};
		await using var bridge = await GatewayEpochBridgeFixture.StartAsync(bridgeState);
		await using var grpc = bridge.CreateClient();
		await using var gateway = new IntegrationGateway(EpochGatewayOptions(), new OperatorControlClient(grpc));
		await gateway.StartAsync();
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var trigger = gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "go"), deadline.Token).AsTask();
		try
		{
			await bridgeState.FirstGoBoundary.Task.WaitAsync(deadline.Token);
			bridgeState.Dispatcher = replacement.Control.IpcServer!;
			bridgeState.ReleaseFirstGo.TrySetResult();
			await trigger.WaitAsync(deadline.Token);

			var requests = bridgeState.GoRequests;
			Assert.Equal(2, requests.Length);
			Assert.NotEqual(requests[0].RequestId, requests[1].RequestId);
			Assert.Equal(first.Control.IpcServer!.HostInstanceId, requests[0].ExpectedHostInstanceId);
			Assert.Equal(replacement.Control.IpcServer!.HostInstanceId, requests[1].ExpectedHostInstanceId);
			Assert.Equal(1, bridgeState.SuccessfulActions);
			Assert.Equal(1, (await independent.GetShowControlSnapshotAsync(deadline.Token)).Execution.CueIndex);
			Assert.Equal("HEALTHY", gateway.Health.State);
		}
		finally
		{
			bridgeState.ReleaseFirstGo.TrySetResult();
		}
	}

	[Fact]
	public async Task Server_epoch_fence_rejects_before_dispatch_and_legacy_context_remains_compatible()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		var independent = await ArmTwoEpochTestCuesAsync(fixture);
		using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{fixture.Port}", new GrpcChannelOptions
		{
			HttpHandler = new HttpClientHandler
			{
				ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
			}
		});
		var request = new EpochWire.ExternalControlRequest
		{
			Context = new EpochWire.RequestContext
			{
				ApiVersion = "1.0", RequestId = Guid.NewGuid().ToString(), CorrelationId = Guid.NewGuid().ToString(),
				ClientName = "epoch-test", ClientVersion = "1.0", ExpectedHostInstanceId = Guid.NewGuid().ToString()
			},
			GoShowControl = new EpochWire.EmptyRequest()
		};
		var method = new Method<EpochWire.ExternalControlRequest, EpochWire.ExternalControlReply>(
			MethodType.Unary, "rtaime.external_control.v1.ExternalControl", "Execute",
			Marshallers.Create(value => value.ToByteArray(), EpochWire.ExternalControlRequest.Parser.ParseFrom),
			Marshallers.Create(value => value.ToByteArray(), EpochWire.ExternalControlReply.Parser.ParseFrom));
		async Task<EpochWire.ExternalControlReply> SendAsync() =>
			await channel.CreateCallInvoker().AsyncUnaryCall(method, null,
				new CallOptions(fixture.AuthenticatedHeaders, DateTime.UtcNow.AddSeconds(5)), request).ResponseAsync;

		var stale = await SendAsync();
		Assert.Equal("control.host_epoch_mismatch", stale.Error.Code);
		Assert.Equal(request.Context.RequestId, stale.RequestId);
		Assert.Equal(0, (await independent.GetShowControlSnapshotAsync()).Execution.CueIndex);

		request.Context.ExpectedHostInstanceId = "invalid-host";
		var invalid = await Assert.ThrowsAsync<RpcException>(SendAsync);
		Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);
		Assert.Equal(0, (await independent.GetShowControlSnapshotAsync()).Execution.CueIndex);

		// Reuse this identity: the stale request must not have reserved or executed it.
		request.Context.ExpectedHostInstanceId = string.Empty;
		var legacy = await SendAsync();
		Assert.Null(legacy.Error);
		Assert.Equal(1, (await independent.GetShowControlSnapshotAsync()).Execution.CueIndex);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Grpc_uncertain_action_is_not_resent_with_or_without_an_initial_snapshot(bool synchronizeFirst)
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		var independent = await ArmTwoEpochTestCuesAsync(fixture);
		var bridgeState = new GatewayEpochBridgeState(fixture.Control.IpcServer!, loseFirstReply: true);
		await using var bridge = await GatewayEpochBridgeFixture.StartAsync(bridgeState);
		await using var grpc = bridge.CreateClient();
		if (synchronizeFirst) await grpc.GetSnapshotAsync();
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var action = grpc.GoShowControlAsync(deadline.Token).AsTask();
		try
		{
			await bridgeState.FirstGoBoundary.Task.WaitAsync(deadline.Token);
			bridgeState.ReleaseFirstGo.TrySetResult();
			var failure = await Assert.ThrowsAsync<RpcException>(() => action);
			Assert.Equal(StatusCode.Unavailable, failure.StatusCode);
			Assert.True(grpc.RequiresFullSnapshot);
			await Assert.ThrowsAsync<ControlSnapshotRequiredException>(() => grpc.GoShowControlAsync(deadline.Token).AsTask());
			Assert.Single(bridgeState.GoRequests);
			Assert.Equal(1, bridgeState.SuccessfulActions);
			Assert.Equal(1, (await independent.GetShowControlSnapshotAsync(deadline.Token)).Execution.CueIndex);
		}
		finally
		{
			bridgeState.ReleaseFirstGo.TrySetResult();
		}
	}

	private static async Task<NamedPipeOperatorControlTransport> ArmTwoEpochTestCuesAsync(ExternalFixture fixture)
	{
		var transport = new NamedPipeOperatorControlTransport(fixture.ControlEndpoint);
		var snapshot = await transport.GetSnapshotAsync();
		var cueList = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), "Epoch test",
		[
			new ShowControlCue(ShowControlCueId.New(), "First take",
			[
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: snapshot.Sources[1].Id),
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.Cut)
			]),
			new ShowControlCue(ShowControlCueId.New(), "Second take",
			[
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: snapshot.Sources[0].Id),
				new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.Cut)
			])
		]);
		await transport.SaveShowControlCueListAsync(cueList);
		await transport.SelectShowControlCueListAsync(cueList.CueListId);
		Assert.Equal(ShowControlExecutionState.Armed, (await transport.ArmShowControlAsync()).Execution.State);
		return transport;
	}

	private static IntegrationGatewayOptions EpochGatewayOptions() => new()
	{
		Enabled = true, TestMode = true, SnapshotMinimumIntervalMs = 100,
		Upstream = new IntegrationUpstreamOptions { Endpoint = "https://127.0.0.1:55101" },
		Adapters = [new IntegrationAdapterOptions { Id = "reference", Kind = IntegrationAdapterKind.Discrete, Discrete = new() }],
		Mappings = [new IntegrationMappingOptions
		{
			Id = "go", AdapterId = "reference", TriggerKey = "go",
			Action = new IntegrationActionOptions { Kind = IntegrationActionKind.ShowControlGo }
		}]
	};

	public sealed class GatewayEpochBridgeState(ControlHostIpcServer dispatcher, bool loseFirstReply)
	{
		private ControlHostIpcServer _dispatcher = dispatcher;
		private readonly List<EpochWire.RequestContext> _goRequests = [];
		private int _successfulActions;
		public bool LoseFirstReply { get; } = loseFirstReply;
		public bool EnforceEpochFence { get; init; } = loseFirstReply;
		public ControlHostIpcServer Dispatcher { get => Volatile.Read(ref _dispatcher); set => Volatile.Write(ref _dispatcher, value); }
		public TaskCompletionSource FirstGoBoundary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirstGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public EpochWire.RequestContext[] GoRequests { get { lock (_goRequests) return _goRequests.ToArray(); } }
		public int SuccessfulActions => Volatile.Read(ref _successfulActions);
		public void RecordAction() => Interlocked.Increment(ref _successfulActions);
		public int RecordRequest(EpochWire.RequestContext context)
		{
			lock (_goRequests) { _goRequests.Add(context.Clone()); return _goRequests.Count; }
		}
	}

	public sealed class GatewayEpochBridgeService(GatewayEpochBridgeState state) : EpochWire.ExternalControl.ExternalControlBase
	{
		public override async Task<EpochWire.ExternalControlReply> Execute(EpochWire.ExternalControlRequest request, ServerCallContext context)
		{
			var isGo = request.OperationCase == EpochWire.ExternalControlRequest.OperationOneofCase.GoShowControl;
			var ordinal = isGo ? state.RecordRequest(request.Context) : 0;
			if (ordinal == 1 && !state.LoseFirstReply)
			{
				state.FirstGoBoundary.TrySetResult();
				await state.ReleaseFirstGo.Task.WaitAsync(context.CancellationToken);
			}
			var dispatcher = state.Dispatcher;
			if (state.EnforceEpochFence && isGo && !string.IsNullOrEmpty(request.Context.ExpectedHostInstanceId) &&
				request.Context.ExpectedHostInstanceId != dispatcher.HostInstanceId)
				return new EpochWire.ExternalControlReply
				{
					ApiVersion = "1.0", RequestId = request.Context.RequestId, CorrelationId = request.Context.CorrelationId,
					HostInstanceId = dispatcher.HostInstanceId, StateVersion = dispatcher.StateVersion,
					Error = new() { Code = "control.host_epoch_mismatch", Message = "Replacement rejected before dispatch." }
				};
			if (!isGo && request.OperationCase != EpochWire.ExternalControlRequest.OperationOneofCase.Snapshot)
				throw new RpcException(new Status(StatusCode.Unimplemented, "Unexpected test operation."));
			var result = await dispatcher.DispatchExternalAsync(isGo ? "control.show_control.go" : "control.snapshot.get",
				request.Context.RequestId, request.Context.CorrelationId, "epoch-bridge", "{}", context.CancellationToken);
			if (isGo && result.ErrorCode is null) state.RecordAction();
			if (ordinal == 1 && state.LoseFirstReply)
			{
				state.FirstGoBoundary.TrySetResult();
				await state.ReleaseFirstGo.Task.WaitAsync(context.CancellationToken);
				throw new RpcException(new Status(StatusCode.Unavailable, "Successful reply was lost."));
			}
			var reply = new EpochWire.ExternalControlReply
			{
				ApiVersion = "1.0", ResponseType = result.ResponseType, RequestId = result.RequestId,
				CorrelationId = result.CorrelationId, HostInstanceId = result.HostInstanceId,
				StateVersion = result.StateVersion, Sequence = result.Sequence,
				PayloadJson = ByteString.CopyFromUtf8(result.PayloadJson),
				Error = result.ErrorCode is null ? null : new() { Code = result.ErrorCode, Message = result.ErrorMessage }
			};
			return reply;
		}
	}

	private sealed class GatewayEpochBridgeFixture(WebApplication app, string root, CertificateMaterial certificate, int port) : IAsyncDisposable
	{
		public static async Task<GatewayEpochBridgeFixture> StartAsync(GatewayEpochBridgeState state)
		{
			var root = Path.Combine(Path.GetTempPath(), "rtaime-gateway-epoch-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var certificate = CreateCertificate(root, "bridge", client: false);
			var port = FreePort();
			var builder = WebApplication.CreateBuilder();
			builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port, listen =>
			{
				listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
				listen.UseHttps(certificate.Path, certificate.Password);
			}));
			builder.Services.AddGrpc();
			builder.Services.AddSingleton(state);
			var app = builder.Build();
			app.MapGrpcService<GatewayEpochBridgeService>();
			await app.StartAsync();
			return new(app, root, certificate, port);
		}
		public GrpcOperatorControlTransport CreateClient() => new(new GrpcOperatorControlOptions
		{
			Endpoint = new Uri("https://localhost:" + port), TrustMode = ExternalControlTrustMode.PinnedServerCertificate,
			TrustedServerCertificateThumbprint = certificate.Thumbprint, RequestTimeout = TimeSpan.FromSeconds(10)
		});
		public async ValueTask DisposeAsync()
		{
			await app.StopAsync();
			await app.DisposeAsync();
			try { Directory.Delete(root, recursive: true); } catch (IOException) { }
		}
	}
}
