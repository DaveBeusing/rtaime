// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
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
		var timing = process.TimingQualification;
		var build = ProductBuildInfo.FromAssembly(typeof(RuntimeHostProcess).Assembly);
		var builder = new SupportSnapshotBuilder(
			build,
			"RuntimeHost",
			Environment.ProcessId,
			lifecycle.State.ToString(),
			lifecycle.Health.ToString())
			.Status("lifecycle.detail", lifecycle.Detail)
			.Status("ipc.running", (process.IpcServer?.Running ?? false).ToString())
			.Status("ipc.listenerState", process.IpcServer?.Listener.State.ToString() ?? "UNAVAILABLE")
			.Status("ipc.listenerFailure", process.IpcServer?.Listener.FailureDetail ?? "none")
			.Status("monitoring.running", (process.MonitoringServer?.Running ?? false).ToString())
			.Status("monitoring.listenerState", process.MonitoringServer?.Listener.State.ToString() ?? "UNAVAILABLE")
			.Status("monitoring.listenerFailure", process.MonitoringServer?.Listener.FailureDetail ?? "none")
			.Status("timing.qualificationState", timing.State.ToString())
			.Status("timing.maximumObservedJitterMs", timing.MaximumObservedJitter.TotalMilliseconds.ToString("F6", CultureInfo.InvariantCulture))
			.Status("timing.maximumObservedProcessingMs", timing.MaximumObservedProcessingDuration.TotalMilliseconds.ToString("F6", CultureInfo.InvariantCulture))
			.Counter("timing.totalBoundaries", ToCounter(timing.TotalBoundaries))
			.Counter("timing.sequenceDiscontinuities", ToCounter(timing.SequenceDiscontinuities))
			.Counter("timing.jitterViolations", ToCounter(timing.JitterViolations))
			.Counter("timing.processingViolations", ToCounter(timing.ProcessingViolations))
			.Counter("timing.consecutiveViolations", timing.ConsecutiveViolations)
			.Configuration("monitoringEndpoint", process.MonitoringEndpoint);

		if (process.IpcServer is { } ipc)
		{
			builder.Identity("hostInstanceId", ipc.HostInstanceId)
				.Configuration("runtimeEndpoint", ipc.Endpoint);
		}

		if (process.MediaIoStatistics is { } mediaIo)
		{
			builder.Counter("mediaIo.capturedA", ToCounter(mediaIo.CapturedA))
				.Counter("mediaIo.capturedB", ToCounter(mediaIo.CapturedB))
				.Counter("mediaIo.captureWouldBlock", ToCounter(mediaIo.CaptureWouldBlock))
				.Counter("mediaIo.captureFailures", ToCounter(mediaIo.CaptureFailures))
				.Counter("mediaIo.outputAccepted", ToCounter(mediaIo.OutputAccepted))
				.Counter("mediaIo.outputBackpressure", ToCounter(mediaIo.OutputBackpressure))
				.Counter("mediaIo.outputRejected", ToCounter(mediaIo.OutputRejected));
		}

		if (process.MediaIoInputAStatus is { } inputA)
			builder.Status("mediaIo.inputA.signal", inputA.SignalState.ToString());
		if (process.MediaIoInputBStatus is { } inputB)
			builder.Status("mediaIo.inputB.signal", inputB.SignalState.ToString());
		if (process.MediaIoProgramOutputStatus is { } programOutput)
			builder.Status("mediaIo.programOutput.signal", programOutput.SignalState.ToString());

		if (process.Runtime is { } runtime)
		{
			var snapshot = runtime.Snapshot;
			var monitorTap = runtime.MonitoringStatistics;
			var monitorHub = runtime.MonitoringHub.Statistics;
			var recording = snapshot.Recording;
			var performance = snapshot.Performance;
			builder.Status("runtime.executionStatus", snapshot.Runtime.Status.ToString())
				.Status("runtime.executionRevision", snapshot.Runtime.ExecutionRevision.ToString())
				.Status("runtime.activeExecutionId", snapshot.Runtime.ActiveExecutionId?.ToString() ?? "none")
				.Status("timing.health", snapshot.TimingHealth.ToString())
				.Status("graphics.visualLayerMode", snapshot.VisualLayerMode.ToString())
				.Status("recording.state", recording.State.ToString())
				.Status("format.width", runtime.Format.Width.ToString())
				.Status("format.height", runtime.Format.Height.ToString())
				.Status("format.frameRate", runtime.Format.FrameRate.ToString())
				.Status("performance.frameBudgetMs", performance.FrameBudget.TotalMilliseconds.ToString("F6", CultureInfo.InvariantCulture))
				.Status("performance.lastFrameProcessingMs", performance.LastFrameProcessingTime.TotalMilliseconds.ToString("F6", CultureInfo.InvariantCulture))
				.Status("performance.outputFramesPerSecond", performance.OutputFramesPerSecond?.ToString("F3", CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("performance.uptime", performance.Uptime.ToString())
				.Status("gpu.deviceName", performance.GpuDeviceName)
				.Status("gpu.hardwareAccelerated", performance.GpuHardwareAccelerated.ToString())
				.Status("gpu.utilizationPercent", performance.GpuUtilizationPercent?.ToString("F3", CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("gpu.vramUsedBytes", performance.GpuVramUsedBytes?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("gpu.vramTotalBytes", performance.GpuVramTotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("gpu.telemetryEvidence", performance.GpuTelemetryEvidence)
				.Status("gpu.physicalDeviceName", performance.PhysicalGpuDeviceName)
				.Status("cpu.deviceName", performance.CpuDeviceName)
				.Status("cpu.logicalProcessors", performance.CpuLogicalProcessorCount.ToString(CultureInfo.InvariantCulture))
				.Status("cpu.utilizationPercent", performance.CpuUtilizationPercent?.ToString("F3", CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("memory.usedBytes", performance.SystemMemoryUsedBytes?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("memory.totalBytes", performance.SystemMemoryTotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED")
				.Status("system.telemetryEvidence", performance.SystemTelemetryEvidence)
				.Counter("runtime.nextSequenceNumber", ToCounter(snapshot.NextSequenceNumber))
				.Counter("runtime.droppedFrames", ToCounter(performance.DroppedFrames))
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

			if (snapshot.AvSyncDiagnostics is { } avSync)
			{
				builder.Status("avSync.enabled", avSync.Enabled.ToString())
					.Status("avSync.state", avSync.State)
					.Status("avSync.expectedMediaTime", avSync.ExpectedMediaTime ?? "UNAVAILABLE")
					.Status("avSync.scheduledVideoOffsetMs", avSync.ScheduledVideoOffsetMilliseconds?.ToString("F6", CultureInfo.InvariantCulture) ?? "UNAVAILABLE")
					.Status("avSync.submitOffsetMs", avSync.SubmitOffsetMilliseconds?.ToString("F6", CultureInfo.InvariantCulture) ?? "UNAVAILABLE")
					.Status("avSync.driftFromBaselineMs", avSync.DriftFromBaselineMilliseconds?.ToString("F6", CultureInfo.InvariantCulture) ?? "UNAVAILABLE")
					.Status("avSync.detail", avSync.Detail);
				if (avSync.EventId is { } eventId)
					builder.Counter("avSync.eventId", ToCounter(eventId));
				if (avSync.TargetVideoFrameSequence is { } targetFrame)
					builder.Counter("avSync.targetVideoFrame", ToCounter(targetFrame));
				if (avSync.TargetAudioSamplePosition is { } targetSample)
					builder.Counter("avSync.targetAudioSample", ToCounter(targetSample));
			}

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
