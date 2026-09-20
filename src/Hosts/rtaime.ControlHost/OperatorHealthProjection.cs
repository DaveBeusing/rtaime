// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public static class OperatorHealthStates
{
	public const string Pass = "PASS";
	public const string Fail = "FAIL";
	public const string Unverified = "UNVERIFIED";
}

public sealed record OperatorHealthMetric
{
	public OperatorHealthMetric(string state, string detail)
	{
		if (state is not (OperatorHealthStates.Pass or OperatorHealthStates.Fail or OperatorHealthStates.Unverified))
			throw new ArgumentException("Health state must be PASS, FAIL or UNVERIFIED.", nameof(state));
		if (string.IsNullOrWhiteSpace(detail))
			throw new ArgumentException("Health detail is required.", nameof(detail));

		State = state;
		Detail = detail.Trim();
	}

	public string State { get; }
	public string Detail { get; }
}

public sealed record OperatorHealthProjectionSnapshot(
	OperatorHealthMetric Engine,
	OperatorHealthMetric Control,
	OperatorHealthMetric Runtime,
	OperatorHealthMetric Media,
	OperatorHealthMetric Provider,
	OperatorHealthMetric GpuProvider,
	string CurrentFormat,
	TimeSpan FrameTime,
	TimeSpan FrameBudget,
	ulong DroppedFrames,
	TimeSpan Uptime,
	string GpuUtilization,
	string Vram,
	DateTimeOffset ObservedAtUtc,
	string CpuDeviceName = "UNVERIFIED",
	string CpuUtilization = "UNVERIFIED",
	string SystemMemory = "UNVERIFIED",
	string GpuDeviceName = "UNVERIFIED",
	double? OutputFramesPerSecond = null,
	string AvSyncState = "UNAVAILABLE",
	string AvSyncEvent = "UNAVAILABLE",
	string AvSyncScheduledOffset = "UNAVAILABLE",
	string AvSyncSubmitOffset = "UNAVAILABLE",
	string AvSyncDrift = "UNAVAILABLE",
	string AvSyncDetail = "A/V sync diagnostics are unavailable.");

public static class OperatorHealthProjection
{
	public static OperatorHealthProjectionSnapshot Evaluate(
		RuntimeRemoteSnapshot? runtime,
		IReadOnlyList<ProviderDescriptor> providers,
		MediaDeckSnapshot? mediaDeck,
		bool controlAuthorityAvailable,
		DateTimeOffset observedAtUtc,
		bool runtimeObservationFresh = true)
	{
		ArgumentNullException.ThrowIfNull(providers);

		var control = controlAuthorityAvailable
			? Pass("Authoritative Control state is available.")
			: Fail("Authoritative Control state is unavailable.");
		var runtimeHealth = EvaluateRuntime(runtime, runtimeObservationFresh);
		var media = EvaluateMedia(runtime, mediaDeck, runtimeObservationFresh);
		var provider = EvaluateProviders(providers, runtime is not null, runtimeObservationFresh);
		var gpuProvider = EvaluateGpuProvider(providers, runtime is not null, runtimeObservationFresh);
		var engine = Combine(control, runtimeHealth, media, provider, gpuProvider);
		var performance = runtime?.Performance;

		return new OperatorHealthProjectionSnapshot(
			engine,
			control,
			runtimeHealth,
			media,
			provider,
			gpuProvider,
			runtime is null ? "UNVERIFIED" : FormatVideo(runtime.Format),
			performance?.LastFrameProcessingTime ?? TimeSpan.Zero,
			performance?.FrameBudget ?? TimeSpan.Zero,
			performance?.DroppedFrames ?? 0,
			performance?.Uptime ?? TimeSpan.Zero,
			FormatGpuUtilization(performance),
			FormatVram(performance),
			observedAtUtc.ToUniversalTime(),
			FormatCpuDeviceName(performance),
			FormatCpuUtilization(performance),
			FormatSystemMemory(performance),
			FormatGpuDeviceName(performance),
			performance?.OutputFramesPerSecond,
			FormatAvSyncState(runtime?.AvSyncDiagnostics),
			FormatAvSyncEvent(runtime?.AvSyncDiagnostics),
			FormatAvSyncValue(runtime?.AvSyncDiagnostics?.ScheduledVideoOffsetMilliseconds),
			FormatAvSyncValue(runtime?.AvSyncDiagnostics?.SubmitOffsetMilliseconds),
			FormatAvSyncValue(runtime?.AvSyncDiagnostics?.DriftFromBaselineMilliseconds),
			runtime?.AvSyncDiagnostics?.Detail ?? "A/V sync diagnostics are unavailable.");
	}

