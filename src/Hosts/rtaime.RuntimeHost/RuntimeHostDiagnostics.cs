// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.RuntimeHost;

public static class RuntimeHostDiagnostics
{
	private const int RetainedSupportEvents = 64;

	public static SupportSnapshot Capture(RuntimeHostProcess process, DateTimeOffset? capturedAtUtc = null)
	{
		ArgumentNullException.ThrowIfNull(process);
		var captured = (capturedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
		var lifecycle = process.Lifecycle;
		var build = ProductBuildInfo.FromAssembly(typeof(RuntimeHostProcess).Assembly);
		var builder = new SupportSnapshotBuilder(
			build,
			"RuntimeHost",
			Environment.ProcessId,
			lifecycle.State.ToString(),
			lifecycle.Health.ToString())
			.Status("lifecycle.detail", lifecycle.Detail)
			.Status("ipc.running", (process.IpcServer?.Running ?? false).ToString())
			.Status("monitoring.running", (process.MonitoringServer?.Running ?? false).ToString())
			.Configuration("monitoringEndpoint", process.MonitoringEndpoint);

		if (process.IpcServer is { } ipc)
		{
			builder.Identity("hostInstanceId", ipc.HostInstanceId)
				.Configuration("runtimeEndpoint", ipc.Endpoint);
		}

		if (process.Runtime is { } runtime)
		{
			var snapshot = runtime.Snapshot;
			var monitorTap = runtime.MonitoringStatistics;
			var monitorHub = runtime.MonitoringHub.Statistics;
			var recording = snapshot.Recording;
			builder.Status("runtime.executionStatus", snapshot.Runtime.Status.ToString())
				.Status("runtime.executionRevision", snapshot.Runtime.ExecutionRevision.ToString())
				.Status("runtime.activeExecutionId", snapshot.Runtime.ActiveExecutionId?.ToString() ?? "none")
				.Status("timing.health", snapshot.TimingHealth.ToString())
				.Status("graphics.visualLayerMode", snapshot.VisualLayerMode.ToString())
				.Status("recording.state", recording.State.ToString())
				.Status("format.width", runtime.Format.Width.ToString())
				.Status("format.height", runtime.Format.Height.ToString())
				.Status("format.frameRate", runtime.Format.FrameRate.ToString())
				.Counter("runtime.nextSequenceNumber", ToCounter(snapshot.NextSequenceNumber))
				.Counter("gpu.activeSurfaces", snapshot.ActiveGpuSurfaces)
				.Counter("monitoring.tapCaptured", ToCounter(monitorTap.Captured))
				.Counter("monitoring.tapDropped", ToCounter(monitorTap.DroppedBeforeProcessing))
				.Counter("monitoring.tapProcessed", ToCounter(monitorTap.Processed))
				.Counter("monitoring.hubPublished", ToCounter(monitorHub.Published))
				.Counter("monitoring.hubDropped", ToCounter(monitorHub.DroppedBySubscribers))
				.Counter("monitoring.subscribers", monitorHub.Subscribers)
				.Counter("recording.accepted", ToCounter(recording.Statistics.Accepted))
				.Counter("recording.written", ToCounter(recording.Statistics.Written))
				.Counter("recording.dropped", ToCounter(recording.Statistics.Dropped))
				.Counter("recording.rejected", ToCounter(recording.Statistics.Rejected))
				.Counter("recording.writerFailures", ToCounter(recording.Statistics.WriterFailures));

			foreach (var signal in snapshot.InputSignals.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
				builder.Status($"input.{signal.Key}.signal", signal.Value.ToString());

			builder.Events(ToDiagnosticEvents(runtime.Observations.TakeLast(RetainedSupportEvents), captured));
		}

		return builder.Build(captured);
	}

	public static string CaptureJson(RuntimeHostProcess process, DateTimeOffset? capturedAtUtc = null) =>
		SupportSnapshotSerializer.Serialize(Capture(process, capturedAtUtc));

	private static IReadOnlyList<DiagnosticEvent> ToDiagnosticEvents(IEnumerable<string> observations, DateTimeOffset capturedAtUtc)
	{
		ulong sequence = 0;
		return observations.Select(value => new DiagnosticEvent(
			sequence++,
			capturedAtUtc,
			DiagnosticSeverity.Information,
			"runtime",
			"runtime.observation",
			DiagnosticRedactor.RedactText(value),
			null,
			new Dictionary<string, string>())).ToArray();
	}

	private static long ToCounter(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
