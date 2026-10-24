// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.IntegrationHost;

namespace rtaime.Tests.Unit;

public sealed partial class ProductionIntegrationGatewayTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public async Task Gateway_does_not_repeat_a_completed_action_when_client_or_gateway_refresh_fails(int failingRefresh)
	{
		var transport = new ProbeTransport
		{
			AfterCutSnapshotFailure = new ControlSnapshotRequiredException("A full snapshot is required."),
			AfterCutSnapshotFailureCountdown = failingRefresh
		};
		var client = new OperatorControlClient(transport);
		await using var gateway = new IntegrationGateway(ActionOutcomeOptions(), client);
		await gateway.StartAsync();

		await gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "cut"));

		Assert.Equal(1, transport.CutAttempts);
		Assert.Equal(1, transport.CutCalls);
		Assert.Equal(OperatorActionOutcome.Confirmed, Assert.Single(client.ActionHistory.Current).Outcome);
		Assert.Equal("DEGRADED", gateway.Health.State);
		Assert.Contains("completed, but observation failed", gateway.Health.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Gateway_does_not_repeat_an_action_when_the_completed_reply_exposes_a_new_host()
	{
		var transport = new ProbeTransport
		{
			AfterCutFailure = new RemoteHostSessionChangedException("previous", "replacement")
		};
		await using var gateway = new IntegrationGateway(ActionOutcomeOptions(), new OperatorControlClient(transport));
		await gateway.StartAsync();

		await gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "cut"));

		Assert.Equal(1, transport.CutAttempts);
		Assert.Equal(1, transport.CutCalls);
		Assert.Equal("DEGRADED", gateway.Health.State);
	}

	[Theory]
	[InlineData("snapshot")]
	[InlineData("pipe-epoch")]
	[InlineData("grpc-epoch")]
	[InlineData("revision")]
	public async Task Gateway_retries_only_a_confirmed_pre_execution_rejection(string kind)
	{
		var transport = new ProbeTransport
		{
			BeforeCutFailure = kind switch
			{
				"snapshot" => new ControlSnapshotRequiredException("A full snapshot is required."),
				"pipe-epoch" => new RemoteHostSessionChangedException("previous", "replacement", requestWasDispatched: false),
				"grpc-epoch" => new ExternalControlRequestException("control.host_epoch_mismatch", "Changed before dispatch."),
				_ => new ExternalControlRequestException("control.audio.routing.revision_conflict", "Stale revision.")
			}
		};
		await using var gateway = new IntegrationGateway(ActionOutcomeOptions(), new OperatorControlClient(transport));
		await gateway.StartAsync();

		await gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "cut"));

		Assert.Equal(2, transport.CutAttempts);
		Assert.Equal(1, transport.CutCalls);
		Assert.Equal("HEALTHY", gateway.Health.State);
	}

	[Theory]
	[InlineData("control.host_epoch_mismatch", true)]
	[InlineData("ipc.request_id_conflict", false)]
	[InlineData("unknown.revision_conflict", false)]
	public async Task Gateway_does_not_retry_uncertain_or_unclassified_rejections(string code, bool requestMayHaveExecuted)
	{
		var transport = new ProbeTransport
		{
			BeforeCutFailure = new ExternalControlRequestException(code, "Request rejected.", requestMayHaveExecuted)
		};
		await using var gateway = new IntegrationGateway(ActionOutcomeOptions(), new OperatorControlClient(transport));
		await gateway.StartAsync();

		await gateway.ProcessTriggerAsync(new IntegrationTrigger("reference", "cut"));

		Assert.Equal(1, transport.CutAttempts);
		Assert.Equal(0, transport.CutCalls);
		Assert.Equal("DEGRADED", gateway.Health.State);
	}

	private static IntegrationGatewayOptions ActionOutcomeOptions() =>
		GatewayOptions([Adapter("reference")],
		[
			new IntegrationMappingOptions
			{
				Id = "cut",
				AdapterId = "reference",
				TriggerKey = "cut",
				Action = new IntegrationActionOptions { Kind = IntegrationActionKind.Cut }
			}
		]) with { SnapshotMinimumIntervalMs = 5000 };
}