	private static OperatorHealthMetric EvaluateRuntime(RuntimeRemoteSnapshot? runtime, bool observationFresh)
	{
		if (runtime is null)
			return Fail("RuntimeHost is disconnected or its snapshot is unavailable.");
		if (!observationFresh)
			return Unverified("RuntimeHost refresh missed; recent runtime observations are being retained temporarily.");
		if (runtime.Runtime.Status != RuntimeExecutionStatus.Committed)
			return Fail($"Runtime execution state is {runtime.Runtime.Status}.");

		return runtime.TimingHealth switch
		{
			2 => Pass("Runtime execution is committed and timing qualification is healthy."),
			1 => Unverified("Runtime timing qualification is still recovering."),
			3 => Fail("Runtime timing qualification is degraded."),
			4 => Fail("Runtime timing qualification is unstable."),
			5 => Fail("Runtime timing qualification is lost."),
			_ => Unverified($"Runtime timing health value '{runtime.TimingHealth}' is not recognized.")
		};
	}

	private static OperatorHealthMetric EvaluateMedia(RuntimeRemoteSnapshot? runtime, MediaDeckSnapshot? mediaDeck, bool observationFresh)
	{
		if (runtime is null)
			return Fail("Media health cannot be observed while RuntimeHost is unavailable.");
		if (!observationFresh)
			return Unverified("Media health is based on a recent retained Runtime observation.");

		var signals = runtime.InputSignals.Values.Select(value => value.Trim().ToUpperInvariant()).ToArray();
		if (signals.Any(value => value == "LOST"))
			return Fail("At least one Runtime media input reports LOST.");
		if (signals.Any(value => value is "UNSTABLE" or "RECOVERING" or "UNKNOWN"))
			return Unverified("At least one Runtime media input is unstable, recovering or unknown.");

		var audio = runtime.AudioProgram.Health.Trim().ToUpperInvariant();
		if (audio is "ERROR" or "UNDERRUN" or "CLIPPING")
			return Fail($"Program audio reports {audio}.");
		if (audio == "SILENCE")
			return Unverified("Program audio reports SILENCE; intent cannot be inferred from telemetry alone.");

		if (mediaDeck?.State == MediaDeckState.Error)
			return Fail(mediaDeck.Failure?.Message ?? "Media Deck reports an error.");

		return Pass("Runtime media inputs and Program audio observations are healthy.");
	}

	private static OperatorHealthMetric EvaluateProviders(IReadOnlyList<ProviderDescriptor> providers, bool runtimeAvailable, bool observationFresh)
	{
		if (!runtimeAvailable)
			return Fail("Provider health cannot be refreshed while RuntimeHost is unavailable.");
		if (!observationFresh)
			return Unverified("Provider health is based on a recent retained Runtime observation.");
		if (providers.Count == 0)
			return Unverified("Runtime provider inventory has not been synchronized.");

		var unavailable = providers.FirstOrDefault(provider => provider.Availability.State == ProviderAvailabilityState.Unavailable);
		if (unavailable is not null)
			return Fail($"{unavailable.Name}: {unavailable.Availability.Failure?.Message ?? "unavailable"}");

		var degraded = providers.FirstOrDefault(provider => provider.Availability.State == ProviderAvailabilityState.Degraded);
		if (degraded is not null)
			return Unverified($"{degraded.Name}: {degraded.Availability.Failure?.Message ?? "degraded without qualification evidence"}");

		return Pass($"{providers.Count} Runtime provider(s) report Available.");
	}

