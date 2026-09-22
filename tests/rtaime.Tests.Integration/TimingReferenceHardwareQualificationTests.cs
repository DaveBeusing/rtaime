// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class TimingReferenceHardwareQualificationTests
{
	[Fact]
	public async Task Reference_loss_relock_host_cycle_and_soak_must_pass_when_explicitly_enabled()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("RTAIME_TIMING_REFERENCE_QUALIFICATION"), "1", StringComparison.Ordinal))
			return;

		var expectedAdapter = RequireEnvironment("RTAIME_TIMING_EXPECTED_ADAPTER");
		var expectedSdkRevision = RequireEnvironment("RTAIME_AJA_SDK_REVISION");
		var evidencePath = RequireEnvironment("RTAIME_TIMING_REFERENCE_EVIDENCE");
		var soakSeconds = ParsePositiveInt("RTAIME_TIMING_SOAK_SECONDS");
		var requireRelock = bool.Parse(RequireEnvironment("RTAIME_TIMING_REQUIRE_REFERENCE_RELOCK"));
		var maximumHostCycleP95Milliseconds = ParsePositiveDouble("RTAIME_TIMING_MAX_HOST_CYCLE_P95_MS");
		var formatName = Environment.GetEnvironmentVariable("RTAIME_TIMING_REFERENCE_FORMAT") ?? "1080p50";
		var format = ParseFormat(formatName);

		var sourceA = new MediaSourceId(Identity.Parse("72000000-0000-0000-0000-00000000000a"));
		var sourceB = new MediaSourceId(Identity.Parse("72000000-0000-0000-0000-00000000000b"));
		var adapter = new NativeMediaIoProviderAdapter();
		Assert.Contains(expectedAdapter, adapter.Metadata.AdapterName, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(expectedSdkRevision, adapter.Metadata.SdkRevision);
		Assert.False(string.IsNullOrWhiteSpace(adapter.Metadata.DriverVersion));

		await using var telemetryRuntime = new V1RuntimeHostService(
			sourceA,
			sourceB,
			format,
			new NullRecordingWriter());

		var hostCycleSamples = new List<double>();
		var seenInitialReferenceLock = false;
		var referenceLossObserved = false;
		var referenceRelockObserved = false;
		var telemetrySampleCount = 0;
		var telemetryCompleteSampleCount = 0;
		var cpuAvailableSamples = 0;
		var memoryAvailableSamples = 0;
		var gpuUtilizationAvailableSamples = 0;
		var vramAvailableSamples = 0;
		string? telemetryCpuDeviceName = null;
		string? telemetryGpuDeviceName = null;
		DateTimeOffset? telemetryFirstSampleAtUtc = null;
		DateTimeOffset? telemetryLastSampleAtUtc = null;
		MediaIoVerticalSliceStatistics statistics;
		MediaIoSignalState finalInputA;
		MediaIoSignalState finalInputB;
		MediaIoSignalState finalOutput;
		ulong? lastSubmittedCapture = null;

		using (var mediaIo = new MediaIoVerticalSlice(adapter, sourceA, sourceB, format, requireExternalReference: true))
		{
			var deadline = DateTimeOffset.UtcNow.AddSeconds(soakSeconds);
			var nextTelemetrySampleAt = DateTimeOffset.UtcNow.AddSeconds(1);
			while (DateTimeOffset.UtcNow < deadline)
			{
				var cycleStarted = Stopwatch.GetTimestamp();
				mediaIo.PumpInputs();
				var outputStatus = mediaIo.ProgramOutputStatus.SignalState;
				if (outputStatus == MediaIoSignalState.Locked)
				{
					if (referenceLossObserved)
						referenceRelockObserved = true;
					else
						seenInitialReferenceLock = true;
				}
				else if (seenInitialReferenceLock && outputStatus is MediaIoSignalState.Lost or MediaIoSignalState.Unstable)
				{
					referenceLossObserved = true;
				}

				var latestA = mediaIo.LatestA;
				var latestB = mediaIo.LatestB;
				if (latestA is not null &&
					latestB is not null &&
					latestA.PortStatus.SignalState == MediaIoSignalState.Locked &&
					latestB.PortStatus.SignalState == MediaIoSignalState.Locked &&
					lastSubmittedCapture != latestA.CaptureSequence)
				{
					var timing = new FrameTiming(
						latestA.CaptureSequence,
						checked((long)latestA.CaptureSequence),
						new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator));
					var submit = mediaIo.TrySubmitProgram(
						sourceA,
						timing,
						latestA.Video.Pixels.ToArray(),
						latestA.AudioSamples,
						latestA.AudioTiming);
					if (submit.Accepted)
					{
						lastSubmittedCapture = latestA.CaptureSequence;
						if (mediaIo.Statistics.OutputAccepted % 10UL == 0)
							hostCycleSamples.Add(Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds);
					}
					else
					{
						Assert.Equal("media.io.output.backpressure", submit.Failure?.Code);
					}
				}

				var now = DateTimeOffset.UtcNow;
				if (now >= nextTelemetrySampleAt)
				{
					var performance = telemetryRuntime.Snapshot.Performance;
					telemetrySampleCount++;
					telemetryFirstSampleAtUtc ??= now;
					telemetryLastSampleAtUtc = now;
					telemetryCpuDeviceName ??= performance.CpuDeviceName;
					if (!string.Equals(performance.PhysicalGpuDeviceName, "UNVERIFIED", StringComparison.OrdinalIgnoreCase))
						telemetryGpuDeviceName ??= performance.PhysicalGpuDeviceName;

					var cpuAvailable = performance.CpuUtilizationPercent is not null;
					var memoryAvailable = performance.SystemMemoryUsedBytes is not null && performance.SystemMemoryTotalBytes is not null;
					var gpuUtilizationAvailable = performance.GpuUtilizationPercent is not null;
					var vramAvailable = performance.GpuVramUsedBytes is not null && performance.GpuVramTotalBytes is not null;
					if (cpuAvailable) cpuAvailableSamples++;
					if (memoryAvailable) memoryAvailableSamples++;
					if (gpuUtilizationAvailable) gpuUtilizationAvailableSamples++;
					if (vramAvailable) vramAvailableSamples++;

					var systemEvidencePassed = performance.SystemTelemetryEvidence.StartsWith("PASS:", StringComparison.Ordinal);
					var gpuEvidencePassed = performance.GpuTelemetryEvidence.StartsWith("PASS:", StringComparison.Ordinal);
					if (cpuAvailable && memoryAvailable && gpuUtilizationAvailable && vramAvailable &&
						systemEvidencePassed && gpuEvidencePassed &&
						!string.Equals(performance.PhysicalGpuDeviceName, "UNVERIFIED", StringComparison.OrdinalIgnoreCase))
					{
						telemetryCompleteSampleCount++;
					}

					nextTelemetrySampleAt = now.AddSeconds(1);
				}

				Thread.Sleep(1);
			}

			statistics = mediaIo.Statistics;
			finalInputA = mediaIo.InputAStatus.SignalState;
			finalInputB = mediaIo.InputBStatus.SignalState;
			finalOutput = mediaIo.ProgramOutputStatus.SignalState;
		}

		var expectedFrames = format.FrameRate.Numerator / (double)format.FrameRate.Denominator * soakSeconds;
		var minimumContinuityFrames = checked((ulong)Math.Floor(expectedFrames * 0.85));
		var hostCycleP95 = Percentile(hostCycleSamples, 0.95);
		var hostCycleMaximum = hostCycleSamples.Count == 0 ? double.PositiveInfinity : hostCycleSamples.Max();
		var continuityPassed = statistics.CapturedA >= minimumContinuityFrames &&
			statistics.CapturedB >= minimumContinuityFrames &&
			statistics.OutputAccepted >= minimumContinuityFrames &&
			statistics.CaptureFailures == 0 &&
			statistics.OutputRejected == 0;
		var referencePassed = seenInitialReferenceLock && finalOutput == MediaIoSignalState.Locked &&
			(!requireRelock || (referenceLossObserved && referenceRelockObserved));
		var hostCyclePassed = hostCycleSamples.Count >= 10 && hostCycleP95 <= maximumHostCycleP95Milliseconds;
		var telemetryPassed = telemetrySampleCount >= 30 &&
			telemetryCompleteSampleCount == telemetrySampleCount &&
			!string.IsNullOrWhiteSpace(telemetryCpuDeviceName) &&
			!string.IsNullOrWhiteSpace(telemetryGpuDeviceName);
		var passed = continuityPassed && referencePassed && hostCyclePassed && telemetryPassed &&
			finalInputA == MediaIoSignalState.Locked && finalInputB == MediaIoSignalState.Locked;

		var evidence = new
		{
			copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
			schemaVersion = "1.0",
			status = passed ? "PASSED" : "FAILED",
			sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
			capturedAtUtc = DateTimeOffset.UtcNow,
			expectedAdapter,
			detectedAdapter = adapter.Metadata.AdapterName,
			driverVersion = adapter.Metadata.DriverVersion,
			ajaSdkRevision = adapter.Metadata.SdkRevision,
			format = formatName,
			soakSeconds,
			minimumContinuityFrames,
			reference = new
			{
				required = true,
				relockExerciseRequired = requireRelock,
				seenInitialReferenceLock,
				referenceLossObserved,
				referenceRelockObserved,
				finalOutput = finalOutput.ToString()
			},
			hostCycle = new
			{
				sampleCount = hostCycleSamples.Count,
				p95Milliseconds = hostCycleP95,
				maximumMilliseconds = hostCycleMaximum,
				maximumAllowedP95Milliseconds = maximumHostCycleP95Milliseconds
			},
			telemetry = new
			{
				status = telemetryPassed ? "PASSED" : "FAILED",
				sampleIntervalMilliseconds = 1000,
				sampleCount = telemetrySampleCount,
				completeSampleCount = telemetryCompleteSampleCount,
				cpuAvailableSamples,
				memoryAvailableSamples,
				gpuUtilizationAvailableSamples,
				vramAvailableSamples,
				cpuDeviceName = telemetryCpuDeviceName ?? "UNVERIFIED",
				gpuDeviceName = telemetryGpuDeviceName ?? "UNVERIFIED",
				firstSampleAtUtc = telemetryFirstSampleAtUtc,
				lastSampleAtUtc = telemetryLastSampleAtUtc
			},
			inputs = new
			{
				finalInputA = finalInputA.ToString(),
				finalInputB = finalInputB.ToString()
			},
			statistics
		};

		var fullEvidencePath = Path.GetFullPath(evidencePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)!);
		File.WriteAllText(
			fullEvidencePath,
			JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

		Assert.True(continuityPassed, $"Soak continuity failed. CapturedA={statistics.CapturedA}, CapturedB={statistics.CapturedB}, OutputAccepted={statistics.OutputAccepted}, minimum={minimumContinuityFrames}.");
		Assert.Equal(MediaIoSignalState.Locked, finalInputA);
		Assert.Equal(MediaIoSignalState.Locked, finalInputB);
		Assert.True(referencePassed, $"Reference qualification failed. initial={seenInitialReferenceLock}, loss={referenceLossObserved}, relock={referenceRelockObserved}, final={finalOutput}.");
		Assert.True(hostCyclePassed, $"Host-cycle p95 {hostCycleP95:F3} ms exceeded {maximumHostCycleP95Milliseconds:F3} ms or had insufficient samples.");
		Assert.True(telemetryPassed, $"Hardware telemetry continuity failed. complete={telemetryCompleteSampleCount}/{telemetrySampleCount}, cpu={cpuAvailableSamples}, memory={memoryAvailableSamples}, gpu={gpuUtilizationAvailableSamples}, vram={vramAvailableSamples}.");
	}

	private static VideoFormat ParseFormat(string value) => value.Trim().ToLowerInvariant() switch
	{
		"1080p50" => VideoFormat.Hd1080p50Rgba8,
		"1080p59.94" or "1080p59_94" => VideoFormat.Hd1080p59_94Rgba8,
		_ => throw new InvalidOperationException("Qualification format must be 1080p50 or 1080p59.94.")
	};

	private static int ParsePositiveInt(string name)
	{
		if (!int.TryParse(RequireEnvironment(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
			throw new InvalidOperationException($"Environment variable '{name}' must be a positive integer.");
		return value;
	}

	private static double ParsePositiveDouble(string name)
	{
		if (!double.TryParse(RequireEnvironment(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
			throw new InvalidOperationException($"Environment variable '{name}' must be a positive finite number.");
		return value;
	}

	private static double Percentile(IReadOnlyList<double> values, double percentile)
	{
		if (values.Count == 0)
			return double.PositiveInfinity;
		var sorted = values.OrderBy(value => value).ToArray();
		var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
		return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
	}

	private static string RequireEnvironment(string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value))
			throw new InvalidOperationException($"Environment variable '{name}' is required for physical timing qualification.");
		return value.Trim();
	}

	private sealed class NullRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
