// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.AIHost;

public static class AIHostDiagnostics
{
	private const int RetainedSupportEvents = 64;

	public static SupportSnapshot Capture(AIHostProcess process, DateTimeOffset? capturedAtUtc = null)
	{
		ArgumentNullException.ThrowIfNull(process);
		var captured = (capturedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
		var lifecycle = process.Lifecycle;
		var build = ProductBuildInfo.FromAssembly(typeof(AIHostProcess).Assembly);
		var builder = new SupportSnapshotBuilder(
			build,
			"AIHost",
			Environment.ProcessId,
			lifecycle.State.ToString(),
			lifecycle.Health.ToString())
			.Status("lifecycle.detail", lifecycle.Detail)
			.Status("ipc.running", (process.IpcServer?.Running ?? false).ToString());

		if (process.IpcServer is { } ipc)
		{
			builder.Identity("hostInstanceId", ipc.HostInstanceId)
				.Configuration("aiEndpoint", ipc.Endpoint);
		}

		if (process.Service is { } service)
		{
			var snapshot = service.Snapshot;
			builder.Status("ai.availability", snapshot.State.ToString())
				.Counter("ai.activeRequests", snapshot.ActiveRequests)
				.Counter("ai.activeExecutions", service.ActiveExecutions)
				.Counter("ai.reservedComputeUnits", snapshot.ReservedComputeUnits)
				.Counter("ai.reservedVramBytes", ToCounter(snapshot.ReservedVramBytes))
				.Counter("ai.completed", ToCounter(snapshot.Completed))
				.Counter("ai.rejected", ToCounter(snapshot.Rejected))
				.Counter("ai.timedOut", ToCounter(snapshot.TimedOut))
				.Counter("ai.cancelled", ToCounter(snapshot.Cancelled))
				.Counter("ai.failed", ToCounter(snapshot.Failed))
				.Counter("ai.providerCount", service.Runtime.Providers.Count)
				.Counter("ai.capabilityCount", service.Capabilities.Count)
				.Events(ToDiagnosticEvents(service.Runtime.Observations.TakeLast(RetainedSupportEvents)));
		}

		return builder.Build(captured);
	}

	public static string CaptureJson(AIHostProcess process, DateTimeOffset? capturedAtUtc = null) =>
		SupportSnapshotSerializer.Serialize(Capture(process, capturedAtUtc));

	private static IReadOnlyList<DiagnosticEvent> ToDiagnosticEvents(IEnumerable<rtaime.AI.AIExecutionObservation> observations) =>
		observations.Select((observation, index) => new DiagnosticEvent(
			(ulong)index,
			observation.ObservedAt.Value,
			observation.Failure is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
			"ai",
			observation.Code,
			$"Inference request {observation.RequestId} completed with status {observation.Status}.",
			observation.Failure is { } failure
				? new Failure(failure.Code, DiagnosticRedactor.RedactText(failure.Message))
				: null,
			new Dictionary<string, string>
			{
				["requestId"] = observation.RequestId.ToString(),
				["status"] = observation.Status.ToString()
			})).ToArray();

	private static long ToCounter(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