	private static OperatorHealthMetric EvaluateGpuProvider(IReadOnlyList<ProviderDescriptor> providers, bool runtimeAvailable, bool observationFresh)
	{
		if (!runtimeAvailable)
			return Fail("GPU provider health cannot be refreshed while RuntimeHost is unavailable.");
		if (!observationFresh)
			return Unverified("GPU provider health is based on a recent retained Runtime observation.");

		var gpu = providers.FirstOrDefault(provider =>
			provider.Capabilities.Any(capability => capability.Kind.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)) ||
			provider.Resources.Any(resource => resource.Kind.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)));
		if (gpu is null)
			return Fail("No GPU processing provider is present in the Runtime provider inventory.");

		return gpu.Availability.State switch
		{
			ProviderAvailabilityState.Available => Pass($"{gpu.Name} reports Available."),
			ProviderAvailabilityState.Degraded => Unverified($"{gpu.Name}: {gpu.Availability.Failure?.Message ?? "degraded without qualification evidence"}"),
			ProviderAvailabilityState.Unavailable => Fail($"{gpu.Name}: {gpu.Availability.Failure?.Message ?? "unavailable"}"),
			_ => Unverified($"{gpu.Name} returned an unknown availability state.")
		};
	}

	private static OperatorHealthMetric Combine(params OperatorHealthMetric[] metrics)
	{
		var failed = metrics.FirstOrDefault(metric => metric.State == OperatorHealthStates.Fail);
		if (failed is not null)
			return Fail($"One or more required subsystems failed. First failure: {failed.Detail}");

		var unverified = metrics.FirstOrDefault(metric => metric.State == OperatorHealthStates.Unverified);
		if (unverified is not null)
			return Unverified($"Runtime is operating, but at least one required subsystem lacks PASS evidence. {unverified.Detail}");

		return Pass("Control, Runtime, Media and required providers all have PASS evidence.");
	}

	private static string FormatAvSyncState(RuntimeAvSyncDiagnosticsSnapshot? diagnostics) =>
		diagnostics is { Enabled: true }
			? diagnostics.State
			: "UNAVAILABLE";

	private static string FormatAvSyncEvent(RuntimeAvSyncDiagnosticsSnapshot? diagnostics) =>
		diagnostics is { Enabled: true, EventId: { } eventId }
			? diagnostics.ExpectedMediaTime is { Length: > 0 } mediaTime
				? $"{eventId} @ {mediaTime} s"
				: eventId.ToString()
			: "UNAVAILABLE";

	private static string FormatAvSyncValue(double? milliseconds) =>
		milliseconds is { } value && double.IsFinite(value)
			? $"{value:0.###} ms"
			: "UNAVAILABLE";

	private static string FormatGpuUtilization(RuntimePerformanceSnapshot? performance) =>
		performance?.GpuUtilizationPercent is { } utilization
			? $"{utilization:0.#}%"
			: "UNVERIFIED";

	private static string FormatCpuDeviceName(RuntimePerformanceSnapshot? performance)
	{
		if (performance is null || string.IsNullOrWhiteSpace(performance.CpuDeviceName) || performance.CpuDeviceName == "UNVERIFIED")
			return "UNVERIFIED";

		return performance.CpuLogicalProcessorCount > 0
			? $"{performance.CpuDeviceName} · {performance.CpuLogicalProcessorCount} logical"
			: performance.CpuDeviceName;
	}

	private static string FormatCpuUtilization(RuntimePerformanceSnapshot? performance) =>
		performance?.CpuUtilizationPercent is { } utilization
			? $"{utilization:0.#}%"
			: "UNVERIFIED";

	private static string FormatSystemMemory(RuntimePerformanceSnapshot? performance)
	{
		if (performance?.SystemMemoryUsedBytes is { } used && performance.SystemMemoryTotalBytes is { } total && total > 0)
			return $"{used * 100d / total:0.#}% · {FormatBytes(used)} / {FormatBytes(total)}";
		if (performance?.SystemMemoryTotalBytes is { } capacity)
			return $"usage UNVERIFIED / {FormatBytes(capacity)} total";
		return "UNVERIFIED";
	}

	private static string FormatGpuDeviceName(RuntimePerformanceSnapshot? performance)
	{
		if (performance is null)
			return "UNVERIFIED";
		if (!string.IsNullOrWhiteSpace(performance.PhysicalGpuDeviceName) && performance.PhysicalGpuDeviceName != "UNVERIFIED")
			return performance.PhysicalGpuDeviceName;
		if (performance.GpuHardwareAccelerated && !string.IsNullOrWhiteSpace(performance.GpuDeviceName))
			return performance.GpuDeviceName;
		return "UNVERIFIED";
	}

	private static string FormatVram(RuntimePerformanceSnapshot? performance)
	{
		if (performance?.GpuVramUsedBytes is { } used && performance.GpuVramTotalBytes is { } total)
			return $"{FormatBytes(used)} / {FormatBytes(total)}";
		if (performance?.GpuVramTotalBytes is { } capacity)
			return $"usage UNVERIFIED / {FormatBytes(capacity)} total";
		return "UNVERIFIED";
	}

	private static string FormatVideo(VideoFormat format) =>
		$"{format.Width}×{format.Height} {format.FrameRate} {format.PixelFormat}";

	private static string FormatBytes(ulong bytes)
	{
		const double gib = 1024d * 1024d * 1024d;
		return $"{bytes / gib:0.00} GiB";
	}

	private static OperatorHealthMetric Pass(string detail) => new(OperatorHealthStates.Pass, detail);
	private static OperatorHealthMetric Fail(string detail) => new(OperatorHealthStates.Fail, detail);
	private static OperatorHealthMetric Unverified(string detail) => new(OperatorHealthStates.Unverified, detail);
}
