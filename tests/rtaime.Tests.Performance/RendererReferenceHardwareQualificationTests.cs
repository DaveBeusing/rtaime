// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Performance;

public sealed class RendererReferenceHardwareQualificationTests
{
    [Fact]
    [Trait("Qualification", "RendererReferenceHardware")]
    public async Task Renderer_reference_profile_must_pass_when_explicitly_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RTAIME_RENDERER_REFERENCE_QUALIFICATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var expectedDevice = RequireEnvironment("RTAIME_RENDERER_REFERENCE_DEVICE");
        var deviceOrdinal = ParseNonNegativeInt("RTAIME_RENDERER_REFERENCE_DEVICE_ORDINAL", 0);
        var profileName = RequireEnvironment("RTAIME_RENDERER_REFERENCE_PROFILE");
        var durationSeconds = ParsePositiveInt("RTAIME_RENDERER_DURATION_SECONDS_PER_FORMAT");
        var warmupBoundaries = ParsePositiveInt("RTAIME_RENDERER_WARMUP_BOUNDARIES");
        var compositorSamples = ParsePositiveInt("RTAIME_RENDERER_COMPOSITOR_SAMPLES");
        var backpressureBoundaries = ParsePositiveInt("RTAIME_RENDERER_RECORDING_BACKPRESSURE_BOUNDARIES");
        var evidencePath = RequireEnvironment("RTAIME_RENDERER_REFERENCE_EVIDENCE");
        var sourceCommit = RequireEnvironment("RTAIME_RENDERER_SOURCE_COMMIT").ToLowerInvariant();

        if (sourceCommit.Length != 40 || sourceCommit.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("RTAIME_RENDERER_SOURCE_COMMIT must be an exact 40-character commit SHA.");

        var detected = CudaGpuProcessingBackend.Detect(deviceOrdinal);
        var failures = new List<string>();
        var compositorResults = new List<object>();
        var runtimeResults = new List<object>();

        if (!detected.Available ||
            !detected.HardwareAccelerated ||
            detected.Kind != GpuBackendKind.NvidiaCuda)
        {
            failures.Add(detected.Failure?.Message ?? "NVIDIA CUDA backend is unavailable.");
        }
        else if (!detected.DeviceName.Contains(expectedDevice, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Detected CUDA device '{detected.DeviceName}' does not match required reference device '{expectedDevice}'.");
        }

        if (failures.Count == 0)
        {
            try
            {
                compositorResults.AddRange(RunCompositorMatrix(deviceOrdinal, compositorSamples));
            }
            catch (Exception exception)
            {
                failures.Add($"CUDA compositor matrix failed: {exception.GetType().Name}: {exception.Message}");
            }

            foreach (var format in new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 })
            {
                try
                {
                    runtimeResults.Add(await RunRuntimeFormatAsync(
                        format,
                        deviceOrdinal,
                        durationSeconds,
                        warmupBoundaries,
                        backpressureBoundaries));
                }
                catch (Exception exception)
                {
                    failures.Add(
                        $"{FormatName(format)} production-path qualification failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        var report = new
        {
            copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
            schemaVersion = "1.0",
            qualification = "rtaime-renderer-reference",
            capturedAtUtc = DateTimeOffset.UtcNow,
            sourceCommit,
            profile = profileName,
            status = failures.Count == 0 ? "PASSED" : "FAILED",
            expectedDeviceName = expectedDevice,
            deviceOrdinal,
            detectedDeviceName = detected.DeviceName,
            totalMemoryBytes = detected.TotalMemoryBytes,
            compositorLayerCounts = new[] { 0, 1, 2, 4, 8 },
            compositor = compositorResults,
            runtime = runtimeResults,
            physicalExternalOutput = new
            {
                status = "UNVERIFIED",
                reason = "Professional Media I/O, genlock, physical A/V synchronization and scan-out latency remain owned by their dedicated physical qualification workflows."
            },
            physicalDeviceFaultInjection = new
            {
                status = "UNVERIFIED",
                reason = "Destructive CUDA device-reset/TDR injection is not performed by this renderer qualification profile; deterministic backend and interop failure behavior is exercised by software fault-injection scenarios."
            },
            failures
        };

        var fullEvidencePath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)!);
        File.WriteAllText(
            fullEvidencePath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        Assert.Empty(failures);
    }

    private static IReadOnlyList<object> RunCompositorMatrix(int deviceOrdinal, int sampleIterations)
    {
        var results = new List<object>();
        foreach (var format in new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 })
        {
            using var cudaBackend = new CudaGpuProcessingBackend(deviceOrdinal);
            using var cuda = new GpuProcessingProvider(cudaBackend);
            cuda.Start();

            using var reference = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
            reference.Start();

            var timing = new FrameTiming(
                0,
                0,
                new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator));

            using var cudaA = new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, 16, 32, 64))
                .Materialize(cuda, timing);
            using var cudaB = new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, 192, 128, 64))
                .Materialize(cuda, timing);
            using var refA = new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, 16, 32, 64))
                .Materialize(reference, timing);
            using var refB = new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, 192, 128, 64))
                .Materialize(reference, timing);

            var cudaLayers = new List<GpuFrame>();
            var referenceLayers = new List<GpuFrame>();
            try
            {
                for (var index = 0; index < GpuCompositeLimits.MaxActiveLayers; index++)
                {
                    var red = checked((byte)(32 + index * 19));
                    var green = checked((byte)(220 - index * 17));
                    var blue = checked((byte)(64 + index * 11));
                    var alpha = checked((byte)(64 + index * 20));
                    cudaLayers.Add(
                        new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, red, green, blue, alpha))
                            .Materialize(cuda, timing));
                    referenceLayers.Add(
                        new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, red, green, blue, alpha))
                            .Materialize(reference, timing));
                }

                foreach (var transition in new[]
                {
                    (Name: "CUT", Value: GpuTransition.CutToA),
                    (Name: "DISSOLVE", Value: GpuTransition.Dissolve(128))
                })
                {
                    foreach (var layerCount in new[] { 0, 1, 2, 4, 8 })
                    {
                        var expectedHash = CompositeHash(
                            reference,
                            refA,
                            refB,
                            referenceLayers.Take(layerCount),
                            transition.Value);

                        var baselineSurfaceCount = cuda.ActiveSurfaceCount;
                        var samples = new double[sampleIterations];
                        var pixelIntegrity = true;
                        var lifetimeCorrect = true;
                        var monitoringExported = false;

                        for (var index = 0; index < sampleIterations; index++)
                        {
                            var started = Stopwatch.GetTimestamp();
                            var result = cuda.Composite(GpuCompositeRequest.WithLayers(
                                MediaSourceId.New(),
                                cudaA,
                                cudaB,
                                transition.Value,
                                cudaLayers.Take(layerCount).Select((frame, layerIndex) =>
                                    new GpuKeyLayer(frame, checked((byte)(255 - layerIndex * 9)), true))));
                            if (!result.Succeeded)
                                throw new InvalidOperationException(result.Failure?.Message ?? "CUDA compositor request failed.");

                            using var output = result.Frame!;
                            using var readback = cuda.RentReadback(output);
                            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            if (index == 0)
                            {
                                pixelIntegrity = SHA256.HashData(readback.Memory.Span).SequenceEqual(expectedHash);
                                if (cuda.TryExportMonitoringResource(output, out var resource))
                                {
                                    monitoringExported = resource is not null;
                                    resource?.Dispose();
                                }
                            }

                            lifetimeCorrect &= cuda.ActiveSurfaceCount == baselineSurfaceCount + 1;
                        }

                        lifetimeCorrect &= cuda.ActiveSurfaceCount == baselineSurfaceCount;
                        Array.Sort(samples);
                        results.Add(new
                        {
                            format = FormatName(format),
                            transition = transition.Name,
                            layerCount,
                            samples = samples.Length,
                            frameBudgetMilliseconds = FrameBudget(format),
                            p50Milliseconds = Percentile(samples, 0.50),
                            p95Milliseconds = Percentile(samples, 0.95),
                            p99Milliseconds = Percentile(samples, 0.99),
                            maximumMilliseconds = samples[^1],
                            pixelIntegrity,
                            surfaceLifetimeCorrect = lifetimeCorrect,
                            monitoringExported
                        });

                        if (!pixelIntegrity)
                            throw new InvalidOperationException($"{FormatName(format)}/{transition.Name}/{layerCount} layer full-frame parity failed.");
                        if (!lifetimeCorrect)
                            throw new InvalidOperationException($"{FormatName(format)}/{transition.Name}/{layerCount} layer surface lifetime did not return to baseline.");
                        if (!monitoringExported)
                            throw new InvalidOperationException($"{FormatName(format)}/{transition.Name}/{layerCount} layer D3D11 monitoring export was unavailable.");
                    }
                }
            }
            finally
            {
                foreach (var frame in cudaLayers) frame.Dispose();
                foreach (var frame in referenceLayers) frame.Dispose();
            }

            cuda.Stop();
            reference.Stop();
            if (cuda.ActiveSurfaceCount != 0)
                throw new InvalidOperationException($"{FormatName(format)} CUDA compositor matrix retained surfaces after stop.");
        }

        return results;
    }

    private static byte[] CompositeHash(
        GpuProcessingProvider provider,
        GpuFrame backgroundA,
        GpuFrame backgroundB,
        IEnumerable<GpuFrame> layerFrames,
        GpuTransition transition)
    {
        var layers = layerFrames
            .Select((frame, index) => new GpuKeyLayer(frame, checked((byte)(255 - index * 9)), true))
            .ToArray();
        var result = provider.Composite(GpuCompositeRequest.WithLayers(
            MediaSourceId.New(),
            backgroundA,
            backgroundB,
            transition,
            layers));
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Failure?.Message ?? "Managed-reference compositor request failed.");

        using var output = result.Frame!;
        using var readback = provider.RentReadback(output);
        return SHA256.HashData(readback.Memory.Span);
    }

    private static async Task<object> RunRuntimeFormatAsync(
        VideoFormat format,
        int deviceOrdinal,
        int durationSeconds,
        int warmupBoundaries,
        int recordingBackpressureBoundaries)
    {
        var sourceA = new ProductionSourceId(Identity.New());
        var sourceB = new ProductionSourceId(Identity.New());
        var mediaA = new MediaSourceId(sourceA.Value);
        var mediaB = new MediaSourceId(sourceB.Value);
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            new ProductionId(Identity.New()),
            $"Renderer qualification {FormatName(format)}",
            new[]
            {
                new ProductionSourceSpecification(sourceA, "Input 1"),
                new ProductionSourceSpecification(sourceB, "Input 2")
            },
            new ProductionRoutingState(sourceB, sourceA));

        var timingCollector = new CudaGpuTimingCollector(65_536);
        var writer = new BackpressureQualificationWriter();
        await using var runtime = new V1RuntimeHostService(
            mediaA,
            mediaB,
            format,
            writer,
            new CudaGpuProcessingBackend(deviceOrdinal, timingCollector));

        var registry = new ProviderRegistry(runtime.ProviderDescriptors);
        var initialState = Assert.IsType<ControlStateSnapshot>(ControlDomainEngine.Initialize(specification).State).Authoritative;
        var initialPlan = CapabilityPlanningEngine.Plan(specification, initialState, registry);
        if (!initialPlan.Succeeded)
            throw new InvalidOperationException(
                "Initial renderer qualification plan failed: " +
                string.Join(" | ", initialPlan.Validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        var programSink = initialPlan.Graph!.Nodes
            .Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink)
            .MediaSinkId!.Value;
        if (!runtime.ApplyExecution(initialPlan.PreparedExecution!, programSink).Committed)
            throw new InvalidOperationException("Initial renderer qualification execution did not commit.");

        runtime.SetVisualLayerMode(V1VisualLayerMode.Static);
        var overlay = new byte[64 * 64 * 4];
        for (var offset = 0; offset < overlay.Length; offset += 4)
        {
            overlay[offset] = 24;
            overlay[offset + 1] = 180;
            overlay[offset + 2] = 240;
            overlay[offset + 3] = 128;
        }
        runtime.LoadGraphicsOverlay("qualification-overlay.rgba", 64, 64, overlay);
        runtime.SetGraphicsOverlay(true, 0.15, 0.12, 1.0);

        for (var index = 0; index < warmupBoundaries; index++)
        {
            using var warmup = runtime.ProcessNextBoundary();
        }

        RuntimeMonitoringSubscription? monitoring = runtime.MonitoringHub.Subscribe(
            capacity: 4,
            requiresCpuFallback: false);
        var recording = await runtime.StartRecordingAsync(RecordingSessionId.New(), RecordingOutputId.New());
        if (!recording.Succeeded)
            throw new InvalidOperationException(recording.Failure?.Message ?? "Renderer qualification recording failed to start.");

        var durations = new List<double>();
        var cpuUtilization = new List<double>();
        var gpuUtilization = new List<double>();
        var vramUsedMiB = new List<double>();
        var expectedSequence = runtime.Snapshot.NextSequenceNumber;
        ulong sequenceDiscontinuities = 0;
        ulong recordingAccepted = 0;
        ulong recordingRejected = 0;
        var acceptedAfterBackpressure = false;
        var monitoringDisconnected = false;
        var monitoringReconnected = false;
        var reconnectCountdown = -1;
        var transitionApplied = false;
        var maxReadbackActive = 0;
        var maxReadbackAllocated = 0;
        var maxSharedMonitoringActive = 0;
        var frameBudgetMilliseconds = FrameBudget(format);
        var startedAt = Stopwatch.GetTimestamp();
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var boundaryIndex = 0;

        try
        {
            while (Stopwatch.GetElapsedTime(startedAt) < duration)
            {
                var boundaryStarted = Stopwatch.GetTimestamp();
                using (var boundary = runtime.ProcessNextBoundary())
                {
                    var elapsedMilliseconds = Stopwatch.GetElapsedTime(boundaryStarted).TotalMilliseconds;
                    durations.Add(elapsedMilliseconds);

                    if (boundary.SequenceNumber != expectedSequence)
                        sequenceDiscontinuities++;
                    expectedSequence = boundary.SequenceNumber + 1;

                    if (boundary.Recording is { Accepted: true })
                    {
                        recordingAccepted++;
                        if (!writer.IsBlocked) acceptedAfterBackpressure = true;
                    }
                    else if (boundary.Recording is { Accepted: false })
                    {
                        recordingRejected++;
                    }

                    if (boundary.ActiveGpuSurfacesAfterBoundary != 0)
                        throw new InvalidOperationException(
                            $"Runtime boundary {boundary.SequenceNumber} retained {boundary.ActiveGpuSurfacesAfterBoundary} GPU surfaces.");
                }

                boundaryIndex++;
                var readback = runtime.ProgramReadbackPoolStatistics;
                var transfers = runtime.GpuMemoryTransfers;
                maxReadbackActive = Math.Max(maxReadbackActive, readback.ActiveBuffers);
                maxReadbackAllocated = Math.Max(maxReadbackAllocated, readback.AllocatedBuffers);
                maxSharedMonitoringActive = Math.Max(
                    maxSharedMonitoringActive,
                    transfers.MonitoringResources.ActiveResources);

                if (boundaryIndex == recordingBackpressureBoundaries)
                    writer.Release();

                if (!monitoringDisconnected && boundaryIndex >= Math.Max(8, recordingBackpressureBoundaries / 2))
                {
                    await monitoring.DisposeAsync();
                    monitoring = null;
                    monitoringDisconnected = true;
                    reconnectCountdown = 3;
                }
                else if (monitoringDisconnected && !monitoringReconnected && reconnectCountdown-- == 0)
                {
                    monitoring = runtime.MonitoringHub.Subscribe(capacity: 4, requiresCpuFallback: false);
                    monitoringReconnected = true;
                }

                if (!transitionApplied && Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(durationSeconds / 3.0))
                {
                    var replacement = new AuthoritativeProductionState(
                        ControlContractVersion.Current,
                        specification.ProductionId,
                        new Revision(1),
                        new ProductionRoutingState(sourceA, sourceB));
                    var replacementPlan = CapabilityPlanningEngine.Plan(specification, replacement, registry);
                    if (!replacementPlan.Succeeded)
                        throw new InvalidOperationException(
                            "Renderer qualification transition plan failed: " +
                            string.Join(" | ", replacementPlan.Validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
                    var applied = runtime.ApplyExecution(
                        replacementPlan.PreparedExecution!,
                        programSink,
                        RuntimeProgramTransitionIntent.Dissolve(mediaA, mediaB, 4));
                    if (!applied.Committed)
                        throw new InvalidOperationException("Renderer qualification DISSOLVE did not commit.");
                    transitionApplied = true;
                }

                if (boundaryIndex % 30 == 0)
                {
                    var performance = runtime.Snapshot.Performance;
                    if (performance.CpuUtilizationPercent is { } cpu) cpuUtilization.Add(cpu);
                    if (performance.GpuUtilizationPercent is { } gpu) gpuUtilization.Add(gpu);
                    if (performance.GpuVramUsedBytes is { } vram) vramUsedMiB.Add(vram / 1024.0 / 1024.0);
                }

                var remaining = TimeSpan.FromMilliseconds(frameBudgetMilliseconds - durations[^1]);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
            }
        }
        finally
        {
            writer.Release();
            if (monitoring is not null)
                await monitoring.DisposeAsync();
        }

        var stop = await runtime.StopRecordingAsync();
        if (stop.Status != RecordingStopStatus.Stopped)
            throw new InvalidOperationException($"Renderer qualification recording stopped with '{stop.Status}'.");

        await WaitUntilAsync(
            () => runtime.MonitoringStatistics.ActiveSharedResources == 0 &&
                  runtime.GpuMemoryTransfers.MonitoringResources.ActiveResources == 0,
            TimeSpan.FromSeconds(5));

        var finalSnapshot = runtime.Snapshot;
        var finalReadback = runtime.ProgramReadbackPoolStatistics;
        var finalTransfers = runtime.GpuMemoryTransfers;
        var monitoringStats = runtime.MonitoringStatistics;
        var hubStats = runtime.MonitoringHub.Statistics;

        if (finalSnapshot.ActiveGpuSurfaces != 0)
            throw new InvalidOperationException("Renderer qualification retained active GPU surfaces after the final boundary.");
        if (finalReadback.ActiveBuffers != 0)
            throw new InvalidOperationException("Renderer qualification retained active Program readback buffers.");
        if (finalTransfers.MonitoringResources.ActiveResources != 0)
            throw new InvalidOperationException("Renderer qualification retained shared monitoring resources.");
        if (!monitoringDisconnected || !monitoringReconnected)
            throw new InvalidOperationException("Renderer qualification did not execute monitoring disconnect/reconnect.");
        if (recordingRejected == 0)
            throw new InvalidOperationException("Renderer qualification did not observe the controlled recording-backpressure phase.");
        if (!acceptedAfterBackpressure)
            throw new InvalidOperationException("Renderer qualification did not recover recording acceptance after backpressure release.");
        if (!transitionApplied)
            throw new InvalidOperationException("Renderer qualification did not execute the governed DISSOLVE transition.");
        if (sequenceDiscontinuities != 0)
            throw new InvalidOperationException($"Renderer qualification observed {sequenceDiscontinuities} sequence discontinuities.");
        if (finalTransfers.MonitoringResources.TotalExports == 0)
            throw new InvalidOperationException("Renderer qualification did not export CUDA/D3D11 monitoring resources.");

        durations.Sort();
        var deadlineViolations = checked((ulong)durations.Count(value => value > frameBudgetMilliseconds));
        var gpuTiming = BuildGpuTiming(timingCollector.Snapshot());
        var runtimeDroppedFrames = finalSnapshot.Performance.DroppedFrames;

        return new
        {
            format = FormatName(format),
            durationSeconds,
            boundaries = durations.Count,
            frameBudgetMilliseconds,
            cpuBoundary = new
            {
                p50Milliseconds = Percentile(durations, 0.50),
                p95Milliseconds = Percentile(durations, 0.95),
                p99Milliseconds = Percentile(durations, 0.99),
                maximumMilliseconds = durations[^1],
                deadlineViolations
            },
            gpuTimings = gpuTiming,
            telemetry = new
            {
                cpuUtilizationPercent = Percentiles(cpuUtilization),
                gpuUtilizationPercent = Percentiles(gpuUtilization),
                vramUsedMiB = Percentiles(vramUsedMiB)
            },
            continuity = new
            {
                sequenceDiscontinuities,
                runtimeDroppedFrames,
                programFramesWritten = runtime.ProgramFramesWritten
            },
            recording = new
            {
                accepted = recordingAccepted,
                rejectedDuringPressure = recordingRejected,
                writerWrites = writer.Writes,
                recoveredAfterPressure = acceptedAfterBackpressure
            },
            monitoring = new
            {
                disconnected = monitoringDisconnected,
                reconnected = monitoringReconnected,
                captured = monitoringStats.Captured,
                droppedBeforeProcessing = monitoringStats.DroppedBeforeProcessing,
                processed = monitoringStats.Processed,
                published = hubStats.Published,
                droppedBySubscribers = hubStats.DroppedBySubscribers,
                totalSharedExports = finalTransfers.MonitoringResources.TotalExports,
                rejectedSharedExports = finalTransfers.MonitoringResources.RejectedExports,
                maxActiveSharedResources = maxSharedMonitoringActive
            },
            transfers = new
            {
                finalTransfers.UploadOperations,
                finalTransfers.UploadBytes,
                finalTransfers.HostToDeviceOperations,
                finalTransfers.HostToDeviceBytes,
                finalTransfers.ReadbackOperations,
                finalTransfers.ReadbackBytes,
                finalTransfers.DeviceToHostOperations,
                finalTransfers.DeviceToHostBytes,
                finalTransfers.MonitoringDeviceCopyOperations,
                finalTransfers.MonitoringDeviceCopyBytes,
                finalTransfers.ReusableUploadHits,
                finalTransfers.ReusableUploadMisses,
                finalTransfers.AvoidedUploadBytes,
                finalTransfers.AvoidedHostToDeviceBytes
            },
            pools = new
            {
                maxReadbackActive,
                maxReadbackAllocated,
                finalReadback.Capacity,
                finalReadback.AllocatedBuffers,
                finalReadback.AvailableBuffers,
                finalReadback.ActiveBuffers,
                finalReadback.ExhaustedRents
            },
            cleanup = new
            {
                activeGpuSurfaces = finalSnapshot.ActiveGpuSurfaces,
                activeReadbackBuffers = finalReadback.ActiveBuffers,
                activeSharedMonitoringResources = finalTransfers.MonitoringResources.ActiveResources
            }
        };
    }

    private static IReadOnlyList<object> BuildGpuTiming(IReadOnlyList<CudaGpuTimingSample> samples)
    {
        var results = new List<object>();
        foreach (var operation in Enum.GetValues<CudaGpuTimingOperation>())
        {
            var values = samples
                .Where(sample => sample.Operation == operation)
                .Select(sample => sample.Milliseconds)
                .OrderBy(value => value)
                .ToArray();
            if (values.Length == 0) continue;

            results.Add(new
            {
                operation = operation.ToString(),
                samples = values.Length,
                p50Milliseconds = Percentile(values, 0.50),
                p95Milliseconds = Percentile(values, 0.95),
                p99Milliseconds = Percentile(values, 0.99),
                maximumMilliseconds = values[^1]
            });
        }

        return results;
    }

    private static object Percentiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return new { samples = 0, p50 = (double?)null, p95 = (double?)null, p99 = (double?)null, maximum = (double?)null };

        var sorted = values.OrderBy(value => value).ToArray();
        return new
        {
            samples = sorted.Length,
            p50 = (double?)Percentile(sorted, 0.50),
            p95 = (double?)Percentile(sorted, 0.95),
            p99 = (double?)Percentile(sorted, 0.99),
            maximum = (double?)sorted[^1]
        };
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return double.NaN;
        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double FrameBudget(VideoFormat format) =>
        1000.0 * format.FrameRate.Denominator / format.FrameRate.Numerator;

    private static string FormatName(VideoFormat format) =>
        format == VideoFormat.Hd1080p50Rgba8 ? "1080p50" : "1080p59.94";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        if (!condition())
            throw new TimeoutException("Renderer qualification resource cleanup did not reach the expected baseline.");
    }

    private static int ParsePositiveInt(string name)
    {
        if (!int.TryParse(RequireEnvironment(name), out var value) || value <= 0)
            throw new InvalidOperationException($"Environment variable '{name}' must be a positive integer.");
        return value;
    }

    private static int ParseNonNegativeInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!int.TryParse(raw, out var value) || value < 0)
            throw new InvalidOperationException($"Environment variable '{name}' must be a non-negative integer.");
        return value;
    }

    private static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Environment variable '{name}' is required for renderer qualification.");
        return value.Trim();
    }

    private sealed class ProviderRegistry : IProviderCapabilityRegistry
    {
        private readonly IReadOnlyList<ProviderDescriptor> _providers;
        public ProviderRegistry(IReadOnlyList<ProviderDescriptor> providers) => _providers = providers;
        public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
    }

    private sealed class BackpressureQualificationWriter : IProgramRecordingWriter
    {
        private readonly TaskCompletionSource<bool> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked = 1;
        private long _writes;

        public bool IsBlocked => Volatile.Read(ref _blocked) != 0;
        public ulong Writes => checked((ulong)Interlocked.Read(ref _writes));

        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            if (IsBlocked)
                await _released.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _writes);
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Release()
        {
            if (Interlocked.Exchange(ref _blocked, 0) == 0) return;
            _released.TrySetResult(true);
        }
    }
}
